using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using UEToVS.Contracts;
using UEToVS.Core.Diagnostics;
using UEToVS.Host.Logic.Link;
using UEToVS.Host.Logic.Localization;
using UEToVS.Host.Logic.Search;

namespace UEToVS.Host.Link
{
    /// <summary>
    /// Go to Related on a log category (LogFoo in DECLARE_LOG_CATEGORY_EXTERN / DEFINE_LOG_CATEGORY / UE_LOG): rows for its
    /// declaration, definition and every UE_LOG / UE_CLOG / UE_LOGFMT use, from the engine's on-demand scan.
    /// </summary>
    internal static class LogCategoryNavigator
    {
        private static readonly Logger L = Log.For("Link");
        private const int MaxUsages = 300;

        public static async Task<List<SearchRow>> RelatedRowsAsync(HostPackage package, string file, string word, string lineText, CancellationToken ct)
        {
            var rows = new List<SearchRow>();
            var engine = package?.Engine;
            if (engine?.IsConnected != true || !LogCategoryText.IsCandidate(word, lineText)) return rows;
            var r = await engine.FindLogSitesAsync(new LogSitesRequest { Category = word, IncludeUsages = true, IncludeDeclarations = true, MaxResults = 2000 }, ct)
                                .ConfigureAwait(false);
            if (!r.Ok || r.Value.Sites == null || r.Value.Sites.Count == 0) return rows;
            var sites = r.Value.Sites
                .Where(s => s.Location?.FilePath != null)
                .OrderBy(s => LogCategoryText.KindRank(s.Kind))
                .ThenBy(s => s.Location.FilePath, StringComparer.OrdinalIgnoreCase)
                .ThenBy(s => s.Location.Line)
                .ToList();
            int usages = 0;
            foreach (var s in sites)
            {
                if (s.Kind == LogSiteKind.Usage && ++usages > MaxUsages) break;
                rows.Add(new SearchRow
                {
                    Kind = SearchRowKind.Symbol,
                    Title = LogCategoryText.Title(s),
                    Detail = Path.GetFileName(s.Location.FilePath) + ":" + (s.Location.Line + 1),
                    Right = s.Kind == LogSiteKind.Declaration ? LinkStrings.CategoryDeclaration
                        : s.Kind == LogSiteKind.Usage ? LinkStrings.CategoryUsage
                        : LinkStrings.CategoryDefinition,
                    Glyph = UEToVS.UI.Logic.SymbolKind.Macro,
                    FilePath = s.Location.FilePath,
                    Line = s.Location.Line,
                    Column = s.Location.Column,
                });
            }
            L.Info("link.log.categoryRelated", null, ("category", word), ("sites", sites.Count), ("rows", rows.Count));
            return rows;
        }
    }
}
