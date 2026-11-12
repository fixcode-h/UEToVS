using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using UEToVS.Contracts;
using UEToVS.Core.Diagnostics;
using UEToVS.Engine.Index.Query;
using UEToVS.Engine.Index.Segments;
using UEToVS.Engine.Index.Storage;

namespace UEToVS.Engine.Index
{
    /// <summary>
    /// Segmented symbol index (LSM style): immutable memory-mapped base segments (engine, project) + an in-memory
    /// delta of recently changed files + tombstones hiding superseded base files.
    /// <list type="bullet">
    /// <item>Readers work on immutable snapshots (<see cref="AcquireSnapshot"/>); the convenience query methods on this
    /// class acquire one per call.</item>
    /// <item><see cref="Update"/> / <see cref="Remove"/> cost O(that file) and publish a new snapshot.</item>
    /// <item><see cref="Compact"/> (call from a background thread) merges the delta and project segments into one new
    /// project segment and swaps it in atomically; readers on the old snapshot are unaffected.</item>
    /// </list>
    /// The delta is not persisted: after a restart the caller re-checks files (mtime / <see cref="IndexedFile.ContentHash"/>)
    /// and re-applies changes. Removals and compacted changes are persisted in manifest.json.
    /// </summary>
    public sealed class SymbolIndex : IDisposable
    {
        private static readonly Logger L = Log.For("Index");

        private readonly IndexOptions _options;
        private readonly object _writeLock = new object();
        private readonly object _maintenanceLock = new object();
        private Snapshot _current;
        private int _nextSegmentNumber;
        private bool _disposed;

        private SymbolIndex(IndexOptions options, Snapshot initial, int nextSegmentNumber)
        {
            _options = options;
            _current = initial;
            _nextSegmentNumber = nextSegmentNumber;
        }

        public string Directory => _options.Directory;

        public IndexOptions Options => _options;

        // ------------------------------------------------------------------ open

        /// <summary>
        /// Opens (or creates) the index in <see cref="IndexOptions.Directory"/>. Never throws for bad on-disk data:
        /// corrupt or version-mismatched segments are logged, deleted and reported in <paramref name="result"/> so the
        /// caller can rebuild them with <see cref="ReplaceBase"/>.
        /// </summary>
        public static SymbolIndex Open(IndexOptions options, out IndexOpenResult result)
        {
            if (options == null) throw new ArgumentNullException(nameof(options));
            if (string.IsNullOrEmpty(options.Directory)) throw new ArgumentException("IndexOptions.Directory is required", nameof(options));
            result = new IndexOpenResult();
            using (L.Time("index.open", 2000, ("dir", options.Directory)))
            {
                System.IO.Directory.CreateDirectory(options.Directory);
                var manifest = Manifest.Load(options.Directory, out string manifestError);
                if (manifest == null)
                {
                    result.ManifestMissing = true;
                    if (manifestError != "missing")
                        L.Warn("index.manifest.invalid", "Index manifest unreadable; starting empty", ("error", manifestError));
                    manifest = new Manifest();
                }

                var views = new List<SegmentView>();
                var opened = new List<Segment>();
                var kept = new List<ManifestSegment>();
                foreach (var entry in manifest.Segments)
                {
                    string path = Path.Combine(options.Directory, entry.File ?? string.Empty);
                    var status = Segment.TryOpen(path, options.KnowledgeBaseVersion, options.EngineBuildId, options.VerifyChecksums,
                        out var seg, out string detail);
                    if (status == SegmentLoadStatus.Ok && seg.Kind != entry.Kind)
                    {
                        status = SegmentLoadStatus.Corrupt;
                        detail = $"segment kind {seg.Kind}, manifest says {entry.Kind}";
                        seg.Release();
                        seg = null;
                    }
                    result.Segments.Add(new SegmentLoadResult { FileName = entry.File, Kind = entry.Kind, Status = status, Detail = detail });
                    if (status != SegmentLoadStatus.Ok)
                    {
                        L.Warn("index.segment.rejected", "Segment rejected; it must be rebuilt", ("file", entry.File), ("kind", entry.Kind.ToString()),
                            ("status", status.ToString()), ("detail", detail));
                        TryDelete(path);
                        continue;
                    }
                    opened.Add(seg);
                    kept.Add(entry);
                    var view = new SegmentView(seg, null, 0);
                    foreach (var t in entry.Tombstones ?? new List<string>()) view = view.WithTombstone(seg.FindFile(PathKey.Normalize(t)));
                    views.Add(view);
                }

                // engine segments first, then project segments (manifest order within a kind)
                var ordered = views.Where(v => v.Segment.Kind == SegmentKind.Engine).Concat(views.Where(v => v.Segment.Kind != SegmentKind.Engine)).ToArray();
                var snap = new Snapshot(ordered, ImmutableDictionary.Create<string, DeltaFile>(StringComparer.Ordinal), 1);
                foreach (var s in opened) s.Release(); // the snapshot now owns them

                result.NeedsEngineRebuild = !ordered.Any(v => v.Segment.Kind == SegmentKind.Engine);
                result.NeedsProjectRebuild = !ordered.Any(v => v.Segment.Kind == SegmentKind.Project);

                var index = new SymbolIndex(options, snap, Math.Max(1, manifest.NextSegmentNumber));
                CleanupStrayFiles(options.Directory, kept.Select(k => k.File));
                if (kept.Count != manifest.Segments.Count || result.ManifestMissing) index.SaveManifest(snap);

                L.Info("index.opened", "Index opened", ("dir", options.Directory), ("segments", ordered.Length),
                    ("rejected", manifest.Segments.Count - kept.Count), ("needsEngine", result.NeedsEngineRebuild),
                    ("needsProject", result.NeedsProjectRebuild));
                return index;
            }
        }

        private static void CleanupStrayFiles(string dir, IEnumerable<string> keep)
        {
            var keepSet = new HashSet<string>(keep, StringComparer.OrdinalIgnoreCase);
            try
            {
                foreach (var f in System.IO.Directory.EnumerateFiles(dir))
                {
                    string name = Path.GetFileName(f);
                    bool stray = (name.EndsWith(".seg", StringComparison.OrdinalIgnoreCase) && !keepSet.Contains(name)) ||
                                 name.EndsWith(".seg.tmp", StringComparison.OrdinalIgnoreCase);
                    if (stray) TryDelete(f);
                }
            }
            catch (Exception ex)
            {
                L.Warn("index.cleanup_failed", "Could not clean up stray segment files", ex, ("dir", dir));
            }
        }

        private static void TryDelete(string path)
        {
            try
            {
                if (File.Exists(path)) File.Delete(path);
            }
            catch (Exception ex)
            {
                L.Warn("index.delete_failed", "Could not delete file", ex, ("path", path));
            }
        }

        // ------------------------------------------------------------------ snapshots

        /// <summary>Returns the current snapshot; dispose it when done.</summary>
        public IndexSnapshot AcquireSnapshot()
        {
            ThrowIfDisposed();
            return new IndexSnapshot(AcquireInner());
        }

        private Snapshot AcquireInner()
        {
            while (true)
            {
                var s = Volatile.Read(ref _current);
                if (s.TryAcquire()) return s;
                ThrowIfDisposed();
            }
        }

        /// <summary>Publishes a new snapshot; caller holds <see cref="_writeLock"/>.</summary>
        private void Publish(Snapshot next)
        {
            var old = Interlocked.Exchange(ref _current, next);
            old.Release();
        }

        // ------------------------------------------------------------------ base segments

        /// <summary>
        /// Builds a new base segment of <paramref name="kind"/> from <paramref name="files"/> and swaps it in, replacing
        /// every existing segment of that kind. Delta entries for files contained in the new segment are dropped (the
        /// rebuild is newer); tombstones on segments of the other kind are kept. Thread-safe; blocks other maintenance.
        /// </summary>
        public void ReplaceBase(SegmentKind kind, IEnumerable<FileSymbols> files, CancellationToken ct = default)
        {
            ThrowIfDisposed();
            lock (_maintenanceLock)
            {
                using (var scope = L.Time("index.replace_base", double.MaxValue, ("kind", kind.ToString())))
                {
                    try
                    {
                        var seg = WriteAndOpen(kind, files, ct);
                        List<Segment> retired;
                        Snapshot published;
                        lock (_writeLock)
                        {
                            var cur = _current;
                            var bases = new List<SegmentView>();
                            retired = cur.Bases.Where(b => b.Segment.Kind == kind).Select(b => b.Segment).ToList();
                            var delta = cur.Delta;
                            foreach (var d in cur.Delta.Values)
                                if (seg.FindFile(d.Key) >= 0) delta = delta.Remove(d.Key);
                            var newView = new SegmentView(seg, null, 0);
                            // delta entries that remain must still hide the new segment's copy (none by construction)
                            if (kind == SegmentKind.Engine) bases.Add(newView);
                            bases.AddRange(cur.Bases.Where(b => b.Segment.Kind != kind));
                            if (kind != SegmentKind.Engine) bases.Add(newView);
                            published = new Snapshot(bases.ToArray(), delta, cur.Version + 1);
                            foreach (var r in retired) r.Retire(); // before publish: the last release deletes the file
                            Publish(published);
                        }
                        seg.Release();
                        SaveManifest(published);
                        L.Info("index.base_replaced", "Base segment replaced", ("kind", kind.ToString()), ("file", seg.FileName),
                            ("files", seg.FileCount), ("symbols", seg.SymbolCount), ("ms", scope.ElapsedMs));
                    }
                    catch (OperationCanceledException)
                    {
                        L.Info("index.replace_base.cancelled", "Base segment build cancelled", ("kind", kind.ToString()));
                        throw;
                    }
                    catch (Exception ex)
                    {
                        scope.Fail();
                        L.Error("index.replace_base.failed", "Base segment build failed", ex, ("kind", kind.ToString()));
                        throw;
                    }
                }
            }
        }

        private Segment WriteAndOpen(SegmentKind kind, IEnumerable<FileSymbols> files, CancellationToken ct)
        {
            int number = Interlocked.Increment(ref _nextSegmentNumber) - 1;
            string name = kind == SegmentKind.Engine
                ? $"engine-{Sanitize(_options.EngineBuildId)}-{number:D4}.seg"
                : $"project-{number:D4}.seg";
            string path = Path.Combine(_options.Directory, name);
            try
            {
                SegmentWriter.Write(path, kind, _options.KnowledgeBaseVersion, _options.EngineBuildId, files, ct);
            }
            catch
            {
                TryDelete(path + ".tmp");
                throw;
            }
            var status = Segment.TryOpen(path, _options.KnowledgeBaseVersion, _options.EngineBuildId, verifyChecksums: false, out var seg, out string detail);
            if (status != SegmentLoadStatus.Ok)
            {
                TryDelete(path);
                throw new IOException($"Freshly written segment {name} failed to open: {status} {detail}");
            }
            return seg;
        }

        private static string Sanitize(string s)
        {
            var sb = new StringBuilder();
            foreach (char c in s ?? "unknown")
                sb.Append(char.IsLetterOrDigit(c) || c == '.' || c == '_' || c == '-' ? c : '_');
            if (sb.Length > 40) sb.Length = 40;
            return sb.Length == 0 ? "unknown" : sb.ToString();
        }

        // ------------------------------------------------------------------ incremental updates

        /// <summary>Replaces (or adds) one file. O(symbols of that file).</summary>
        public void Update(FileSymbols file)
        {
            if (file == null) throw new ArgumentNullException(nameof(file));
            if (string.IsNullOrEmpty(file.Path)) throw new ArgumentException("FileSymbols.Path is required", nameof(file));
            ThrowIfDisposed();
            using (L.Time("index.update", 100, ("path", file.Path)))
            {
                var df = new DeltaFile(file);
                lock (_writeLock)
                {
                    var cur = _current;
                    var bases = Tombstone(cur.Bases, df.Key);
                    Publish(new Snapshot(bases, cur.Delta.SetItem(df.Key, df), cur.Version + 1));
                }
            }
        }

        /// <summary>Removes a file from the index. Returns false when it was not indexed.</summary>
        public bool Remove(string path)
        {
            ThrowIfDisposed();
            string key = PathKey.Normalize(path);
            using (L.Time("index.remove", 100, ("path", path)))
            {
                lock (_writeLock)
                {
                    var cur = _current;
                    var bases = Tombstone(cur.Bases, key);
                    bool found = !ReferenceEquals(bases, cur.Bases) || cur.Delta.ContainsKey(key);
                    if (!found) return false;
                    Publish(new Snapshot(bases, cur.Delta.Remove(key), cur.Version + 1));
                    return true;
                }
            }
        }

        /// <summary>Returns <paramref name="bases"/> itself when nothing changed.</summary>
        private static SegmentView[] Tombstone(SegmentView[] bases, string key)
        {
            SegmentView[] result = bases;
            for (int i = 0; i < bases.Length; i++)
            {
                int fi = bases[i].Segment.FindFile(key);
                if (fi < 0 || bases[i].IsDead((uint)fi)) continue;
                if (ReferenceEquals(result, bases)) result = (SegmentView[])bases.Clone();
                result[i] = bases[i].WithTombstone(fi);
            }
            return result;
        }

        /// <summary>True when the delta is past the configured thresholds and <see cref="Compact"/> should run.</summary>
        public bool NeedsCompaction
        {
            get
            {
                var s = Volatile.Read(ref _current);
                return s.Delta.Count >= _options.CompactionFileThreshold || s.DeltaBytes >= _options.CompactionByteThreshold;
            }
        }

        // ------------------------------------------------------------------ compaction

        /// <summary>
        /// Merges the delta and all project segments into one new project segment and swaps it in atomically.
        /// Updates made while compaction runs are preserved (they stay in the delta and tombstone the new segment).
        /// Returns false when there was nothing to do. Run on a background thread.
        /// </summary>
        public bool Compact(CancellationToken ct = default)
        {
            ThrowIfDisposed();
            lock (_maintenanceLock)
            {
                using (var scope = L.Time("index.compact", double.MaxValue))
                {
                    Snapshot s0 = AcquireInner();
                    try
                    {
                        var oldProject = s0.Bases.Where(b => b.Segment.Kind == SegmentKind.Project).ToArray();
                        bool projectDirty = oldProject.Length > 1 || oldProject.Any(v => v.TombstoneCount > 0);
                        if (s0.Delta.Count == 0 && !projectDirty) return false;

                        // Delta entries that override engine files are moved into the project segment too; the engine
                        // tombstone stays (and is persisted) so the new copy wins.
                        var input = oldProject.SelectMany(v => SegmentExport.LiveFiles(v, ct)).Concat(s0.DeltaSorted.Select(d => d.Source));
                        var seg = WriteAndOpen(SegmentKind.Project, input, ct);
                        Snapshot published;
                        lock (_writeLock)
                        {
                            var s1 = _current;
                            // Which of the new segment's files are still current in s1?
                            var view = new SegmentView(seg, null, 0);
                            var files = seg.Files;
                            for (int fi = 0; fi < files.Length; fi++)
                            {
                                string key = PathKey.Normalize(seg.GetString(files[fi].PathId));
                                bool live;
                                if (s0.Delta.TryGetValue(key, out var e0))
                                {
                                    live = s1.Delta.TryGetValue(key, out var e1) && ReferenceEquals(e0, e1);
                                }
                                else
                                {
                                    live = !s1.Delta.ContainsKey(key);
                                    foreach (var old in s1.Bases)
                                    {
                                        if (old.Segment.Kind != SegmentKind.Project) continue;
                                        int ofi = old.Segment.FindFile(key);
                                        if (ofi >= 0 && old.IsDead((uint)ofi)) live = false;
                                    }
                                }
                                if (!live) view = view.WithTombstone(fi);
                            }
                            var delta = s1.Delta;
                            foreach (var kv in s0.Delta)
                                if (s1.Delta.TryGetValue(kv.Key, out var e1) && ReferenceEquals(e1, kv.Value)) delta = delta.Remove(kv.Key);
                            var bases = s1.Bases.Where(b => b.Segment.Kind != SegmentKind.Project).Append(view).ToArray();
                            published = new Snapshot(bases, delta, s1.Version + 1);
                            foreach (var old in oldProject) old.Segment.Retire(); // before publish: the last release deletes the file
                            Publish(published);
                        }
                        seg.Release();
                        SaveManifest(published);
                        L.Info("index.compacted", "Delta compacted into a new project segment", ("file", seg.FileName),
                            ("deltaFiles", s0.Delta.Count), ("files", seg.FileCount), ("symbols", seg.SymbolCount),
                            ("bytes", seg.Length), ("ms", scope.ElapsedMs));
                        return true;
                    }
                    catch (OperationCanceledException)
                    {
                        L.Info("index.compact.cancelled", "Compaction cancelled");
                        throw;
                    }
                    catch (Exception ex)
                    {
                        scope.Fail();
                        L.Error("index.compact.failed", "Compaction failed; the delta is kept", ex);
                        throw;
                    }
                    finally
                    {
                        s0.Release();
                    }
                }
            }
        }

        // ------------------------------------------------------------------ manifest

        private void SaveManifest(Snapshot snap)
        {
            var m = new Manifest
            {
                KnowledgeBaseVersion = _options.KnowledgeBaseVersion,
                EngineBuildId = _options.EngineBuildId,
                NextSegmentNumber = Volatile.Read(ref _nextSegmentNumber),
            };
            foreach (var view in snap.Bases)
            {
                var entry = new ManifestSegment { File = view.Segment.FileName, Kind = view.Segment.Kind };
                foreach (int fi in view.DeadFiles())
                {
                    string path = view.Segment.GetString(view.Segment.Files[fi].PathId);
                    // tombstones caused by un-persisted delta updates are not persisted: on restart the base copy is
                    // visible again until the caller re-applies the change
                    if (snap.Delta.ContainsKey(PathKey.Normalize(path))) continue;
                    entry.Tombstones.Add(path);
                }
                m.Segments.Add(entry);
            }
            try
            {
                m.Save(_options.Directory);
            }
            catch (Exception ex)
            {
                L.Error("index.manifest.write_failed", "Could not write index manifest", ex, ("dir", _options.Directory));
                throw;
            }
        }

        /// <summary>Persists removals (tombstones) without compacting.</summary>
        public void Flush()
        {
            ThrowIfDisposed();
            lock (_maintenanceLock)
            {
                var s = AcquireInner();
                try
                {
                    SaveManifest(s);
                }
                finally
                {
                    s.Release();
                }
            }
        }

        // ------------------------------------------------------------------ convenience queries (one snapshot per call)

        public SearchResponse Search(SearchRequest request, CancellationToken ct = default)
        {
            using (var s = AcquireSnapshot()) return s.Search(request, ct);
        }

        public IReadOnlyList<SymbolInfo> FindByName(string name, string container = null, CancellationToken ct = default)
        {
            using (var s = AcquireSnapshot()) return s.FindByName(name, container, ct);
        }

        public IReadOnlyList<SymbolInfo> GetFileSymbols(string path)
        {
            using (var s = AcquireSnapshot()) return s.GetFileSymbols(path);
        }

        public IReadOnlyList<TypeEntry> FindTypes(string name, string container = null, CancellationToken ct = default)
        {
            using (var s = AcquireSnapshot()) return s.FindTypes(name, container, ct);
        }

        public IReadOnlyList<TypeEntry> FindDerived(string simpleBaseName, CancellationToken ct = default)
        {
            using (var s = AcquireSnapshot()) return s.FindDerived(simpleBaseName, ct);
        }

        public IndexedFile GetFile(string path)
        {
            using (var s = AcquireSnapshot()) return s.GetFile(path);
        }

        /// <summary>Streams occurrences; the snapshot is held until enumeration ends or is disposed.</summary>
        public IEnumerable<OccurrenceHit> FindOccurrences(string name, CancellationToken ct = default)
        {
            using (var s = AcquireSnapshot())
                foreach (var h in s.FindOccurrences(name, ct)) yield return h;
        }

        public IEnumerable<IndexedFile> ListFiles(CancellationToken ct = default)
        {
            using (var s = AcquireSnapshot())
                foreach (var f in s.ListFiles(ct)) yield return f;
        }

        public IndexStats Stats
        {
            get
            {
                using (var s = AcquireSnapshot()) return s.GetStats();
            }
        }

        // ------------------------------------------------------------------ dispose

        private void ThrowIfDisposed()
        {
            if (_disposed) throw new ObjectDisposedException(nameof(SymbolIndex));
        }

        /// <summary>Releases the index's reference; segments unmap once outstanding snapshots are disposed.</summary>
        public void Dispose()
        {
            lock (_maintenanceLock)
            {
                lock (_writeLock)
                {
                    if (_disposed) return;
                    _disposed = true;
                    _current.Release();
                }
            }
        }
    }
}
