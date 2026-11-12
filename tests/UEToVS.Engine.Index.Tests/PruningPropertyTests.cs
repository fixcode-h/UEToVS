using System;
using System.Collections.Generic;
using System.Linq;
using UEToVS.Contracts;
using UEToVS.Core.Matching;
using Xunit;

namespace UEToVS.Engine.Index.Tests
{
    /// <summary>
    /// The candidate pruning (prefix ranges, initials, trigrams, case-aware subsequence, early exit) must never change
    /// results: compare against a brute-force FuzzyMatcher scan and against an unbounded search.
    /// </summary>
    public class PruningPropertyTests
    {
        private static readonly string[] Parts =
        {
            "Actor", "Act", "Player", "Camera", "Update", "Comp", "on", "Net", "HTTP", "Server", "Ui", "x", "Tick", "Load", "_", "2", "ate", "Pawn",
        };

        [Fact]
        public void PrunedSearch_MatchesBruteForce()
        {
            var r = new Random(12345);
            var names = new HashSet<string>(StringComparer.Ordinal);
            while (names.Count < 3000)
            {
                string prefix = r.Next(3) == 0 ? "AUFEITS"[r.Next(7)].ToString() : "";
                string n = prefix + string.Concat(Enumerable.Range(0, r.Next(1, 4)).Select(_ => Parts[r.Next(Parts.Length)]));
                if (r.Next(5) == 0) n = n.ToLowerInvariant();
                names.Add(n);
            }
            var list = names.ToList();
            var files = new List<FileSymbols>();
            for (int i = 0; i < list.Count; i += 10)
            {
                var decls = list.Skip(i).Take(10).Select((n, k) => T.D(n, k % 2 == 0 ? SymbolKind.Class : SymbolKind.Function)).ToArray();
                files.Add(T.F($"/p/F{i}.h", i % 20 == 0 ? ItemOrigin.Engine : ItemOrigin.Project, decls));
            }

            using var dir = new TempDir();
            using var idx = T.Open(dir.Path);
            idx.ReplaceBase(SegmentKind.Project, files);

            var patterns = new List<string> { "a", "A", "ac", "AC", "act", "Actor", "actor", "PCU", "pcu", "ate", "ATE", "Ui", "HTTPS", "HS", "tick", "x2", "_", "on", "Pl*Up", "nP", "e", "T", "PlayerCam" };
            for (int i = 0; i < 40; i++)
            {
                string n = list[r.Next(list.Count)];
                int s = r.Next(n.Length);
                patterns.Add(n.Substring(s, Math.Min(n.Length - s, r.Next(1, 6))));
            }

            foreach (var p in patterns.Distinct())
            {
                var matcher = new FuzzyMatcher(p);
                var expectedTypes = new HashSet<string>(list.Where((n, i) => (i % 10) % 2 == 0 && matcher.MatchUnreal(n).IsMatch));
                var expectedSyms = new HashSet<string>(list.Where((n, i) => (i % 10) % 2 == 1 && matcher.Match(n).IsMatch));

                var full = idx.Search(new SearchRequest { Pattern = p, IncludeNonProject = true, IncludeFiles = false, MaxResultsPerCategory = 100000 });
                Assert.True(expectedTypes.SetEquals(T.Names(full, ResultCategory.Type)), $"types differ for '{p}'");
                Assert.True(expectedSyms.SetEquals(T.Names(full, ResultCategory.Symbol)), $"symbols differ for '{p}'");

                foreach (int max in new[] { 1, 5, 20 })
                {
                    foreach (bool nonProject in new[] { true, false })
                    {
                        var small = idx.Search(new SearchRequest { Pattern = p, IncludeNonProject = nonProject, IncludeFiles = false, MaxResultsPerCategory = max });
                        var reference = idx.Search(new SearchRequest { Pattern = p, IncludeNonProject = nonProject, IncludeFiles = false, MaxResultsPerCategory = 100000 });
                        foreach (var cat in new[] { ResultCategory.Type, ResultCategory.Symbol })
                        {
                            var got = small.Hits.Where(h => h.Category == cat).Select(T.Describe).ToList();
                            var want = reference.Hits.Where(h => h.Category == cat).Take(max).Select(T.Describe).ToList();
                            Assert.True(want.SequenceEqual(got), $"top-{max} differs for '{p}' ({cat}, nonProject={nonProject})");
                        }
                    }
                }
            }
        }
    }
}
