using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using UEToVS.Contracts;
using UEToVS.Core.Query;
using UEToVS.Host.Logic.Actions;
using UEToVS.Host.Logic.Localization;
using UiBadge = UEToVS.UI.Logic.BadgeKind;
using UiGlyph = UEToVS.UI.Logic.SymbolKind;

namespace UEToVS.Host.Logic.Search
{
    public enum SearchRowKind
    {
        Type,
        Symbol,
        File,
        Asset,
        Action,
        Recent,
        Header,
        More,
        Hint,
        PrefixCommand,
    }

    /// <summary>One row of Search Everywhere (pure data; the host presenter maps it to a UEToVS.UI row).</summary>
    public sealed class SearchRow
    {
        public SearchRowKind Kind { get; set; }
        public string Title { get; set; }

        /// <summary>[start, length] pairs into <see cref="Title"/>.</summary>
        public int[] Highlights { get; set; } = Array.Empty<int>();

        public string Detail { get; set; }
        public string Right { get; set; }
        public UiGlyph? Glyph { get; set; }
        public List<KeyValuePair<string, UiBadge>> Badges { get; } = new List<KeyValuePair<string, UiBadge>>();
        public int Score { get; set; }

        /// <summary>Target file; null for actions/assets/headers.</summary>
        public string FilePath { get; set; }

        /// <summary>0-based line; -1 = just open the file.</summary>
        public int Line { get; set; } = -1;

        public int Column { get; set; }
        public bool IsNonProject { get; set; }
        public int? GroupCount { get; set; }
        public SymbolInfo Symbol { get; set; }
        public AssetItem Asset { get; set; }

        /// <summary>Headers/"More" rows and prefix commands: the tab they lead to.</summary>
        public SearchTab? TargetTab { get; set; }

        /// <summary>Action rows: the action id; prefix rows: the prefix text.</summary>
        public string ActionId { get; set; }

        public string Shortcut { get; set; }

        public bool IsSelectable => Kind != SearchRowKind.Header && Kind != SearchRowKind.Hint;

        public override string ToString() => $"{Kind} {Title} {FilePath}:{Line}";
    }

    public sealed class SearchRowSet
    {
        public List<SearchRow> Rows { get; } = new List<SearchRow>();

        /// <summary>Footer status (null = none).</summary>
        public string Status { get; set; }

        public Dictionary<string, int> TabCounts { get; } = new Dictionary<string, int>();
        public bool ShowingNonProjectFallback { get; set; }
    }

    /// <summary>Options of one Search Everywhere query (from the palette chips and settings).</summary>
    public sealed class SearchOptions
    {
        public bool IncludeNonProject { get; set; }

        /// <summary>Types: only UCLASS/USTRUCT/UENUM/UINTERFACE; symbols: only UFUNCTION/UPROPERTY.</summary>
        public bool OnlyReflected { get; set; }

        public int MaxResultsPerCategory { get; set; } = 60;

        /// <summary>Recently opened files, most recent first.</summary>
        public IReadOnlyList<string> RecentPaths { get; set; } = Array.Empty<string>();

        public string ProjectRoot { get; set; }
    }

    /// <summary>
    /// Search Everywhere behaviour (Rider, ported from the prototype): tabs, SearchQuery syntax ("/types foo",
    /// "AActor::Tick", "Foo.cpp:120:8", path fragments), group order in "All" by best match, "more" rows, automatic
    /// fallback to non-project items, recent files for an empty query. Pure: the host does the RPC and the UI.
    /// </summary>
    public static class SearchEverywhereModel
    {
        public const int AllTabGroupSize = 8;
        public const int RecentShown = 15;

        /// <summary>Palette tab order: 全部 / 类型 / 符号 / 文件 / 资产（蓝图） / 操作.</summary>
        public static readonly SearchTab[] Tabs = { SearchTab.All, SearchTab.Types, SearchTab.Symbols, SearchTab.Files, SearchTab.Assets, SearchTab.Actions };

        public static string TabId(SearchTab tab) => tab.ToString().ToLowerInvariant();

        public static SearchTab? TabFromId(string id)
        {
            foreach (var t in Tabs)
                if (string.Equals(TabId(t), id, StringComparison.OrdinalIgnoreCase)) return t;
            return null;
        }

        public static string TabTitle(SearchTab tab)
        {
            switch (tab)
            {
                case SearchTab.All: return FeatureStrings.TabAll;
                case SearchTab.Types: return FeatureStrings.TabTypes;
                case SearchTab.Symbols: return FeatureStrings.TabSymbols;
                case SearchTab.Files: return FeatureStrings.TabFiles;
                case SearchTab.Assets: return FeatureStrings.TabAssets;
                case SearchTab.Actions: return FeatureStrings.TabActions;
                default: return tab.ToString();
            }
        }

        /// <summary>The tab a query runs on: a "/xxx" prefix wins; "/text" (no engine full-text search yet) maps to All.</summary>
        public static SearchTab EffectiveTab(SearchQuery q, SearchTab selected)
        {
            var t = q.TabOverride ?? selected;
            return Array.IndexOf(Tabs, t) >= 0 ? t : SearchTab.All;
        }

        /// <summary>True when the tab needs the engine (everything except Actions).</summary>
        public static bool NeedsEngine(SearchTab tab) => tab != SearchTab.Actions;

        public static SearchRequest BuildRequest(SearchQuery q, SearchTab tab, SearchOptions o)
        {
            var req = new SearchRequest
            {
                Pattern = q.Pattern, // the engine matches file names; path fragments are applied in BuildRows
                Container = q.Container,
                IncludeTypes = tab == SearchTab.All || tab == SearchTab.Types,
                IncludeSymbols = tab == SearchTab.All || tab == SearchTab.Symbols,
                IncludeFiles = tab == SearchTab.All || tab == SearchTab.Files,
                IncludeAssets = tab == SearchTab.All || tab == SearchTab.Assets,
                IncludeNonProject = o.IncludeNonProject,
                MaxResultsPerCategory = tab == SearchTab.All ? AllTabGroupSize * 3 : Math.Max(10, o.MaxResultsPerCategory),
                RecentPaths = (o.RecentPaths ?? Array.Empty<string>()).Take(50).ToArray(),
            };
            if (q.LooksLikeFile && tab == SearchTab.All)
            {
                // "Foo.cpp" / "Engine/Ren/Scene": files first; types are still useful for "Foo".
                req.IncludeSymbols = false;
                req.IncludeAssets = false;
            }
            if (tab == SearchTab.Symbols && o.OnlyReflected) req.RequireUe = UeFlags.UFunction | UeFlags.UProperty;
            return req;
        }

        /// <summary>Rider: nothing in the project but matches outside → query again with non-project items.</summary>
        public static bool ShouldFallBackToNonProject(SearchOptions o, SearchResponse r) =>
            !o.IncludeNonProject && r != null && r.Hits.Count == 0 && r.NonProjectHitCount > 0;

        /// <summary>Rows for "/" or a partial prefix command.</summary>
        public static SearchRowSet PrefixRows(string text)
        {
            var set = new SearchRowSet();
            string t = (text ?? string.Empty).Trim();
            foreach (var kv in SearchQuery.PrefixCommands)
            {
                if (!kv.Key.StartsWith(t, StringComparison.OrdinalIgnoreCase)) continue;
                if (Array.IndexOf(Tabs, kv.Value) < 0) continue;
                set.Rows.Add(new SearchRow
                {
                    Kind = SearchRowKind.PrefixCommand,
                    Title = kv.Key,
                    Detail = FeatureStrings.SwitchToTab(TabTitle(kv.Value)),
                    Glyph = UiGlyph.Keyword,
                    TargetTab = kv.Value,
                    ActionId = kv.Key,
                });
            }
            return set;
        }

        /// <summary>Empty query: recently opened files (not on the Actions tab).</summary>
        public static SearchRowSet EmptyRows(SearchTab tab, SearchOptions o, Func<string, bool> fileExists = null)
        {
            var set = new SearchRowSet();
            if (tab == SearchTab.Actions || tab == SearchTab.Assets) return set;
            fileExists = fileExists ?? File.Exists;
            var recent = (o.RecentPaths ?? Array.Empty<string>()).Where(p => !string.IsNullOrEmpty(p) && fileExists(p)).Take(RecentShown).ToList();
            if (recent.Count == 0) return set;
            set.Rows.Add(new SearchRow { Kind = SearchRowKind.Header, Title = FeatureStrings.RecentFilesGroup, GroupCount = recent.Count });
            foreach (var p in recent) set.Rows.Add(RecentFileRow(p, o.ProjectRoot, false));
            return set;
        }

        public static SearchRow RecentFileRow(string path, string projectRoot, bool edited)
        {
            var row = new SearchRow
            {
                Kind = SearchRowKind.Recent,
                Title = Path.GetFileName(path),
                Detail = ShortDir(path, projectRoot),
                Right = edited ? FeatureStrings.Edited : null,
                Glyph = UiGlyph.File,
                FilePath = path,
                Line = -1,
            };
            AddFileBadge(row, path);
            return row;
        }

        /// <summary>Turns an engine response (plus the action rows for "All"/"Actions") into the palette rows.</summary>
        public static SearchRowSet BuildRows(SearchQuery q, SearchTab tab, SearchResponse resp, SearchOptions o, IReadOnlyList<SearchRow> actions)
        {
            var set = new SearchRowSet();
            actions = actions ?? Array.Empty<SearchRow>();
            if (tab == SearchTab.Actions)
            {
                set.Rows.AddRange(actions);
                set.TabCounts[TabId(SearchTab.Actions)] = actions.Count;
                return set;
            }

            var hits = resp?.Hits ?? new List<SearchHit>();
            var types = hits.Where(h => h.Category == ResultCategory.Type && h.Symbol != null).Select(h => FromSymbol(h, q, true)).ToList();
            if (o.OnlyReflected) types = types.Where(r => (r.Symbol.Ue & (UeFlags.UClass | UeFlags.UStruct | UeFlags.UEnum | UeFlags.UInterface)) != 0).ToList();
            var symbols = hits.Where(h => h.Category == ResultCategory.Symbol && h.Symbol != null).Select(h => FromSymbol(h, q, false)).ToList();
            if (o.OnlyReflected) symbols = symbols.Where(r => (r.Symbol.Ue & (UeFlags.UFunction | UeFlags.UProperty)) != 0).ToList();
            var files = hits.Where(h => h.Category == ResultCategory.File && h.File != null && MatchesFragments(h.File.RelativePath ?? h.File.Path, q.PathFragments))
                            .Select(h => FromFile(h, q)).ToList();
            var assets = hits.Where(h => h.Category == ResultCategory.Asset && h.Asset != null).Select(FromAsset).ToList();

            set.TabCounts[TabId(SearchTab.Types)] = types.Count;
            set.TabCounts[TabId(SearchTab.Symbols)] = symbols.Count;
            set.TabCounts[TabId(SearchTab.Files)] = files.Count;
            set.TabCounts[TabId(SearchTab.Assets)] = assets.Count;

            if (tab == SearchTab.All)
            {
                var groups = new List<(SearchTab Tab, List<SearchRow> Rows)>
                {
                    (SearchTab.Types, types),
                    (SearchTab.Files, files),
                    (SearchTab.Symbols, symbols),
                    (SearchTab.Assets, assets),
                };
                // Rider orders groups by their best match; a file-looking query puts files first.
                groups = q.LooksLikeFile
                    ? groups.OrderByDescending(g => g.Tab == SearchTab.Files).ToList()
                    : groups.OrderByDescending(g => g.Rows.Count == 0 ? int.MinValue : g.Rows.Max(r => r.Score)).ToList();
                foreach (var g in groups) AddGroup(set, g.Tab, g.Rows);
                AddGroup(set, SearchTab.Actions, actions.ToList());
                set.TabCounts[TabId(SearchTab.Actions)] = actions.Count;
                set.TabCounts[TabId(SearchTab.All)] = types.Count + files.Count + symbols.Count + assets.Count + actions.Count;
            }
            else
            {
                var rows = tab == SearchTab.Types ? types : tab == SearchTab.Symbols ? symbols : tab == SearchTab.Files ? files : assets;
                set.Rows.AddRange(rows.OrderByDescending(r => r.Score));
            }
            return set;
        }

        /// <summary>Footer text: index progress, non-project hint, fallback note. Null when nothing to say.</summary>
        public static string StatusFor(SearchResponse resp, SearchOptions o, bool fellBack, EngineStatus status)
        {
            var parts = new List<string>();
            if (resp != null && !resp.IndexReady)
            {
                if (status != null && status.Phase == IndexPhase.Parsing && status.FilesToParse > 0)
                    parts.Add(FeatureStrings.IndexingProgress(status.FilesParsed, status.FilesToParse));
                else parts.Add(FeatureStrings.IndexNotReady);
            }
            if (fellBack) parts.Add(FeatureStrings.ShowingNonProject);
            else if (resp != null && !o.IncludeNonProject && resp.NonProjectHitCount > 0) parts.Add(FeatureStrings.MoreNonProject(resp.NonProjectHitCount));
            return parts.Count == 0 ? null : string.Join(" · ", parts);
        }

        /// <summary>"Engine/Ren/SceneRen": every directory fragment must appear, in order, in the file's directory path.</summary>
        public static bool MatchesFragments(string path, IReadOnlyList<string> fragments)
        {
            if (fragments == null || fragments.Count == 0) return true;
            if (string.IsNullOrEmpty(path)) return false;
            string p = path.Replace('\\', '/');
            int slash = p.LastIndexOf('/');
            string dir = slash < 0 ? string.Empty : p.Substring(0, slash);
            int at = 0;
            foreach (var f in fragments)
            {
                if (string.IsNullOrEmpty(f)) continue;
                int i = dir.IndexOf(f, at, StringComparison.OrdinalIgnoreCase);
                if (i < 0) return false;
                at = i + f.Length;
            }
            return true;
        }

        private static void AddGroup(SearchRowSet set, SearchTab tab, List<SearchRow> rows)
        {
            if (rows.Count == 0) return;
            set.Rows.Add(new SearchRow { Kind = SearchRowKind.Header, Title = TabTitle(tab), TargetTab = tab, GroupCount = rows.Count });
            set.Rows.AddRange(rows.Take(AllTabGroupSize));
            if (rows.Count > AllTabGroupSize)
                set.Rows.Add(new SearchRow { Kind = SearchRowKind.More, Title = FeatureStrings.MoreIn(TabTitle(tab)), TargetTab = tab, Glyph = UiGlyph.Keyword });
        }

        public static bool IsTypeKind(Contracts.SymbolKind k) =>
            k == Contracts.SymbolKind.Class || k == Contracts.SymbolKind.Struct || k == Contracts.SymbolKind.Enum ||
            k == Contracts.SymbolKind.Union || k == Contracts.SymbolKind.Typedef || k == Contracts.SymbolKind.Delegate;

        public static SearchRow FromSymbol(SearchHit h, SearchQuery q, bool isType)
        {
            var s = h.Symbol;
            string path = s.Location?.FilePath;
            int line = s.Location?.Line ?? 0, col = s.Location?.Column ?? 0;
            if (q != null && q.Line.HasValue)
            {
                line = q.Line.Value - 1;
                col = (q.Column ?? 1) - 1;
            }
            var row = new SearchRow
            {
                Kind = isType ? SearchRowKind.Type : SearchRowKind.Symbol,
                Title = s.Name,
                Highlights = h.Highlights ?? Array.Empty<int>(),
                Detail = (string.IsNullOrEmpty(s.Container) ? "" : s.Container + "  ") +
                         (path == null ? "" : Path.GetFileName(path) + ":" + ((s.Location?.Line ?? 0) + 1)),
                Right = OriginText(s.Module, s.Origin),
                Glyph = GlyphFor(s),
                Score = h.Score,
                FilePath = path,
                Line = Math.Max(0, line),
                Column = Math.Max(0, col),
                Symbol = s,
                IsNonProject = !IsProject(s.Origin),
            };
            AddUeBadges(row, s.Ue);
            return row;
        }

        public static SearchRow FromFile(SearchHit h, SearchQuery q)
        {
            var f = h.File;
            int line = q != null && q.Line.HasValue ? q.Line.Value - 1 : -1;
            int col = q != null && q.Column.HasValue ? q.Column.Value - 1 : 0;
            var row = new SearchRow
            {
                Kind = SearchRowKind.File,
                Title = f.Name ?? Path.GetFileName(f.Path),
                Highlights = h.Highlights ?? Array.Empty<int>(),
                Detail = DirOf(f.RelativePath ?? f.Path),
                Right = OriginText(f.Module, f.Origin),
                Glyph = UiGlyph.File,
                Score = h.Score,
                FilePath = f.Path,
                Line = line,
                Column = Math.Max(0, col),
                IsNonProject = !IsProject(f.Origin),
            };
            AddFileBadge(row, f.Path);
            return row;
        }

        public static SearchRow FromAsset(SearchHit h)
        {
            var a = h.Asset;
            var row = new SearchRow
            {
                Kind = SearchRowKind.Asset,
                Title = a.Name,
                Highlights = h.Highlights ?? Array.Empty<int>(),
                Detail = a.ObjectPath,
                Right = OriginText(null, a.Origin),
                Glyph = UiGlyph.Asset,
                Score = h.Score,
                Asset = a,
                IsNonProject = !IsProject(a.Origin),
            };
            row.Badges.Add(new KeyValuePair<string, UiBadge>(string.IsNullOrEmpty(a.ClassName) ? "Asset" : a.ClassName, UiBadge.Asset));
            return row;
        }

        public static SearchRow FromAction(ActionMatch m)
        {
            int[] hl = Array.Empty<int>();
            var ranges = m.TitleMatch?.Ranges;
            if (ranges != null && ranges.Count > 0) hl = ranges.SelectMany(r => new[] { r.Start, r.Length }).ToArray();
            return new SearchRow
            {
                Kind = SearchRowKind.Action,
                Title = m.Action.Title,
                Highlights = hl,
                Right = Strings.CategoryName(m.Action.Category),
                Glyph = UiGlyph.Action,
                Score = m.Score,
                ActionId = m.Action.Id,
            };
        }

        public static bool IsProject(ItemOrigin o) => o == ItemOrigin.Project || o == ItemOrigin.ProjectPlugin;

        public static string OriginText(string module, ItemOrigin origin)
        {
            string o = OriginLabel(origin);
            if (string.IsNullOrEmpty(module)) return o;
            return o == null ? module : module + " · " + o;
        }

        public static string OriginLabel(ItemOrigin o)
        {
            switch (o)
            {
                case ItemOrigin.ProjectPlugin: return FeatureStrings.OriginPlugin;
                case ItemOrigin.Engine: return FeatureStrings.OriginEngine;
                case ItemOrigin.EnginePlugin: return FeatureStrings.OriginEnginePlugin;
                case ItemOrigin.ThirdParty: return FeatureStrings.OriginThirdParty;
                default: return null;
            }
        }

        public static UiGlyph GlyphFor(SymbolInfo s)
        {
            if ((s.Ue & UeFlags.UInterface) != 0) return UiGlyph.Interface;
            switch (s.Kind)
            {
                case Contracts.SymbolKind.Class: return UiGlyph.Class;
                case Contracts.SymbolKind.Struct:
                case Contracts.SymbolKind.Union: return UiGlyph.Struct;
                case Contracts.SymbolKind.Enum: return UiGlyph.Enum;
                case Contracts.SymbolKind.EnumValue: return UiGlyph.EnumMember;
                case Contracts.SymbolKind.Typedef: return UiGlyph.Typedef;
                case Contracts.SymbolKind.Function: return UiGlyph.Function;
                case Contracts.SymbolKind.Method:
                case Contracts.SymbolKind.Constructor:
                case Contracts.SymbolKind.Destructor: return UiGlyph.Method;
                case Contracts.SymbolKind.Field: return (s.Ue & UeFlags.UProperty) != 0 ? UiGlyph.Property : UiGlyph.Field;
                case Contracts.SymbolKind.Variable: return UiGlyph.Variable;
                case Contracts.SymbolKind.Macro: return UiGlyph.Macro;
                case Contracts.SymbolKind.Delegate: return UiGlyph.Delegate;
                case Contracts.SymbolKind.Namespace: return UiGlyph.Namespace;
                default: return UiGlyph.Unknown;
            }
        }

        private static void AddUeBadges(SearchRow row, UeFlags ue)
        {
            if ((ue & UeFlags.UClass) != 0) row.Badges.Add(new KeyValuePair<string, UiBadge>("UCLASS", UiBadge.UClass));
            else if ((ue & UeFlags.UStruct) != 0) row.Badges.Add(new KeyValuePair<string, UiBadge>("USTRUCT", UiBadge.UStruct));
            else if ((ue & UeFlags.UEnum) != 0) row.Badges.Add(new KeyValuePair<string, UiBadge>("UENUM", UiBadge.UEnum));
            else if ((ue & UeFlags.UInterface) != 0) row.Badges.Add(new KeyValuePair<string, UiBadge>("UINTERFACE", UiBadge.UInterface));
            if ((ue & UeFlags.UFunction) != 0) row.Badges.Add(new KeyValuePair<string, UiBadge>("UFUNCTION", UiBadge.UFunction));
            if ((ue & UeFlags.UProperty) != 0) row.Badges.Add(new KeyValuePair<string, UiBadge>("UPROPERTY", UiBadge.UProperty));
            if ((ue & UeFlags.UDelegate) != 0) row.Badges.Add(new KeyValuePair<string, UiBadge>("DELEGATE", UiBadge.Delegate));
        }

        private static void AddFileBadge(SearchRow row, string path)
        {
            string ext = Path.GetExtension(path ?? string.Empty).TrimStart('.').ToLowerInvariant();
            if (path != null && path.EndsWith(".Build.cs", StringComparison.OrdinalIgnoreCase)) ext = "build.cs";
            if (ext.Length > 0) row.Badges.Add(new KeyValuePair<string, UiBadge>(ext, ext == "build.cs" ? UiBadge.Module : UiBadge.File));
        }

        private static string DirOf(string relative)
        {
            if (string.IsNullOrEmpty(relative)) return null;
            string r = relative.Replace('\\', '/');
            int i = r.LastIndexOf('/');
            return i <= 0 ? null : r.Substring(0, i);
        }

        /// <summary>Directory shown under a file: relative to the project root, otherwise the last three folders.</summary>
        public static string ShortDir(string path, string root)
        {
            string dir = Path.GetDirectoryName(path ?? string.Empty) ?? string.Empty;
            if (!string.IsNullOrEmpty(root) && dir.StartsWith(root, StringComparison.OrdinalIgnoreCase))
                return dir.Substring(root.Length).TrimStart('\\', '/');
            var parts = dir.Split('\\', '/').Where(p => p.Length > 0).ToArray();
            return parts.Length <= 3 ? dir : "…/" + string.Join("/", parts.Skip(parts.Length - 3));
        }
    }
}
