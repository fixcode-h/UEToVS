using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
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
    /// tools/list + tools/call, two resources (index status, usage guide) and two prompts. Transport-agnostic: Program
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
            "UEToVS indexes this Unreal Engine project: its C++ (project, plugins, engine source), its assets (.uasset/.umap) and, when " +
            "configured, its Lua scripts (UnLua/slua). USE THESE TOOLS FIRST, before Grep/Glob/find or reading whole files, whenever you look for " +
            "code or assets in this project: they answer from a prebuilt index in milliseconds, return exact file:line, and skip the millions of " +
            "engine lines that make grep slow and noisy.\n" +
            "Which tool: C++ class/function/property/macro by (part of) its name -> search_symbols; where is X defined -> go_to_definition; " +
            "who uses/calls X -> find_references; base/derived classes -> get_type_hierarchy; fields/UFUNCTIONs/UPROPERTYs of a type -> get_members; " +
            "structure of a .h/.cpp -> get_file_outline; a source file by name -> search_files (instead of Glob); Blueprints, widgets, maps, " +
            "materials, data tables -> search_assets; Lua function/method/class/module table -> search_lua; a Lua script by name or require path " +
            "-> search_lua_files; structure of a Lua script -> get_lua_outline.\n" +
            "Then read only the lines you need around the returned line. Lines/columns are 1-based; lists are paged (limit/offset, hasMore/nextOffset); " +
            "scope='all' adds engine code and content.\n" +
            "Use grep only for what is not indexed: string literals, comments, log text, .ini/config/.uplugin/.Build.cs contents, and usages of " +
            "Lua functions (Lua references are not indexed). If results look empty or stale, call index_status (indexing may still be running, or " +
            "no Lua folder is set) and fall back to grep.";

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
            Annotations = new ToolAnnotations { Title = t.Title, ReadOnlyHint = true, IdempotentHint = true, DestructiveHint = false, OpenWorldHint = false },
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
| A Lua script by name or require path | `search_lua_files` (`UI.MainPanel`) |
| Structure of a Lua script | `get_lua_outline` (path or module name) |
| Is the index complete? | `index_status` |

Use these before Grep/Glob or reading whole files. Grep is still the tool for what is not indexed: string literals, comments,
log text, `.ini`/config/`.uplugin`/`.Build.cs` contents, and usages of Lua functions (Lua references are not indexed).

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
            new Prompt
            {
                Name = "find_blueprint_usage",
                Title = "Find Blueprint-facing API",
                Description = "List the UFUNCTIONs/UPROPERTYs of a class that Blueprints can use, and assets that likely use them.",
                Arguments = new List<PromptArgument> { new PromptArgument { Name = "class_name", Description = "e.g. UMyComponent", Required = true } },
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
                    $"2) get_type_hierarchy type_name={cls} direction=both max_depth=6. 3) get_members type_name={cls} ue=[\"REFLECTED\"]. " +
                    $"4) find_references symbol={cls} limit=50. 5) search_assets query={TrimPrefix(cls)} class=Blueprint. " +
                    "Then summarise: purpose, base chain, key reflected properties/functions (with specifiers), main users, related assets. Cite file:line.",
                "find_blueprint_usage" =>
                    $"List the Blueprint-facing API of {cls}: get_members type_name={cls} ue=[\"UFUNCTION\",\"UPROPERTY\"] include_inherited=false, " +
                    "keep members whose spec contains BlueprintCallable, BlueprintPure, BlueprintImplementableEvent, BlueprintNativeEvent, " +
                    $"BlueprintReadWrite, BlueprintReadOnly or BlueprintAssignable. Then search_assets query={TrimPrefix(cls)} class=Blueprint for likely users. " +
                    "Answer as a table (member, kind, specifiers, file:line).",
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
