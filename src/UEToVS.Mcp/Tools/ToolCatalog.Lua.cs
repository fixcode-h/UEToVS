using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using UEToVS.Contracts;
using UEToVS.Engine.Index.Lua;
using UEToVS.Mcp.Data;

namespace UEToVS.Mcp.Tools
{
    /// <summary>Lua script tools: search_lua, search_lua_files, get_lua_outline (data: <see cref="LuaIndexReader"/>).</summary>
    public sealed partial class ToolCatalog
    {
        /// <summary>Source of every Lua answer: the engine's lua-index.json on disk.</summary>
        public const string LuaSource = "lua-index";

        private const string NoLuaIndexNote =
            "No Lua index: set UEToVS 设置 > 索引 > Lua 脚本路径 (e.g. Content/Script) in Visual Studio, then press 索引. " +
            "Until then, search Lua files with grep/glob.";

        private static readonly Dictionary<string, LuaSymbolKind[]> LuaKindGroups = new Dictionary<string, LuaSymbolKind[]>(StringComparer.OrdinalIgnoreCase)
        {
            ["function"] = new[] { LuaSymbolKind.Function, LuaSymbolKind.Method },
            ["method"] = new[] { LuaSymbolKind.Method },
            ["class"] = new[] { LuaSymbolKind.Class },
            ["table"] = new[] { LuaSymbolKind.Table, LuaSymbolKind.Class },
            ["field"] = new[] { LuaSymbolKind.Field },
            ["variable"] = new[] { LuaSymbolKind.Variable },
        };

        private const string LuaKindsDesc = "Filter by kind (any of): function (incl. methods), method (a:b / self first), class (---@class, class(...), UnLua.Class()), " +
                                            "table (module tables, incl. classes), field, variable.";

        public static HashSet<LuaSymbolKind> ParseLuaKinds(List<string> kinds)
        {
            if (kinds == null || kinds.Count == 0) return null;
            var set = new HashSet<LuaSymbolKind>();
            foreach (var k in kinds)
            {
                if (!LuaKindGroups.TryGetValue(k, out var group))
                    throw ToolArgs.Invalid($"unknown Lua kind '{k}'. Valid: {string.Join(", ", LuaKindGroups.Keys)}");
                set.UnionWith(group);
            }
            return set;
        }

        private void RegisterLuaTools()
        {
            Register(SearchLuaTool());
            Register(SearchLuaFilesTool());
            Register(LuaOutlineTool());
        }

        private static JsonObject LuaSymbolJson(LuaSymbol s, LuaFileEntry f, bool includeFile = true)
        {
            var o = new JsonObject { ["name"] = s.Name, ["kind"] = s.Kind.ToString().ToLowerInvariant() };
            if (!string.IsNullOrEmpty(s.Container)) o["container"] = s.Container;
            if (includeFile) o["file"] = f.Path;
            o["line"] = s.Line + 1;
            o["col"] = s.Column + 1;
            if (!string.IsNullOrEmpty(s.Signature)) o["sig"] = s.Signature;
            if (includeFile && !string.IsNullOrEmpty(f.Module)) o["module"] = f.Module;
            if (s.IsLocal) o["local"] = true;
            return o;
        }

        private (LuaIndexData Data, string Note) LuaData()
        {
            var data = _index.Lua.Data;
            if (data == null) return (null, NoLuaIndexNote);
            if (data.Files.Count == 0)
                return (data, data.Roots.Count == 0 ? NoLuaIndexNote : "The Lua index is empty: no .lua files under " + string.Join("; ", data.Roots) + ".");
            return (data, null);
        }

        private IEnumerable<string> LuaBases()
        {
            yield return _index.SolutionDir;
            string projectRoot = _index.Offline.Workspace()?.ProjectRoot;
            if (!string.IsNullOrEmpty(projectRoot)) yield return projectRoot;
        }

        // ================================================================== search_lua

        private ToolDefinition SearchLuaTool() => Define(
            "search_lua", "Search Lua symbols",
            "Use this first, instead of grep, to find Lua code by name: where a Lua function, method or class is defined. " +
            "Searches the UEToVS index of the project's Lua scripts (UnLua / sluaunreal / plain Lua, folders set in the UEToVS settings): " +
            "functions, methods (M:Foo), classes (---@class, class(...), UnLua.Class()), module tables, fields and file-level variables. " +
            "Fuzzy by default (prefix, CamelHumps like 'OBC' → OnButtonClicked, substring); 'M:Fire' or 'UI.MainPanel.Show' restricts the container. " +
            "Results: name, kind, container, file, line/col (1-based), sig, module (require name), local.",
            Schema(
                "\"query\":{\"type\":\"string\",\"description\":\"Name or fragment; 'Container.Name' / 'Container:Name' restricts the container.\"}," +
                "\"match\":{\"type\":\"string\",\"enum\":[\"fuzzy\",\"exact\"],\"description\":\"fuzzy (default) or exact (case-sensitive) name.\"}," +
                "\"kinds\":{\"type\":\"array\",\"items\":{\"type\":\"string\"},\"description\":\"" + LuaKindsDesc + "\"}," +
                "\"container\":{\"type\":\"string\",\"description\":\"Owning table/class, e.g. 'M' or 'UI.MainPanel' (suffix match; substring for fuzzy).\"}," +
                "\"include_locals\":{\"type\":\"boolean\",\"description\":\"Include 'local' declarations (default true).\"}," +
                PagingProps, "query"),
            SearchLuaAsync);

        private Task<ToolOutput> SearchLuaAsync(ToolArgs a, CancellationToken ct)
        {
            string query = a.String("query", required: true, maxLength: 256);
            bool exact = a.Choice("match", "fuzzy", "fuzzy", "exact") == "exact";
            var kinds = ParseLuaKinds(a.StringList("kinds"));
            string container = a.String("container", maxLength: 256);
            bool locals = a.Bool("include_locals", true);
            var (limit, offset) = Paging(a);
            var (qualifier, name) = LuaIndexReader.Split(query);
            if (string.IsNullOrEmpty(name)) throw ToolArgs.Invalid("'query' has no name");
            container ??= qualifier;

            var (data, note) = LuaData();
            var hits = data == null ? new List<LuaHit>() : _index.Lua.Search(name, container, exact, kinds, locals);
            var (page, more) = Slice(hits, offset, limit);
            return Task.FromResult(new ToolOutput
            {
                Payload = ResultJson.Page(LuaSource, data != null, page.Select(h => (JsonNode)LuaSymbolJson(h.Symbol, h.File)).ToList(), offset, more, note),
                ResultCount = page.Count,
                Source = LuaSource,
            });
        }

        // ================================================================== search_lua_files

        private ToolDefinition SearchLuaFilesTool() => Define(
            "search_lua_files", "Search Lua script files",
            "Find a Lua script by file name or require path: use this instead of Glob/find. " +
            "Find indexed Lua script files by file name (fuzzy: 'MainPan' → MainPanel.lua) or by require/module path ('UI.MainPanel', 'UI/MainPanel'). " +
            "Results: name, path (absolute), module (require name), symbols (declaration count).",
            Schema(
                "\"query\":{\"type\":\"string\",\"description\":\"File name, fragment or module path.\"}," +
                PagingProps, "query"),
            SearchLuaFilesAsync);

        private Task<ToolOutput> SearchLuaFilesAsync(ToolArgs a, CancellationToken ct)
        {
            string query = a.String("query", required: true, maxLength: 512);
            var (limit, offset) = Paging(a);
            var (data, note) = LuaData();
            var files = data == null ? new List<(LuaFileEntry File, int Score)>() : _index.Lua.SearchFiles(query);
            var (page, more) = Slice(files, offset, limit);
            var items = page.Select(x =>
            {
                var o = new JsonObject { ["name"] = Path.GetFileName(x.File.Path), ["path"] = x.File.Path };
                if (!string.IsNullOrEmpty(x.File.Module)) o["module"] = x.File.Module;
                o["symbols"] = x.File.Symbols.Count;
                return (JsonNode)o;
            }).ToList();
            return Task.FromResult(new ToolOutput
            {
                Payload = ResultJson.Page(LuaSource, data != null, items, offset, more, note),
                ResultCount = page.Count,
                Source = LuaSource,
            });
        }

        // ================================================================== get_lua_outline

        private ToolDefinition LuaOutlineTool() => Define(
            "get_lua_outline", "Lua file outline",
            "Use it before reading a large Lua script. Declarations of one Lua script in source order (classes, tables, functions, methods, fields, file-level variables) with 1-based line/col and signature. " +
            "Accepts an absolute path, a path relative to a Lua script folder or the project, or a module name ('UI.MainPanel').",
            Schema(
                "\"path\":{\"type\":\"string\",\"description\":\"Lua file path or module name.\"}," +
                "\"kinds\":{\"type\":\"array\",\"items\":{\"type\":\"string\"},\"description\":\"" + LuaKindsDesc + "\"}," +
                "\"limit\":{\"type\":\"integer\",\"minimum\":1,\"maximum\":2000,\"description\":\"Max items (default 300).\"}," +
                "\"offset\":{\"type\":\"integer\",\"minimum\":0,\"maximum\":100000,\"description\":\"Items to skip.\"}", "path"),
            LuaOutlineAsync);

        private Task<ToolOutput> LuaOutlineAsync(ToolArgs a, CancellationToken ct)
        {
            string path = a.String("path", required: true);
            var kinds = ParseLuaKinds(a.StringList("kinds"));
            int limit = a.Int("limit", 300, 1, 2000);
            int offset = a.Int("offset", 0, 0, 100000);
            var (data, note) = LuaData();
            var file = data == null ? null : _index.Lua.FindFile(path, LuaBases());
            if (data != null && file == null)
                note = "Not in the Lua index: " + path + " (not a .lua file under " + string.Join("; ", data.Roots) + ", or not indexed yet). Try search_lua_files.";
            var list = file == null ? new List<LuaSymbol>() : file.Symbols.Where(s => kinds == null || kinds.Contains(s.Kind)).ToList();
            var (page, more) = Slice(list, offset, limit);
            var payload = ResultJson.Page(LuaSource, data != null, page.Select(s => (JsonNode)LuaSymbolJson(s, file, includeFile: false)).ToList(), offset, more, note);
            if (file != null)
            {
                payload.Insert(0, "file", file.Path);
                if (!string.IsNullOrEmpty(file.Module)) payload.Insert(1, "module", file.Module);
            }
            return Task.FromResult(new ToolOutput { Payload = payload, ResultCount = page.Count, Source = LuaSource });
        }

        // ================================================================== index_status part

        private JsonObject LuaStatus(EngineStatus live)
        {
            var data = _index.Lua.Data;
            var o = new JsonObject();
            bool fromEngine = live != null && (live.LuaState != LuaIndexState.Disabled || live.LuaRoots != null);
            if (fromEngine)
            {
                o["state"] = live.LuaState.ToString();
                o["files"] = live.LuaFileCount;
                o["symbols"] = live.LuaSymbolCount;
                if (live.LuaRoots != null) o["roots"] = new JsonArray(live.LuaRoots.Select(r => (JsonNode)r).ToArray());
                if (!string.IsNullOrEmpty(live.LuaLastError)) o["lastError"] = live.LuaLastError;
            }
            else if (data != null)
            {
                o["state"] = "OnDisk";
                o["files"] = data.Files.Count;
                o["symbols"] = data.SymbolCount;
                o["roots"] = new JsonArray(data.Roots.Select(r => (JsonNode)r).ToArray());
                o["writtenUtc"] = data.WrittenUtc.ToString("u");
            }
            else
            {
                o["state"] = LuaIndexState.Disabled.ToString();
                o["note"] = NoLuaIndexNote;
            }
            if (!string.IsNullOrEmpty(_index.Lua.LoadError)) o["loadError"] = _index.Lua.LoadError;
            return o;
        }
    }
}
