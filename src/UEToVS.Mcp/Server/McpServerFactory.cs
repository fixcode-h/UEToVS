using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using UEToVS.Core.Diagnostics;
using UEToVS.Mcp.Data;
using UEToVS.Mcp.Tools;

namespace UEToVS.Mcp.Server
{
    /// <summary>
    /// Builds the MCP server (official ModelContextProtocol C# SDK, low-level handler API) around <see cref="ToolInvoker"/>:
    /// tools/list + tools/call, two resources (index status, usage guide) and one prompt. Transport-agnostic: Program
    /// passes the stdio transport, tests pass in-memory streams.
    /// </summary>
    public static class McpServerFactory
    {
        public const string ServerName = "uetovs";
        public const string StatusUri = "uetovs://index/status";
        public const string GuideUri = "uetovs://guide";

        private static readonly Logger L = Log.For("Mcp.Server");

        // Kept under ~2000 chars: some clients (Claude Code) truncate server instructions beyond 2 KB.
        public const string Instructions =
            "UEToVS indexes this Unreal project: C++ (project, plugins, engine), assets and Lua scripts (UnLua/slua). USE THESE TOOLS FIRST, " +
            "before Grep/rg/Glob/find or reading whole files, whenever you look for a C++ or Lua name: function, class, variable, module, " +
            "GM/debug/console command, or an identifier from an error, log, stack or screenshot. They answer from a prebuilt index in " +
            "milliseconds with exact file:line and skip the engine's millions of lines and comment/string noise.\n" +
            "If you only see tool names (deferred tools), load these tools' descriptions first: each says when to use it and what it cannot do.\n" +
            "Which tool: C++ name -> search_symbols; definition -> go_to_definition; callers/users -> find_references; base/derived -> " +
            "get_type_hierarchy; members -> get_members; specifiers, replication, RPCs -> get_reflection_info; file structure -> get_file_outline; " +
            "file by name -> search_files; Blueprints/maps -> search_assets; module, Build.cs, header's module -> get_module_info; #include for a " +
            "type -> suggest_include; ini, CVars, console commands, gameplay tags -> search_config; who binds a handler -> find_delegate_bindings; " +
            "Lua function/class/GM command -> search_lua; Lua file -> search_lua_files; Lua structure -> get_lua_outline.\n" +
            "Then read only the lines around the result. Grep only what is not indexed: string literals, comments, log text, Lua call sites. " +
            "If results look empty or stale, call index_status before falling back to grep.\n" +
            "After editing C++ call get_diagnostics; after a build get_build_errors instead of the build log. Rename with rename_symbol (preview, then apply=true).";

        public static McpServerOptions CreateOptions(ToolInvoker invoker)
        {
            var tools = invoker.Catalog.All.Select(ToProtocol).ToList();
            return new McpServerOptions
            {
                ServerInfo = new Implementation { Name = ServerName, Title = "UEToVS (Unreal Engine C++ index)", Version = EngineConnection.Version },
                ServerInstructions = Instructions,
                Capabilities = new ServerCapabilities
                {
                    Tools = new ToolsCapability { ListChanged = false },
                    Resources = new ResourcesCapability(),
                    Prompts = new PromptsCapability(),
                },
                Handlers = new McpServerHandlers
                {
                    ListToolsHandler = (ctx, ct) => new ValueTask<ListToolsResult>(new ListToolsResult { Tools = tools }),
                    CallToolHandler = async (ctx, ct) =>
                    {
                        var p = ctx.Params;
                        var args = p?.Arguments as IReadOnlyDictionary<string, JsonElement> ?? ToReadOnly(p?.Arguments);
                        var r = await invoker.InvokeAsync(p?.Name, args, ct).ConfigureAwait(false);
                        return new CallToolResult
                        {
                            IsError = r.IsError,
                            Content = new List<ContentBlock> { new TextContentBlock { Text = r.Text } },
                        };
                    },
                    ListResourcesHandler = (ctx, ct) => new ValueTask<ListResourcesResult>(new ListResourcesResult
                    {
                        Resources = new List<Resource>
                        {
                            new Resource { Uri = StatusUri, Name = "index-status", Title = "UEToVS index status", MimeType = "application/json",
                                Description = "Engine/index state, counts, project and engine roots (same as the index_status tool)." },
                            new Resource { Uri = GuideUri, Name = "guide", Title = "How to use the UEToVS tools", MimeType = "text/markdown",
                                Description = "When to use which tool, result conventions, UE-specific tips." },
                        },
                    }),
                    ReadResourceHandler = async (ctx, ct) =>
                    {
                        string uri = ctx.Params?.Uri;
                        string text, mime;
                        if (uri == StatusUri)
                        {
                            var r = await invoker.InvokeAsync("index_status", new Dictionary<string, JsonElement>(), ct).ConfigureAwait(false);
                            text = r.Text;
                            mime = "application/json";
                        }
                        else if (uri == GuideUri)
                        {
                            text = Guide;
                            mime = "text/markdown";
                        }
                        else throw new ArgumentException("Unknown resource: " + uri);
                        L.Info("mcp.resource.read", null, ("uri", uri), ("chars", text.Length));
                        return new ReadResourceResult { Contents = new List<ResourceContents> { new TextResourceContents { Uri = uri, MimeType = mime, Text = text } } };
                    },
                    ListPromptsHandler = (ctx, ct) => new ValueTask<ListPromptsResult>(new ListPromptsResult { Prompts = Prompts.All }),
                    GetPromptHandler = (ctx, ct) =>
                    {
                        var p = ctx.Params;
                        L.Info("mcp.prompt.get", null, ("name", p?.Name));
                        return new ValueTask<GetPromptResult>(Prompts.Get(p?.Name, p?.Arguments));
                    },
                },
            };
        }

        private static IReadOnlyDictionary<string, JsonElement> ToReadOnly(IDictionary<string, JsonElement> d) =>
            d == null ? new Dictionary<string, JsonElement>() : new Dictionary<string, JsonElement>(d);

        public static Tool ToProtocol(ToolDefinition t) => new Tool
        {
            Name = t.Name,
            Title = t.Title,
            Description = t.Description,
            InputSchema = t.InputSchema,
            Annotations = new ToolAnnotations { Title = t.Title, ReadOnlyHint = t.ReadOnly, IdempotentHint = t.ReadOnly, DestructiveHint = false, OpenWorldHint = false },
            // Display names per language (ToolTitles): the client picks the one for its own UI language.
            Meta = new JsonObject { ["titles"] = new JsonObject { ["zh"] = ToolTitles.Chinese(t.Name) ?? t.Title, ["en"] = t.Title } },
        };

        public const string Guide =
@"# UEToVS tools for agents

The index covers the solution's Unreal project (Source, Plugins), the engine source it builds against, and the project's assets.
Lua scripts are indexed separately when Lua script folders are set (UEToVS settings > Indexing); Lua answers have `source: ""lua-index""`.

| Need | Tool |
|---|---|
| Find a class/function/property by (part of) its name | `search_symbols` (`kinds`, `ue`, `scope`) |
| All declarations of an exact name | `search_symbols` with `match: ""exact""` |
| Where is X defined / what is at file:line | `go_to_definition` (`symbol` or `path`+`line`+`column`) |
| Structure of a file without reading it | `get_file_outline` |
| Fields/functions of a type, UPROPERTY/UFUNCTION specifiers | `get_members` (`ue: [""UFUNCTION""]`, `include_inherited`) |
| Base/derived classes | `get_type_hierarchy` |
| Who uses X | `find_references` |
| A file by name | `search_files` |
| Blueprints, maps, materials, data tables | `search_assets` (`class: ""Blueprint""`) |
| Lua function / method / class / module table | `search_lua` (`M:Fire`, `kinds: [""method""]`) |
| A Lua GM / cheat / debug command or UI handler by name | `search_lua` |
| A console variable or C++ console command | `search_config` |
| A Lua script by name or require path | `search_lua_files` (`UI.MainPanel`) |
| Structure of a Lua script | `get_lua_outline` (path or module name) |
| UPROPERTY/UFUNCTION specifiers, meta, replication, RPCs | `get_reflection_info` (`specifiers: [""BlueprintReadWrite""]`, `replicated_only`) |
| Module of a file, Build.cs dependencies, which module provides a header | `get_module_info` (`path` or `module`, `header`) |
| `#include` line and Build.cs dependency for a type | `suggest_include` (`type`, `from`) |
| ini keys, UPROPERTY(config) ↔ ini, console variables, gameplay tags | `search_config` (`kinds`) |
| Who binds a function to a delegate / input / timer | `find_delegate_bindings` |
| Is the index complete? | `index_status` |
| Did my edit break a UE / UHT / replication rule? | `get_diagnostics` (`path` / `paths`) |
| Errors / warnings of the last VS build | `get_build_errors` (`file`, `severity`) |
| Rename a symbol everywhere (preview, then `apply=true`) | `rename_symbol` |

Use these before Grep/rg/Glob or reading whole files whenever you look for a C++ or Lua name, including names copied from
an error, log, crash stack or screenshot. Each tool's description says when to use it (USE FIRST WHEN), what it replaces
(INSTEAD OF) and what it cannot do (NOT FOR); if your client shows only tool names, load the descriptions first. Grep is still the tool for what is not indexed: string literals, comments,
log text, `.uplugin` contents, and call sites of Lua functions (Lua references are not indexed; `find_references` and
`get_reflection_info` list Lua functions with the same name as a UFUNCTION, i.e. UnLua overrides).

When the index is not ready: `index_status` says why (`phase` still `Parsing`/`Listing`, `engine.running: false` and no `onDisk`
index, or a `lastError`). Results are then partial or empty, and each answer's `note` says so. Fall back to Grep/Glob for that
question (scope the search to the project's Source/Plugins/Config folders, not the engine), and use the tools again once
`ready` is true. Without a running engine the answers come from the last index written to disk (`source: ""index""`).

Conventions: 1-based `line`/`col`; absolute paths; omitted fields mean default (no `container` = global scope,
`origin` absent = project code); `def: true` marks definitions (bodies); paged lists return `hasMore`/`nextOffset`.
`source` says where the answer came from: `engine` (live), `index` (on-disk index, engine not running),
`index-heuristic` (name-based fallback; may include same-named symbols of other types).

UE tips: UE type prefixes matter (A = Actor, U = UObject, F = struct, E = enum, I = interface, T = template);
`ue: [""REFLECTED""]` keeps only reflected (Blueprint-visible) declarations; Blueprint assets usually start with BP_/WBP_/ABP_.";
    }

    /// <summary>MCP prompts: ready-made instructions that chain the tools for common UE questions.</summary>
    public static class Prompts
    {
        public static List<Prompt> All => new List<Prompt>
        {
            new Prompt
            {
                Name = "explain_ue_class",
                Title = "Explain an Unreal class",
                Description = "Summarise a C++ class: hierarchy, reflected members, where it is used and which Blueprints may derive from it.",
                Arguments = new List<PromptArgument> { new PromptArgument { Name = "class_name", Description = "e.g. AMyCharacter", Required = true } },
            },
        };

        public static GetPromptResult Get(string name, IDictionary<string, JsonElement> args)
        {
            string cls = args != null && args.TryGetValue("class_name", out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
            if (string.IsNullOrWhiteSpace(cls)) throw new ArgumentException("class_name is required");
            string text = name switch
            {
                "explain_ue_class" =>
                    $"Explain the Unreal C++ class {cls} using the uetovs tools (do not grep). 1) go_to_definition symbol={cls} kind=declaration. " +
                    $"2) get_type_hierarchy type_name={cls} direction=both max_depth=6. 3) get_reflection_info type_name={cls}. " +
                    $"4) find_references symbol={cls} limit=50. 5) search_assets query={TrimPrefix(cls)} class=Blueprint. " +
                    "Then summarise: purpose, base chain, key reflected properties/functions (with specifiers), main users, related assets. Cite file:line.",
                _ => throw new ArgumentException("Unknown prompt: " + name),
            };
            return new GetPromptResult
            {
                Description = name,
                Messages = new List<PromptMessage> { new PromptMessage { Role = Role.User, Content = new TextContentBlock { Text = text } } },
            };
        }

        private static string TrimPrefix(string cls) =>
            cls.Length > 1 && "AUFEIT".IndexOf(cls[0]) >= 0 && char.IsUpper(cls[1]) ? cls.Substring(1) : cls;
    }
}
