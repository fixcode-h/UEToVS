using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using UEToVS.Contracts;
using UEToVS.Core.Diagnostics;
using UEToVS.Engine.Index;

namespace UEToVS.Engine.Indexing
{
    /// <summary>
    /// <see cref="ISymbolIndexBackend"/> over the segmented, memory-mapped <see cref="SymbolIndex"/> stored in
    /// &lt;solution&gt;/.vs/UEToVS/Index/symbols. The cache key passed to <see cref="TryLoad"/> becomes the segments'
    /// knowledge-base version, so a parser/format change automatically invalidates them. Compaction runs in the
    /// background whenever the delta grows past its threshold; <see cref="Save"/> compacts so nothing is lost on exit.
    /// </summary>
    public sealed class SegmentedIndexBackend : ISymbolIndexBackend, IParallelBulkBackend, IDeprecationIndex
    {
        private static readonly Logger L = Log.For("Index");
        private readonly object _gate = new object();
        private SymbolIndex _index;
        private string _key;
        private int _compacting;

        public string Name => "segmented";

        public string FormatVersion => "3"; // 2: inheritance sections (segment format 2); 3: deprecation markers (segment format 3, v0.4.5)

        private SymbolIndex Current => Volatile.Read(ref _index);

        /// <summary>The open store (null before <see cref="TryLoad"/>); used by the shared engine cache.</summary>
        public SymbolIndex Store => Current;

        /// <summary>Bulk stages leave at most this many base segments per kind; more are merged at the end of the stage.</summary>
        public int MaxBulkSegments { get; set; } = 64;

        /// <summary>Segments merged together per step when there are more than <see cref="MaxBulkSegments"/>.</summary>
        public int MergeFanIn { get; set; } = 6;

        public bool TryLoad(string indexDir, string key)
        {
            lock (_gate)
            {
                if (_index != null && _key == key) return true;
                _index?.Dispose();
                // Project segments + manifest in Index/project, engine segments in Index/engine (or, when shared, in the
                // shared engine cache, referenced by absolute path). They are written and updated separately.
                var options = new IndexOptions
                {
                    Directory = Path.Combine(indexDir, "project"),
                    EngineDirectory = Path.Combine(indexDir, "engine"),
                    KnowledgeBaseVersion = key,
                    EngineBuildId = "keyed",
                };
                DeleteLegacy(Path.Combine(indexDir, "symbols"));
                var idx = SymbolIndex.Open(options, out var result);
                _key = key;
                Volatile.Write(ref _index, idx);
                // Usable as soon as a manifest exists: files whose segment was rejected (or that were never indexed) are
                // simply not found by ContainsFile and get re-parsed. (Requiring both an engine and a project segment
                // made every restart a full re-index.)
                bool ok = !result.ManifestMissing;
                L.Info("index.store.open", null, ("dir", options.Directory), ("loaded", ok), ("manifestMissing", result.ManifestMissing),
                    ("segments", result.Segments.Select(s => s.ToString()).ToArray()));
                return ok;
            }
        }

        public void Save(string indexDir, string key)
        {
            var idx = Current;
            if (idx == null) return;
            if (idx.Stats.DeltaFileCount > 0) idx.Compact();
            idx.Flush();
        }

        public int PersistDelta(CancellationToken ct) => Current?.PersistDelta(ct) ?? 0;

        public SymbolSearchResult Search(SymbolQuery query, CancellationToken ct)
        {
            var result = new SymbolSearchResult();
            var idx = Current;
            if (idx == null) return result;
            var resp = idx.Search(new SearchRequest
            {
                Pattern = query.Pattern,
                Container = query.Container,
                IncludeTypes = query.IncludeTypes,
                IncludeSymbols = query.IncludeSymbols,
                IncludeFiles = false,
                IncludeAssets = false,
                IncludeNonProject = query.IncludeNonProject,
                RequireUe = query.RequireUe,
                MaxResultsPerCategory = query.MaxResults,
            }, ct);
            foreach (var h in resp.Hits)
            {
                if (h.Symbol == null) continue;
                result.Matches.Add(new SymbolMatch { Symbol = h.Symbol, Category = h.Category, Score = h.Score, Highlights = h.Highlights });
            }
            result.NonProjectMatchCount = resp.NonProjectHitCount;
            return result;
        }

        public IReadOnlyList<SymbolInfo> GetFileSymbols(string filePath) => Current?.GetFileSymbols(filePath) ?? Array.Empty<SymbolInfo>();

        public IReadOnlyList<SymbolInfo> FindSymbols(string name, string container) =>
            Current?.FindByName(name, container) ?? Array.Empty<SymbolInfo>();

        public IReadOnlyList<TypeEntry> FindTypes(string name, string container) =>
            Current?.FindTypes(name, container) ?? (IReadOnlyList<TypeEntry>)Array.Empty<TypeEntry>();

        public IReadOnlyList<TypeEntry> FindDerivedTypes(string simpleBaseName) =>
            Current?.FindDerived(simpleBaseName) ?? (IReadOnlyList<TypeEntry>)Array.Empty<TypeEntry>();

        // ---- v0.4.5 heavy features: deprecation markers (upgrade assistant) ----

        public IReadOnlyList<DeprecatedSymbol> ListDeprecated(CancellationToken ct) => Current?.ListDeprecated(ct) ?? (IReadOnlyList<DeprecatedSymbol>)Array.Empty<DeprecatedSymbol>();

        public IReadOnlyList<OccurrenceHit> FindProjectOccurrences(string name, bool includePlugins, CancellationToken ct) =>
            Current?.FindProjectOccurrences(name, includePlugins, ct) ?? (IReadOnlyList<OccurrenceHit>)Array.Empty<OccurrenceHit>();

        public IEnumerable<string> FindOccurrenceFiles(string name, CancellationToken ct)
        {
            var idx = Current;
            if (idx == null) yield break;
            string last = null;
            foreach (var h in idx.FindOccurrences(name, ct))
            {
                if (ReferenceEquals(h.Path, last) || string.Equals(h.Path, last, StringComparison.Ordinal)) continue;
                last = h.Path;
                yield return h.Path;
            }
        }

        public void UpdateFile(string filePath, IReadOnlyList<SymbolInfo> symbols)
        {
            // Fallback path (no parser payload): declarations without offsets or occurrences.
            var first = symbols.Count > 0 ? symbols[0] : null;
            Apply(new FileSymbols
            {
                Path = filePath,
                Module = first?.Module,
                Origin = first?.Origin ?? ItemOrigin.Project,
                Decls = symbols.Select(s => new DeclInput
                {
                    Name = s.Name,
                    Container = s.Container,
                    Kind = s.Kind,
                    Ue = s.Ue,
                    Specifiers = s.Specifiers,
                    Signature = s.Signature,
                    Line = s.Location?.Line ?? 0,
                    Column = s.Location?.Column ?? 0,
                    IsDefinition = s.IsDefinition,
                }).ToArray(),
            });
        }

        public void UpdateParsed(string filePath, ParseResult result)
        {
            if (result.Payload is FileSymbols fs)
            {
                Apply(Fill(fs, filePath, result));
            }
            else UpdateFile(filePath, result.Symbols);
        }

        private static FileSymbols Fill(FileSymbols fs, string filePath, ParseResult result)
        {
            fs.Path = filePath;
            fs.MtimeTicks = result.MtimeTicks;
            fs.Length = result.Length;
            fs.ContentHash = result.ContentHash;
            return fs;
        }

        public void AppendBatch(bool engine, IReadOnlyList<(string Path, ParseResult Result)> batch, CancellationToken ct)
        {
            var idx = Current ?? throw new InvalidOperationException("Index store not opened (TryLoad was not called)");
            var list = new List<FileSymbols>(batch.Count);
            foreach (var (path, result) in batch)
            {
                if (result.Payload is FileSymbols fs) list.Add(Fill(fs, path, result));
                else UpdateFile(path, result.Symbols); // foreign payload: delta path
            }
            idx.AppendSegment(engine ? SegmentKind.Engine : SegmentKind.Project, list, ct);
        }

        public IDisposable PrepareBatch(bool engine, IReadOnlyList<(string Path, ParseResult Result)> batch, CancellationToken ct)
        {
            var idx = Current ?? throw new InvalidOperationException("Index store not opened (TryLoad was not called)");
            var list = new List<FileSymbols>(batch.Count);
            foreach (var (path, result) in batch)
            {
                if (result.Payload is FileSymbols fs) list.Add(Fill(fs, path, result));
                else UpdateFile(path, result.Symbols); // foreign payload: delta path (the delta always wins over bases)
            }
            return idx.PrepareSegment(engine ? SegmentKind.Engine : SegmentKind.Project, list, ct);
        }

        public void PublishBatch(IDisposable prepared)
        {
            if (prepared == null) return;
            if (!(prepared is PreparedSegment seg)) throw new ArgumentException("Not a prepared segment of this backend", nameof(prepared));
            var idx = Current ?? throw new InvalidOperationException("Index store not opened (TryLoad was not called)");
            idx.PublishSegment(seg);
        }

        public void FinishBulk(bool engine, CancellationToken ct)
        {
            var idx = Current;
            if (idx == null) return;
            var kind = engine ? SegmentKind.Engine : SegmentKind.Project;
            // Tiered: merge the smallest few at a time so one merge never materialises the whole engine in memory.
            int guard = 0;
            while (idx.BaseSegmentCount(kind) > MaxBulkSegments && guard++ < 64)
                if (!idx.MergeBases(kind, MergeFanIn, ct)) break;
        }

        public void Reset(bool project, bool engine)
        {
            var kinds = new List<SegmentKind>();
            if (project) kinds.Add(SegmentKind.Project);
            if (engine) kinds.Add(SegmentKind.Engine);
            if (kinds.Count > 0) Current?.Reset(kinds.ToArray());
        }

        /// <summary>v0.2 stored everything in Index/symbols; that layout is never read again.</summary>
        private static void DeleteLegacy(string dir)
        {
            try
            {
                if (Directory.Exists(dir)) Directory.Delete(dir, true);
            }
            catch (Exception ex)
            {
                L.Debug("index.legacy.delete_failed", ex.Message, ("dir", dir));
            }
        }

        private void Apply(FileSymbols fs)
        {
            var idx = Current ?? throw new InvalidOperationException("Index store not opened (TryLoad was not called)");
            idx.Update(fs);
            if (idx.NeedsCompaction && Interlocked.CompareExchange(ref _compacting, 1, 0) == 0)
            {
                Task.Run(() =>
                {
                    try
                    {
                        using (L.Time("index.compact.auto")) idx.Compact();
                    }
                    catch (Exception ex)
                    {
                        L.Error("index.compact.failed", "Background compaction failed", ex);
                    }
                    finally
                    {
                        Volatile.Write(ref _compacting, 0);
                    }
                });
            }
        }

        public void RemoveFile(string filePath) => Current?.Remove(filePath);

        public bool ContainsFile(string filePath) => Current?.GetFile(filePath) != null;

        public IndexStats GetStats()
        {
            var s = Current?.Stats;
            if (s == null) return new IndexStats();
            return new IndexStats
            {
                Files = s.FileCount,
                Symbols = s.SymbolCount,
                ManagedBytes = s.DeltaBytes,
                MappedBytes = s.BytesOnDisk,
                DiskBytes = s.BytesOnDisk,
            };
        }

        public IReadOnlyList<MemoryPart> GetMemoryParts()
        {
            var s = Current?.Stats;
            if (s == null) return Array.Empty<MemoryPart>();
            return new[]
            {
                new MemoryPart { Name = "segments", MappedBytes = s.BytesOnDisk, DiskBytes = s.BytesOnDisk },
                new MemoryPart { Name = "delta", ManagedBytes = s.DeltaBytes },
            };
        }

        public void Dispose()
        {
            lock (_gate)
            {
                _index?.Dispose();
                _index = null;
            }
        }
    }
}
