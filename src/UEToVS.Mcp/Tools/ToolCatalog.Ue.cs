using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using UEToVS.Contracts;
using UEToVS.Core.Unreal;
using UEToVS.Engine.IntelliSense;
using UEToVS.Mcp.Data;

namespace UEToVS.Mcp.Tools
{
    /// <summary>
    /// Unreal-specific tools: get_reflection_info, get_module_info, suggest_include, search_config, find_delegate_bindings,
    /// plus the Lua ↔ C++ link used by find_references. They answer from the same sources as the other tools (running
    /// engine first, the on-disk index otherwise) plus small files read on demand (Build.cs, descriptors, Config/*.ini,
    /// the C++ files the index points to); nothing scans the source tree or .uasset files.
    /// </summary>
    public sealed partial class ToolCatalog
    {
        private ModuleResolver _modules;
        private ConfigSearch _config;

        private ModuleResolver Modules => LazyInitializer.EnsureInitialized(ref _modules, () => new ModuleResolver(_index));
        private ConfigSearch Config => LazyInitializer.EnsureInitialized(ref _config, () => new ConfigSearch(_index));

        private const string LuaNameNote =
            "Lua functions with the same name (UnLua overrides a BlueprintImplementableEvent/BlueprintNativeEvent by defining it in the bound Lua module; " +
            "matched by name only). Lua call sites are not indexed: grep ':Name(' / '.Name(' in the Lua folders for callers.";

        private void RegisterUeTools()
        {
            Register(ReflectionInfoTool());
            Register(ModuleInfoTool());
            Register(SuggestIncludeTool());
            Register(SearchConfigTool());
            Register(DelegateBindingsTool());
        }

        // ================================================================== get_reflection_info

        private ToolDefinition ReflectionInfoTool() => Define(
            "get_reflection_info", "UE reflection info of a type",
            "Every UPROPERTY and UFUNCTION of a UCLASS/USTRUCT with its specifiers split out (flags, key=value, meta=(...)), replication " +
            "(Replicated / ReplicatedUsing=OnRep_X) and RPCs (Server/Client/NetMulticast, Reliable, WithValidation), plus the type's own UCLASS/USTRUCT " +
            "specifiers. Filter with 'specifiers' (all must be present, e.g. [\"BlueprintReadWrite\"] or a meta key like \"AllowPrivateAccess\"), " +
            "'kind' (property/function) or 'replicated_only'. UFUNCTIONs list same-named Lua functions (UnLua overrides) when a Lua index exists.",
            Schema(
                "\"type_name\":{\"type\":\"string\",\"description\":\"Class or struct, e.g. AMyCharacter or 'Ns::FFoo'.\"}," +
                "\"kind\":{\"type\":\"string\",\"enum\":[\"all\",\"property\",\"function\"],\"description\":\"Default all.\"}," +
                "\"specifiers\":{\"type\":\"array\",\"items\":{\"type\":\"string\"},\"description\":\"Keep members having all of these specifiers or meta keys (case-insensitive).\"}," +
                "\"replicated_only\":{\"type\":\"boolean\",\"description\":\"Only replicated properties and RPC functions (default false).\"}," +
                "\"include_inherited\":{\"type\":\"boolean\",\"description\":\"Also members of the base classes (default false).\"}," +
                "\"limit\":{\"type\":\"integer\",\"minimum\":1,\"maximum\":500,\"description\":\"Max members (default 100).\"}," +
                "\"offset\":{\"type\":\"integer\",\"minimum\":0,\"maximum\":10000,\"description\":\"Members to skip.\"}", "type_name"),
            ReflectionInfoAsync);

        private async Task<ToolOutput> ReflectionInfoAsync(ToolArgs a, CancellationToken ct)
        {
            var (name, container) = CppText.SplitQualified(a.String("type_name", required: true, maxLength: 512));
            string kind = a.Choice("kind", "all", "all", "property", "function");
            var required = a.StringList("specifiers");
            bool replicatedOnly = a.Bool("replicated_only", false);
            bool inherited = a.Bool("include_inherited", false);
            int limit = a.Int("limit", 100, 1, 500);
            int offset = a.Int("offset", 0, 0, 10000);

            var m = await _index.GetMembersAsync(new MembersRequest { TypeName = name, Container = container, IncludeInherited = inherited, MaxResults = 5000 }, ct)
                .ConfigureAwait(false);
            var type = m.Value.Type;
            var members = new List<(SymbolInfo Sym, UeSpecifiers Spec, bool IsFunction)>();
            int properties = 0, functions = 0, replicated = 0, rpcs = 0;
            foreach (var s in m.Value.Members)
            {
                bool isProp = (s.Ue & UeFlags.UProperty) != 0, isFunc = (s.Ue & UeFlags.UFunction) != 0;
                if (!isProp && !isFunc) continue;
                var spec = UeSpecifiers.Parse(s.Specifiers);
                if (isProp) properties++;
                else functions++;
                if (isProp && spec.IsReplicated) replicated++;
                if (isFunc && spec.Rpc != null) rpcs++;
                if (kind == "property" && !isProp || kind == "function" && !isFunc) continue;
                if (replicatedOnly && !(isProp ? spec.IsReplicated : spec.Rpc != null)) continue;
                if (required.Any(r => !spec.Has(r) && !spec.Meta.ContainsKey(r))) continue;
                members.Add((s, spec, isFunc));
            }
            var (page, more) = Slice(members, offset, limit);
            var items = new List<JsonNode>();
            string typeFull = type == null ? null : string.IsNullOrEmpty(type.Container) ? type.Name : type.Container + "::" + type.Name;
            int luaLinks = 0;
            foreach (var (s, spec, isFunc) in page)
            {
                var o = new JsonObject { ["name"] = s.Name, ["kind"] = isFunc ? "function" : "property" };
                if (!string.IsNullOrEmpty(s.Container) && s.Container != typeFull && s.Container != type?.Name) o["declaredIn"] = s.Container;
                ResultJson.AddLocation(o, s.Location);
                if (!string.IsNullOrEmpty(s.Signature)) o["sig"] = s.Signature;
                if (spec.Flags.Count > 0) o["specifiers"] = new JsonArray(spec.Flags.Select(f => (JsonNode)f).ToArray());
                if (spec.Values.Count > 0) o["values"] = ToJson(spec.Values);
                if (spec.Meta.Count > 0) o["meta"] = ToJson(spec.Meta);
                if (!isFunc && spec.IsReplicated)
                    o["replication"] = spec.Value("ReplicatedUsing") is string onRep ? "ReplicatedUsing=" + onRep : "Replicated";
                if (isFunc && spec.Rpc != null)
                {
                    var rpc = new JsonObject { ["type"] = spec.Rpc };
                    if (spec.Reliability != null) rpc["reliability"] = spec.Reliability;
                    if (spec.Has("WithValidation")) rpc["withValidation"] = true;
                    o["rpc"] = rpc;
                }
                if (!isFunc && spec.IsConfig) o["config"] = true;
                if (isFunc)
                {
                    var lua = _index.Lua.FunctionsNamed(s.Name);
                    if (lua.Count > 0)
                    {
                        o["lua"] = new JsonArray(lua.Take(5).Select(h => (JsonNode)LuaSymbolJson(h.Symbol, h.File)).ToArray());
                        luaLinks++;
                    }
                }
                items.Add(o);
            }
            string note = m.Note;
            if (type == null) note = Join(note, "Type '" + name + "' not found in the index. Try search_symbols.");
            if (luaLinks > 0) note = Join(note, LuaNameNote);
            var payload = ResultJson.Page(m.Source, m.IndexReady, items, offset, more, note);
            if (type != null)
            {
                var t = ResultJson.Symbol(type);
                var classSpec = UeSpecifiers.Parse(type.Specifiers);
                if (classSpec.Flags.Count > 0) t["specifiers"] = new JsonArray(classSpec.Flags.Select(f => (JsonNode)f).ToArray());
                if (classSpec.Values.Count > 0) t["values"] = ToJson(classSpec.Values);
                if (classSpec.Meta.Count > 0) t["meta"] = ToJson(classSpec.Meta);
                payload.Insert(0, "type", t);
            }
            payload.Insert(1, "totals", new JsonObject { ["properties"] = properties, ["functions"] = functions, ["replicated"] = replicated, ["rpcs"] = rpcs, ["matching"] = members.Count });
            return new ToolOutput { Payload = payload, ResultCount = page.Count, Source = m.Source };
        }

        private static JsonObject ToJson(Dictionary<string, string> d)
        {
            var o = new JsonObject();
            foreach (var kv in d) o[kv.Key] = kv.Value;
            return o;
        }

        // ================================================================== get_module_info

        private ToolDefinition ModuleInfoTool() => Define(
            "get_module_info", "UE module of a file / module rules",
            "Which UE module a file belongs to, the module's type (Runtime/Editor/Developer/UncookedOnly...), plugin and Build.cs " +
            "Public/Private dependencies. Pass 'path' (any source file) or 'module' (name). With 'header' ('GameFramework/Actor.h' or a path) it also " +
            "answers 'which module provides this header and does my module already depend on it' — the fix for 'cannot open include file' / LNK2019 " +
            "after adding an include (dependency to add and whether it belongs in Public or Private).",
            Schema(
                "\"path\":{\"type\":\"string\",\"description\":\"A file of the module (absolute or relative to the solution/project).\"}," +
                "\"module\":{\"type\":\"string\",\"description\":\"Module name, e.g. Engine, UMG, MyGame.\"}," +
                "\"header\":{\"type\":\"string\",\"description\":\"Optional: an #include path or header file to check against the module's dependencies.\"}"),
            ModuleInfoAsync);

        private async Task<ToolOutput> ModuleInfoAsync(ToolArgs a, CancellationToken ct)
        {
            bool hasPath = a.Has("path"), hasModule = a.Has("module");
            if (hasPath == hasModule) throw ToolArgs.Invalid("pass either 'path' or 'module'");
            UeModule module;
            string source = Sources.Index, path = null;
            if (hasPath)
            {
                path = ResolvePath(a.String("path", required: true));
                module = Modules.ForFile(path);
            }
            else
            {
                (module, source) = await Modules.ForNameAsync(a.String("module", required: true, maxLength: 256), ct).ConfigureAwait(false);
            }
            var payload = new JsonObject { ["source"] = source ?? Sources.Index };
            if (module == null)
            {
                payload["note"] = hasPath
                    ? "No Build.cs above " + path + " (not inside a UE module)."
                    : "Module '" + a.String("module") + "' not found (no indexed file belongs to it, or its Build.cs is gone).";
                return new ToolOutput { Payload = payload, ResultCount = 0, Source = source };
            }
            payload["module"] = ModuleJson(module);
            if (a.Has("header"))
            {
                bool fromPublic = path != null && IsPublicPath(path);
                payload["header"] = await HeaderDependencyAsync(a.String("header", required: true), module, fromPublic, ct).ConfigureAwait(false);
            }
            return new ToolOutput { Payload = payload, ResultCount = 1, Source = source };
        }

        private static JsonObject ModuleJson(UeModule m)
        {
            var o = new JsonObject { ["name"] = m.Name, ["type"] = m.Type, ["typeFrom"] = m.TypeSource, ["buildCs"] = m.BuildCsPath, ["dir"] = m.Directory };
            if (m.Origin != null) o["origin"] = ResultJson.OriginName(m.Origin.Value);
            if (m.Plugin != null) o["plugin"] = m.Plugin;
            var r = m.Rules;
            o["publicDependencies"] = Arr(r.PublicDependencies);
            o["privateDependencies"] = Arr(r.PrivateDependencies);
            if (r.PublicIncludePathModules.Count > 0) o["publicIncludePathModules"] = Arr(r.PublicIncludePathModules);
            if (r.PrivateIncludePathModules.Count > 0) o["privateIncludePathModules"] = Arr(r.PrivateIncludePathModules);
            var dirs = new[] { "Public", "Classes", "Internal" }.Where(d => Directory.Exists(Path.Combine(m.Directory, d))).ToArray();
            if (dirs.Length > 0) o["includeFolders"] = Arr(dirs);
            if (m.Error != null) o["error"] = m.Error;
            return o;
        }

        private static JsonArray Arr(IEnumerable<string> items) => new JsonArray(items.OrderBy(x => x, StringComparer.OrdinalIgnoreCase).Select(x => (JsonNode)x).ToArray());

        private static bool IsPublicPath(string p)
        {
            string s = p.Replace('\\', '/');
            return s.IndexOf("/Public/", StringComparison.OrdinalIgnoreCase) >= 0 || s.IndexOf("/Classes/", StringComparison.OrdinalIgnoreCase) >= 0;
        }

        /// <summary>Header (include text or path) → providing module, and whether <paramref name="from"/> can already see it.</summary>
        private async Task<JsonObject> HeaderDependencyAsync(string header, UeModule from, bool fromPublic, CancellationToken ct)
        {
            var o = new JsonObject { ["query"] = header };
            string file = null;
            string trimmed = header.Trim().Trim('"', '<', '>');
            if (Path.IsPathRooted(trimmed) && File.Exists(trimmed)) file = Path.GetFullPath(trimmed);
            else
            {
                var resp = await _index.SearchAsync(new SearchRequest
                {
                    Pattern = Path.GetFileName(trimmed.Replace('\\', '/')),
                    IncludeFiles = true,
                    IncludeNonProject = true,
                    MaxResultsPerCategory = 200,
                }, ct).ConfigureAwait(false);
                var candidates = resp.Value.Hits.Where(h => h.File != null && IncludeDirective.Matches(h.File.Path, trimmed)).Select(h => h.File.Path).ToList();
                file = candidates.OrderByDescending(IsPublicPath).FirstOrDefault();
                if (candidates.Count > 1) o["candidates"] = candidates.Count;
            }
            if (file == null)
            {
                o["note"] = "Header not found in the index.";
                return o;
            }
            o["file"] = file;
            var owner = Modules.ForFile(file);
            if (owner == null)
            {
                o["note"] = "The header is not inside a UE module.";
                return o;
            }
            o["module"] = owner.Name;
            o["include"] = ModuleResolver.IncludePath(file, owner);
            bool same = string.Equals(owner.Name, from.Name, StringComparison.OrdinalIgnoreCase);
            if (same)
            {
                o["needsDependency"] = false;
                o["reason"] = "same module";
                return o;
            }
            if (ModuleResolver.IsPrivateHeader(file)) o["warning"] = "Private header of another module: it cannot be included from " + from.Name + " (look for a public header).";
            var visible = await Modules.VisibleModulesAsync(from, ct).ConfigureAwait(false);
            bool direct = from.Rules.DependsOn(owner.Name);
            bool ok = visible.Contains(owner.Name);
            o["needsDependency"] = !ok;
            if (ok && !direct) o["reason"] = "visible through another module's public dependencies (adding it explicitly is still recommended)";
            if (!ok)
            {
                o["addTo"] = fromPublic ? "PublicDependencyModuleNames" : "PrivateDependencyModuleNames";
                o["buildCs"] = from.BuildCsPath;
            }
            return o;
        }

        // ================================================================== suggest_include

        private ToolDefinition SuggestIncludeTool() => Define(
            "suggest_include", "#include and Build.cs dependency for a type",
            "The #include line to write for a C++ type (UE style: path below the module's Public/Classes folder, e.g. \"GameFramework/Character.h\") " +
            "and the module to add to Build.cs. Pass 'from' (the file that will include it) to learn whether that module already depends on it " +
            "and whether the dependency belongs in Public or Private.",
            Schema(
                "\"type\":{\"type\":\"string\",\"description\":\"Type (or other symbol) name, optionally qualified: ACharacter, UUserWidget, FGameplayTag.\"}," +
                "\"from\":{\"type\":\"string\",\"description\":\"Optional: the file that will include it (absolute or relative).\"}", "type"),
            SuggestIncludeAsync);

        private async Task<ToolOutput> SuggestIncludeAsync(ToolArgs a, CancellationToken ct)
        {
            var (name, container) = CppText.SplitQualified(a.String("type", required: true, maxLength: 512));
            string from = a.Has("from") ? ResolvePath(a.String("from")) : null;
            var found = await _index.FindSymbolsAsync(name, container, ct).ConfigureAwait(false);
            var ranked = found.Value.Where(s => IsHeaderPath(s.Location?.FilePath))
                .OrderBy(s => CodeIndex.IsType(s.Kind) ? 0 : 1)
                .ThenBy(s => s.Location.FilePath.EndsWith(".generated.h", StringComparison.OrdinalIgnoreCase) ? 1 : 0)
                .ThenBy(s => ModuleResolver.IsPrivateHeader(s.Location.FilePath) ? 1 : 0)
                .ThenBy(s => s.IsDefinition ? 0 : 1) // a class definition, not a forward declaration
                .ThenBy(s => IsProjectOrigin(s.Origin) ? 0 : 1)
                .ToList();
            var payload = new JsonObject { ["source"] = found.Source, ["type"] = name };
            var best = ranked.FirstOrDefault();
            if (best == null)
            {
                payload["note"] = Join(found.Note, found.Value.Count > 0 ? "Declared only in source files, not in a header." : "Not found in the index. Try search_symbols.");
                return new ToolOutput { Payload = payload, ResultCount = 0, Source = found.Source };
            }
            string header = best.Location.FilePath;
            var module = Modules.ForFile(header);
            string include = ModuleResolver.IncludePath(header, module);
            payload["symbol"] = ResultJson.Symbol(best, includeSignature: false);
            payload["include"] = "#include \"" + include + "\"";
            payload["header"] = header;
            if (module != null)
            {
                payload["module"] = module.Name;
                payload["moduleType"] = module.Type;
                if (module.Plugin != null) payload["plugin"] = module.Plugin;
            }
            if (ModuleResolver.IsPrivateHeader(header)) payload["warning"] = "Declared in a Private header: only code of module " + module?.Name + " can include it.";
            if (module != null && from != null)
            {
                var fromModule = Modules.ForFile(from);
                if (fromModule == null) payload["dependency"] = new JsonObject { ["note"] = "'from' is not inside a UE module." };
                else if (string.Equals(fromModule.Name, module.Name, StringComparison.OrdinalIgnoreCase))
                    payload["dependency"] = new JsonObject { ["needed"] = false, ["reason"] = "same module" };
                else
                {
                    var visible = await Modules.VisibleModulesAsync(fromModule, ct).ConfigureAwait(false);
                    var dep = new JsonObject { ["from"] = fromModule.Name, ["needed"] = !visible.Contains(module.Name) };
                    if (!visible.Contains(module.Name))
                    {
                        dep["addTo"] = IsPublicPath(from) ? "PublicDependencyModuleNames" : "PrivateDependencyModuleNames";
                        dep["buildCs"] = fromModule.BuildCsPath;
                    }
                    else if (!fromModule.Rules.DependsOn(module.Name)) dep["reason"] = "visible through another module's public dependencies";
                    payload["dependency"] = dep;
                }
            }
            else if (module != null) payload["dependency"] = new JsonObject { ["module"] = module.Name, ["note"] = "Add it to the including module's Build.cs unless it is the same module (pass 'from' to check)." };
            var others = ranked.Skip(1).Where(s => s.Location.FilePath != header).Select(s => s.Location.FilePath).Distinct(StringComparer.OrdinalIgnoreCase).Take(5).ToList();
            if (others.Count > 0) payload["otherHeaders"] = new JsonArray(others.Select(p => (JsonNode)p).ToArray());
            if (!string.IsNullOrEmpty(found.Note)) payload["note"] = found.Note;
            return new ToolOutput { Payload = payload, ResultCount = 1, Source = found.Source };
        }

        private static bool IsHeaderPath(string path)
        {
            string ext = Path.GetExtension(path ?? string.Empty).ToLowerInvariant();
            return ext == ".h" || ext == ".hpp" || ext == ".hh" || ext == ".hxx" || ext == ".inl";
        }

        // ================================================================== search_config

        private static readonly string[] ConfigKinds = { "ini", "config_property", "cvar", "tag" };

        private ToolDefinition SearchConfigTool() => Define(
            "search_config", "Search ini settings, CVars, gameplay tags",
            "Use instead of grepping Config/*.ini or source for settings. Finds: ini keys ([Section] Key=Value with file:line, project Config " +
            "and project plugins; scope='all' adds Engine/Config), UPROPERTY(config) properties with the ini file/section/key they are read from " +
            "(and the current value when set), console variables (TAutoConsoleVariable / FAutoConsoleVariableRef / FAutoConsoleCommand in project " +
            "code: name, variable, help, file:line) and gameplay tags (DefaultGameplayTags.ini, Config/Tags/*.ini, UE_DEFINE_GAMEPLAY_TAG). " +
            "Every space-separated word of the query must occur (case-insensitive).",
            Schema(
                "\"query\":{\"type\":\"string\",\"description\":\"Words to find, e.g. 'r.Shadow', 'MaxPlayers', 'Ability.Fire', 'GameMapsSettings'.\"}," +
                "\"kinds\":{\"type\":\"array\",\"items\":{\"type\":\"string\",\"enum\":[\"ini\",\"config_property\",\"cvar\",\"tag\"]},\"description\":\"Default: all four.\"}," +
                "\"scope\":{\"type\":\"string\",\"enum\":[\"project\",\"all\"],\"description\":\"project (default): project and project plugins; all: also engine ini files, engine config properties and engine native tags (engine CVars are not covered).\"}," +
                PagingProps, "query"),
            SearchConfigAsync);

        private async Task<ToolOutput> SearchConfigAsync(ToolArgs a, CancellationToken ct)
        {
            string query = a.String("query", required: true, maxLength: 256).Trim();
            var kinds = a.StringList("kinds").Select(k => k.ToLowerInvariant()).ToList();
            foreach (var k in kinds)
                if (!ConfigKinds.Contains(k)) throw ToolArgs.Invalid($"unknown kind '{k}'. Valid: {string.Join(", ", ConfigKinds)}");
            if (kinds.Count == 0) kinds = ConfigKinds.ToList();
            bool all = a.Choice("scope", "project", "project", "all") == "all";
            var (limit, offset) = Paging(a);
            int cap = Math.Min(MaxFetch, offset + limit + 1);

            var items = new List<JsonObject>();
            var notes = new List<string>();
            string source = Sources.Index;
            List<IniFileData> ini = null;
            List<IniFileData> Ini() => ini ??= Config.IniFiles(all, ct);

            foreach (var kind in ConfigKinds.Where(kinds.Contains))
            {
                if (items.Count >= cap) break;
                switch (kind)
                {
                    case "ini":
                        foreach (var f in Ini())
                            foreach (var sec in f.Sections)
                                foreach (var kv in sec.Keys)
                                {
                                    if (items.Count >= cap) break;
                                    if (!ConfigSearch.Matches(query, sec.Name + " " + kv.Key, kv.Value)) continue;
                                    items.Add(new JsonObject { ["type"] = "ini", ["file"] = f.Path, ["section"] = sec.Name, ["key"] = kv.Key, ["value"] = kv.Value });
                                }
                        break;
                    case "config_property":
                        source = await ConfigPropertiesAsync(query, all, cap - items.Count, items, ct).ConfigureAwait(false) ?? source;
                        break;
                    case "cvar":
                        {
                            var files = Config.OccurrenceFiles(ConfigSearch.CVarMarkers, includeNonProject: false, ConfigSearch.MaxCodeFiles, ct, out bool cut);
                            if (cut) notes.Add($"Only the first {ConfigSearch.MaxCodeFiles} files defining console variables were read.");
                            foreach (var f in files)
                                foreach (var c in Config.CodeFile(f).CVars)
                                {
                                    if (items.Count >= cap) break;
                                    if (!ConfigSearch.Matches(query, c.Name + " " + c.Variable, c.Help)) continue;
                                    var o = new JsonObject { ["type"] = "cvar", ["name"] = c.Name, ["variable"] = c.Variable, ["declaration"] = c.Declaration, ["file"] = c.File, ["line"] = c.Line + 1 };
                                    if (!string.IsNullOrEmpty(c.Help)) o["help"] = c.Help.Length > 300 ? c.Help.Substring(0, 299) + "…" : c.Help;
                                    items.Add(o);
                                }
                            if (all) notes.Add("Engine console variables are not covered (too many engine files to read per query); grep Engine/Source for the name if needed.");
                            break;
                        }
                    case "tag":
                        {
                            foreach (var f in Ini())
                                foreach (var t in f.Tags)
                                {
                                    if (items.Count >= cap) break;
                                    if (!ConfigSearch.Matches(query, t.Tag, t.Comment)) continue;
                                    var o = new JsonObject { ["type"] = "tag", ["tag"] = t.Tag, ["file"] = f.Path };
                                    if (t.Comment != null) o["comment"] = t.Comment;
                                    items.Add(o);
                                }
                            var files = Config.OccurrenceFiles(GameplayTagCatalog.DefineMacros, all, ConfigSearch.MaxCodeFiles, ct, out bool cut);
                            if (cut) notes.Add($"Only the first {ConfigSearch.MaxCodeFiles} files defining native tags were read.");
                            foreach (var f in files)
                                foreach (var t in Config.CodeFile(f).Tags)
                                {
                                    if (items.Count >= cap) break;
                                    if (!ConfigSearch.Matches(query, t.Tag, t.Comment)) continue;
                                    var o = new JsonObject { ["type"] = "tag", ["tag"] = t.Tag, ["file"] = f, ["native"] = true };
                                    if (t.Comment != null) o["comment"] = t.Comment;
                                    items.Add(o);
                                }
                            break;
                        }
                }
            }

            var (page, more) = Slice(items, offset, limit);
            foreach (var o in page) await CompleteConfigItemAsync(o, ct).ConfigureAwait(false);
            if (!_index.Offline.IsAvailable && (kinds.Contains("cvar") || kinds.Contains("tag")))
                notes.Add("No index on disk yet: console variables and native tags in C++ cannot be found; ini results are complete.");
            var payload = ResultJson.Page(source, _index.Offline.IsAvailable, page.Select(o => (JsonNode)o).ToList(), offset, more,
                notes.Count == 0 ? null : string.Join(" ", notes.Distinct()));
            return new ToolOutput { Payload = payload, ResultCount = page.Count, Source = source };
        }

        /// <summary>Adds line numbers (ini) and the UPROPERTY behind a [/Script/Module.Class] key, for the returned page only.</summary>
        private async Task CompleteConfigItemAsync(JsonObject o, CancellationToken ct)
        {
            string type = (string)o["type"];
            if (type == "ini")
            {
                string file = (string)o["file"], section = (string)o["section"], key = (string)o["key"];
                int line = Config.IniLine(file, section, key);
                if (line >= 0) o["line"] = line + 1;
                if (ConfigSearch.ParseScriptSection(section) is { } script)
                {
                    string module = script.Module, cls = script.Class;
                    foreach (var prefix in new[] { "U", "A", "" })
                    {
                        var found = await _index.FindSymbolsAsync(key, prefix + cls, ct).ConfigureAwait(false);
                        var prop = found.Value.FirstOrDefault(s => (s.Ue & UeFlags.UProperty) != 0 && (s.Module == null || string.Equals(s.Module, module, StringComparison.OrdinalIgnoreCase)));
                        if (prop == null) continue;
                        var p = new JsonObject { ["class"] = prop.Container };
                        ResultJson.AddLocation(p, prop.Location);
                        o["property"] = p;
                        break;
                    }
                }
            }
            else if (type == "tag" && o["line"] == null && o["file"] is JsonNode fileNode)
            {
                string file = (string)fileNode;
                if ((bool?)o["native"] == true) return; // tag text is in a macro argument; the file is enough
                var text = _index.Texts.Get(file);
                if (text == null) return;
                string tag = (string)o["tag"];
                for (int i = 0; i < text.LineCount; i++)
                    if (text.Line(i).IndexOf("\"" + tag + "\"", StringComparison.OrdinalIgnoreCase) >= 0) { o["line"] = i + 1; break; }
            }
        }

        /// <summary>UPROPERTY(config / globalconfig) matching the query, mapped to Default{Config}.ini [/Script/Module.Class] Key.</summary>
        private async Task<string> ConfigPropertiesAsync(string query, bool all, int room, List<JsonObject> into, CancellationToken ct)
        {
            if (room <= 0) return null;
            string word = query.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries).FirstOrDefault(w => w.All(c => char.IsLetterOrDigit(c) || c == '_'));
            if (word == null) return null; // not an identifier query (e.g. 'r.Shadow')
            var resp = await _index.SearchAsync(new SearchRequest
            {
                Pattern = word,
                IncludeSymbols = true,
                IncludeTypes = false,
                IncludeNonProject = all,
                RequireUe = UeFlags.UProperty,
                MaxResultsPerCategory = 300,
            }, ct).ConfigureAwait(false);
            var classes = new Dictionary<string, SymbolInfo>(StringComparer.Ordinal);
            var iniFiles = Config.IniFiles(all, ct);
            foreach (var h in resp.Value.Hits.Where(h => h.Symbol != null).OrderByDescending(h => h.Score))
            {
                if (room <= 0) break;
                var s = h.Symbol;
                var spec = UeSpecifiers.Parse(s.Specifiers);
                if (!spec.IsConfig || !ConfigSearch.Matches(query, s.Name, s.Container)) continue;
                var o = new JsonObject { ["type"] = "config_property", ["name"] = s.Name, ["class"] = s.Container };
                ResultJson.AddLocation(o, s.Location);
                if (spec.Has("GlobalConfig")) o["globalConfig"] = true;
                if (!string.IsNullOrEmpty(s.Container))
                {
                    if (!classes.TryGetValue(s.Container, out var cls))
                        classes[s.Container] = cls = await _index.FindTypeAsync(LastSegment(s.Container), null, ct).ConfigureAwait(false);
                    string category = cls == null ? null : UeSpecifiers.Parse(cls.Specifiers).Value("config");
                    string section = ConfigSearch.ScriptSection(cls?.Module ?? s.Module, LastSegment(s.Container));
                    if (category != null) o["iniFile"] = ConfigSearch.DefaultIniFor(category);
                    else o["note"] = "config category not found on the class (it may be inherited from a base UCLASS(config=...))";
                    if (section != null)
                    {
                        o["section"] = section;
                        o["key"] = s.Name;
                        foreach (var f in iniFiles)
                        {
                            var sec = f.Sections.FirstOrDefault(x => string.Equals(x.Name, section, StringComparison.OrdinalIgnoreCase));
                            if (sec == null || !sec.Keys.TryGetValue(s.Name, out var value)) continue;
                            var set = new JsonObject { ["file"] = f.Path, ["value"] = value };
                            int line = Config.IniLine(f.Path, section, s.Name);
                            if (line >= 0) set["line"] = line + 1;
                            (o["setIn"] as JsonArray ?? (JsonArray)(o["setIn"] = new JsonArray())).Add(set);
                        }
                    }
                }
                into.Add(o);
                room--;
            }
            return resp.Source;
        }

        private static string LastSegment(string qualified)
        {
            if (string.IsNullOrEmpty(qualified)) return qualified;
            int i = qualified.LastIndexOf("::", StringComparison.Ordinal);
            return i >= 0 ? qualified.Substring(i + 2) : qualified;
        }

        // ================================================================== find_delegate_bindings

        private ToolDefinition DelegateBindingsTool() => Define(
            "find_delegate_bindings", "Where a function is bound to delegates",
            "Who binds this function to a delegate / event / input / timer? Finds AddDynamic / AddUniqueDynamic / BindDynamic (&UClass::Func), " +
            "AddUObject / BindUObject / BindRaw / BindSP / CreateUObject..., BindUFunction / AddUFunction (by name string or GET_FUNCTION_NAME_CHECKED), " +
            "InputComponent BindAction / BindAxis and SetTimer. Use it for 'why is this called / who triggers this handler' instead of grep. " +
            "Results: file, line/col, method, kind (dynamic/native/ufunction_name/input/timer), delegate expression, target, source line.",
            Schema(
                "\"function\":{\"type\":\"string\",\"description\":\"Function name, optionally 'UMyClass::OnClicked' to keep only bindings naming that class.\"}," +
                "\"scope\":{\"type\":\"string\",\"enum\":[\"project\",\"all\"],\"description\":\"" + ScopeDesc + "\"}," +
                PagingProps, "function"),
            DelegateBindingsAsync);

        private async Task<ToolOutput> DelegateBindingsAsync(ToolArgs a, CancellationToken ct)
        {
            var (name, qualifier) = CppText.SplitQualified(a.String("function", required: true, maxLength: 512));
            if (string.IsNullOrEmpty(name)) throw ToolArgs.Invalid("'function' has no identifier");
            bool all = a.Choice("scope", "project", "project", "all") == "all";
            var (limit, offset) = Paging(a);
            string qualClass = LastSegment(qualifier);
            var found = new List<DelegateBinding>();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            string projectRoot = all ? null : _index.Offline.Workspace()?.ProjectRoot;

            void Consider(string file, int line)
            {
                if (!seen.Add(file + ":" + line)) return;
                var text = _index.Texts.Get(file);
                if (text == null || line < 0 || line >= text.LineCount) return;
                var prev = new List<string>();
                for (int i = Math.Max(0, line - 2); i < line; i++) prev.Add(text.Line(i));
                var b = DelegateBindings.Match(text.Line(line), name, prev);
                if (b == null) return;
                if (qualClass != null && b.TargetClass != null && !string.Equals(b.TargetClass, qualClass, StringComparison.Ordinal)) return;
                b.File = file;
                b.Line = line;
                found.Add(b);
            }

            // 1) member pointers / GET_FUNCTION_NAME_CHECKED: the function name is an indexed identifier at the bind site
            var refs = await _index.FindReferencesAsync(new ReferencesRequest
            {
                Symbol = new SymbolInfo { Name = name, Container = qualifier },
                IncludeDeclarations = false,
                IncludeNonProject = all,
                MaxResults = 5000,
            }, name, ct).ConfigureAwait(false);
            foreach (var h in refs.Value.Hits)
            {
                ct.ThrowIfCancellationRequested();
                string file = h.Location?.FilePath;
                if (file == null) continue;
                if (projectRoot != null && refs.Source != Sources.Engine && !IsUnder(file, projectRoot)) continue;
                Consider(file, h.Location.Line);
            }

            // 2) bindings by name string ("Func" is not an identifier): only files that call BindUFunction & co.
            var byName = Config.OccurrenceFiles(DelegateBindings.ByNameMethods, all, ConfigSearch.MaxCodeFiles, ct, out bool cut);
            string quoted = "\"" + name + "\"";
            foreach (var f in byName)
            {
                var text = _index.Texts.Get(f);
                if (text == null) continue;
                for (int i = 0; i < text.LineCount; i++)
                    if (text.Line(i).IndexOf(quoted, StringComparison.Ordinal) >= 0) Consider(f, i);
            }

            var ordered = found.OrderBy(b => b.File, StringComparer.OrdinalIgnoreCase).ThenBy(b => b.Line).ToList();
            var (page, more) = Slice(ordered, offset, limit);
            var items = page.Select(b =>
            {
                var o = new JsonObject { ["file"] = b.File, ["line"] = b.Line + 1, ["col"] = b.Column + 1, ["method"] = b.Method, ["kind"] = b.Kind };
                if (b.Delegate != null && b.Delegate.Any(char.IsLetter)) o["delegate"] = b.Delegate;
                o["target"] = b.Target;
                if (b.TargetClass != null) o["targetClass"] = b.TargetClass;
                o["text"] = b.Text;
                return (JsonNode)o;
            }).ToList();
            string note = refs.Note;
            if (cut) note = Join(note, $"Name-string bindings: only the first {ConfigSearch.MaxCodeFiles} files calling BindUFunction/AddUFunction were read.");
            if (ordered.Count == 0) note = Join(note, "No binding found. Blueprint bindings live in .uasset files and are not indexed.");
            return new ToolOutput
            {
                Payload = ResultJson.Page(refs.Source, refs.IndexReady, items, offset, more, note),
                ResultCount = page.Count,
                Source = refs.Source,
            };
        }

        // ================================================================== Lua ↔ C++ (find_references)

        /// <summary>For find_references: Lua functions named like the C++ function (UnLua overrides), when a Lua index exists.</summary>
        private JsonObject LuaLink(string name, SymbolInfo symbol)
        {
            if (string.IsNullOrEmpty(name)) return null;
            if (symbol != null && symbol.Kind != SymbolKind.Function && symbol.Kind != SymbolKind.Method && (symbol.Ue & UeFlags.UFunction) == 0 &&
                symbol.Location != null) return null; // a resolved non-function: no Lua counterpart
            var hits = _index.Lua.FunctionsNamed(name);
            if (hits.Count == 0) return null;
            var o = new JsonObject
            {
                ["source"] = LuaSource,
                ["count"] = hits.Count,
                ["definitions"] = new JsonArray(hits.Take(20).Select(h => (JsonNode)LuaSymbolJson(h.Symbol, h.File)).ToArray()),
                ["note"] = LuaNameNote,
            };
            return o;
        }
    }
}
