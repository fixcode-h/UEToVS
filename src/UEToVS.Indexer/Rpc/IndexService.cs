using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using UEToVS.Core.Protocol;
using UEToVS.Core.Unreal;
using UEToVS.Indexer.Index;
using UEToVS.Indexer.Text;

namespace UEToVS.Indexer.Rpc
{
    /// <summary>Implements the JSON-RPC contract on top of the index.</summary>
    internal sealed class IndexService : IIndexService
    {
        private readonly IndexStore _store;
        private readonly TextSearcher _text;
        private readonly CancellationTokenSource _shutdown;

        public IndexService(IndexStore store, TextSearcher text, CancellationTokenSource shutdown)
        {
            _store = store;
            _text = text;
            _shutdown = shutdown;
        }

        public Task<IndexStatus> GetStatusAsync(CancellationToken ct)
        {
            var snap = _store.Current;
            return Task.FromResult(new IndexStatus
            {
                FilesReady = _store.FilesReady,
                SymbolsReady = _store.SymbolsReady,
                FileCount = snap.Files.Length,
                SymbolCount = snap.Symbols.Length,
                AssetCount = snap.Assets.Length,
                FilesParsed = _store.FilesParsed,
                FilesToParse = _store.FilesToParse,
                ProjectRoot = _store.Layout.ProjectRoot,
                EngineRoot = _store.Layout.EngineRoot,
            });
        }

        public Task<SearchResponse> SearchAsync(SearchRequest request, CancellationToken ct) =>
            Task.Run(() => Searcher.Search(_store.Current, request, _store.SymbolsReady, ct), ct);

        public Task<List<SymbolDto>> GetFileSymbolsAsync(string filePath, CancellationToken ct)
        {
            var snap = _store.Current;
            var rec = snap.Files.FirstOrDefault(f => string.Equals(f.Path, filePath, StringComparison.OrdinalIgnoreCase));
            if (rec != null && rec.Parseable)
                return Task.FromResult(rec.Symbols.OrderBy(s => s.Line).Select(rec.ToDto).ToList());

            // Not indexed (or not yet): parse on demand.
            return Task.Run(() =>
            {
                if (!File.Exists(filePath)) return new List<SymbolDto>();
                var tmp = new FileRecord { Path = filePath, Name = Path.GetFileName(filePath), Origin = ItemOrigin.Project };
                return IndexStore.ParseFile(filePath).OrderBy(s => s.Line).Select(tmp.ToDto).ToList();
            }, ct);
        }

        public Task<List<SymbolDto>> FindSymbolsAsync(string[] names, string container, CancellationToken ct)
        {
            var snap = _store.Current;
            var result = new List<SymbolDto>();
            foreach (var name in names ?? Array.Empty<string>())
            {
                if (!snap.SymbolsByName.TryGetValue(name, out var idxs)) continue;
                foreach (int i in idxs)
                {
                    var s = snap.Symbols[i];
                    if (container != null && s.Container != null &&
                        !string.Equals(Searcher.LastSegment(s.Container), Searcher.LastSegment(container), StringComparison.Ordinal))
                        continue;
                    result.Add(snap.SymbolFiles[i].ToDto(s));
                }
            }
            return Task.FromResult(result
                .OrderByDescending(s => s.Origin == ItemOrigin.Project || s.Origin == ItemOrigin.ProjectPlugin)
                .ThenBy(s => s.IsDefinition)
                .ToList());
        }

        public Task<List<FileDto>> FindFilesByNameAsync(string[] fileNames, CancellationToken ct)
        {
            var snap = _store.Current;
            var result = new List<FileDto>();
            foreach (var name in fileNames ?? Array.Empty<string>())
                if (snap.FilesByName.TryGetValue(name, out var list))
                    result.AddRange(list.Select(f => f.ToDto()));
            return Task.FromResult(result);
        }

        public Task<List<TextMatch>> SearchTextAsync(TextSearchRequest request, CancellationToken ct) => _text.SearchAsync(request, ct);

        public Task<ModuleInfo> FindModuleForIncludeAsync(string includePath, CancellationToken ct)
        {
            var snap = _store.Current;
            string fileName = Path.GetFileName(includePath.Replace('/', Path.DirectorySeparatorChar));
            if (!snap.FilesByName.TryGetValue(fileName, out var candidates)) return Task.FromResult<ModuleInfo>(null);
            var match = candidates
                .Where(f => IncludeDirective.Matches(f.Path, includePath) && f.Module != null)
                .OrderByDescending(f => f.IsProject)
                .ThenByDescending(f => f.Path.Contains(Path.DirectorySeparatorChar + "Public" + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
                                    || f.Path.Contains(Path.DirectorySeparatorChar + "Classes" + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                .FirstOrDefault();
            return Task.FromResult(match == null ? null : ToModule(snap, match.Module, match.Origin));
        }

        public Task<ModuleInfo> GetModuleForFileAsync(string filePath, CancellationToken ct)
        {
            var snap = _store.Current;
            var rec = snap.Files.FirstOrDefault(f => string.Equals(f.Path, filePath, StringComparison.OrdinalIgnoreCase));
            string module = rec?.Module ?? ModuleLocator.ForFileSystem().FindModuleName(filePath);
            return Task.FromResult(module == null ? null : ToModule(snap, module, rec?.Origin ?? ItemOrigin.Project));
        }

        private static ModuleInfo ToModule(Snapshot snap, string module, ItemOrigin origin)
        {
            snap.BuildCsByModule.TryGetValue(module, out var path);
            if (snap.ModuleOrigin.TryGetValue(module, out var o)) origin = o;
            return new ModuleInfo { Name = module, BuildCsPath = path, Origin = origin };
        }

        public Task NotifyFileChangedAsync(string filePath, CancellationToken ct)
        {
            _store.NotifyChanged(filePath);
            return Task.CompletedTask;
        }

        public Task ShutdownAsync()
        {
            _shutdown.Cancel();
            return Task.CompletedTask;
        }
    }
}
