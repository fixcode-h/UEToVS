using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.IO;
using System.Linq;
using UEToVS.Engine.Index.Segments;
using UEToVS.Engine.Index.Storage;

namespace UEToVS.Engine.Index
{
    /// <summary>What <see cref="ReadOnlyIndex.TryOpen"/> found on disk.</summary>
    public sealed class ReadOnlyIndexInfo
    {
        public string Directory { get; internal set; }
        public bool ManifestFound { get; internal set; }
        public string KnowledgeBaseVersion { get; internal set; }
        public DateTime ManifestWrittenUtc { get; internal set; }
        public int SegmentsOpened { get; internal set; }
        public List<string> Rejected { get; } = new List<string>();
    }

    /// <summary>
    /// Opens the persisted segments of an index directory for reading by another process (UEToVS.Mcp when the engine
    /// is not running, or to read occurrences next to a running engine). Unlike <see cref="SymbolIndex.Open"/> it never
    /// writes: no manifest rewrite, no deleting of rejected or stray segments, no knowledge-base check (the engine
    /// that wrote the manifest decided the segments were current). Segments are immutable files opened with
    /// FileShare.Delete, so a running engine may compact and retire them while a snapshot is held.
    /// The in-memory delta of the engine (files changed since the last compaction) is not visible here.
    /// </summary>
    public static class ReadOnlyIndex
    {
        /// <summary>Returns a snapshot (dispose it) or null when there is no usable index in <paramref name="symbolsDir"/>.</summary>
        public static IndexSnapshot TryOpen(string symbolsDir, out ReadOnlyIndexInfo info, bool verifyChecksums = false)
        {
            info = new ReadOnlyIndexInfo { Directory = symbolsDir };
            if (string.IsNullOrEmpty(symbolsDir) || !System.IO.Directory.Exists(symbolsDir)) return null;
            var manifest = Manifest.Load(symbolsDir, out _);
            if (manifest == null) return null;
            info.ManifestFound = true;
            info.KnowledgeBaseVersion = manifest.KnowledgeBaseVersion;
            info.ManifestWrittenUtc = manifest.WrittenUtc;

            var views = new List<SegmentView>();
            foreach (var entry in manifest.Segments)
            {
                string path = Path.Combine(symbolsDir, entry.File ?? string.Empty);
                var status = Segment.TryOpen(path, null, null, verifyChecksums, out var seg, out string detail);
                if (status != SegmentLoadStatus.Ok)
                {
                    info.Rejected.Add($"{entry.File}: {status} {detail}");
                    continue;
                }
                var view = new SegmentView(seg, null, 0);
                foreach (var t in entry.Tombstones ?? new List<string>()) view = view.WithTombstone(seg.FindFile(PathKey.Normalize(t)));
                views.Add(view);
            }
            if (views.Count == 0)
            {
                return null;
            }
            var ordered = views.Where(v => v.Segment.Kind == SegmentKind.Engine).Concat(views.Where(v => v.Segment.Kind != SegmentKind.Engine)).ToArray();
            var snap = new Snapshot(ordered, ImmutableDictionary.Create<string, DeltaFile>(StringComparer.Ordinal), 1);
            foreach (var v in ordered) v.Segment.Release(); // the snapshot holds its own references
            info.SegmentsOpened = ordered.Length;
            return new IndexSnapshot(snap);
        }

        /// <summary>Path of the manifest whose timestamp tells readers that segments changed.</summary>
        public static string ManifestPath(string symbolsDir) => Path.Combine(symbolsDir, Manifest.FileName);
    }
}
