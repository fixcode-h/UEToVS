using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using UEToVS.Contracts;
using UEToVS.Mcp.Data;

namespace UEToVS.Mcp.Tools
{
    /// <summary>One MCP tool: metadata for tools/list and the handler for tools/call.</summary>
    public sealed class ToolDefinition
    {
        public string Name { get; set; }
        public string Title { get; set; }
        public string Description { get; set; }

        /// <summary>JSON Schema (draft 2020-12 subset) of the arguments object.</summary>
        public JsonElement InputSchema { get; set; }

        public string[] Arguments { get; set; }
        public Func<ToolArgs, CancellationToken, Task<ToolOutput>> Handler { get; set; }
    }

    /// <summary>Handler result: the JSON payload plus what the log needs.</summary>
    public sealed class ToolOutput
    {
        public JsonObject Payload { get; set; }
        public int ResultCount { get; set; }
        public string Source { get; set; }
    }

    /// <summary>
    /// The UEToVS MCP tools. All are read-only and idempotent. Lines/columns are 1-based in arguments and results.
    /// Results are compact JSON (one text content block) with limit/offset paging where lists can be long.
    /// </summary>
    public sealed class ToolCatalog
    {
        public const int MaxFetch = 1000;

        private readonly CodeIndex _index;
        private readonly Dictionary<string, ToolDefinition> _tools = new Dictionary<string, ToolDefinition>(StringComparer.Ordinal);

        public ToolCatalog(CodeIndex index)
        {
            _index = index;
            Register(SearchSymbolsTool());
            Register(SearchFilesTool());
            Register(SearchAssetsTool());
            Register(FileOutlineTool());
            Register(GoToDefinitionTool());
            Register(FindReferencesTool());
            Register(TypeHierarchyTool());
            Register(MembersTool());
            Register(IndexStatusTool());
        }

        public IReadOnlyList<ToolDefinition> All => _tools.Values.ToList();

        public ToolDefinition Get(string name) => name != null && _tools.TryGetValue(name, out var t) ? t : null;

        private void Register(ToolDefinition t) => _tools.Add(t.Name, t);

        // ================================================================== schema helpers

        private const string ScopeDesc = "'project' (default): project and project-plugin code only; 'all': include engine, engine plugins and third-party.";

        private static JsonElement Schema(string propertiesJson, params string[] required)
        {
            string req = required.Length == 0 ? "" : ",\"required\":[" + string.Join(",", required.Select(r => "\"" + r + "\"")) + "]";
            using var doc = JsonDocument.Parse("{\"type\":\"object\",\"properties\":{" + propertiesJson + "}" + req + ",\"additionalProperties\":false}");
            return doc.RootElement.Clone();
        }

        private static string[] Names(JsonElement schema) => schema.GetProperty("properties").EnumerateObject().Select(p => p.Name).ToArray();

        private static ToolDefinition Define(string name, string title, string description, JsonElement schema,
                                             Func<ToolArgs, CancellationToken, Task<ToolOutput>> handler) =>
            new ToolDefinition { Name = name, Title = title, Description = description, InputSchema = schema, Arguments = Names(schema), Handler = handler };

        private const string PagingProps =
            "\"limit\":{\"type\":\"integer\",\"minimum\":1,\"maximum\":200,\"description\":\"Max items to return (default 30).\"}," +
            "\"offset\":{\"type\":\"integer\",\"minimum\":0,\"maximum\":1000,\"description\":\"Items to skip, for paging (use nextOffset from the previous call).\"}";

        private const string CaretProps =
            "\"path\":{\"type\":\"string\",\"description\":\"Source file: absolute, or relative to the solution/project folder.\"}," +
            "\"line\":{\"type\":\"integer\",\"minimum\":1,\"description\":\"1-based line.\"}," +
            "\"column\":{\"type\":\"integer\",\"minimum\":1,\"description\":\"1-based column (UTF-16); any column inside the identifier works.\"}";

        private static (int Limit, int Offset) Paging(ToolArgs a, int defaultLimit = 30, int maxLimit = 200)
        {
            int limit = a.Int("limit", defaultLimit, 1, maxLimit);
            int offset = a.Int("offset", 0, 0, MaxFetch);
            return (limit, offset);
        }

        /// <summary>Takes one page out of an ordered list; hasMore when items remain after it.</summary>
        private static (List<T> Page, bool HasMore) Slice<T>(IReadOnlyList<T> all, int offset, int limit)
        {
            var page = all.Skip(offset).Take(limit).ToList();
            return (page, all.Count > offset + limit);
        }

        // ================================================================== kinds / UE filters

        private static readonly Dictionary<string, SymbolKind[]> KindGroups = new Dictionary<string, SymbolKind[]>(StringComparer.OrdinalIgnoreCase)
        {
            ["type"] = new[] { SymbolKind.Class, SymbolKind.Struct, SymbolKind.Union, SymbolKind.Enum, SymbolKind.Typedef, SymbolKind.Delegate, SymbolKind.Concept },
            ["class"] = new[] { SymbolKind.Class },
            ["struct"] = new[] { SymbolKind.Struct },
            ["union"] = new[] { SymbolKind.Union },
            ["enum"] = new[] { SymbolKind.Enum },
            ["typedef"] = new[] { SymbolKind.Typedef },
            ["delegate"] = new[] { SymbolKind.Delegate },
            ["concept"] = new[] { SymbolKind.Concept },
            ["function"] = new[] { SymbolKind.Function, SymbolKind.Method, SymbolKind.Constructor, SymbolKind.Destructor },
            ["method"] = new[] { SymbolKind.Method, SymbolKind.Constructor, SymbolKind.Destructor },
            ["constructor"] = new[] { SymbolKind.Constructor },
            ["destructor"] = new[] { SymbolKind.Destructor },
            ["field"] = new[] { SymbolKind.Field },
            ["variable"] = new[] { SymbolKind.Variable, SymbolKind.Field },
            ["macro"] = new[] { SymbolKind.Macro },
            ["enum_value"] = new[] { SymbolKind.EnumValue },
            ["namespace"] = new[] { SymbolKind.Namespace },
        };

        private const string KindsDesc = "Filter by kind (any of): type, class, struct, union, enum, typedef, delegate, concept, function, method, constructor, destructor, field, variable, macro, enum_value, namespace.";

        public static HashSet<SymbolKind> ParseKinds(List<string> kinds)
        {
            if (kinds == null || kinds.Count == 0) return null;
            var set = new HashSet<SymbolKind>();
            foreach (var k in kinds)
            {
                if (!KindGroups.TryGetValue(k, out var group))
                    throw ToolArgs.Invalid($"unknown kind '{k}'. Valid: {string.Join(", ", KindGroups.Keys)}");
                set.UnionWith(group);
            }
            return set;
        }

        private static readonly Dictionary<string, UeFlags> UeNames = new Dictionary<string, UeFlags>(StringComparer.OrdinalIgnoreCase)
        {
            ["UCLASS"] = UeFlags.UClass,
            ["USTRUCT"] = UeFlags.UStruct,
            ["UENUM"] = UeFlags.UEnum,
            ["UINTERFACE"] = UeFlags.UInterface,
            ["UFUNCTION"] = UeFlags.UFunction,
            ["UPROPERTY"] = UeFlags.UProperty,
            ["UDELEGATE"] = UeFlags.UDelegate,
            ["UMETA"] = UeFlags.UMeta,
            ["REFLECTED"] = UeFlags.UClass | UeFlags.UStruct | UeFlags.UEnum | UeFlags.UInterface | UeFlags.UFunction | UeFlags.UProperty | UeFlags.UDelegate,
        };

        public static UeFlags ParseUe(List<string> ue)
        {
            var f = UeFlags.None;
            foreach (var u in ue)
            {
                if (!UeNames.TryGetValue(u, out var v)) throw ToolArgs.Invalid($"unknown UE marker '{u}'. Valid: {string.Join(", ", UeNames.Keys)}");
                f |= v;
            }
            return f;
        }

        private static bool IsProjectOrigin(ItemOrigin o) => o == ItemOrigin.Project || o == ItemOrigin.ProjectPlugin;

        // ================================================================== path helpers

        /// <summary>Absolute path for a tool argument: as-is when rooted, else under the solution dir or the project root.</summary>
        public string ResolvePath(string path)
        {
            if (string.IsNullOrWhiteSpace(path)) throw ToolArgs.Invalid("'path' must not be empty");
            path = path.Trim().Trim('"');
            if (Path.IsPathRooted(path)) return Path.GetFullPath(path);
            var candidates = new List<string> { Path.GetFullPath(Path.Combine(_index.SolutionDir, path)) };
            string projectRoot = _index.Offline.Workspace()?.ProjectRoot;
            if (!string.IsNullOrEmpty(projectRoot)) candidates.Add(Path.GetFullPath(Path.Combine(projectRoot, path)));
            return candidates.FirstOrDefault(File.Exists) ?? candidates[0];
        }

        private CaretRequest Caret(ToolArgs a)
        {
            string path = ResolvePath(a.String("path", required: true));
            int line = a.Int("line", 0, 1, int.MaxValue);
            int column = a.Int("column", 1, 1, int.MaxValue);
            if (line == 0) throw ToolArgs.Invalid("'line' is required with 'path'");
            return new CaretRequest { FilePath = path, Line = line - 1, Column = column - 1 };
        }

        private static string RequireOne(ToolArgs a, string named)
        {
            bool hasName = a.Has(named), hasCaret = a.Has("path");
            if (hasName == hasCaret) throw ToolArgs.Invalid($"pass either '{named}' or 'path' + 'line' (+ 'column')");
            return hasName ? named : "path";
        }

        // ================================================================== search_symbols

        private ToolDefinition SearchSymbolsTool() => Define(
            "search_symbols", "Search C++ symbols",
            "Search the UEToVS index of C++ declarations (project + engine source): classes, structs, enums, functions, methods, fields, " +
            "macros, delegates... Much faster and more precise than grep: matches identifiers only (fuzzy: prefix, CamelHumps like 'AMC' → " +
            "AMyCharacter, substring), ranked, with UE reflection markers. Use match='exact' to list every declaration/definition of a name. " +
            "Results: name, kind, container, file, line/col (1-based), sig, ue (UCLASS/UFUNCTION/UPROPERTY...), spec (macro specifiers), module, def.",
            Schema(
                "\"query\":{\"type\":\"string\",\"description\":\"Identifier or fragment. 'Outer::Name' restricts the container.\"}," +
                "\"match\":{\"type\":\"string\",\"enum\":[\"fuzzy\",\"exact\"],\"description\":\"fuzzy (default) or exact name.\"}," +
                "\"kinds\":{\"type\":\"array\",\"items\":{\"type\":\"string\"},\"description\":\"" + KindsDesc + "\"}," +
                "\"ue\":{\"type\":\"array\",\"items\":{\"type\":\"string\"},\"description\":\"Only symbols with any of these UE markers: UCLASS, USTRUCT, UENUM, UINTERFACE, UFUNCTION, UPROPERTY, UDELEGATE, UMETA, REFLECTED (= any).\"}," +
                "\"container\":{\"type\":\"string\",\"description\":\"Enclosing class/namespace (fuzzy for match=fuzzy, exact suffix for match=exact).\"}," +
                "\"scope\":{\"type\":\"string\",\"enum\":[\"project\",\"all\"],\"description\":\"" + ScopeDesc + "\"}," +
                PagingProps, "query"),
            SearchSymbolsAsync);

        private async Task<ToolOutput> SearchSymbolsAsync(ToolArgs a, CancellationToken ct)
        {
            string query = a.String("query", required: true, maxLength: 256);
            string match = a.Choice("match", "fuzzy", "fuzzy", "exact");
            var kinds = ParseKinds(a.StringList("kinds"));
            var ue = ParseUe(a.StringList("ue"));
            string container = a.String("container", maxLength: 256);
            bool all = a.Choice("scope", "project", "project", "all") == "all";
            var (limit, offset) = Paging(a);

            var (name, qual) = CppText.SplitQualified(query);
            if (string.IsNullOrEmpty(name)) throw ToolArgs.Invalid("'query' has no identifier");
            container ??= qual;

            List<SymbolInfo> ordered;
            string source, note;
            bool ready;
            int hidden = 0;
            if (match == "exact")
            {
                var found = await _index.FindSymbolsAsync(name, container, ct).ConfigureAwait(false);
                source = found.Source;
                note = found.Note;
                ready = found.IndexReady;
                var filtered = found.Value.Where(s => (kinds == null || kinds.Contains(s.Kind)) && (ue == UeFlags.None || (s.Ue & ue) != 0)).ToList();
                hidden = all ? 0 : filtered.Count(s => !IsProjectOrigin(s.Origin));
                ordered = filtered.Where(s => all || IsProjectOrigin(s.Origin))
                    .OrderBy(s => IsProjectOrigin(s.Origin) ? 0 : 1).ThenBy(s => s.IsDefinition ? 1 : 0)
                    .ThenBy(s => s.Location?.FilePath, StringComparer.OrdinalIgnoreCase).ThenBy(s => s.Location?.Line ?? 0).ToList();
            }
            else
            {
                bool wantTypes = kinds == null || kinds.Any(CodeIndex.IsType);
                bool wantSymbols = kinds == null || kinds.Any(k => !CodeIndex.IsType(k));
                int fetch = Math.Min(MaxFetch, (offset + limit + 1) * (kinds == null ? 1 : 3));
                var resp = await _index.SearchAsync(new SearchRequest
                {
                    Pattern = query.Contains("::") ? name : query,
                    Container = container,
                    IncludeTypes = wantTypes,
                    IncludeSymbols = wantSymbols,
                    IncludeFiles = false,
                    IncludeAssets = false,
                    IncludeNonProject = all,
                    RequireUe = ue,
                    MaxResultsPerCategory = fetch,
                }, ct).ConfigureAwait(false);
                source = resp.Source;
                note = resp.Note;
                ready = resp.IndexReady;
                hidden = resp.Value.NonProjectHitCount;
                ordered = resp.Value.Hits.Where(h => h.Symbol != null && (kinds == null || kinds.Contains(h.Symbol.Kind)))
                    .OrderByDescending(h => h.Score).Select(h => h.Symbol).ToList();
            }
            var (page, more) = Slice(ordered, offset, limit);
            if (!ready) note = Join(note, "The index is still being built; results may be incomplete.");
            return new ToolOutput
            {
                Payload = ResultJson.Page(source, ready, page.Select(s => (JsonNode)ResultJson.Symbol(s)).ToList(), offset, more, note, all ? null : hidden),
                ResultCount = page.Count,
                Source = source,
            };
        }

        private static string Join(string a, string b) => string.IsNullOrEmpty(a) ? b : string.IsNullOrEmpty(b) ? a : a + " " + b;

        // ================================================================== search_files

        private ToolDefinition SearchFilesTool() => Define(
            "search_files", "Search indexed files",
            "Fuzzy file-name search over the UEToVS index (project, plugins and, with scope='all', engine source) — no directory walk. " +
            "'MyCharH' finds MyCharacter.h. Results: name, path (absolute), module, origin.",
            Schema(
                "\"query\":{\"type\":\"string\",\"description\":\"File name or fragment (no folders).\"}," +
                "\"extensions\":{\"type\":\"array\",\"items\":{\"type\":\"string\"},\"description\":\"Keep only these extensions, e.g. [\\\".h\\\", \\\".cpp\\\"].\"}," +
                "\"scope\":{\"type\":\"string\",\"enum\":[\"project\",\"all\"],\"description\":\"" + ScopeDesc + "\"}," +
                PagingProps, "query"),
            SearchFilesAsync);

        private async Task<ToolOutput> SearchFilesAsync(ToolArgs a, CancellationToken ct)
        {
            string query = a.String("query", required: true, maxLength: 256);
            var exts = a.StringList("extensions").Select(e => e.StartsWith(".") ? e : "." + e).ToList();
            bool all = a.Choice("scope", "project", "project", "all") == "all";
            var (limit, offset) = Paging(a);
            int fetch = Math.Min(MaxFetch, (offset + limit + 1) * (exts.Count > 0 ? 4 : 1));
            var resp = await _index.SearchAsync(new SearchRequest
            {
                Pattern = query,
                IncludeTypes = false,
                IncludeSymbols = false,
                IncludeFiles = true,
                IncludeNonProject = all,
                MaxResultsPerCategory = fetch,
            }, ct).ConfigureAwait(false);
            var files = resp.Value.Hits.Where(h => h.File != null)
                .Where(h => exts.Count == 0 || exts.Any(e => h.File.Path.EndsWith(e, StringComparison.OrdinalIgnoreCase)))
                .OrderByDescending(h => h.Score).Select(h => h.File).ToList();
            var (page, more) = Slice(files, offset, limit);
            return new ToolOutput
            {
                Payload = ResultJson.Page(resp.Source, resp.IndexReady, page.Select(f => (JsonNode)ResultJson.File(f)).ToList(), offset, more, resp.Note,
                    all ? null : resp.Value.NonProjectHitCount),
                ResultCount = page.Count,
                Source = resp.Source,
            };
        }

        // ================================================================== search_assets

        private ToolDefinition SearchAssetsTool() => Define(
            "search_assets", "Search Unreal assets (Blueprints, maps, materials...)",
            "Fuzzy search over the project's Unreal assets (.uasset/.umap under Content, plugin content; engine content with scope='all'). " +
            "'BPHero' finds BP_Hero. Filter by class: 'Blueprint', 'WidgetBlueprint', 'Material', 'World', 'DataTable'... (substring match on the " +
            "known class, or 'classGuess' inferred from UE naming prefixes such as BP_, WBP_, M_, T_, SM_, DT_). Results: name, objectPath (/Game/...), file, class or classGuess.",
            Schema(
                "\"query\":{\"type\":\"string\",\"description\":\"Asset name or fragment.\"}," +
                "\"class\":{\"type\":\"string\",\"description\":\"Asset class filter (case-insensitive substring), e.g. Blueprint, AnimBlueprint, Material, World.\"}," +
                "\"scope\":{\"type\":\"string\",\"enum\":[\"project\",\"all\"],\"description\":\"" + ScopeDesc + "\"}," +
                PagingProps, "query"),
            SearchAssetsAsync);

        private async Task<ToolOutput> SearchAssetsAsync(ToolArgs a, CancellationToken ct)
        {
            string query = a.String("query", required: true, maxLength: 256);
            string cls = a.String("class", maxLength: 128);
            bool all = a.Choice("scope", "project", "project", "all") == "all";
            var (limit, offset) = Paging(a);
            int fetch = string.IsNullOrWhiteSpace(cls) ? Math.Min(MaxFetch, offset + limit + 1) : MaxFetch;
            var resp = await _index.SearchAsync(new SearchRequest
            {
                Pattern = query,
                IncludeTypes = false,
                IncludeSymbols = false,
                IncludeFiles = false,
                IncludeAssets = true,
                IncludeNonProject = all,
                MaxResultsPerCategory = fetch,
            }, ct).ConfigureAwait(false);
            var assets = resp.Value.Hits.Where(h => h.Asset != null && AssetClassGuess.Matches(h.Asset, cls))
                .OrderByDescending(h => h.Score).Select(h => h.Asset).ToList();
            var (page, more) = Slice(assets, offset, limit);
            return new ToolOutput
            {
                Payload = ResultJson.Page(resp.Source, resp.IndexReady, page.Select(x => (JsonNode)ResultJson.Asset(x)).ToList(), offset, more, resp.Note,
                    all ? null : resp.Value.NonProjectHitCount),
                ResultCount = page.Count,
                Source = resp.Source,
            };
        }

        // ================================================================== get_file_outline

        private ToolDefinition FileOutlineTool() => Define(
            "get_file_outline", "File outline",
            "Declarations of one source file in source order (types, members, functions, macros) with 1-based line/col, signature and UE markers. " +
            "Cheaper than reading the file when you need its structure or a line number to read around.",
            Schema(
                "\"path\":{\"type\":\"string\",\"description\":\"Absolute path, or relative to the solution/project folder.\"}," +
                "\"kinds\":{\"type\":\"array\",\"items\":{\"type\":\"string\"},\"description\":\"" + KindsDesc + "\"}," +
                "\"limit\":{\"type\":\"integer\",\"minimum\":1,\"maximum\":2000,\"description\":\"Max items (default 300).\"}," +
                "\"offset\":{\"type\":\"integer\",\"minimum\":0,\"maximum\":100000,\"description\":\"Items to skip.\"}", "path"),
            FileOutlineAsync);

        private async Task<ToolOutput> FileOutlineAsync(ToolArgs a, CancellationToken ct)
        {
            string path = ResolvePath(a.String("path", required: true));
            var kinds = ParseKinds(a.StringList("kinds"));
            int limit = a.Int("limit", 300, 1, 2000);
            int offset = a.Int("offset", 0, 0, 100000);
            var outline = await _index.GetFileSymbolsAsync(path, ct).ConfigureAwait(false);
            var list = outline.Value.Where(s => kinds == null || kinds.Contains(s.Kind))
                .OrderBy(s => s.Location?.Line ?? 0).ThenBy(s => s.Location?.Column ?? 0).ToList();
            var (page, more) = Slice(list, offset, limit);
            string note = outline.Note;
            if (outline.Value.Count == 0)
                note = Join(note, File.Exists(path) ? "File is not in the index (not a C/C++ source under an indexed root, or not parsed yet)." : "File does not exist: " + path);
            var items = page.Select(s =>
            {
                var o = ResultJson.Symbol(s);
                o.Remove("file"); // same for every item
                return (JsonNode)o;
            }).ToList();
            var payload = ResultJson.Page(outline.Source, outline.IndexReady, items, offset, more, note);
            payload.Insert(0, "file", path);
            return new ToolOutput { Payload = payload, ResultCount = page.Count, Source = outline.Source };
        }

        // ================================================================== go_to_definition

        private ToolDefinition GoToDefinitionTool() => Define(
            "go_to_definition", "Go to definition",
            "Where is it defined/declared? Pass a symbol name ('AMyCharacter', 'AMyCharacter::BeginPlay', 'UE_LOG') or a position (path + 1-based line/column) " +
            "to resolve the identifier written there. Returns ranked targets (definition first unless kind='declaration') with file, line, col, signature.",
            Schema(
                "\"symbol\":{\"type\":\"string\",\"description\":\"Name, optionally qualified 'Outer::Name'.\"}," + CaretProps + "," +
                "\"kind\":{\"type\":\"string\",\"enum\":[\"definition\",\"declaration\",\"implementation\"],\"description\":\"definition (default): body first; declaration: header first; implementation: overrides/derived (engine only).\"}," +
                "\"limit\":{\"type\":\"integer\",\"minimum\":1,\"maximum\":100,\"description\":\"Max targets (default 10).\"}"),
            GoToDefinitionAsync);

        private async Task<ToolOutput> GoToDefinitionAsync(ToolArgs a, CancellationToken ct)
        {
            string mode = RequireOne(a, "symbol");
            string kindArg = a.Choice("kind", "definition", "definition", "declaration", "implementation");
            var kind = kindArg == "declaration" ? NavigationKind.Declaration : kindArg == "implementation" ? NavigationKind.Implementation : NavigationKind.Definition;
            int limit = a.Int("limit", 10, 1, 100);
            JsonObject payload;
            string source;
            int count;
            if (mode == "symbol")
            {
                var (name, container) = CppText.SplitQualified(a.String("symbol", required: true, maxLength: 512));
                var found = await _index.FindSymbolsAsync(name, container, ct).ConfigureAwait(false);
                bool preferDecl = kind == NavigationKind.Declaration;
                var ranked = found.Value
                    .OrderBy(s => s.IsDefinition == preferDecl ? 1 : 0)
                    .ThenBy(s => IsProjectOrigin(s.Origin) ? 0 : 1)
                    .ThenBy(s => CodeIndex.IsType(s.Kind) || s.Kind == SymbolKind.Function || s.Kind == SymbolKind.Method ? 0 : 1)
                    .ThenBy(s => s.Location?.FilePath, StringComparer.OrdinalIgnoreCase).ThenBy(s => s.Location?.Line ?? 0)
                    .Take(limit).ToList();
                source = found.Source;
                count = ranked.Count;
                var targets = new JsonArray();
                foreach (var s in ranked)
                {
                    var o = ResultJson.Symbol(s);
                    o["role"] = s.IsDefinition ? "definition" : "declaration";
                    targets.Add(o);
                }
                payload = new JsonObject { ["source"] = source, ["symbol"] = a.String("symbol"), ["count"] = count };
                if (found.Value.Count > ranked.Count) payload["more"] = found.Value.Count - ranked.Count;
                string note = found.Note;
                if (count == 0) note = Join(note, "Not found by exact name. Try search_symbols for fuzzy matching.");
                if (!string.IsNullOrEmpty(note)) payload["note"] = note;
                payload["targets"] = targets;
            }
            else
            {
                var caret = Caret(a);
                var nav = await _index.NavigateAsync(caret, kind, ct).ConfigureAwait(false);
                var v = nav.Value;
                source = nav.Source;
                var targets = new JsonArray();
                foreach (var t in v.Targets.Take(limit))
                {
                    if (t.Symbol == null) continue;
                    var o = ResultJson.Symbol(t.Symbol);
                    o["role"] = RoleName(t.Role);
                    targets.Add(o);
                }
                count = targets.Count;
                payload = new JsonObject
                {
                    ["source"] = source,
                    ["word"] = v.Word,
                    ["confidence"] = v.Confidence.ToString().ToLowerInvariant(),
                    ["count"] = count,
                };
                if (!string.IsNullOrEmpty(v.Reason)) payload["reason"] = v.Reason;
                if (v.Targets.Count > limit) payload["more"] = v.Targets.Count - limit;
                string note = nav.Note;
                if (count == 0 && !string.IsNullOrEmpty(v.Word)) note = Join(note, "No declaration of '" + v.Word + "' in the index (macro-generated, local variable or external?).");
                if (!string.IsNullOrEmpty(note)) payload["note"] = note;
                payload["targets"] = targets;
            }
            return new ToolOutput { Payload = payload, ResultCount = count, Source = source };
        }

        private static string RoleName(NavigationTargetRole r) => r switch
        {
            NavigationTargetRole.Declaration => "declaration",
            NavigationTargetRole.Definition => "definition",
            NavigationTargetRole.Override => "override",
            NavigationTargetRole.UeCounterpart => "ue_counterpart",
            NavigationTargetRole.DerivedType => "derived_type",
            _ => r.ToString().ToLowerInvariant(),
        };

        // ================================================================== find_references

        private ToolDefinition FindReferencesTool() => Define(
            "find_references", "Find references",
            "Every use of a symbol across the indexed code, grouped by file, with 1-based line/col, kind (read/write/call/type_use) and the source line. " +
            "Pass a symbol name or a position (path + line/column). Answers from the precomputed occurrence index, so it does not scan files like grep; " +
            "it matches identifiers only (no hits in comments or strings).",
            Schema(
                "\"symbol\":{\"type\":\"string\",\"description\":\"Name, optionally qualified 'Outer::Name'.\"}," + CaretProps + "," +
                "\"include_declarations\":{\"type\":\"boolean\",\"description\":\"Include declarations/definitions (default true).\"}," +
                "\"scope\":{\"type\":\"string\",\"enum\":[\"project\",\"all\"],\"description\":\"" + ScopeDesc + "\"}," +
                "\"limit\":{\"type\":\"integer\",\"minimum\":1,\"maximum\":1000,\"description\":\"Max hits (default 100).\"}," +
                "\"offset\":{\"type\":\"integer\",\"minimum\":0,\"maximum\":5000,\"description\":\"Hits to skip.\"}"),
            FindReferencesAsync);

        private async Task<ToolOutput> FindReferencesAsync(ToolArgs a, CancellationToken ct)
        {
            string mode = RequireOne(a, "symbol");
            bool includeDecl = a.Bool("include_declarations", true);
            bool all = a.Choice("scope", "project", "project", "all") == "all";
            int limit = a.Int("limit", 100, 1, 1000);
            int offset = a.Int("offset", 0, 0, 5000);
            var req = new ReferencesRequest { IncludeDeclarations = includeDecl, IncludeNonProject = all, MaxResults = Math.Min(5000, offset + limit + 1) };
            string fallbackName;
            if (mode == "symbol")
            {
                var (name, container) = CppText.SplitQualified(a.String("symbol", required: true, maxLength: 512));
                fallbackName = name;
                var found = await _index.FindSymbolsAsync(name, container, ct).ConfigureAwait(false);
                req.Symbol = found.Value.OrderBy(s => s.IsDefinition ? 1 : 0).ThenBy(s => IsProjectOrigin(s.Origin) ? 0 : 1).FirstOrDefault()
                             ?? new SymbolInfo { Name = name, Container = container };
            }
            else
            {
                req.Caret = Caret(a);
                var text = _index.Texts.Get(req.Caret.FilePath);
                if (text == null) throw new ToolException("file_not_found", "Cannot read " + req.Caret.FilePath);
                fallbackName = CppText.IdentifierAt(text.Line(req.Caret.Line), req.Caret.Column)?.Name;
                if (fallbackName == null) throw ToolArgs.Invalid("no identifier at that position");
            }
            var refs = await _index.FindReferencesAsync(req, fallbackName, ct).ConfigureAwait(false);
            string projectRoot = all ? null : _index.Offline.Workspace()?.ProjectRoot;
            var hits = refs.Value.Hits
                .Where(h => includeDecl || (h.Kind & (ReferenceKind.Declaration | ReferenceKind.Definition)) == 0)
                .Where(h => all || refs.Source == Sources.Engine || projectRoot == null || IsUnder(h.Location?.FilePath, projectRoot))
                .ToList();
            var (page, more) = Slice(hits, offset, limit);
            var files = new JsonArray();
            foreach (var group in page.GroupBy(h => h.Location?.FilePath ?? "", StringComparer.OrdinalIgnoreCase))
            {
                var arr = new JsonArray();
                foreach (var h in group)
                {
                    var o = new JsonObject { ["line"] = (h.Location?.Line ?? 0) + 1, ["col"] = (h.Location?.Column ?? 0) + 1 };
                    string kind = RefKind(h.Kind);
                    if (kind != null) o["kind"] = kind;
                    if (!string.IsNullOrEmpty(h.Context)) o["context"] = h.Context;
                    if (!string.IsNullOrEmpty(h.LineText)) o["text"] = h.LineText;
                    arr.Add(o);
                }
                files.Add(new JsonObject { ["file"] = group.Key, ["hits"] = arr });
            }
            var payload = new JsonObject
            {
                ["source"] = refs.Source,
                ["symbol"] = refs.Value.Symbol != null ? ResultJson.Symbol(refs.Value.Symbol, includeSignature: false) : (JsonNode)fallbackName,
                ["count"] = page.Count,
                ["offset"] = offset,
                ["hasMore"] = more || (refs.Value.Truncated && hits.Count >= offset + limit),
            };
            if (more) payload["nextOffset"] = offset + page.Count;
            if (!string.IsNullOrEmpty(refs.Note)) payload["note"] = refs.Note;
            payload["files"] = files;
            return new ToolOutput { Payload = payload, ResultCount = page.Count, Source = refs.Source };
        }

        private static bool IsUnder(string path, string root) =>
            path != null && path.Replace('\\', '/').StartsWith(root.Replace('\\', '/').TrimEnd('/') + "/", StringComparison.OrdinalIgnoreCase);

        private static string RefKind(ReferenceKind k)
        {
            if (k == ReferenceKind.None) return null;
            var parts = new List<string>();
            if ((k & ReferenceKind.Definition) != 0) parts.Add("definition");
            if ((k & ReferenceKind.Declaration) != 0) parts.Add("declaration");
            if ((k & ReferenceKind.Call) != 0) parts.Add("call");
            if ((k & ReferenceKind.Write) != 0) parts.Add("write");
            if ((k & ReferenceKind.Read) != 0 && (k & ReferenceKind.Call) == 0) parts.Add("read");
            if ((k & ReferenceKind.TypeUse) != 0) parts.Add("type_use");
            return string.Join(",", parts);
        }

        // ================================================================== get_type_hierarchy

        private ToolDefinition TypeHierarchyTool() => Define(
            "get_type_hierarchy", "Type hierarchy",
            "Base classes and/or derived classes of a C++ type as a tree (e.g. AMyCharacter → ACharacter → APawn → AActor → UObject). " +
            "Pass type_name ('AMyCharacter' or 'Ns::FFoo') or a position on the type.",
            Schema(
                "\"type_name\":{\"type\":\"string\",\"description\":\"Type name, optionally qualified.\"}," + CaretProps + "," +
                "\"direction\":{\"type\":\"string\",\"enum\":[\"bases\",\"derived\",\"both\"],\"description\":\"Default both.\"}," +
                "\"max_depth\":{\"type\":\"integer\",\"minimum\":1,\"maximum\":16,\"description\":\"Tree depth (default 4).\"}," +
                "\"max_nodes\":{\"type\":\"integer\",\"minimum\":1,\"maximum\":2000,\"description\":\"Total nodes (default 200).\"}," +
                "\"scope\":{\"type\":\"string\",\"enum\":[\"project\",\"all\"],\"description\":\"Where to look for derived types. " + ScopeDesc + "\"}"),
            TypeHierarchyAsync);

        private async Task<ToolOutput> TypeHierarchyAsync(ToolArgs a, CancellationToken ct)
        {
            string mode = RequireOne(a, "type_name");
            string dir = a.Choice("direction", "both", "bases", "derived", "both");
            var req = new TypeHierarchyRequest
            {
                Direction = dir == "bases" ? HierarchyDirection.Bases : dir == "derived" ? HierarchyDirection.Derived : HierarchyDirection.Both,
                MaxDepth = a.Int("max_depth", 4, 1, 16),
                MaxNodes = a.Int("max_nodes", 200, 1, 2000),
            };
            if (mode == "type_name")
            {
                var (name, container) = CppText.SplitQualified(a.String("type_name", required: true, maxLength: 512));
                req.TypeName = name;
                req.Container = container;
            }
            else
            {
                req.Caret = Caret(a);
                var text = _index.Texts.Get(req.Caret.FilePath);
                var ident = text == null ? null : CppText.IdentifierAt(text.Line(req.Caret.Line), req.Caret.Column);
                req.TypeName = ident?.Name; // used only when the engine cannot resolve the caret
                req.Container = ident?.Qualifier;
                if (req.TypeName == null) throw ToolArgs.Invalid("no identifier at that position");
            }
            bool allScope = a.Choice("scope", "project", "project", "all") == "all";
            var th = await _index.GetTypeHierarchyAsync(req, ct, allScope).ConfigureAwait(false);
            int nodes = 0;
            JsonArray Tree(List<TypeHierarchyNode> list)
            {
                var arr = new JsonArray();
                foreach (var n in list ?? new List<TypeHierarchyNode>())
                {
                    nodes++;
                    var o = new JsonObject { ["name"] = n.Symbol?.Name ?? n.DisplayName };
                    if (n.DisplayName != null && n.Symbol != null && n.DisplayName != n.Symbol.Name) o["written"] = n.DisplayName;
                    if (n.Symbol != null)
                    {
                        if (!string.IsNullOrEmpty(n.Symbol.Container)) o["container"] = n.Symbol.Container;
                        ResultJson.AddLocation(o, n.Symbol.Location);
                        if (n.Symbol.Ue != UeFlags.None) o["ue"] = ResultJson.UeNames(n.Symbol.Ue);
                    }
                    else o["external"] = true;
                    if (n.Truncated) o["truncated"] = true;
                    if (n.Children != null && n.Children.Count > 0) o[dirNameOf(n)] = Tree(n.Children);
                    arr.Add(o);
                }
                return arr;
            }
            string current = "bases";
            string dirNameOf(TypeHierarchyNode _) => current;
            var payload = new JsonObject { ["source"] = th.Source };
            if (th.Value.Root != null) payload["root"] = ResultJson.Symbol(th.Value.Root);
            if (req.Direction != HierarchyDirection.Derived)
            {
                current = "bases";
                payload["bases"] = Tree(th.Value.Bases);
            }
            if (req.Direction != HierarchyDirection.Bases)
            {
                current = "derived";
                payload["derived"] = Tree(th.Value.Derived);
            }
            payload["nodeCount"] = nodes;
            string note = th.Note;
            if (th.Value.Root?.Location == null) note = Join(note, "Type '" + req.TypeName + "' not found in the index.");
            if (!string.IsNullOrEmpty(note)) payload["note"] = note;
            return new ToolOutput { Payload = payload, ResultCount = nodes, Source = th.Source };
        }

        // ================================================================== get_members

        private ToolDefinition MembersTool() => Define(
            "get_members", "Members of a type",
            "Fields, methods, nested types and enum values of a class/struct/enum, with signatures and UE markers (UPROPERTY/UFUNCTION " +
            "specifiers). include_inherited adds members of the base classes (declaring type in 'container').",
            Schema(
                "\"type_name\":{\"type\":\"string\",\"description\":\"Type name, optionally qualified.\"}," +
                "\"include_inherited\":{\"type\":\"boolean\",\"description\":\"Also list base-class members (default false).\"}," +
                "\"kinds\":{\"type\":\"array\",\"items\":{\"type\":\"string\"},\"description\":\"" + KindsDesc + "\"}," +
                "\"ue\":{\"type\":\"array\",\"items\":{\"type\":\"string\"},\"description\":\"Only members with any of these UE markers (e.g. UFUNCTION, UPROPERTY).\"}," +
                "\"limit\":{\"type\":\"integer\",\"minimum\":1,\"maximum\":2000,\"description\":\"Max members (default 200).\"}," +
                "\"offset\":{\"type\":\"integer\",\"minimum\":0,\"maximum\":10000,\"description\":\"Members to skip.\"}", "type_name"),
            MembersAsync);

        private async Task<ToolOutput> MembersAsync(ToolArgs a, CancellationToken ct)
        {
            var (name, container) = CppText.SplitQualified(a.String("type_name", required: true, maxLength: 512));
            bool inherited = a.Bool("include_inherited", false);
            var kinds = ParseKinds(a.StringList("kinds"));
            var ue = ParseUe(a.StringList("ue"));
            int limit = a.Int("limit", 200, 1, 2000);
            int offset = a.Int("offset", 0, 0, 10000);
            var m = await _index.GetMembersAsync(new MembersRequest { TypeName = name, Container = container, IncludeInherited = inherited, MaxResults = 5000 }, ct)
                .ConfigureAwait(false);
            var list = m.Value.Members.Where(s => (kinds == null || kinds.Contains(s.Kind)) && (ue == UeFlags.None || (s.Ue & ue) != 0)).ToList();
            var (page, more) = Slice(list, offset, limit);
            var payload = ResultJson.Page(m.Source, m.IndexReady, page.Select(s => (JsonNode)ResultJson.Symbol(s)).ToList(), offset, more, m.Note);
            if (m.Value.Type != null) payload.Insert(0, "type", ResultJson.Symbol(m.Value.Type));
            return new ToolOutput { Payload = payload, ResultCount = page.Count, Source = m.Source };
        }

        // ================================================================== index_status

        private ToolDefinition IndexStatusTool() => Define(
            "index_status", "Index status",
            "State of the UEToVS index for this solution: whether the engine is running, indexing phase/progress, counts of files, symbols and assets, " +
            "project/engine roots and UE version. Call it when results look incomplete.",
            Schema(""),
            IndexStatusAsync);

        private async Task<ToolOutput> IndexStatusAsync(ToolArgs a, CancellationToken ct)
        {
            var st = await _index.GetStatusAsync(ct).ConfigureAwait(false);
            var s = st.Value ?? new EngineStatus();
            var payload = new JsonObject
            {
                ["source"] = st.Source,
                ["solutionDir"] = _index.SolutionDir,
                ["phase"] = s.Phase.ToString(),
                ["ready"] = st.IndexReady,
                ["files"] = s.FileCount,
                ["symbols"] = s.SymbolCount,
            };
            if (s.AssetCount > 0) payload["assets"] = s.AssetCount;
            if (s.FilesToParse > 0) payload["progress"] = new JsonObject { ["parsed"] = s.FilesParsed, ["total"] = s.FilesToParse };
            if (s.ProjectRoot != null) payload["projectRoot"] = s.ProjectRoot;
            if (s.EngineRoot != null) payload["engineRoot"] = s.EngineRoot;
            if (s.UnrealVersion != null) payload["unrealVersion"] = s.UnrealVersion;
            if (!string.IsNullOrEmpty(s.LastError)) payload["lastError"] = s.LastError;
            var d = _index.Engine.Discovery;
            var engine = new JsonObject { ["running"] = st.Source == Sources.Engine };
            if (d != null)
            {
                engine["pid"] = d.Pid;
                engine["version"] = d.EngineVersion;
            }
            if (st.Source != Sources.Engine && _index.Engine.LastError != null) engine["reason"] = _index.Engine.LastError;
            payload["engine"] = engine;
            var info = _index.Offline.Info;
            if (info != null && info.ManifestFound)
            {
                var idx = new JsonObject { ["segments"] = info.SegmentsOpened };
                if (info.ManifestWrittenUtc > DateTime.MinValue) idx["writtenUtc"] = info.ManifestWrittenUtc.ToString("u");
                if (info.Rejected.Count > 0) idx["rejected"] = new JsonArray(info.Rejected.Select(r => (JsonNode)r).ToArray());
                payload["onDisk"] = idx;
            }
            payload["mcpVersion"] = EngineConnection.Version;
            payload["dataSource"] = _index.Mode.ToString();
            if (!string.IsNullOrEmpty(st.Note)) payload["note"] = st.Note;
            return new ToolOutput { Payload = payload, ResultCount = 1, Source = st.Source };
        }
    }
}
