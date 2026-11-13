using System.Collections.Generic;
using System.Linq;
using UEToVS.Contracts;
using UEToVS.Host.Logic.Commands;
using UEToVS.Host.Logic.Editing;
using UEToVS.Host.Logic.Localization;
using UEToVS.Host.Logic.Refactoring;
using Xunit;

namespace UEToVS.Host.Logic.Tests
{
    /// <summary>v0.5.12 灯泡菜单：UE 规则修复的文本计算和各 provider 的出现条件。</summary>
    public sealed class LightBulbTests
    {
        private static CodeDiagnostic D(string code, int line, int col, int len, params string[] args) =>
            new CodeDiagnostic { Code = code, Line = line, Column = col, Length = len, Args = args.ToList() };

        private static string Apply(string text, IEnumerable<TextEdit> edits, string file)
        {
            string r = TextEditApplier.Apply(text, edits.Where(e => e.FilePath == file), out int skipped);
            Assert.Equal(0, skipped);
            return r;
        }

        private static RefactorContext Ctx(string file, string text, int caret, int selStart = -1, int selEnd = -1) => new RefactorContext
        {
            FilePath = file, TextSource = () => text, CaretOffset = caret, SelectionStart = selStart, SelectionEnd = selEnd,
        };

        // ============================================================ planner

        [Fact]
        public void GeneratedBody_InsertedBelowTheBrace()
        {
            const string h = "UCLASS()\nclass AFoo : public AActor\n{\npublic:\n\tint32 X;\n};\n";
            var edits = UeRuleFixPlanner.GeneratedBody("A.h", h, 1, 6, out string problem);
            Assert.Null(problem);
            Assert.Equal("UCLASS()\nclass AFoo : public AActor\n{\n\tGENERATED_BODY()\npublic:\n\tint32 X;\n};\n", Apply(h, edits, "A.h"));

            Assert.Null(UeRuleFixPlanner.GeneratedBody("A.h", "UCLASS()\nclass AFoo { int32 X; };\n", 1, 6, out problem));
            Assert.Equal("brace-not-at-line-end", problem);
            Assert.Null(UeRuleFixPlanner.GeneratedBody("A.h", "UCLASS()\nclass AFoo;\n", 1, 6, out problem));
            Assert.Equal("no-body", problem);
        }

        [Fact]
        public void GeneratedBody_KeepsCrLfAndIndent()
        {
            const string h = "namespace N\r\n{\r\n\tUSTRUCT()\r\n\tstruct FBar\r\n\t{\r\n\t};\r\n}\r\n";
            var edits = UeRuleFixPlanner.GeneratedBody("B.h", h, 3, 8, out _);
            Assert.Contains("\t{\r\n\t\tGENERATED_BODY()\r\n\t};", Apply(h, edits, "B.h"));
        }

        [Fact]
        public void RenameToImplementation_OnlyOutOfLine()
        {
            const string cpp = "void AFoo::ServerFire(int32 A)\n{\n}\n";
            var edits = UeRuleFixPlanner.RenameToImplementation("A.cpp", cpp, 0, 11, "ServerFire", "Server", out string problem);
            Assert.Null(problem);
            Assert.Equal("void AFoo::ServerFire_Implementation(int32 A)\n{\n}\n", Apply(cpp, edits, "A.cpp"));

            Assert.Null(UeRuleFixPlanner.RenameToImplementation("A.cpp", cpp + "void AFoo::ServerFire_Implementation(int32 A) {}\n", 0, 11, "ServerFire", "Server", out problem));
            Assert.Equal("exists", problem);
            Assert.Null(UeRuleFixPlanner.RenameToImplementation("A.h", "\tvoid ServerFire() {}\n", 0, 6, "ServerFire", "Server", out problem));
            Assert.Equal("in-class", problem);
            Assert.Null(UeRuleFixPlanner.RenameToImplementation("A.cpp", cpp, 0, 11, "ServerFire", "BlueprintImplementableEvent", out problem));
            Assert.Null(UeRuleFixPlanner.RenameToImplementation("A.cpp", cpp, 0, 12, "ServerFire", "Server", out problem));
            Assert.Equal("stale", problem);
        }

        [Fact]
        public void RegisterReplicated_AddsMissingAndInclude()
        {
            const string cpp = "#include \"Foo.h\"\n\nvoid AFoo::GetLifetimeReplicatedProps(TArray<FLifetimeProperty>& Out) const\n{\n" +
                               "\tSuper::GetLifetimeReplicatedProps(Out);\n\tDOREPLIFETIME(AFoo, A);\n}\n";
            int col = "void AFoo::".Length;
            var edits = UeRuleFixPlanner.RegisterReplicated("A.cpp", cpp, 2, col, "AFoo", new[] { "A", "B" }, out string problem);
            Assert.Null(problem);
            string r = Apply(cpp, edits, "A.cpp");
            Assert.Contains("\tDOREPLIFETIME(AFoo, A);\n\tDOREPLIFETIME(AFoo, B);\n}\n", r);
            Assert.Single(System.Text.RegularExpressions.Regex.Matches(r, @"DOREPLIFETIME\(AFoo, A\)"));
            Assert.Contains("#include \"Foo.h\"\n#include \"Net/UnrealNetwork.h\"\n", r);

            Assert.Null(UeRuleFixPlanner.RegisterReplicated("A.cpp", cpp, 2, col, "AFoo", new[] { "A" }, out problem));
            Assert.Equal("exists", problem);
        }

        [Fact]
        public void RegisterReplicated_OneLineBody()
        {
            const string cpp = "#include \"Net/UnrealNetwork.h\"\nvoid AFoo::GetLifetimeReplicatedProps(TArray<FLifetimeProperty>& Out) const { Super::GetLifetimeReplicatedProps(Out); }\n";
            var edits = UeRuleFixPlanner.RegisterReplicated("A.cpp", cpp, 1, "void AFoo::".Length, "AFoo", new[] { "B" }, out _);
            Assert.Single(edits); // include already there
            Assert.Contains("(Out); \n\tDOREPLIFETIME(AFoo, B);\n}", Apply(cpp, edits, "A.cpp"));
        }

        [Fact]
        public void RepNotify_DeclaresBelowPropertyAndDefinesInSource()
        {
            const string h = "UCLASS()\nclass AFoo : public AActor\n{\n\tGENERATED_BODY()\n\n\tUPROPERTY(ReplicatedUsing = OnRep_Health)\n\tfloat Health = 100.f;\n};\n";
            const string cpp = "#include \"Foo.h\"\n";
            var edits = UeRuleFixPlanner.RepNotify("A.h", h, 6, 7, "OnRep_Health", "AFoo", "A.cpp", cpp, out string problem);
            Assert.Null(problem);
            Assert.Contains("\tfloat Health = 100.f;\n\n\tUFUNCTION()\n\tvoid OnRep_Health();\n};", Apply(h, edits, "A.h"));
            Assert.Equal("#include \"Foo.h\"\n\nvoid AFoo::OnRep_Health()\n{\n}\n", Apply(cpp, edits, "A.cpp"));

            // no source file: declaration only
            Assert.Single(UeRuleFixPlanner.RepNotify("A.h", h, 6, 7, "OnRep_Health", "AFoo", null, null, out _));
            // already declared somewhere in the header
            Assert.Null(UeRuleFixPlanner.RepNotify("A.h", h.Replace("};", "\tvoid OnRep_Health();\n};"), 6, 7, "OnRep_Health", "AFoo", null, null, out problem));
            Assert.Equal("exists", problem);
        }

        // ============================================================ providers

        [Fact]
        public void RuleFixProvider_OffersOnlyCertainFixes()
        {
            const string cpp = "void AFoo::ServerFire()\n{\n}\n";
            var c = Ctx("C:/P/A.cpp", cpp, 12);
            c.Diagnostics = new List<CodeDiagnostic>
            {
                D(CodeDiagnosticCodes.GeneratedFunctionBody, 0, 11, 10, "ServerFire", "Server", "AFoo"),
                D(CodeDiagnosticCodes.UnregisteredReplicatedProperty, 4, 11, 26, "A", "AFoo"),
                D(CodeDiagnosticCodes.UnregisteredReplicatedProperty, 4, 11, 26, "B", "AFoo"),
                D(CodeDiagnosticCodes.MissingModuleDependency, 0, 10, 6, "UMG.h", "UMG", "MyGame", "C:/P/MyGame.Build.cs"),
                D(CodeDiagnosticCodes.MissingRepNotify, 0, 0, 1, "Health", "OnRep_Health", "AFoo", "not-ufunction"),
            };
            var items = new UeRuleFixProvider().GetActions(c).ToList();
            Assert.All(items, i => Assert.Equal(UeRuleFixIds.Fix, i.ActionId));
            var codes = items.Select(i => ((UeRuleFix)i.Argument).Code).ToList();
            Assert.Single(codes, x => x == CodeDiagnosticCodes.GeneratedFunctionBody);
            var reg = Assert.Single(items, i => ((UeRuleFix)i.Argument).Code == CodeDiagnosticCodes.UnregisteredReplicatedProperty);
            Assert.Equal(new[] { "A", "B" }, ((UeRuleFix)reg.Argument).Properties);
            var deps = items.Where(i => ((UeRuleFix)i.Argument).Code == CodeDiagnosticCodes.MissingModuleDependency).OrderByDescending(i => i.Priority).ToList();
            Assert.Equal(2, deps.Count);
            Assert.False(((UeRuleFix)deps[0].Argument).IsPublic); // .cpp: Private first
            Assert.DoesNotContain(CodeDiagnosticCodes.MissingRepNotify, codes); // not-ufunction: no automatic fix

            // in-class body, missing Build.cs path, engine file: nothing
            var inClass = Ctx("C:/P/A.h", "\tvoid ServerFire() {}\n", 8);
            inClass.Diagnostics = new List<CodeDiagnostic>
            {
                D(CodeDiagnosticCodes.GeneratedFunctionBody, 0, 6, 10, "ServerFire", "Server", "AFoo"),
                D(CodeDiagnosticCodes.MissingModuleDependency, 0, 10, 6, "UMG.h", "UMG", "MyGame"),
            };
            Assert.Empty(new UeRuleFixProvider().GetActions(inClass));
            c.IsProjectFile = false;
            Assert.Empty(new UeRuleFixProvider().GetActions(c));
        }

        [Fact]
        public void RuleFixProvider_RepNotifyAndGeneratedBodyInHeader()
        {
            var c = Ctx("C:/P/A.h", "x\n", 0);
            c.Diagnostics = new List<CodeDiagnostic>
            {
                D(CodeDiagnosticCodes.MissingRepNotify, 6, 7, 6, "Health", "OnRep_Health", "AFoo", "missing"),
                D(CodeDiagnosticCodes.MissingGeneratedBody, 1, 6, 4, "UCLASS", "AFoo"),
                D(CodeDiagnosticCodes.MissingModuleDependency, 0, 10, 6, "UMG.h", "UMG", "MyGame", "C:/P/MyGame.Build.cs"),
            };
            var items = new UeRuleFixProvider().GetActions(c).ToList();
            Assert.Contains(items, i => ((UeRuleFix)i.Argument).Code == CodeDiagnosticCodes.MissingRepNotify);
            Assert.Contains(items, i => ((UeRuleFix)i.Argument).Code == CodeDiagnosticCodes.MissingGeneratedBody);
            var best = items.Where(i => ((UeRuleFix)i.Argument).Code == CodeDiagnosticCodes.MissingModuleDependency).OrderByDescending(i => i.Priority).First();
            Assert.True(((UeRuleFix)best.Argument).IsPublic); // header: Public first
        }

        private const string Header =
            "#pragma once\n#include \"Foo.generated.h\"\n\nUCLASS()\nclass AFoo : public AActor\n{\n\tGENERATED_BODY()\n\n" +
            "\tUPROPERTY(Replicated)\n\tint32 Ammo;\n\n\tvoid Fire(int32 A);\n\n\t// Fires the weapon\n\tint32 Count;\n};\n";

        private static int At(string text, string marker) => text.IndexOf(marker, System.StringComparison.Ordinal) + 1;

        [Fact]
        public void GenerateProvider_ConstructorReplicationRpc()
        {
            var gen = new LightBulbProviders.GenerateProvider();
            var ids = gen.GetActions(Ctx("C:/P/Foo.h", Header, At(Header, "int32 Ammo"))).Select(i => i.ActionId).ToList();
            Assert.Contains(CommandCatalog.ActionIds.GenerateConstructor, ids);
            Assert.Contains(CommandCatalog.ActionIds.GenerateReplication, ids);
            Assert.DoesNotContain(CommandCatalog.ActionIds.GenerateRpc, ids); // caret not on a function declaration

            ids = gen.GetActions(Ctx("C:/P/Foo.h", Header, At(Header, "Fire("))).Select(i => i.ActionId).ToList();
            Assert.Contains(CommandCatalog.ActionIds.GenerateRpc, ids);

            // constructor exists; .cpp; outside any class
            string withCtor = Header.Replace("\tvoid Fire(int32 A);", "\tAFoo();\n\tvoid Fire(int32 A);");
            Assert.DoesNotContain(CommandCatalog.ActionIds.GenerateConstructor, gen.GetActions(Ctx("C:/P/Foo.h", withCtor, At(withCtor, "int32 Ammo"))).Select(i => i.ActionId));
            Assert.Empty(gen.GetActions(Ctx("C:/P/Foo.cpp", Header, At(Header, "int32 Ammo"))));
            Assert.Empty(gen.GetActions(Ctx("C:/P/Foo.h", Header, 3)));
        }

        [Fact]
        public void NavigateProvider_GeneratedHeaderAndImplementation()
        {
            var nav = new LightBulbProviders.NavigateProvider();
            var item = Assert.Single(nav.GetActions(Ctx("C:/P/Foo.h", Header, At(Header, "Foo.generated"))));
            Assert.Equal(LightBulbActionIds.OpenGeneratedHeader, item.ActionId);
            Assert.Equal("Foo.generated.h", item.Argument);
            item = Assert.Single(nav.GetActions(Ctx("C:/P/Foo.h", Header, At(Header, "GENERATED_BODY"))));
            Assert.Equal("Foo.generated.h", item.Argument);
            Assert.Empty(nav.GetActions(Ctx("C:/P/Foo.h", Header, At(Header, "int32 Ammo"))));

            var c = Ctx("C:/P/Foo.h", Header, At(Header, "Fire("));
            c.Engine = new RefactoringContextResponse
            {
                Function = new FunctionSignature { Name = "ServerFire", IsDefinition = false },
                UeCounterpartDefinitions = new List<SymbolInfo>
                {
                    new SymbolInfo { Name = "ServerFire_Validate", Location = new SourceLocation { FilePath = "C:/P/Foo.cpp", Line = 20 } },
                    new SymbolInfo { Name = "ServerFire_Implementation", Location = new SourceLocation { FilePath = "C:/P/Foo.cpp", Line = 10 } },
                },
            };
            var go = nav.GetActions(c).Where(i => i.ActionId == LightBulbActionIds.GoToLocation).ToList();
            Assert.Equal(2, go.Count);
            Assert.Equal(10, ((SourceLocation)go[0].Argument).Line);
        }

        [Fact]
        public void TranslateAndSurround_FollowSelectionAndComments()
        {
            var tr = new LightBulbProviders.TranslateProvider(() => true);
            var sur = new LightBulbProviders.SurroundProvider(() => true);
            int comment = At(Header, "Fires the");
            Assert.Equal(LightBulbStrings.TranslateComment, Assert.Single(tr.GetActions(Ctx("C:/P/Foo.h", Header, comment))).Title);
            Assert.Empty(tr.GetActions(Ctx("C:/P/Foo.h", Header, At(Header, "int32 Ammo"))));
            Assert.Empty(sur.GetActions(Ctx("C:/P/Foo.h", Header, comment)));

            int a = At(Header, "UPROPERTY(Replicated)") - 1, b = At(Header, "int32 Ammo;") + 10;
            var multi = Ctx("C:/P/Foo.h", Header, b, a, b);
            Assert.Equal(EditorFeatureIds.SurroundWith, Assert.Single(sur.GetActions(multi)).ActionId);
            Assert.Equal(LightBulbStrings.TranslateSelection, Assert.Single(tr.GetActions(multi)).Title);
            var word = Ctx("C:/P/Foo.h", Header, a + 3, a, a + 3);
            Assert.Empty(sur.GetActions(word)); // single-line selection
            Assert.Empty(new LightBulbProviders.SurroundProvider(() => false).GetActions(multi));
            Assert.Empty(new LightBulbProviders.TranslateProvider(() => false).GetActions(multi));
        }

        [Fact]
        public void Helpers()
        {
            Assert.True(UeRuleFixPlanner.IsOutOfLineName("void AFoo:: Fire()", 12));
            Assert.False(UeRuleFixPlanner.IsOutOfLineName("void Fire()", 5));
            Assert.True(LightBulbProviders.IsCommentLine("   // x"));
            Assert.True(LightBulbProviders.IsCommentLine(" * x"));
            Assert.False(LightBulbProviders.IsCommentLine("*Ptr = 1;"));
            Assert.Equal((1, 2), UeRuleFixPlanner.PositionOf(new[] { 0, 5, 9 }, 7));
        }
    }
}
