using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using UEToVS.Contracts;
using UEToVS.Core.Matching;
using UEToVS.Engine.Indexing;

namespace UEToVS.Engine.Search
{
    /// <summary>Search Everywhere: types and symbols from the backend, files and assets from the engine's tables.</summary>
    public sealed class SearchService
    {
        private readonly IndexOrchestrator _index;

        public SearchService(IndexOrchestrator index) => _index = index;

        public SearchResponse Search(SearchRequest req, CancellationToken ct)
        {
            var sw = Stopwatch.StartNew();
            var resp = new SearchResponse { IndexReady = _index.Phase == IndexPhase.Ready };
            if (req == null) return resp;
            int max = req.MaxResultsPerCategory > 0 ? req.MaxResultsPerCategory : 100;
            var matcher = new FuzzyMatcher(req.Pattern);
            if (matcher.IsEmpty)
            {
                resp.ElapsedMs = sw.Elapsed.TotalMilliseconds;
                return resp;
            }

            if (req.IncludeTypes || req.IncludeSymbols)
            {
                var r = _index.Backend.Search(new SymbolQuery
                {
                    Pattern = req.Pattern,
                    Container = req.Container,
                    IncludeTypes = req.IncludeTypes,
                    IncludeSymbols = req.IncludeSymbols,
                    IncludeNonProject = req.IncludeNonProject,
                    RequireUe = req.RequireUe,
                    MaxResults = max,
                }, ct);
                foreach (var m in r.Matches)
                    resp.Hits.Add(new SearchHit { Category = m.Category, Score = m.Score, Highlights = m.Highlights, Symbol = m.Symbol });
                resp.NonProjectHitCount += r.NonProjectMatchCount;
            }

            // Files and assets carry no reflection flags: a UE-only query excludes them.
            bool plainQuery = req.RequireUe == UeFlags.None && string.IsNullOrEmpty(req.Container);
            var recent = RecentRanks(req.RecentPaths);

            if (req.IncludeFiles && plainQuery)
            {
                var top = new TopN<SearchHit>(max, h => h.Score);
                var files = _index.Files.Snapshot();
                for (int i = 0; i < files.Length; i++)
                {
                    if ((i & 4095) == 0) ct.ThrowIfCancellationRequested();
                    var f = files[i].File;
                    var m = matcher.Match(f.Name);
                    if (!m.IsMatch) continue;
                    bool project = InMemorySymbolIndex.IsProject(f.Origin);
                    if (!project && !req.IncludeNonProject)
                    {
                        resp.NonProjectHitCount++;
                        continue;
                    }
                    int score = m.Score + (project ? 50 : 0) + RecentBoost(recent, f.Path);
                    top.Add(new SearchHit
                    {
                        Category = ResultCategory.File,
                        Score = score,
                        Highlights = InMemorySymbolIndex.Ranges(m),
                        File = new FileItem { Path = f.Path, Name = f.Name, RelativePath = f.RelativePath, Module = f.Module, Origin = f.Origin },
                    });
                }
                resp.Hits.AddRange(top.ToSortedList());
            }

            if (req.IncludeAssets && plainQuery)
            {
                var top = new TopN<(SearchHit Hit, AssetEntry Entry)>(max, h => h.Hit.Score);
                var assets = _index.Assets.Snapshot();
                for (int i = 0; i < assets.Length; i++)
                {
                    if ((i & 4095) == 0) ct.ThrowIfCancellationRequested();
                    var a = assets[i];
                    var m = matcher.MatchUnreal(a.Name);
                    if (!m.IsMatch) continue;
                    bool project = InMemorySymbolIndex.IsProject(a.Origin);
                    if (!project && !req.IncludeNonProject)
                    {
                        resp.NonProjectHitCount++;
                        continue;
                    }
                    top.Add((new SearchHit
                    {
                        Category = ResultCategory.Asset,
                        Score = m.Score + (project ? 50 : 0) + RecentBoost(recent, a.FilePath),
                        Highlights = InMemorySymbolIndex.Ranges(m),
                    }, a));
                }
                foreach (var (hit, entry) in top.ToSortedList())
                {
                    hit.Asset = entry.ToItem(); // object path / class only for returned hits
                    resp.Hits.Add(hit);
                }
            }

            resp.ElapsedMs = sw.Elapsed.TotalMilliseconds;
            return resp;
        }

        private static Dictionary<string, int> RecentRanks(string[] recent)
        {
            if (recent == null || recent.Length == 0) return null;
            var d = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            for (int i = 0; i < recent.Length && i < 50; i++)
                if (!string.IsNullOrEmpty(recent[i])) d.TryAdd(recent[i], i);
            return d;
        }

        private static int RecentBoost(Dictionary<string, int> recent, string path) =>
            recent != null && recent.TryGetValue(path, out int rank) ? 5000 - rank * 50 : 0;
    }
}
