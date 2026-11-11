using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.VisualStudio.Shell;
using UEToVS.Core.Matching;
using UEToVS.Core.Protocol;
using UEToVS.Core.Query;

namespace UEToVS.Search
{
    internal sealed class SearchFilters
    {
        // Types tab
        public bool UClass = true, UStruct = true, UEnum = true, UInterface = true, PlainCpp = true;

        // Symbols tab
        public bool OnlyReflected;

        // Files tab: null = all extensions
        public string[] FileExtensions;

        // Text tab
        public bool CaseSensitive, WholeWord, Regex;
        public string FileMask;

        public bool IsDefault(SearchTab tab)
        {
            switch (tab)
            {
                case SearchTab.Types: return UClass && UStruct && UEnum && UInterface && PlainCpp;
                case SearchTab.Symbols: return !OnlyReflected;
                case SearchTab.Files: return FileExtensions == null;
                case SearchTab.Text: return !CaseSensitive && !WholeWord && !Regex && string.IsNullOrEmpty(FileMask);
                default: return true;
            }
        }
    }

    internal sealed class SearchResultSet
    {
        public List<SearchItem> Items = new List<SearchItem>();
        public string Footer;
        public bool ShowingNonProjectFallback;
    }

    /// <summary>Turns the query, tab and options into result rows (Rider Search Everywhere behaviour).</summary>
    internal sealed class SearchController
    {
        private const int AllTabGroupSize = 8;
        private readonly ISearchHost _host;

        public SearchController(ISearchHost host)
        {
            _host = host;
        }

        public SearchFilters Filters { get; } = new SearchFilters();

        public static readonly SearchTab[] Tabs = { SearchTab.All, SearchTab.Types, SearchTab.Files, SearchTab.Symbols, SearchTab.Actions, SearchTab.Text, SearchTab.Assets };

        public static string TabTitle(SearchTab t)
        {
            switch (t)
            {
                case SearchTab.All: return "All";
                case SearchTab.Types: return "Types";
                case SearchTab.Files: return "Files";
                case SearchTab.Symbols: return "Symbols";
                case SearchTab.Actions: return "Actions";
                case SearchTab.Text: return "Text";
                case SearchTab.Assets: return "Assets";
            }
            return t.ToString();
        }

        public async Task<SearchResultSet> RunAsync(string text, SearchTab tab, bool includeNonProject, CancellationToken ct)
        {
            var q = SearchQuery.Parse(text);
            var result = new SearchResultSet();
            if (q.TabOverride.HasValue) tab = q.TabOverride.Value;

            if (q.IsPrefixCommandListing)
            {
                foreach (var kv in SearchQuery.PrefixCommands.Where(k => k.Key.StartsWith(text.Trim(), StringComparison.OrdinalIgnoreCase)))
                {
                    string cmd = kv.Key;
                    result.Items.Add(new SearchItem
                    {
                        Kind = ItemKind.PrefixCommand,
                        Title = cmd,
                        Detail = "切换到 " + TabTitle(kv.Value),
                        Badge = "/",
                        TargetTab = kv.Value,
                    });
                }
                return result;
            }

            if (q.IsEmpty)
            {
                AddEmptyState(result, tab);
                return result;
            }

            switch (tab)
            {
                case SearchTab.Actions:
                    await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync(ct);
                    result.Items.AddRange(_host.Actions.Search(q.Pattern, _host.Options.MaxResultsPerCategory));
                    break;
                case SearchTab.Text:
                    await RunTextAsync(text.Trim(), includeNonProject, result, ct);
                    break;
                default:
                    await RunIndexAsync(q, tab, includeNonProject, result, ct);
                    break;
            }

            if (result.Items.Count == 0 && result.Footer == null)
                result.Items.Add(new SearchItem { Kind = ItemKind.Hint, Title = "没有找到结果" });
            return result;
        }

        private void AddEmptyState(SearchResultSet result, SearchTab tab)
        {
            if (tab == SearchTab.Actions || tab == SearchTab.Text) return;
            var recent = _host.RecentFilePaths.Take(15).ToList();
            if (recent.Count > 0)
            {
                result.Items.Add(new SearchItem { Kind = ItemKind.Header, Title = "最近打开的文件" });
                foreach (var path in recent)
                {
                    var (badge, color) = Badges.ForFile(path);
                    string p = path;
                    result.Items.Add(new SearchItem
                    {
                        Kind = ItemKind.Recent,
                        Title = Path.GetFileName(path),
                        Detail = ShortDir(path),
                        Badge = badge,
                        BadgeColor = color,
                        FilePath = path,
                        Line = -1,
                        Activate = mode => { _host.NavigateTo(p, -1, 0, mode); return true; },
                    });
                }
            }
        }

        private async Task RunIndexAsync(SearchQuery q, SearchTab tab, bool includeNonProject, SearchResultSet result, CancellationToken ct)
        {
            var index = await _host.GetIndexAsync().ConfigureAwait(false);
            if (index == null)
            {
                result.Footer = _host.IndexStatusText ?? "索引不可用：没有找到 .uproject（详见“输出 > UEToVS”）";
                if (tab == SearchTab.All)
                {
                    await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync(ct);
                    AddGroup(result, "Actions", SearchTab.Actions, _host.Actions.Search(q.Pattern, AllTabGroupSize + 1));
                }
                return;
            }

            var req = new SearchRequest
            {
                Pattern = q.Pattern,
                Container = q.Container,
                PathFragments = q.PathFragments.ToArray(),
                IncludeTypes = tab == SearchTab.All || tab == SearchTab.Types,
                IncludeSymbols = tab == SearchTab.All || tab == SearchTab.Symbols,
                IncludeFiles = tab == SearchTab.All || tab == SearchTab.Files,
                IncludeAssets = tab == SearchTab.All || tab == SearchTab.Assets,
                IncludeNonProject = includeNonProject,
                MaxResultsPerCategory = tab == SearchTab.All ? AllTabGroupSize * 3 : _host.Options.MaxResultsPerCategory,
                RecentPaths = _host.RecentFilePaths.Take(50).ToArray(),
            };
            if (q.LooksLikeFile && tab == SearchTab.All)
            {
                // "Foo.cpp" / "Engine/Ren/Scene": files first, still show types for "Foo"
                req.IncludeSymbols = false;
                req.IncludeAssets = false;
            }
            if (tab == SearchTab.Symbols && Filters.OnlyReflected) req.RequireUe = UeFlags.UFunction | UeFlags.UProperty;
            if (tab == SearchTab.Files) req.FileExtensions = Filters.FileExtensions;

            SearchResponse resp;
            try
            {
                resp = await index.SearchAsync(req, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                result.Footer = "索引查询失败：" + ex.Message;
                return;
            }

            // Nothing in the project: show non-project items automatically (with a note), like Rider.
            if (!includeNonProject && resp.Hits.Count == 0 && resp.NonProjectHitCount > 0)
            {
                req.IncludeNonProject = true;
                resp = await index.SearchAsync(req, ct).ConfigureAwait(false);
                result.ShowingNonProjectFallback = true;
                result.Footer = "项目中没有匹配项，已显示非项目项";
            }
            else if (!includeNonProject && resp.NonProjectHitCount > 0)
            {
                result.Footer = $"非项目项中还有 {resp.NonProjectHitCount:N0} 个匹配 · 双击 Shift 显示";
            }
            if (!resp.IndexReady)
                result.Footer = (_host.IndexStatusText ?? "正在建立索引…") + (result.Footer != null ? " · " + result.Footer : "");

            var types = resp.Hits.Where(h => h.Category == ResultCategory.Type).Select(h => FromSymbol(h, q)).ToList();
            if (tab == SearchTab.Types) types = types.Where(PassesTypeFilter).ToList();
            var symbols = resp.Hits.Where(h => h.Category == ResultCategory.Symbol).Select(h => FromSymbol(h, q)).ToList();
            var files = resp.Hits.Where(h => h.Category == ResultCategory.File).Select(h => FromFile(h, q)).ToList();
            var assets = resp.Hits.Where(h => h.Category == ResultCategory.Asset).Select(FromAsset).ToList();

            if (tab == SearchTab.All)
            {
                var groups = new List<(string title, SearchTab target, List<SearchItem> items)>
                {
                    ("Types", SearchTab.Types, types),
                    ("Files", SearchTab.Files, files),
                    ("Symbols", SearchTab.Symbols, symbols),
                    ("Assets", SearchTab.Assets, assets),
                };
                // Rider orders groups by their best match
                if (q.LooksLikeFile) groups = groups.OrderByDescending(g => g.target == SearchTab.Files).ToList();
                else groups = groups.OrderByDescending(g => g.items.Count == 0 ? int.MinValue : g.items[0].Score).ToList();
                foreach (var g in groups) AddGroup(result, g.title, g.target, g.items);

                await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync(ct);
                var actions = _host.Actions.Search(q.Pattern, AllTabGroupSize + 1);
                AddGroup(result, "Actions", SearchTab.Actions, actions);
            }
            else
            {
                result.Items.AddRange(tab == SearchTab.Types ? types : tab == SearchTab.Files ? files : tab == SearchTab.Symbols ? symbols : assets);
            }
        }

        private bool PassesTypeFilter(SearchItem item)
        {
            var s = item.Symbol;
            if (s == null) return true;
            if ((s.Ue & UeFlags.UClass) != 0) return Filters.UClass;
            if ((s.Ue & UeFlags.UStruct) != 0) return Filters.UStruct;
            if ((s.Ue & UeFlags.UEnum) != 0) return Filters.UEnum;
            if ((s.Ue & UeFlags.UInterface) != 0) return Filters.UInterface;
            return Filters.PlainCpp;
        }

        private static void AddGroup(SearchResultSet result, string title, SearchTab target, List<SearchItem> items)
        {
            if (items.Count == 0) return;
            result.Items.Add(new SearchItem { Kind = ItemKind.Header, Title = title, TargetTab = target });
            result.Items.AddRange(items.Take(AllTabGroupSize));
            if (items.Count > AllTabGroupSize)
                result.Items.Add(new SearchItem { Kind = ItemKind.More, Title = "更多…", Detail = title, TargetTab = target, Badge = "…" });
        }

        private SearchItem FromSymbol(SearchHit h, SearchQuery q)
        {
            var s = h.Symbol;
            var (badge, color) = Badges.For(s);
            string title = s.Name;
            int[] hl = h.Highlights ?? Array.Empty<int>();
            string container = s.Container;
            string detail = container != null ? "in " + container : null;
            string origin = Badges.OriginLabel(s.Origin);
            string right = s.Module != null ? (origin != null ? s.Module + " · " + origin : s.Module) : origin;
            int line = q.Line.HasValue ? q.Line.Value - 1 : s.Line;
            int col = q.Line.HasValue ? (q.Column ?? 1) - 1 : s.Column;
            string path = s.FilePath;
            return new SearchItem
            {
                Kind = Searcher_IsType(s.Kind) ? ItemKind.Type : ItemKind.Symbol,
                Title = title,
                Highlights = hl,
                Detail = (detail != null ? detail + "  " : "") + Path.GetFileName(path) + ":" + (s.Line + 1),
                Right = right,
                Badge = badge,
                BadgeColor = color,
                Score = h.Score,
                FilePath = path,
                Line = line,
                Column = col,
                Symbol = s,
                Activate = mode => { _host.NavigateTo(path, line, col, mode); return true; },
            };
        }

        private static bool Searcher_IsType(SymbolKind k) =>
            k == SymbolKind.Class || k == SymbolKind.Struct || k == SymbolKind.Enum || k == SymbolKind.Union || k == SymbolKind.Typedef || k == SymbolKind.Delegate;

        private SearchItem FromFile(SearchHit h, SearchQuery q)
        {
            var f = h.File;
            var (badge, color) = Badges.ForFile(f.Name);
            string origin = Badges.OriginLabel(f.Origin);
            int line = q.Line.HasValue ? q.Line.Value - 1 : -1;
            int col = q.Column.HasValue ? q.Column.Value - 1 : 0;
            string path = f.Path;
            return new SearchItem
            {
                Kind = ItemKind.File,
                Title = f.Name,
                Highlights = h.Highlights ?? Array.Empty<int>(),
                Detail = Path.GetDirectoryName(f.RelativePath?.Replace('/', Path.DirectorySeparatorChar) ?? ""),
                Right = f.Module != null ? (origin != null ? f.Module + " · " + origin : f.Module) : origin,
                Badge = badge,
                BadgeColor = color,
                Score = h.Score,
                FilePath = path,
                Line = line,
                Column = col,
                Activate = mode => { _host.NavigateTo(path, line, col, mode); return true; },
            };
        }

        private SearchItem FromAsset(SearchHit h)
        {
            var a = h.Asset;
            return new SearchItem
            {
                Kind = ItemKind.Asset,
                Title = a.Name,
                Highlights = h.Highlights ?? Array.Empty<int>(),
                Detail = a.ObjectPath,
                Right = a.IsMap ? "Map" : Badges.OriginLabel(a.Origin),
                Badge = a.IsMap ? "M" : "A",
                BadgeColor = a.IsMap ? BadgeColor.Green : BadgeColor.Orange,
                Score = h.Score,
                Asset = a,
                Activate = _ => { _host.OpenAsset(a); return true; },
            };
        }

        private async Task RunTextAsync(string text, bool includeNonProject, SearchResultSet result, CancellationToken ct)
        {
            if (text.Length < 2) return;
            await Task.Delay(120, ct).ConfigureAwait(false); // text search is expensive; wait for typing to settle
            var index = await _host.GetIndexAsync().ConfigureAwait(false);
            if (index == null)
            {
                result.Footer = "全文搜索需要索引进程（没有找到 .uproject）";
                return;
            }
            var hits = await index.SearchTextAsync(new TextSearchRequest
            {
                Query = text,
                CaseSensitive = Filters.CaseSensitive,
                WholeWord = Filters.WholeWord,
                Regex = Filters.Regex,
                FileMask = Filters.FileMask,
                IncludeNonProject = includeNonProject,
                MaxResults = 500,
            }, ct).ConfigureAwait(false);
            foreach (var m in hits)
            {
                string lineText = m.LineText ?? "";
                int trim = lineText.Length - lineText.TrimStart().Length;
                string shown = lineText.Trim();
                if (shown.Length > 200) shown = shown.Substring(0, 200);
                int hlStart = Math.Max(0, m.Column - trim);
                var match = m;
                result.Items.Add(new SearchItem
                {
                    Kind = ItemKind.Text,
                    Title = shown,
                    Highlights = hlStart < shown.Length ? new[] { hlStart, Math.Min(m.Length, shown.Length - hlStart) } : Array.Empty<int>(),
                    Detail = null,
                    Right = Path.GetFileName(m.FilePath) + ":" + (m.Line + 1),
                    Badge = "T",
                    BadgeColor = BadgeColor.Gray,
                    FilePath = m.FilePath,
                    Line = m.Line,
                    Column = m.Column,
                    Activate = mode => { _host.NavigateTo(match.FilePath, match.Line, match.Column, mode); return true; },
                });
            }
            if (hits.Count >= 500) result.Footer = "只显示前 500 条 · 按“在查找窗口中打开”查看全部";
        }

        private static string ShortDir(string path)
        {
            string dir = Path.GetDirectoryName(path) ?? "";
            var parts = dir.Split(Path.DirectorySeparatorChar);
            return parts.Length <= 3 ? dir : "…" + Path.DirectorySeparatorChar + string.Join(Path.DirectorySeparatorChar.ToString(), parts.Skip(parts.Length - 3));
        }
    }
}
