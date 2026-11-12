using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using UEToVS.Engine.Index.Query;
using UEToVS.Engine.Index.Segments;
using UEToVS.Engine.Index.Storage;

namespace UEToVS.Engine.Index
{
    /// <summary>Per-file metadata stored in a base segment (what the file looked like when it was parsed).</summary>
    public readonly struct BaseFileState
    {
        public BaseFileState(string path, long mtimeTicks, long length, ulong contentHash)
        {
            Path = path;
            MtimeTicks = mtimeTicks;
            Length = length;
            ContentHash = contentHash;
        }

        public string Path { get; }
        public long MtimeTicks { get; }
        public long Length { get; }
        public ulong ContentHash { get; }
    }

    /// <summary>
    /// Bulk operations used by the indexing pipeline (no on-disk format change): append whole segments instead of
    /// pushing thousands of files through the delta, merge segments of one kind, adopt a segment file built elsewhere
    /// (shared engine cache), list the per-file state of base segments, and reset everything (rebuild).
    /// </summary>
    public sealed partial class SymbolIndex
    {
        /// <summary>
        /// Writes <paramref name="files"/> as a new base segment of <paramref name="kind"/> and adds it next to the
        /// existing ones. Older copies of the same files (in any base) are tombstoned; a file that has a delta entry keeps
        /// the delta version (the delta is always at least as new). Bases left without live files are retired.
        /// O(files in the batch); does not touch other segments' data. Thread-safe; blocks other maintenance.
        /// </summary>
        public int AppendSegment(SegmentKind kind, IReadOnlyCollection<FileSymbols> files, CancellationToken ct = default)
        {
            ThrowIfDisposed();
            if (files == null || files.Count == 0) return 0;
            lock (_maintenanceLock)
            {
                using (var scope = L.Time("index.segment.append", double.MaxValue, ("kind", kind.ToString()), ("files", files.Count)))
                {
                    var seg = WriteAndOpen(kind, files, ct);
                    Snapshot published;
                    lock (_writeLock)
                    {
                        var s1 = _current;
                        var newView = new SegmentView(seg, null, 0);
                        var newDead = new List<int>();
                        var kill = new List<int>[s1.Bases.Length];
                        var segFiles = seg.Files;
                        for (int fi = 0; fi < segFiles.Length; fi++)
                        {
                            string key = PathKey.Normalize(seg.GetString(segFiles[fi].PathId));
                            if (s1.Delta.ContainsKey(key))
                            {
                                newDead.Add(fi);
                                continue;
                            }
                            for (int b = 0; b < s1.Bases.Length; b++)
                            {
                                int ofi = s1.Bases[b].Segment.FindFile(key);
                                if (ofi >= 0 && !s1.Bases[b].IsDead((uint)ofi)) (kill[b] ??= new List<int>()).Add(ofi);
                            }
                        }
                        newView = WithTombstones(newView, newDead);
                        var bases = new List<SegmentView>(s1.Bases.Length + 1);
                        var retired = new List<Segment>();
                        for (int b = 0; b < s1.Bases.Length; b++)
                        {
                            var v = kill[b] == null ? s1.Bases[b] : WithTombstones(s1.Bases[b], kill[b]);
                            if (v.TombstoneCount >= v.Segment.FileCount && v.Segment.FileCount > 0) retired.Add(v.Segment);
                            else bases.Add(v);
                        }
                        int insertAt = kind == SegmentKind.Engine ? bases.Count(v => v.Segment.Kind == SegmentKind.Engine) : bases.Count;
                        bases.Insert(insertAt, newView);
                        published = new Snapshot(bases.ToArray(), s1.Delta, s1.Version + 1);
                        foreach (var r in retired) RetireIfOwned(r);
                        Publish(published);
                    }
                    seg.Release();
                    SaveManifest(published);
                    L.Debug("index.segment.appended", null, ("kind", kind.ToString()), ("file", seg.FileName), ("files", seg.FileCount),
                        ("symbols", seg.SymbolCount), ("bytes", seg.Length), ("ms", Math.Round(scope.ElapsedMs, 1)));
                    return seg.FileCount;
                }
            }
        }

        /// <summary>
        /// Merges every base segment of <paramref name="kind"/> (live files only) into one. Returns false when there is
        /// at most one segment of that kind and it has no tombstones. Updates made meanwhile are preserved.
        /// Memory: materialises the merged files once (like <see cref="Compact"/>); run on a background thread.
        /// </summary>
        public bool MergeBases(SegmentKind kind, CancellationToken ct = default)
        {
            ThrowIfDisposed();
            lock (_maintenanceLock)
            {
                Snapshot s0 = AcquireInner();
                try
                {
                    var olds = s0.Bases.Where(b => b.Segment.Kind == kind).ToArray();
                    if (olds.Length == 0 || (olds.Length == 1 && olds[0].TombstoneCount == 0)) return false;
                    using (var scope = L.Time("index.segment.merge", double.MaxValue, ("kind", kind.ToString()), ("segments", olds.Length)))
                    {
                        var seg = WriteAndOpen(kind, olds.SelectMany(v => SegmentExport.LiveFiles(v, ct)), ct);
                        Snapshot published;
                        lock (_writeLock)
                        {
                            var s1 = _current;
                            var oldSegs = new HashSet<Segment>(olds.Select(o => o.Segment));
                            var dead = new List<int>();
                            var segFiles = seg.Files;
                            for (int fi = 0; fi < segFiles.Length; fi++)
                            {
                                string key = PathKey.Normalize(seg.GetString(segFiles[fi].PathId));
                                if (s1.Delta.ContainsKey(key)) { dead.Add(fi); continue; }
                                foreach (var v in s1.Bases)
                                {
                                    if (!oldSegs.Contains(v.Segment)) continue;
                                    int ofi = v.Segment.FindFile(key);
                                    if (ofi >= 0 && v.IsDead((uint)ofi)) { dead.Add(fi); break; } // superseded while merging
                                }
                            }
                            var view = WithTombstones(new SegmentView(seg, null, 0), dead);
                            var bases = s1.Bases.Where(b => !oldSegs.Contains(b.Segment)).ToList();
                            bases.Insert(kind == SegmentKind.Engine ? 0 : bases.Count, view);
                            published = new Snapshot(bases.ToArray(), s1.Delta, s1.Version + 1);
                            foreach (var o in oldSegs) RetireIfOwned(o);
                            Publish(published);
                        }
                        seg.Release();
                        SaveManifest(published);
                        L.Info("index.segment.merged", "Base segments merged", ("kind", kind.ToString()), ("from", olds.Length), ("file", seg.FileName),
                            ("files", seg.FileCount), ("symbols", seg.SymbolCount), ("bytes", seg.Length), ("ms", Math.Round(scope.ElapsedMs, 1)));
                        return true;
                    }
                }
                finally
                {
                    s0.Release();
                }
            }
        }

        /// <summary>
        /// Replaces every base segment of <paramref name="kind"/> with the given segment files, used in place (memory-
        /// mapped read-only, never written or deleted by this index): the shared engine cache. Copies of the same files
        /// in the delta or in bases of the other kind win over the attached ones. Replaced segments that this index owns
        /// are deleted once unused. Returns false (and changes nothing) when a file does not open with this index's
        /// knowledge-base version.
        /// </summary>
        public bool AttachExternalSegments(SegmentKind kind, IReadOnlyList<string> paths, out string detail)
        {
            ThrowIfDisposed();
            detail = null;
            lock (_maintenanceLock)
            {
                var segs = new List<Segment>();
                foreach (var path in paths)
                {
                    var status = Segment.TryOpen(Path.GetFullPath(path), _options.KnowledgeBaseVersion, _options.EngineBuildId, _options.VerifyChecksums, out var seg, out string openDetail);
                    if (status != SegmentLoadStatus.Ok || seg.Kind != kind)
                    {
                        seg?.Release();
                        foreach (var x in segs) x.Release();
                        detail = $"{Path.GetFileName(path)}: {status} {openDetail}";
                        return false;
                    }
                    segs.Add(seg);
                }
                Snapshot published;
                lock (_writeLock)
                {
                    var s1 = _current;
                    var others = s1.Bases.Where(b => b.Segment.Kind != kind).ToList();
                    var views = new List<SegmentView>();
                    var seen = new HashSet<string>(StringComparer.Ordinal);
                    // later segments are newer (append order): walk backwards so the newest copy of a file stays live
                    for (int si = segs.Count - 1; si >= 0; si--)
                    {
                        var seg = segs[si];
                        var dead = new List<int>();
                        var segFiles = seg.Files;
                        for (int fi = 0; fi < segFiles.Length; fi++)
                        {
                            string key = PathKey.Normalize(seg.GetString(segFiles[fi].PathId));
                            bool superseded = s1.Delta.ContainsKey(key) || !seen.Add(key);
                            if (!superseded)
                            {
                                foreach (var v in others)
                                {
                                    int ofi = v.Segment.FindFile(key);
                                    if (ofi >= 0 && !v.IsDead((uint)ofi)) { superseded = true; break; }
                                }
                            }
                            if (superseded) dead.Add(fi);
                        }
                        views.Insert(0, WithTombstones(new SegmentView(seg, null, 0), dead));
                    }
                    var bases = kind == SegmentKind.Engine ? views.Concat(others) : others.Concat(views);
                    published = new Snapshot(bases.ToArray(), s1.Delta, s1.Version + 1);
                    foreach (var b in s1.Bases) if (b.Segment.Kind == kind) RetireIfOwned(b.Segment);
                    Publish(published);
                }
                foreach (var seg in segs) seg.Release();
                SaveManifest(published);
                L.Info("index.segment.attached", "External segments attached", ("kind", kind.ToString()), ("segments", segs.Count),
                    ("files", segs.Sum(x => x.FileCount)), ("dir", paths.Count > 0 ? Path.GetDirectoryName(paths[0]) : null));
                return true;
            }
        }

        /// <summary>True when there are base segments of <paramref name="kind"/> and all of them live outside this index's folders (shared).</summary>
        public bool AllBasesExternal(SegmentKind kind)
        {
            var s = AcquireInner();
            try
            {
                var list = s.Bases.Where(b => b.Segment.Kind == kind).ToList();
                return list.Count > 0 && list.All(b => !IsOwnedPath(_options, b.Segment.FullPath));
            }
            finally
            {
                s.Release();
            }
        }

        /// <summary>A segment file inside <see cref="IndexOptions.Directory"/> or <see cref="IndexOptions.EngineDirectory"/>.</summary>
        internal static bool IsOwnedPath(IndexOptions options, string path) =>
            IsUnder(options.Directory, path) || (!string.IsNullOrEmpty(options.EngineDirectory) && IsUnder(options.EngineDirectory, path));

        private static bool IsUnder(string dir, string file)
        {
            if (string.IsNullOrEmpty(dir) || string.IsNullOrEmpty(file)) return false;
            string d = Path.TrimEndingDirectorySeparator(Path.GetFullPath(dir)) + Path.DirectorySeparatorChar;
            return Path.GetFullPath(file).StartsWith(d, StringComparison.OrdinalIgnoreCase);
        }

        private void RetireIfOwned(Segment seg)
        {
            if (IsOwnedPath(_options, seg.FullPath)) seg.Retire();
        }

        /// <summary>Manifest entry: file name inside the index folder, relative path for the engine folder, absolute otherwise.</summary>
        private string ManifestName(string fullPath)
        {
            string dir = Path.TrimEndingDirectorySeparator(Path.GetFullPath(_options.Directory));
            string full = Path.GetFullPath(fullPath);
            if (string.Equals(Path.GetDirectoryName(full), dir, StringComparison.OrdinalIgnoreCase)) return Path.GetFileName(full);
            if (IsOwnedPath(_options, full)) return Path.GetRelativePath(dir, full);
            return full;
        }

        /// <summary>Full paths of the base segment files of <paramref name="kind"/> (current snapshot).</summary>
        public IReadOnlyList<string> BaseSegmentPaths(SegmentKind kind)
        {
            var s = AcquireInner();
            try
            {
                return s.Bases.Where(b => b.Segment.Kind == kind).Select(b => b.Segment.FullPath).ToList();
            }
            finally
            {
                s.Release();
            }
        }

        /// <summary>Number of base segments of <paramref name="kind"/>.</summary>
        public int BaseSegmentCount(SegmentKind kind) => BaseSegmentPaths(kind).Count;

        /// <summary>
        /// Stored per-file state of the base segments of <paramref name="kind"/>. <paramref name="includeDead"/> lists
        /// tombstoned files too: the state then describes exactly what the segment files contain.
        /// </summary>
        public List<BaseFileState> BaseFileStates(SegmentKind kind, bool includeDead)
        {
            var list = new List<BaseFileState>();
            var s = AcquireInner();
            try
            {
                foreach (var v in s.Bases)
                {
                    if (v.Segment.Kind != kind) continue;
                    var seg = v.Segment;
                    var files = seg.Files;
                    for (int fi = 0; fi < files.Length; fi++)
                    {
                        if (!includeDead && v.IsDead((uint)fi)) continue;
                        list.Add(new BaseFileState(seg.GetString(files[fi].PathId), files[fi].MtimeTicks, files[fi].Length, files[fi].ContentHash));
                    }
                }
                return list;
            }
            finally
            {
                s.Release();
            }
        }

        /// <summary>
        /// Runs <paramref name="action"/> with the current segment files of <paramref name="kind"/> kept alive (they cannot
        /// be deleted by a concurrent merge while the action copies them).
        /// </summary>
        public void WithBaseSegments(SegmentKind kind, Action<IReadOnlyList<string>> action)
        {
            var s = AcquireInner();
            try
            {
                action(s.Bases.Where(b => b.Segment.Kind == kind).Select(b => b.Segment.FullPath).ToList());
            }
            finally
            {
                s.Release();
            }
        }

        /// <summary>Drops every base segment of the given kinds (and the delta with the project part) for a rebuild. The files are deleted once unused.</summary>
        public void Reset(params SegmentKind[] kinds)
        {
            ThrowIfDisposed();
            var set = new HashSet<SegmentKind>(kinds == null || kinds.Length == 0 ? new[] { SegmentKind.Engine, SegmentKind.Project } : kinds);
            lock (_maintenanceLock)
            {
                Snapshot published;
                lock (_writeLock)
                {
                    var s1 = _current;
                    var keep = s1.Bases.Where(b => !set.Contains(b.Segment.Kind)).ToArray();
                    foreach (var b in s1.Bases) if (set.Contains(b.Segment.Kind)) RetireIfOwned(b.Segment);
                    // the delta holds project-side increments: dropped with the project part, kept for an engine-only reset
                    var delta = set.Contains(SegmentKind.Project) ? ImmutableDictionary.Create<string, DeltaFile>(StringComparer.Ordinal) : s1.Delta;
                    published = new Snapshot(keep, delta, s1.Version + 1);
                    Publish(published);
                }
                SaveManifest(published);
                L.Info("index.reset", "Index segments dropped", ("kinds", string.Join(",", set)));
            }
        }

        private static SegmentView WithTombstones(SegmentView view, List<int> fileIds)
        {
            if (fileIds == null || fileIds.Count == 0) return view;
            var bits = view.Tombstones != null ? (ulong[])view.Tombstones.Clone() : new ulong[(view.Segment.FileCount + 63) / 64];
            int count = view.TombstoneCount;
            foreach (int fi in fileIds)
            {
                ulong mask = 1UL << (fi & 63);
                if ((bits[fi >> 6] & mask) != 0) continue;
                bits[fi >> 6] |= mask;
                count++;
            }
            return new SegmentView(view.Segment, bits, count);
        }
    }

    /// <summary>Hard links (NTFS / POSIX); false when not supported (different volume, FAT, permissions).</summary>
    public static class FileLinks
    {
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true, EntryPoint = "CreateHardLinkW")]
        private static extern bool CreateHardLink(string newFile, string existingFile, IntPtr security);

        [DllImport("libc", SetLastError = true, EntryPoint = "link")]
        private static extern int PosixLink(string oldPath, string newPath);

        public static bool TryHardLink(string existing, string newPath)
        {
            try
            {
                if (File.Exists(newPath)) File.Delete(newPath);
                if (OperatingSystem.IsWindows()) return CreateHardLink(newPath, existing, IntPtr.Zero);
                return PosixLink(existing, newPath) == 0;
            }
            catch (Exception)
            {
                return false;
            }
        }
    }
}
