using System.Collections.Generic;
using System.Text;

namespace UEToVS.Mcp.Tools
{
    /// <summary>
    /// The tools/list description of every MCP tool, written in one place and in one shape so an agent can decide from the
    /// description alone whether this tool beats its generic ones (Grep/rg, Glob/find, reading files, reading the build log):
    /// <list type="bullet">
    /// <item><c>What</c>: what it answers, one sentence.</item>
    /// <item><c>UseWhen</c>: the concrete situations to call it FIRST (C++ and Lua identifiers, GM / console commands, names copied
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

        private const string IndexMiss = "If it finds nothing although the name surely exists, call index_status (indexing may still run) before falling back to grep.";

        private static readonly Dictionary<string, Guide> All = new Dictionary<string, Guide>
        {
            // ------------------------------------------------------------------ C++ code
            ["search_symbols"] = new Guide
            {
                What = "Finds Unreal C++ declarations by name in the UEToVS index (project, plugins, engine source with scope='all'): classes, structs, enums, functions, methods, fields, macros, delegates.",
                UseWhen = "you have a C++ name or part of one — a class/function/variable/macro you want to locate, an identifier from a compile error, crash stack, log line or screenshot, an Exec/console function — and need its file:line. Fuzzy: prefix, substring, CamelHumps ('AMC' → AMyCharacter); 'Outer::Name' restricts the container; match='exact' lists every declaration and definition.",
                InsteadOf = "grep/rg over Source and Engine: answers from the index in milliseconds, matches identifiers only (no hits in comments, strings or the engine's millions of lines) and ranks them, with UE markers.",
                NotFor = "Lua (use search_lua), string literals, comments and log text (grep). " + IndexMiss,
                Results = "name, kind, container, file, line/col (1-based), sig, ue (UCLASS/UFUNCTION/UPROPERTY...), spec, module, def.",
            },
            ["go_to_definition"] = new Guide
            {
                What = "Resolves where a C++ symbol is defined or declared.",
                UseWhen = "you know the name ('AMyCharacter', 'AMyCharacter::BeginPlay', 'UE_LOG') or a position (path + 1-based line/column) and want the definition — e.g. a call you are reading, a type in an error message.",
                InsteadOf = "grepping for 'class X' / 'X::Foo' and opening each hit: understands C++ scopes, overloads and macros, ranks the definition first (kind='declaration' for the header).",
                NotFor = "Lua (use search_lua); a fragment of a name (use search_symbols). " + IndexMiss,
                Results = "ranked targets with file, line, col, signature.",
            },
            ["find_references"] = new Guide
            {
                What = "Lists every use of a C++ symbol across the indexed code, grouped by file.",
                UseWhen = "you ask who calls / reads / writes / uses a C++ function, property, class or enum value — impact of a change, callers of a handler, where a value is set.",
                InsteadOf = "grep for usages: answers from the precomputed occurrence index without scanning files, matches the symbol itself (not same-named text in comments, strings or other classes), tags each hit read/write/call/type_use.",
                NotFor = "Lua call sites and string-based lookups (FindFunction(\"X\"), config names) — not indexed, use grep there. Delegate/timer/input bindings: find_delegate_bindings also covers bindings by name string.",
                Results = "file, line/col (1-based), kind, source line.",
            },
            ["get_type_hierarchy"] = new Guide
            {
                What = "Base and/or derived classes of a C++ type as a tree (e.g. AMyCharacter → ACharacter → APawn → AActor → UObject).",
                UseWhen = "you ask what X inherits from, which classes derive from X, or which override a virtual might reach. Pass type_name ('AMyCharacter', 'Ns::FFoo') or a position on the type.",
                InsteadOf = "grepping for ': public X' (misses multi-line declarations, macros and indirect children): walks the whole indexed hierarchy.",
                NotFor = "Blueprint children of a class (use search_assets with class filter) or Lua classes.",
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
                NotFor = "Lua scripts (use get_lua_outline); finding which file to open (use search_symbols / search_files).",
            },
            ["search_files"] = new Guide
            {
                What = "Fuzzy file-name search over the indexed C++ source and headers (project, plugins; engine with scope='all').",
                UseWhen = "you know (part of) a C++ file name: 'MyCharH' finds MyCharacter.h; also Build.cs / Target.cs files.",
                InsteadOf = "Glob/find/dir: no directory walk over the engine tree, ranked by match quality.",
                NotFor = "Lua scripts (use search_lua_files), assets (search_assets), finding a file by its content (search_symbols first, grep for text).",
                Results = "name, path (absolute), module, origin.",
            },
            ["search_assets"] = new Guide
            {
                What = "Fuzzy search over the Unreal assets (.uasset/.umap under Content, plugin content; engine content with scope='all').",
                UseWhen = "you look for a Blueprint, widget, map, material, texture, mesh or data table: 'BPHero' finds BP_Hero. Filter 'class' ('Blueprint', 'WidgetBlueprint', 'World', 'DataTable'...).",
                InsteadOf = "grep/Glob: assets are binary, grep cannot read them; this lists object paths (/Game/...) and classes.",
                NotFor = "what is inside an asset (graph, properties) or C++/Lua code.",
                Results = "name, objectPath (/Game/...), file, class or classGuess.",
            },

            // ------------------------------------------------------------------ Lua
            ["search_lua"] = new Guide
            {
                What = "Finds where Lua code is declared in the UEToVS Lua index (UnLua / sluaunreal / plain Lua): functions, methods (M:Foo), classes, module tables, fields, file-level variables.",
                UseWhen = "you have a Lua name or part of one — a function, method, class or module, a GM / cheat / debug command name, a handler name from a UI, an identifier from a Lua error, log or screenshot — and need its file:line. Fuzzy: prefix, substring, CamelHumps ('OBC' → OnButtonClicked); 'M:Fire' or 'UI.MainPanel.Show' restricts the container.",
                InsteadOf = "grep/rg over the script folders: one indexed lookup instead of scanning thousands of .lua files, declarations only (not every line that mentions the name).",
                NotFor = "Lua call sites (who calls a Lua function — not indexed, grep for the name then) and C++ (use search_symbols). An empty answer with a note means no Lua folder is configured: grep then.",
                Results = "name, kind, container, file, line/col (1-based), sig, module (require name), local.",
            },
            ["search_lua_files"] = new Guide
            {
                What = "Finds indexed Lua script files by file name or require/module path.",
                UseWhen = "you know (part of) a script name ('MainPan' → MainPanel.lua) or a require path ('UI.MainPanel', 'UI/MainPanel').",
                InsteadOf = "Glob/find over Content/Script: no directory walk, understands require paths.",
                NotFor = "finding code inside scripts (use search_lua); C++ files (search_files).",
                Results = "name, path (absolute), module (require name), symbols (declaration count).",
            },
            ["get_lua_outline"] = new Guide
            {
                What = "Declarations of one Lua script in source order (classes, tables, functions, methods, fields, file-level variables) with 1-based line/col and signature.",
                UseWhen = "before reading a large Lua script: get its structure and line numbers, then read only the range you need. Accepts an absolute path, a path relative to a Lua script folder or the project, or a module name ('UI.MainPanel').",
                InsteadOf = "reading the whole script or grepping it for 'function '.",
                NotFor = "C++ files (use get_file_outline); locating which script (search_lua / search_lua_files).",
            },

            // ------------------------------------------------------------------ Unreal specifics
            ["get_reflection_info"] = new Guide
            {
                What = "Every UPROPERTY and UFUNCTION of a UCLASS/USTRUCT with specifiers split out (flags, key=value, meta=(...)), replication (Replicated / ReplicatedUsing) and RPCs (Server/Client/NetMulticast, Reliable), plus the type's own specifiers.",
                UseWhen = "you ask what is Blueprint-visible, replicated, an RPC or EditAnywhere on a type, or which C++ UFUNCTIONs Lua overrides (UnLua: same-named Lua functions are listed). Filter with 'specifiers' (e.g. [\"BlueprintReadWrite\"]), 'kind' or 'replicated_only'.",
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
                NotFor = "Lua GM / debug commands (use search_lua); Exec UFUNCTIONs (search_symbols with ue=[\"UFUNCTION\"]).",
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
                What = "Runs UEToVS's Unreal checks on whole C++ files: unknown member on a known type (UE0001), missing GENERATED_BODY (UE0003), hand-written body of a generated UFUNCTION (UE0004), ReplicatedUsing without OnRep (UE0005), replicated property not in GetLifetimeReplicatedProps (UE0006), unknown gameplay tag / asset path (UE0007/8), types UHT rejects (UE0009/10).",
                UseWhen = "right after you edit C++ files, before building — pass the changed paths. Only certain problems are reported.",
                InsteadOf = "a full build to find UHT/replication mistakes.",
                NotFor = "general C++ compile errors (build, then get_build_errors); engine files (skipped). Reads files as saved on disk.",
            },
            ["rename_symbol"] = new Guide
            {
                What = "Index-based rename of a project class, struct, enum, function, property or variable across every reference.",
                UseWhen = "you rename a C++ symbol. By DEFAULT ONLY PREVIEWS the edits (file, 1-based line/col, source line); call again with apply=true to write. Reflected types/properties get a [CoreRedirects] line in Config/DefaultEngine.ini (write_redirects, default true) so Blueprints keep loading.",
                InsteadOf = "search-and-replace or sed: renames the symbol, not same-named text; never touches engine/third-party files.",
                NotFor = "Lua names; file renames (files named after a class are not renamed). Name-only matches are left out unless include_name_only=true. After applying, run get_diagnostics.",
            },
            ["run_project_inspection"] = new Guide
            {
                What = "Whole-project run of the UEToVS UE rules (UE0001-UE0010, same as get_diagnostics) over every project source file.",
                UseWhen = "the user asks to check the whole project. Asynchronous: action 'start' returns at once; poll 'status' until Completed, then page 'results' (filter by rule / file); 'cancel' stops.",
                InsteadOf = "calling get_diagnostics file by file or a full rebuild.",
                NotFor = "checking a few edited files (use get_diagnostics). Heavy on large projects; refuses when switched off in the UEToVS settings.",
            },
            ["index_status"] = new Guide
            {
                What = "State of the UEToVS index: engine running, indexing phase/progress, file/symbol/asset counts, project/engine roots, UE version and the Lua index state.",
                UseWhen = "another UEToVS tool returned nothing or too little for a name that should exist, or before concluding the index is useless.",
                InsteadOf = "giving up on the index and grepping the whole tree at the first empty answer.",
                NotFor = "finding code (use the search tools). When it says indexing is unfinished or no Lua folder is set, grep the project's Source/Plugins/Config/Script folders for that question.",
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
