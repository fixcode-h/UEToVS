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
    public sealed class SegmentedIndexBackend : ISymbolIndexBackend
    {
        private static readonly Logger L = Log.For("Index");
        private readonly object _gate = new object();
        private SymbolIndex _index;
        private string _key;
        private int _compacting;

        public string Name => "segmented";

        public string FormatVersion => "1";

        private SymbolIndex Current => Volatile.Read(ref _index);

        public bool TryLoad(string indexDir, string key)
        {
            lock (_gate)
            {
                if (_index != null && _key == key) return true;
                _index?.Dispose();
                var options = new IndexOptions { Directory = Path.Combine(indexDir, "symbols"), KnowledgeBaseVersion = key, EngineBuildId = "keyed" };
                var idx = SymbolIndex.Open(options, out var result);
                _key = key;
                Volatile.Write(ref _index, idx);
                bool ok = !result.ManifestMissing && !result.NeedsEngineRebuild && !result.NeedsProjectRebuild;
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
                fs.Path = filePath;
                Apply(fs);
            }
            else UpdateFile(filePath, result.Symbols);
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
