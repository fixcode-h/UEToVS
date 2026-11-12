using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using UEToVS.Contracts;
using UEToVS.Host.Engine;

namespace UEToVS.Host.Search
{
    /// <summary>
    /// Engine lookups used by the M1 features, on top of <see cref="EngineClient.InvokeAsync{T}"/> (timeout, cancellation,
    /// logging, latency budget). The "Try" variants degrade to empty results when the engine is unavailable.
    /// </summary>
    internal static class EngineQueries
    {
        /// <summary>Search Everywhere query (budget 100 ms). Throws when the engine is unavailable (caller shows status).</summary>
        public static Task<SearchResponse> SearchAsync(EngineClient engine, SearchRequest request, CancellationToken ct)
        {
            if (engine == null) throw new EngineUnavailableException("Engine not started");
            return engine.InvokeAsync("search", (s, c) => s.SearchAsync(request, c), ct, TimeSpan.FromSeconds(10), 100);
        }

        /// <summary>Indexed files whose name equals <paramref name="fileName"/> (any origin), absolute paths.</summary>
        public static async Task<IReadOnlyList<string>> FindFilesByNameAsync(EngineClient engine, string fileName, CancellationToken ct)
        {
            if (engine == null || string.IsNullOrEmpty(fileName)) return Array.Empty<string>();
            var req = new SearchRequest
            {
                Pattern = fileName,
                IncludeTypes = false,
                IncludeSymbols = false,
                IncludeFiles = true,
                IncludeAssets = false,
                IncludeNonProject = true,
                MaxResultsPerCategory = 200,
            };
            var (ok, resp) = await engine.TryInvokeAsync("search.fileByName", (s, c) => s.SearchAsync(req, c), ct, TimeSpan.FromSeconds(10), 200).ConfigureAwait(false);
            if (!ok || resp == null) return Array.Empty<string>();
            return resp.Hits.Where(h => h.File != null && string.Equals(h.File.Name, fileName, StringComparison.OrdinalIgnoreCase))
                       .Select(h => h.File.Path).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        }

        public static async Task<List<SymbolInfo>> FindSymbolsAsync(EngineClient engine, string name, string container, CancellationToken ct)
        {
            if (engine == null || string.IsNullOrEmpty(name)) return new List<SymbolInfo>();
            var (ok, list) = await engine.TryInvokeAsync("findSymbols", (s, c) => s.FindSymbolsAsync(name, container, c), ct, null, 50).ConfigureAwait(false);
            return ok && list != null ? list : new List<SymbolInfo>();
        }

        public static async Task<List<SymbolInfo>> GetFileSymbolsAsync(EngineClient engine, string path, CancellationToken ct)
        {
            if (engine == null || string.IsNullOrEmpty(path)) return null;
            var (ok, list) = await engine.TryInvokeAsync("fileSymbols", (s, c) => s.GetFileSymbolsAsync(path, c), ct, null, 100).ConfigureAwait(false);
            return ok ? list ?? new List<SymbolInfo>() : null;
        }
    }
}
