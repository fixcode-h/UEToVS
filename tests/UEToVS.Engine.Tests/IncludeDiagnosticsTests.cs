using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using UEToVS.Contracts;
using UEToVS.Engine.Includes;
using UEToVS.Engine.Indexing;
using UEToVS.Engine.IntelliSense;
using UEToVS.Engine.Unreal;
using Xunit;
using Xunit.Abstractions;

namespace UEToVS.Engine.Tests
{
    /// <summary>
    /// v0.11.0 rules that need the include model (UE0017…UE0023): a small engine + game tree with Build.cs files, parsed into a
    /// real index, the module catalog and the include graph wired like <see cref="IndexOrchestrator"/> does.
    /// </summary>
    public sealed class IncludeDiagnosticsFixture : IDisposable
    {
        private static readonly Dictionary<string, string> Tree = new Dictionary<string, string>
        {
            ["UE/Engine/Source/Runtime/Core/Core.Build.cs"] = Rules("Core"),
            ["UE/Engine/Source/Runtime/Core/Public/CoreMinimal.h"] = "#pragma once\n#include \"Containers/Array.h\"\nclass FString { public: int Len() const; };\nstruct FVector { float X; float Y; float Z; };\n",
            ["UE/Engine/Source/Runtime/Core/Public/Containers/Array.h"] = "#pragma once\ntemplate<typename T> class TArray { public: int Num() const; };\n",
            ["UE/Engine/Source/Runtime/Core/Private/CorePrivate.h"] = "#pragma once\nstruct FCorePrivate { int A; };\n",
            ["UE/Engine/Source/Runtime/CoreUObject/CoreUObject.Build.cs"] = Rules("CoreUObject", "PublicDependencyModuleNames.Add(\"Core\");"),
            ["UE/Engine/Source/Runtime/CoreUObject/Public/UObject/Object.h"] = "#pragma once\n#include \"CoreMinimal.h\"\nclass UObject { public: virtual void PostInitProperties(); };\ntemplate<class T> T* NewObject(UObject* Outer);\ntemplate<class T> T* Cast(UObject* Src);\n",
            ["UE/Engine/Source/Runtime/Engine/Engine.Build.cs"] = Rules("Engine", "PublicDependencyModuleNames.AddRange(new string[] { \"Core\", \"CoreUObject\" });"),
            ["UE/Engine/Source/Runtime/Engine/Classes/GameFramework/Actor.h"] = "#pragma once\n#include \"UObject/Object.h\"\nclass AActor : public UObject\n{\npublic:\n\tvirtual void BeginPlay();\n\tFVector GetActorLocation() const;\n};\n",
            ["UE/Engine/Source/Runtime/Engine/Classes/Kismet/GameplayStatics.h"] = "#pragma once\n#include \"UObject/Object.h\"\nclass UGameplayStatics : public UObject\n{\npublic:\n\tstatic float GetTime();\n};\n",
            ["UE/Engine/Source/Runtime/Engine/Public/Engine/HitResult.h"] = "#pragma once\n#include \"CoreMinimal.h\"\nstruct FHitResult { float Time; };\n",
            ["UE/Engine/Source/Runtime/Engine/Private/EnginePrivate.h"] = "#pragma once\nstruct FEnginePrivate { int A; };\n",
            ["UE/Engine/Source/Runtime/Legacy/Legacy.Build.cs"] = Rules("Legacy", "PublicDependencyModuleNames.Add(\"Core\");"),
            ["UE/Engine/Source/Runtime/Legacy/Public/Sub/LegacyThing.h"] = "#pragma once\nstruct FLegacyThing { int A; };\n",
            ["Game/Source/MyGame/MyGame.Build.cs"] = Rules("MyGame",
                "PublicDependencyModuleNames.AddRange(new string[] { \"Core\", \"CoreUObject\", \"Engine\" });\nPrivateDependencyModuleNames.Add(\"Legacy\");"),
            ["Game/Source/MyGame/Public/MyActor.h"] = "#pragma once\n#include \"GameFramework/Actor.h\"\n#include \"MyActor.generated.h\"\nUCLASS()\nclass AMyActor : public AActor\n{\n\tGENERATED_BODY()\npublic:\n\tvirtual void BeginPlay() override;\n\tint Health;\n\tvoid Fire();\n};\n",
            ["Game/Source/MyGame/Private/MyActor.cpp"] = "#include \"MyActor.h\"\nvoid AMyActor::Fire()\n{\n\tHealth = 1;\n}\n",
            ["Game/Source/Other/Other.Build.cs"] = Rules("Other", "PublicDependencyModuleNames.Add(\"Core\");"),
            ["Game/Source/Other/Public/OtherThing.h"] = "#pragma once\nstruct FOtherThing { int A; };\n",
            ["Game/Source/Other/Private/OtherPrivate.h"] = "#pragma once\nstruct FOtherPrivate { int A; };\n",
        };

        public IncludeDiagnosticsFixture()
        {
            Root = Path.Combine(Path.GetTempPath(), "uetovs-include-diag", Guid.NewGuid().ToString("N"));
            Backend = new SegmentedIndexBackend();
            Backend.TryLoad(Path.Combine(Root, ".index"), "include-diag");
            var parser = new SyntaxFileParser();
            var found = new List<FoundBuildCs>();
            foreach (var kv in Tree)
            {
                string path = P(kv.Key);
                Directory.CreateDirectory(Path.GetDirectoryName(path));
                File.WriteAllText(path, kv.Value);
                bool engine = kv.Key.StartsWith("UE/", StringComparison.Ordinal);
                var origin = engine ? ItemOrigin.Engine : ItemOrigin.Project;
                string module = kv.Key.Split('/')[engine ? 4 : 2];
                if (path.EndsWith(".Build.cs", StringComparison.Ordinal))
                {
                    found.Add(new FoundBuildCs { Path = path, Module = module, Origin = origin });
                    continue;
                }
                var listed = new ListedFile
                {
                    Path = path,
                    Name = Path.GetFileName(path),
                    Module = module,
                    Origin = origin,
                    Kind = path.EndsWith(".h", StringComparison.Ordinal) ? SourceFileKind.Header : SourceFileKind.Source,
                };
                Table.Upsert(listed);
                var ctx = new FileContext { Path = path, Module = module, Origin = origin, Kind = listed.Kind };
                Backend.UpdateParsed(path, parser.Parse(ctx, File.ReadAllBytes(path), CancellationToken.None));
            }
            var ws = new Workspace { EngineRoot = P("UE"), ProjectRoot = P("Game") };
            Modules = ModuleCatalog.Build(ws, found);
            Model = new IncludeModel(Table, () => Modules, () => ws);
            Table.Includes = Model;
            Model.Graph.IncludesOf = p => Backend.Store?.GetFile(p)?.Includes;
            Service = new IntelliSenseService(Backend, Table, () => Ready);
        }

        public string Root { get; }
        public SegmentedIndexBackend Backend { get; }
        public FileTable Table { get; } = new FileTable();
        public ModuleCatalog Modules { get; }
        public IncludeModel Model { get; }
        public IntelliSenseService Service { get; }
        public bool Ready { get; set; } = true;

        public string P(string relative) => Path.Combine(Root, relative.Replace('/', Path.DirectorySeparatorChar));

        private static string Rules(string name, string body = "") =>
            "using System.IO;\nusing UnrealBuildTool;\npublic class " + name + " : ModuleRules\n{\n\tpublic " + name + "(ReadOnlyTargetRules Target) : base(Target)\n\t{\n" + body + "\n\t}\n}\n";

        public void Dispose()
        {
            Backend.Dispose();
            try { Directory.Delete(Root, true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }
    }

    public sealed class IncludeDiagnosticsTests : IClassFixture<IncludeDiagnosticsFixture>
    {
        private static long _version = 50000;
        private readonly IncludeDiagnosticsFixture _f;
        private readonly ITestOutputHelper _out;

        public IncludeDiagnosticsTests(IncludeDiagnosticsFixture f, ITestOutputHelper output)
        {
            _f = f;
            _out = output;
        }

        private List<CodeDiagnostic> Run(string relative, string text, List<string> unsaved = null, List<string> disabled = null)
        {
            var req = new CodeDiagnosticsRequest
            {
                Document = new CaretRequest { FilePath = _f.P(relative), Text = text, TextVersion = Interlocked.Increment(ref _version) },
                StartLine = 0,
                EndLine = 100000,
                UnsavedFiles = unsaved ?? new List<string>(),
                DisabledRules = disabled,
            };
            var resp = _f.Service.GetDiagnostics(req, CancellationToken.None);
            var lines = text.Split('\n');
            foreach (var d in resp.Items)
                _out.WriteLine($"{d.Code} {d.Line + 1}:{d.Column + 1} '{lines[d.Line].Substring(d.Column, Math.Min(d.Length, lines[d.Line].Length - d.Column))}' {d.Message} [{string.Join(",", d.Args)}]");
            return resp.Items;
        }

        private static string At(string text, CodeDiagnostic d) => text.Split('\n')[d.Line].Substring(d.Column, d.Length);

        private const string Cpp = "Game/Source/MyGame/Private/Test.cpp";

        [Fact]
        public void Include_not_found_and_wrong_folder()
        {
            string text = "#include \"MyActor.h\"\n#include \"Nope/Missing.h\"\n#include \"Actor.h\"\n#include \"windows.h\"\n#include <vector>\n";
            var items = Run(Cpp, text);
            var missing = Assert.Single(items, d => At(text, d) == "\"Nope/Missing.h\"");
            Assert.Equal(CodeDiagnosticCodes.IncludeNotFound, missing.Code);
            Assert.Null(missing.Fixes);
            var wrong = Assert.Single(items, d => At(text, d) == "\"Actor.h\"");
            Assert.Equal(CodeDiagnosticCodes.IncludeNotFound, wrong.Code);
            Assert.Equal("\"GameFramework/Actor.h\"", Assert.Single(wrong.Fixes).Edits[0].NewText);
            Assert.Equal(2, items.Count);
        }

        [Fact]
        public void Include_of_private_header_and_of_a_module_that_is_not_a_dependency()
        {
            string text = "#include \"EnginePrivate.h\"\n#include \"OtherPrivate.h\"\n#include \"OtherThing.h\"\n";
            var items = Run(Cpp, text);
            Assert.Equal(2, items.Count);
            Assert.All(items, d => Assert.Equal(CodeDiagnosticCodes.IncludeOfPrivateHeader, d.Code));
            Assert.Equal("Engine", items[0].Args[1]);
            Assert.Equal("Other", items[1].Args[1]);
        }

        [Fact]
        public void Legacy_include_spelling_is_a_suggestion()
        {
            string text = "#include \"LegacyThing.h\"\n#include \"Sub/LegacyThing.h\"\n";
            var items = Run(Cpp, text);
            var d = Assert.Single(items);
            Assert.Equal(CodeDiagnosticCodes.IncludeSpelling, d.Code);
            Assert.Equal(CodeDiagnosticSeverity.Info, d.Severity);
            Assert.Equal(0, d.Line);
            Assert.Equal("\"Sub/LegacyThing.h\"", d.Fixes[0].Edits[0].NewText);
        }

        [Fact]
        public void Generated_header_missing_misnamed_and_not_last()
        {
            string h = "Game/Source/MyGame/Public/Bad.h";
            string text = "#pragma once\n#include \"UObject/Object.h\"\nUCLASS()\nclass UBad : public UObject\n{\n\tGENERATED_BODY()\n};\n";
            var d = Assert.Single(Run(h, text));
            Assert.Equal(CodeDiagnosticCodes.GeneratedInclude, d.Code);
            Assert.Equal("missing", d.Args[0]);
            Assert.Equal("UBad", At(text, d));
            Assert.Equal(2, d.Fixes[0].Edits[0].Line);
            Assert.Equal("#include \"Bad.generated.h\"\n", d.Fixes[0].Edits[0].NewText);

            text = "#pragma once\n#include \"Old.generated.h\"\n#include \"UObject/Object.h\"\nUCLASS()\nclass UBad : public UObject\n{\n\tGENERATED_BODY()\n};\n";
            var items = Run(h, text);
            Assert.Equal(new[] { "name", "not-last" }, items.Select(x => x.Args[0]).ToArray());
            Assert.Equal("\"Bad.generated.h\"", items[0].Fixes[0].Edits[0].NewText);

            // no reflected type: nothing to say
            Assert.Empty(Run(h, "#pragma once\n#include \"CoreMinimal.h\"\nclass FPlain {};\n"));
        }

        [Fact]
        public void Syntax_errors()
        {
            string text = "#include \"MyActor.h\"\nstruct FA { int A; }\nstruct FB { int B; };\nvoid F()\n{\n\tint A = 1\n}\nvoid G()\n{\n\tF(;\n}\nvoid H()\n{\n\tint B = (1 + 2));\n}\n";
            var items = Run(Cpp, text, disabled: new List<string> { CodeDiagnosticCodes.UnknownIdentifier, CodeDiagnosticCodes.MissingInclude });
            Assert.All(items, d => Assert.Equal(CodeDiagnosticCodes.SyntaxError, d.Code));
            Assert.Contains(items, d => d.Args[0] == "missing-semicolon-after-type" && d.Line == 1);
            Assert.Contains(items, d => d.Args[0] == "missing-semicolon" && d.Line == 5);
            Assert.Contains(items, d => d.Args[0] == "unbalanced-paren" && d.Line == 9);
            Assert.Contains(items, d => d.Args[0] == "unbalanced-paren" && d.Line == 13);
        }

        [Fact]
        public void Syntax_no_false_positives()
        {
            string text = "#include \"MyActor.h\"\n" +
                          "struct FA { int A; } GA;\n" +
                          "enum class EColor { Red, Green };\n" +
                          "int Sum(int A, int B)\n{\n" +
                          "\tFVector V{ 1, 2, 3 };\n" +
                          "\tint Arr[] = { 1, 2 };\n" +
                          "\tauto L = [&](int Q) { return Q + A; };\n" +
                          "\tfor (int I = 0; I < 3; I++) { B += I; }\n" +
                          "\tif (A) { return L(B); } else { return B; }\n" +
                          "\tdo { A--; } while (A > 0);\n" +
                          "\tswitch (A) { case 1: { break; } default: break; }\n" +
                          "\treturn FVector{ 1, 2, 3 }.X > 0 ? A : B;\n" +
                          "}\n" +
                          "#if SOMETHING\nvoid K() {\n#else\nvoid K(int) {\n#endif\n}\n";
            Assert.Empty(Run(Cpp, text, disabled: new List<string> { CodeDiagnosticCodes.UnknownIdentifier, CodeDiagnosticCodes.MissingInclude }));
        }

        [Fact]
        public void Missing_include_for_types_used_by_value_or_qualified()
        {
            string text = "#include \"MyActor.h\"\nvoid AMyActor::Fire()\n{\n\tfloat T = UGameplayStatics::GetTime();\n\tFHitResult* P = nullptr;\n\tFHitResult Hit;\n\tFHitResult Again;\n\tAActor* A = Cast<AActor>(this);\n\tFString S;\n}\n";
            var items = Run(Cpp, text);
            Assert.Equal(2, items.Count);
            var stat = items.Single(d => d.Args[0] == "UGameplayStatics");
            Assert.Equal(CodeDiagnosticCodes.MissingInclude, stat.Code);
            Assert.Equal(CodeDiagnosticSeverity.Warning, stat.Severity);
            Assert.Equal("Kismet/GameplayStatics.h", stat.Args[1]);
            Assert.Equal("missing", stat.Args[2]);
            Assert.Equal("add-include", stat.Fixes[0].Kind);
            var hit = items.Single(d => d.Args[0] == "FHitResult");
            Assert.Equal(5, hit.Line); // the by-value use, not the pointer
            Assert.Equal("Engine/HitResult.h", hit.Args[1]);

            // forward declaration only
            text = "#include \"MyActor.h\"\nclass UGameplayStatics;\nvoid AMyActor::Fire()\n{\n\tfloat T = UGameplayStatics::GetTime();\n}\n";
            Assert.Equal("incomplete", Assert.Single(Run(Cpp, text)).Args[2]);

            // included: nothing
            text = "#include \"MyActor.h\"\n#include \"Kismet/GameplayStatics.h\"\nvoid AMyActor::Fire()\n{\n\tfloat T = UGameplayStatics::GetTime();\n}\n";
            Assert.Empty(Run(Cpp, text));
        }

        [Fact]
        public void Unknown_identifiers_with_suggestions()
        {
            string text = "#include \"MyActor.h\"\nvoid AMyActor::Fire()\n{\n" +
                          "\tHealht = 3;\n" +
                          "\tHealth = 2;\n" +
                          "\tint Local = 0;\n" +
                          "\tLocla = 1;\n" +
                          "\tauto L = [](int Q) { return Q; };\n" +
                          "\tUE_LOG(LogTemp, Log, TEXT(\"x\"));\n" +
                          "\tprintf(\"x\");\n" +
                          "\tTAG_Weapon_Fire.IsValid();\n" +
                          "\tFString S; S.Len();\n" +
                          "}\n";
            var items = Run(Cpp, text);
            Assert.Equal(new[] { "Healht", "Locla" }, items.Select(d => At(text, d)).ToArray());
            Assert.All(items, d => Assert.Equal(CodeDiagnosticCodes.UnknownIdentifier, d.Code));
            Assert.Equal("Health", items[0].Args[1].Split('|')[0]);
            Assert.Equal("Local", items[1].Args[1].Split('|')[0]);
            Assert.Equal("Health", items[0].Fixes[0].Edits[0].NewText);

            // another file with unsaved changes may declare it: silent
            Assert.Empty(Run(Cpp, text, unsaved: new List<string> { _f.P("Game/Source/MyGame/Public/MyActor.h") }));
            // index not ready: silent
            _f.Ready = false;
            try { Assert.Empty(Run(Cpp, text)); }
            finally { _f.Ready = true; }
        }

        [Fact]
        public void Rules_can_be_switched_off()
        {
            string text = "#include \"Nope/Missing.h\"\nvoid F()\n{\n\tHealht = 3;\n}\n";
            Assert.Equal(2, Run(Cpp, text).Count);
            Assert.Empty(Run(Cpp, text, disabled: new List<string> { CodeDiagnosticCodes.IncludeNotFound, CodeDiagnosticCodes.UnknownIdentifier }));
        }

        [Fact]
        public void Platform_header_macro_resolves()
        {
            Assert.Equal("Windows/WindowsPlatform.h", IncludeGraph.PlatformHeader("COMPILED_PLATFORM_HEADER(Platform.h)"));
            Assert.Equal("HAL/Windows/WindowsPlatformMisc.h", IncludeGraph.PlatformHeader("COMPILED_PLATFORM_HEADER_WITH_PREFIX(HAL, PlatformMisc.h)"));
            Assert.Null(IncludeGraph.PlatformHeader("SOME_OTHER(X.h)"));
        }
    }
}
