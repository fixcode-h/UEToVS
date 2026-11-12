using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using UEToVS.Contracts;
using UEToVS.Engine.Index;
using UEToVS.Engine.Indexing;
using UEToVS.Engine.IntelliSense;
using UEToVS.Engine.Unreal;

namespace UEToVS.Mcp.Data
{
    /// <summary>
    /// get_diagnostics without a running engine: the engine's own <see cref="IntelliSenseService"/> / DiagnosticsProvider run
    /// in-process over the on-disk index (a read-only <see cref="SnapshotBackend"/>). Same rules and the same certainty
    /// thresholds as the squiggles in VS; gameplay tag / asset path rules give up (no tag or asset tables offline) rather than
    /// guess. One service per call: the member tables it builds are released with it (no resident memory).
    /// </summary>
    public static class OfflineDiagnostics
    {
        /// <summary>Runs every rule on the whole of each file. Engine files and files missing from the index are reported as skipped.</summary>
        public static List<(string Path, string Skipped, CodeDiagnosticsResponse Result)> Run(IndexSnapshot snapshot, IReadOnlyList<string> paths, CancellationToken ct)
        {
            var results = new List<(string, string, CodeDiagnosticsResponse)>();
            var files = new FileTable();
            var backend = new SnapshotBackend(snapshot);
            var service = new IntelliSenseService(backend, files) { DiagnosticsBudgetMs = 2000 };
            foreach (var path in paths)
            {
                ct.ThrowIfCancellationRequested();
                var indexed = snapshot.GetFile(path);
                var origin = indexed?.Origin ?? ItemOrigin.Project;
                if (origin != ItemOrigin.Project && origin != ItemOrigin.ProjectPlugin)
                {
                    results.Add((path, "engine-file", null));
                    continue;
                }
                files.Upsert(new ListedFile { Path = path, Name = Path.GetFileName(path), Module = indexed?.Module, Origin = origin, Kind = KindOf(path) });
                var resp = service.GetDiagnostics(new CodeDiagnosticsRequest
                {
                    Document = new CaretRequest { FilePath = path },
                    StartLine = 0,
                    EndLine = int.MaxValue,
                }, ct);
                results.Add((path, indexed == null ? "not-indexed" : null, resp));
            }
            return results;
        }

        private static SourceFileKind KindOf(string path)
        {
            string ext = Path.GetExtension(path).ToLowerInvariant();
            return ext == ".h" || ext == ".hpp" || ext == ".hxx" || ext == ".inl" ? SourceFileKind.Header : SourceFileKind.Source;
        }
    }

    /// <summary>Read-only <see cref="ISymbolIndexBackend"/> over an opened index snapshot (the snapshot's owner keeps it mapped).</summary>
    public sealed class SnapshotBackend : ISymbolIndexBackend
    {
        private readonly IndexSnapshot _s;

        public SnapshotBackend(IndexSnapshot snapshot) => _s = snapshot ?? throw new ArgumentNullException(nameof(snapshot));

        public string Name => "snapshot";

        public string FormatVersion => "2";

        public SymbolSearchResult Search(SymbolQuery query, CancellationToken ct)
        {
            var result = new SymbolSearchResult();
            var resp = _s.Search(new SearchRequest
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
                if (h.Symbol != null) result.Matches.Add(new SymbolMatch { Symbol = h.Symbol, Category = h.Category, Score = h.Score, Highlights = h.Highlights });
            result.NonProjectMatchCount = resp.NonProjectHitCount;
            return result;
        }

        public IReadOnlyList<SymbolInfo> GetFileSymbols(string filePath) => _s.GetFileSymbols(filePath);

        public IReadOnlyList<SymbolInfo> FindSymbols(string name, string container) => _s.FindByName(name, container);

        public IReadOnlyList<TypeEntry> FindTypes(string name, string container) => _s.FindTypes(name, container);

        public IReadOnlyList<TypeEntry> FindDerivedTypes(string simpleBaseName) => _s.FindDerived(simpleBaseName);

        public IEnumerable<string> FindOccurrenceFiles(string name, CancellationToken ct)
        {
            string last = null;
            foreach (var h in _s.FindOccurrences(name, ct))
            {
                if (string.Equals(h.Path, last, StringComparison.Ordinal)) continue;
                last = h.Path;
                yield return h.Path;
            }
        }

        public void UpdateFile(string filePath, IReadOnlyList<SymbolInfo> symbols) => throw new NotSupportedException("read-only index");

        public void RemoveFile(string filePath) => throw new NotSupportedException("read-only index");

        public bool ContainsFile(string filePath) => _s.GetFile(filePath) != null;

        public UEToVS.Engine.Indexing.IndexStats GetStats() => new UEToVS.Engine.Indexing.IndexStats();

        public IReadOnlyList<MemoryPart> GetMemoryParts() => Array.Empty<MemoryPart>();

        public bool TryLoad(string indexDir, string key) => true;

        public void Save(string indexDir, string key)
        {
        }

        public void Dispose()
        {
        }
    }
}
