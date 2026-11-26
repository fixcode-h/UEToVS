using System.Collections.Generic;
using System.Text;

namespace UEToVS.Mcp.Tools
{
    /// <summary>
    /// The tools/list description of every MCP tool, written in one place and in one shape so an agent can decide from the
    /// description alone whether this tool beats its generic ones (Grep/rg, Glob/find, reading files, reading the build log):
    /// <list type="bullet">
    /// <item><c>What</c>: what it answers, one sentence.</item>
    /// <item><c>UseWhen</c>: the concrete situations to call it FIRST (C++ identifiers, console commands, names copied
    /// from an error, log or screenshot...). Agents that see only tool names (deferred tool loading) skip tools whose purpose is
    /// not obvious, so this is the part that makes them pick it.</item>
    /// <item><c>InsteadOf</c>: which generic tool it replaces and why it is better (index, no directory walk, no comment/string noise).</item>
    /// <item><c>NotFor</c>: where it does not help and what to use then, so agents do not give up on the index after one miss
    /// or call it for things it cannot answer.</item>
    /// <item><c>Results</c>: optional, the fields of an answer.</item>
    /// </list>
    /// Every tool must have an entry with all four parts (ProtocolTests checks it); keep each description under 1200 chars.
    /// </summary>
    public static class ToolGuides
    {
        public sealed class Guide
        {
            public string What { get; set; }
            public string UseWhen { get; set; }
            public string InsteadOf { get; set; }
            public string NotFor { get; set; }
            public string Results { get; set; }
        }

        /// <summary>v0.9.0：Lua 归 IntelliJ IDEA 的 EmmyLua 插件（IDE 自带 MCP 服务器里的 lua_code 工具），UEToVS 不再索引 Lua。</summary>
        private const string LuaTool = "use the lua_code tool of the IntelliJ IDEA EmmyLua plugin";

        private const string LuaElsewhere = "Lua (" + LuaTool + ")";

        /// <summary>已移除的 Lua 工具：说明写明已移除、改用哪个；四段都要有（ProtocolTests 检查）。</summary>
        private static Guide Removed(string replacement) => new Guide
        {
            What = "REMOVED in UEToVS 0.9.0: UEToVS no longer indexes Lua. Every call returns error tool_removed.",
            UseWhen = "never. For Lua code use the IntelliJ IDEA EmmyLua plugin's tools in the IDE's built-in MCP server: " + replacement + ".",
            InsteadOf = "nothing: kept one version only so old prompts get a clear error.",
            NotFor = "anything; C++ stays here (search_symbols, search_files, get_file_outline).",
        };

        private const string IndexMiss = "If it finds nothing although the name surely exists, call index_status (indexing may still run) before falling back to grep.";

        private static readonly Dictionary<string, Guide> All = new Dictionary<string, Guide>
        {
            // ------------------------------------------------------------------ C++ code
            ["search_symbols"] = new Guide
            {
                What = "Finds Unreal C++ declarations by name in the UEToVS index (project, plugins, engine source with scope='all'): classes, structs, enums, functions, methods, fields, macros, delegates.",
                UseWhen = "you have a C++ name or part of one — a class/function/variable/macro you want to locate, an identifier from a compile error, crash stack, log line or screenshot, an Exec/console function — and need its file:line. Fuzzy: prefix, substring, CamelHumps ('AMC' → AMyCharacter); 'Outer::Name' restricts the container; match='exact' lists every declaration and definition.",
                InsteadOf = "grep/rg over Source and Engine: answers from the index in milliseconds, matches identifiers only (no hits in comments, strings or the engine's millions of lines) and ranks them, with UE markers.",
                NotFor = LuaElsewhere + ", string literals, comments and log text (grep). " + IndexMiss,
                Results = "name, kind, container, file, line/col (1-based), sig, ue (UCLASS/UFUNCTION/UPROPERTY...), spec, module, def.",
            },
            ["go_to_definition"] = new Guide
            {
                What = "Resolves where a C++ symbol is defined or declared.",
                UseWhen = "you know the name ('AMyCharacter', 'AMyCharacter::BeginPlay', 'UE_LOG') or a position (path + 1-based line/column) and want the definition — e.g. a call you are reading, a type in an error message.",
                InsteadOf = "grepping for 'class X' / 'X::Foo' and opening each hit: understands C++ scopes, overloads and macros, ranks the definition first (kind='declaration' for the header).",
                NotFor = LuaElsewhere + "; a fragment of a name (use search_symbols). " + IndexMiss,
                Results = "ranked targets with file, line, col, signature.",
            },
            ["find_references"] = new Guide
            {
                What = "Lists every use of a C++ symbol across the indexed code, grouped by file.",
                UseWhen = "you ask who calls / reads / writes / uses a C++ function, property, class or enum value — impact of a change, callers of a handler, where a value is set.",
                InsteadOf = "grep for usages: answers from the precomputed occurrence index without scanning files, matches the symbol itself (not same-named text in comments, strings or other classes), tags each hit read/write/call/type_use.",
                NotFor = "Lua (" + LuaTool + "), string-based lookups (FindFunction(\"X\"), config names) — not indexed, use grep there. Delegate/timer/input bindings: find_delegate_bindings also covers bindings by name string.",
                Results = "file, line/col (1-based), kind, source line.",
            },
            ["get_type_hierarchy"] = new Guide
            {
                What = "Base and/or derived classes of a C++ type as a tree (e.g. AMyCharacter → ACharacter → APawn → AActor → UObject).",
                UseWhen = "you ask what X inherits from, which classes derive from X, or which override a virtual might reach. Pass type_name ('AMyCharacter', 'Ns::FFoo') or a position on the type.",
                InsteadOf = "grepping for ': public X' (misses multi-line declarations, macros and indirect children): walks the whole indexed hierarchy.",
                NotFor = "Blueprint children of a class (use search_assets with class filter); " + LuaElsewhere + ".",
            },
            ["get_members"] = new Guide
            {
                What = "Fields, methods, nested types and enum values of a C++ class/struct/enum with signatures and UE markers.",
                UseWhen = "you need to know what a type contains or whether a member exists before calling it; include_inherited adds base-class members (declaring type in 'container').",
                InsteadOf = "reading the whole header (and its bases): one call, only the declarations.",
                NotFor = "replication / specifier details (use get_reflection_info); the body of a function (read the lines it returns).",
            },
            ["get_file_outline"] = new Guide
            {
                What = "Declarations of one C++ file in source order (types, members, functions, macros) with 1-based line/col, signature and UE markers.",
                UseWhen = "before reading a large .h/.cpp: get its structure and the line numbers, then read only the range you need.",
                InsteadOf = "reading the whole file or grepping it for 'void '/'class ': cheaper and complete.",
                NotFor = LuaElsewhere + "; finding which file to open (use search_symbols / search_files).",
            },
            ["search_files"] = new Guide
            {
                What = "Fuzzy file-name search over the indexed C++ source and headers (project, plugins; engine with scope='all').",
                UseWhen = "you know (part of) a C++ file name: 'MyCharH' finds MyCharacter.h; also Build.cs / Target.cs files.",
                InsteadOf = "Glob/find/dir: no directory walk over the engine tree, ranked by match quality.",
                NotFor = LuaElsewhere + "; assets (search_assets), finding a file by its content (search_symbols first, grep for text).",
                Results = "name, path (absolute), module, origin.",
            },
            ["search_assets"] = new Guide
            {
                What = "Fuzzy search over the Unreal assets (.uasset/.umap under Content, plugin content; engine content with scope='all').",
                UseWhen = "you look for a Blueprint, widget, map, material, texture, mesh or data table: 'BPHero' finds BP_Hero. Filter 'class' ('Blueprint', 'WidgetBlueprint', 'World', 'DataTable'...).",
                InsteadOf = "grep/Glob: assets are binary, grep cannot read them; this lists object paths (/Game/...) and classes.",
                NotFor = "what is inside an asset (graph, properties) or C++ code (search_symbols).",
                Results = "name, objectPath (/Game/...), file, class or classGuess.",
            },

            // ------------------------------------------------------------------ v0.9.0 已移除的 Lua 工具（旧名字保留一版，调用报 tool_removed）
            ["search_lua"] = Removed("lua_code action=search"),
            ["search_lua_files"] = Removed("lua_code action=files"),
            ["get_lua_outline"] = Removed("lua_code action=outline"),

            ["find_blueprint_usages"] = new Guide
            {
                What = "Blueprints and levels that use a C++ function or property, or Blueprints deriving from a C++ class (with their Blueprint children), read offline from the project's .uasset/.umap headers (parent class tags and name tables).",
                UseWhen = "before changing or removing a UFUNCTION/UPROPERTY/UCLASS: who uses it from Blueprints. 'class' = the C++ class (AMyCharacter or MyCharacter); add 'member' for a function or property of it. Without 'member' you get derived Blueprints plus other Blueprints that mention the class.",
                InsteadOf = "grep: .uasset files are binary; find_references only covers C++ (it also appends these Blueprint hits when VS is running).",
                NotFor = "where inside the graph it is used (open the Blueprint in the editor), engine content, or cooked packages. Member matches are by name (member + class name in the same asset), so a same-named member of another class can show up.",
                Results = "derived: name, objectPath, file, parent, children. usages: name, objectPath, file, kind (blueprint/level).",
            },

            // ------------------------------------------------------------------ Unreal specifics
            ["get_reflection_info"] = new Guide
            {
                What = "Every UPROPERTY and UFUNCTION of a UCLASS/USTRUCT with specifiers split out (flags, key=value, meta=(...)), replication (Replicated / ReplicatedUsing) and RPCs (Server/Client/NetMulticast, Reliable), plus the type's own specifiers.",
                UseWhen = "you ask what is Blueprint-visible, replicated, an RPC or EditAnywhere on a type. Filter with 'specifiers' (e.g. [\"BlueprintReadWrite\"]), 'kind' or 'replicated_only'.",
                InsteadOf = "reading the header and parsing UPROPERTY(...) macros by hand or grepping for specifiers.",
                NotFor = "non-reflected members (use get_members).",
            },
            ["get_module_info"] = new Guide
            {
                What = "Which UE module a file belongs to, the module type, plugin and Build.cs Public/Private dependencies; with 'header' also which module provides a header.",
                UseWhen = "'cannot open include file', LNK2019/unresolved external after adding an include, or before adding a dependency: it says the module to add and whether in Public or Private. Pass 'path' or 'module' (+ 'header').",
                InsteadOf = "finding and reading Build.cs files and grepping the engine for the header.",
                NotFor = "the #include line for a type name (use suggest_include).",
            },
            ["suggest_include"] = new Guide
            {
                What = "The #include line for a C++ type (UE style, e.g. \"GameFramework/Character.h\") and the module to add to Build.cs.",
                UseWhen = "you use a type and need its include, or the compiler says it is undeclared/incomplete. Pass 'from' (the including file) to learn whether its module already depends on it, and Public vs Private.",
                InsteadOf = "grepping the engine for 'class X' and guessing the include path from the folder.",
                NotFor = "headers you already know by path (use get_module_info with 'header').",
            },
            ["search_config"] = new Guide
            {
                What = "Finds settings: ini keys ([Section] Key=Value), UPROPERTY(config) with the ini file/section/key and current value, console variables and commands (TAutoConsoleVariable / FAutoConsoleVariableRef / FAutoConsoleCommand) and gameplay tags.",
                UseWhen = "you look for a setting, a CVar or C++ console command by name or words (e.g. from a log or a 'r.Foo' typed in the console), or a gameplay tag. Every space-separated word of the query must occur (case-insensitive); scope='all' adds Engine/Config.",
                InsteadOf = "grepping Config/*.ini and the source for settings: one call covers all ini layers, config properties, CVars and tags, with file:line.",
                NotFor = "Lua GM / debug commands (" + LuaTool + "); Exec UFUNCTIONs (search_symbols with ue=[\"UFUNCTION\"]).",
            },
            ["find_delegate_bindings"] = new Guide
            {
                What = "Finds where a C++ function is bound to a delegate, event, input or timer: AddDynamic / BindDynamic, AddUObject / BindUObject / BindRaw / BindSP, BindUFunction (by name string), InputComponent BindAction / BindAxis, SetTimer.",
                UseWhen = "you ask why a handler runs, who triggers it, or what is bound to an event — 'OnHit is called but I cannot find the caller'.",
                InsteadOf = "grepping for the function name across bind macros and name strings: knows every binding form, including GET_FUNCTION_NAME_CHECKED and name strings.",
                NotFor = "direct calls (use find_references); Blueprint bindings inside assets.",
                Results = "file, line/col, method, kind (dynamic/native/ufunction_name/input/timer), delegate expression, target, source line.",
            },

            // ------------------------------------------------------------------ build and checks
            ["get_build_errors"] = new Guide
            {
                What = "Compiler / linker / UHT / UBT errors and warnings of the last build in Visual Studio, parsed from the Build output, with 1-based file:line:col, code (C2065, LNK2019...), message and tool.",
                UseWhen = "after a build, or when the user says it does not compile. Filter by 'file' and 'severity'; 'build' tells when it ran and whether it succeeded (an old finishedUtc means not rebuilt since).",
                InsteadOf = "reading or grepping the build log / Output window text: already parsed and de-duplicated.",
                NotFor = "builds run outside Visual Studio (read that log); checking edits without building (use get_diagnostics).",
            },
            ["get_diagnostics"] = new Guide
            {
                What = "Runs UEToVS's Unreal checks on whole C++ files: unknown member on a known type (UE0001), missing GENERATED_BODY (UE0003), hand-written body of a generated UFUNCTION (UE0004), ReplicatedUsing without OnRep (UE0005), replicated property not in GetLifetimeReplicatedProps (UE0006), unknown gameplay tag / asset path (UE0007/8), types UHT rejects (UE0009/10), include lines that resolve nowhere / private headers / .generated.h problems (UE0017-UE0020), syntax errors (UE0021), types used without their #include (UE0022), undefined identifiers (UE0023).",
                UseWhen = "right after you edit C++ files, before building — pass the changed paths. Only certain problems are reported.",
                InsteadOf = "a full build to find UHT/replication mistakes.",
                NotFor = "general C++ compile errors (build, then get_build_errors); engine files (skipped). Reads files as saved on disk.",
            },
            ["rename_symbol"] = new Guide
            {
                What = "Index-based rename of a project class, struct, enum, function, property or variable across every reference.",
                UseWhen = "you rename a C++ symbol. By DEFAULT ONLY PREVIEWS the edits (file, 1-based line/col, source line); call again with apply=true to write. Reflected types/properties get a [CoreRedirects] line in Config/DefaultEngine.ini (write_redirects, default true) so Blueprints keep loading.",
                InsteadOf = "search-and-replace or sed: renames the symbol, not same-named text; never touches engine/third-party files.",
                NotFor = "Lua names (" + LuaTool + "); file renames (files named after a class are not renamed). Name-only matches are left out unless include_name_only=true. After applying, run get_diagnostics.",
            },
            ["run_project_inspection"] = new Guide
            {
                What = "Whole-project run of the UEToVS UE rules (UE0001-UE0010, UE0013-UE0023, same as get_diagnostics) over every project source file.",
                UseWhen = "the user asks to check the whole project. Asynchronous: action 'start' returns at once; poll 'status' until Completed, then page 'results' (filter by rule / file); 'cancel' stops.",
                InsteadOf = "calling get_diagnostics file by file or a full rebuild.",
                NotFor = "checking a few edited files (use get_diagnostics). Heavy on large projects; refuses when switched off in the UEToVS settings.",
            },
            ["index_status"] = new Guide
            {
                What = "State of the UEToVS index: engine running, indexing phase/progress, file/symbol/asset counts, project/engine roots, UE version.",
                UseWhen = "another UEToVS tool returned nothing or too little for a name that should exist, or before concluding the index is useless.",
                InsteadOf = "giving up on the index and grepping the whole tree at the first empty answer.",
                NotFor = "finding code (use the search tools); Lua (" + LuaTool + "). When it says indexing is unfinished, grep the project's Source/Plugins/Config folders for that question.",
            },

            // ------------------------------------------------------------------ v0.8.0 VS debug control (needs VS open with this solution)
            ["debug_session"] = new Guide
            {
                What = "Drives Visual Studio's debugger on the UE project: start (F5, optional map / -game / extra args for this launch), stop, restart, attach to a running UnrealEditor, build, pause, continue, step over/into/out/to line, and wait until it stops.",
                UseWhen = "you need to see C++ run: reproduce a bug under the debugger, stop at a breakpoint (set with debug_breakpoints) and step; a crash, check() or ensure() to catch; or rebuild and relaunch after a code change. Typical: start or attach -> debug_breakpoints add -> trigger the code -> wait -> debug_inspect -> step / continue.",
                InsteadOf = "adding UE_LOG lines and rebuilding to guess values: breakpoints show the real state without code changes.",
                NotFor = "Lua or Blueprint logic (C++ only). stop/restart end the UE process and lose unsaved editor work: ask the user first. While UE is stopped it is frozen, nothing else can talk to it until continue. To wait for the editor to finish loading use the panel's ue_editor action=wait_ready, not a long wait.",
                Results = "mode (design/run/break), location, lastReason, stack, buildErrors; wait: event = break / exited / buildDone / buildFailed / timeout / idle.",
            },
            ["debug_breakpoints"] = new Guide
            {
                What = "Lists, adds, removes, enables and clears Visual Studio breakpoints: file+line, function name, condition, hit count, tracepoints (log and continue) and data breakpoints (stop when a value changes).",
                UseWhen = "you want UE to stop where a suspect C++ line runs, only under a condition (Health <= 0), log values without stopping (message 'HP={Health}', read with debug_inspect output), or find who changes a value (data=this->Health while stopped).",
                InsteadOf = "editing code to add logs or asserts.",
                NotFor = "Lua / Blueprint. clear removes only breakpoints set by these tools unless scope=all; the user's own breakpoints are listed too, leave them alone.",
                Results = "items with number i (for remove/enable), file, line, condition, hits, bound (0 while debugging = symbols not loaded).",
            },
            ["debug_inspect"] = new Guide
            {
                What = "Reads the stopped UE process in Visual Studio: call stack, threads, locals and arguments, any C++ expression (natvis formats FString, TArray, UObject*), the Debug output (UE_LOG goes there when debugging) and a minidump.",
                UseWhen = "debug_session reports mode=break (breakpoint, step, crash, check/ensure): see where it is, read variables and members (this->Health, depth 1-3), evaluate expressions, switch frames or threads; output any time.",
                InsteadOf = "guessing values from code or logs.",
                NotFor = "a running process (pause or wait first). eval can call functions with side effects: prefer reading members.",
                Results = "value nodes {n,t,v,c} with budgets; output returns next for the following call.",
            },
        };

        public static Guide Get(string name) => name != null && All.TryGetValue(name, out var g) ? g : null;

        /// <summary>The description sent in tools/list.</summary>
        public static string Description(string name)
        {
            var g = Get(name) ?? throw new KeyNotFoundException("no tool guide for " + name);
            var sb = new StringBuilder(g.What);
            sb.Append(" USE FIRST WHEN: ").Append(g.UseWhen);
            sb.Append(" INSTEAD OF: ").Append(g.InsteadOf);
            sb.Append(" NOT FOR: ").Append(g.NotFor);
            if (!string.IsNullOrEmpty(g.Results)) sb.Append(" Results: ").Append(g.Results);
            return sb.ToString();
        }
    }
}
