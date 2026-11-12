using System;
using System.Linq;
using System.Threading;
using UEToVS.Engine.Index.Segments;
using UEToVS.Engine.Index.Storage;

namespace UEToVS.Engine.Index
{
    /// <summary>Quick persistence of the in-memory delta (engine shutdown), without rewriting the project segment.</summary>
    public sealed partial class SymbolIndex
    {
        /// <summary>
        /// Writes the delta (files changed since the last compaction) as one additional project segment and drops it from
        /// memory; the base copies it replaced stay tombstoned and the tombstones are persisted with the manifest. Cost is
        /// O(delta), unlike <see cref="Compact"/> which rewrites every project file. Readers of the on-disk index (the MCP
        /// server) see the result as soon as manifest.json is written. The next <see cref="Compact"/> merges the extra
        /// segment back. Returns the number of files written (0 = nothing to do). Honours <paramref name="ct"/> while writing;
        /// a cancelled write leaves the index unchanged.
        /// </summary>
        public int PersistDelta(CancellationToken ct = default)
        {
            ThrowIfDisposed();
            lock (_maintenanceLock)
            {
                using (var scope = L.Time("index.delta.persist", double.MaxValue))
                {
                    Snapshot s0 = AcquireInner();
                    try
                    {
                        if (s0.Delta.Count == 0) return 0;
                        var seg = WriteAndOpen(SegmentKind.Project, s0.DeltaSorted.Select(d => d.Source).ToList(), ct);
                        Snapshot published;
                        int written = seg.FileCount;
                        lock (_writeLock)
                        {
                            var s1 = _current;
                            var view = new SegmentView(seg, null, 0);
                            var files = seg.Files;
                            for (int fi = 0; fi < files.Length; fi++)
                            {
                                // changed again (or removed) while writing: the newer state wins
                                string key = PathKey.Normalize(seg.GetString(files[fi].PathId));
                                bool live = s0.Delta.TryGetValue(key, out var e0) && s1.Delta.TryGetValue(key, out var e1) && ReferenceEquals(e0, e1);
                                if (!live) view = view.WithTombstone(fi);
                            }
                            var delta = s1.Delta;
                            foreach (var kv in s0.Delta)
                                if (s1.Delta.TryGetValue(kv.Key, out var e1) && ReferenceEquals(e1, kv.Value)) delta = delta.Remove(kv.Key);
                            published = new Snapshot(s1.Bases.Append(view).ToArray(), delta, s1.Version + 1);
                            Publish(published);
                        }
                        seg.Release();
                        SaveManifest(published);
                        L.Info("index.delta.persisted", "Delta written as an extra project segment", ("file", seg.FileName), ("files", written),
                            ("symbols", seg.SymbolCount), ("bytes", seg.Length), ("ms", Math.Round(scope.ElapsedMs, 1)));
                        return written;
                    }
                    catch (OperationCanceledException)
                    {
                        L.Info("index.delta.persist.cancelled", "Writing the delta was cancelled; it stays in memory");
                        throw;
                    }
                    catch (Exception ex)
                    {
                        scope.Fail();
                        L.Error("index.delta.persist.failed", "Writing the delta failed; it stays in memory", ex);
                        throw;
                    }
                    finally
                    {
                        s0.Release();
                    }
                }
            }
        }
    }
}
