using System;
using System.Collections.Generic;
using System.Linq;
using UEToVS.Contracts;
using UEToVS.Core.Localization;
using UEToVS.Host.Logic.Commands;
using UEToVS.Host.Logic.Localization;
using UEToVS.Host.Logic.Refactoring;
using Xunit;

namespace UEToVS.Host.Logic.Tests
{
    public class RefactoringLogicTests
    {
        private static FunctionSignature Fn(string name, string ret, string pars, string container = "AMyActor", FunctionModifiers mods = FunctionModifiers.None,
                                            UeFlags ue = UeFlags.None, string specifiers = null) => new FunctionSignature
        {
            Name = name, ReturnType = ret, Parameters = pars, Container = container, Kind = SymbolKind.Method, Modifiers = mods, Ue = ue, Specifiers = specifiers,
        };

        // ---------------------------------------------------------------- SignatureText

        [Fact]
        public void DefinitionStripsDefaultsAndKeepsConst()
        {
            var f = Fn("Fire", "int32", "float Speed = 1.f, const FString& Name = TEXT(\"a,b\")", mods: FunctionModifiers.Const | FunctionModifiers.Virtual);
            string text = SignatureText.Definition(f, "AMyActor");
            Assert.Equal("int32 AMyActor::Fire(float Speed, const FString& Name) const\n{\n}\n", text);
            Assert.DoesNotContain("virtual", text);
        }

        [Fact]
        public void SplitParametersRespectsNesting()
        {
            var p = SignatureText.SplitParameters("TMap<int32, FString> A, TFunction<void(int, int)> B, int C = Max(1, 2)");
            Assert.Equal(3, p.Count);
            Assert.Equal("int C", SignatureText.StripDefaults("int C = Max(1, 2)"));
        }

        [Fact]
        public void UnnamedParametersGetNamesAndForwarding()
        {
            string named = SignatureText.NameParameters("float, const FHitResult&");
            Assert.Equal("float Arg0, const FHitResult& Arg1", named);
            Assert.Equal("Arg0, Arg1", SignatureText.ForwardArguments(named));
            Assert.Equal("Other", SignatureText.ParameterName("AActor* Other"));
        }

        [Fact]
        public void RpcGeneratesImplementationAndValidate()
        {
            var f = Fn("ServerFire", "void", "int32 Ammo", ue: UeFlags.UFunction, specifiers: "Server, Reliable, WithValidation");
            var bodies = SignatureText.Implementations(f);
            Assert.Equal(new[] { "ServerFire_Implementation", "ServerFire_Validate" }, bodies.Select(b => b.Name));
            Assert.StartsWith("void AMyActor::ServerFire_Implementation(int32 Ammo)", bodies[0].Text);
            Assert.StartsWith("bool AMyActor::ServerFire_Validate(int32 Ammo)", bodies[1].Text);
            Assert.Contains("return true;", bodies[1].Text);

            var bne = Fn("OnHit", "void", "", ue: UeFlags.UFunction, specifiers: "BlueprintNativeEvent, Category = \"X\"");
            Assert.Equal(new[] { "OnHit_Implementation" }, SignatureText.Implementations(bne).Select(b => b.Name));
            var plain = Fn("Tick", "void", "float DeltaTime");
            Assert.Equal(new[] { "Tick" }, SignatureText.Implementations(plain).Select(b => b.Name));
            Assert.False(SignatureText.HasSpecifier("meta=(Server)", "Server"));
        }

        [Fact]
        public void OverrideTextCallsSuperUnlessPureOrInterface()
        {
            var f = Fn("GetHealth", "float", "int32 = 0", container: "ABase", mods: FunctionModifiers.Virtual | FunctionModifiers.Const);
            f.DeclaringType = "ABase";
            Assert.Equal("\tvirtual float GetHealth(int32 Arg0) const override;\n", SignatureText.OverrideDeclaration(f));
            string def = SignatureText.OverrideDefinition(f, "AMyActor");
            Assert.Contains("float AMyActor::GetHealth(int32 Arg0) const", def);
            Assert.Contains("return Super::GetHealth(Arg0);", def);

            var pure = Fn("Use", "bool", "", container: "IUsable", mods: FunctionModifiers.Virtual | FunctionModifiers.Pure);
            pure.DeclaringType = "IUsable";
            string pd = SignatureText.OverrideDefinition(pure, "AMyActor");
            Assert.DoesNotContain("Super::", pd);
            Assert.Contains("return {};", pd);
        }

        [Fact]
        public void DeclarationFromDefinition()
        {
            var f = Fn("Reload", "void", "int32 Count", mods: FunctionModifiers.Const);
            Assert.Equal("\tvoid Reload(int32 Count) const;\n", SignatureText.Declaration(f));
            Assert.Equal("AMyActor", SignatureText.ClassName("Game::AMyActor"));
        }

        // ---------------------------------------------------------------- CppTextScan / CodePlacement

        private const string Header =
            "#pragma once\n" +
            "#include \"CoreMinimal.h\"\n" +
            "#include \"MyActor.generated.h\"\n" +
            "\n" +
            "UCLASS()\n" +
            "class AMyActor : public AActor\n" +
            "{\n" +
            "\tGENERATED_BODY()\n" +
            "public:\n" +
            "\tvoid A();\n" +
            "\tvoid B(int X = 1);\n" +
            "\t// \"}\" in a comment\n" +
            "\tvoid C();\n" +
            "protected:\n" +
            "\tint32 Value;\n" +
            "};\n";

        private const string Cpp =
            "#include \"MyActor.h\"\n" +
            "\n" +
            "void AMyActor::A()\n" +
            "{\n" +
            "}\n" +
            "\n" +
            "void AMyActor::C()\n" +
            "{\n" +
            "\tconst char* S = \"}\";\n" +
            "}\n";

        [Fact]
        public void ScannerFindsClassBodyAndSections()
        {
            var t = CppTextScan.Tokenize(Header);
            var body = CppTextScan.FindClassBody(t, "AMyActor");
            Assert.NotNull(body);
            Assert.Equal(6, body.OpenLine);
            Assert.Equal(15, body.CloseLine);
            var sections = CppTextScan.Sections(t, body);
            Assert.Contains(sections, s => s.Access == AccessKind.Public && s.LastMemberLine == 12);
            Assert.Contains(sections, s => s.Access == AccessKind.Protected);
            Assert.Equal(10, CppTextScan.MemberDeclarationEndLine(t, body, "B"));
            Assert.Equal(new[] { "A", "B" }, CppTextScan.MemberFunctionNamesBefore(t, body, 12));
        }

        [Fact]
        public void ScannerFindsMemberDefinitionsIgnoringStrings()
        {
            var defs = CppTextScan.MemberDefinitions(Cpp, "AMyActor");
            Assert.Equal(new[] { "A", "C" }, defs.Select(d => d.Name));
            Assert.Equal(9, defs[1].EndLine);
            Assert.True(CodePlacement.HasDefinition(Cpp, "AMyActor", "C"));
            Assert.False(CodePlacement.HasDefinition(Cpp, "AMyActor", "B"));
        }

        [Fact]
        public void ImplementationGoesAfterPreviousMemberDefinition()
        {
            var before = CodePlacement.DeclaredBefore(Header, "AMyActor", 5, 10);
            Assert.Equal(new[] { "A" }, before);
            var p = CodePlacement.Implementation(Cpp, "AMyActor", before, "void AMyActor::B(int X)\n{\n}\n");
            Assert.Equal(5, p.Line);
            Assert.Equal("after:A", p.Reason);
            Assert.StartsWith("\n", p.Text);

            var none = CodePlacement.Implementation("#include \"X.h\"\n", "AOther", null, "void AOther::F()\n{\n}\n");
            Assert.Equal("end-of-file", none.Reason);
        }

        [Fact]
        public void DeclarationGoesNextToNeighbourOrIntoSection()
        {
            var p = CodePlacement.Declaration(Header, "AMyActor", 5, new[] { "A" }, AccessKind.Public, new[] { "void D();" });
            Assert.Equal(10, p.Line);
            Assert.Equal("\tvoid D();\n", p.Text);

            var s = CodePlacement.Declaration(Header, "AMyActor", 5, null, AccessKind.Public, new[] { "void D();" });
            Assert.Equal(13, s.Line);
            Assert.Equal("section:public", s.Reason);

            // class members before the first label are private (after GENERATED_BODY)
            var implicitPrivate = CodePlacement.Declaration(Header, "AMyActor", 5, null, AccessKind.Private, new[] { "void D();" });
            Assert.Equal("section:private", implicitPrivate.Reason);
            Assert.Equal(8, implicitPrivate.Line);

            const string onlyPublic = "struct FData\n{\npublic:\n\tint32 A;\n};\n";
            var n = CodePlacement.Declaration(onlyPublic, "FData", -1, null, AccessKind.Protected, new[] { "void D();" });
            Assert.Equal("new-section", n.Reason);
            Assert.Equal(4, n.Line);
            Assert.Equal("protected:\n\tvoid D();\n", n.Text);

            Assert.Null(CodePlacement.Declaration(Header, "ANotThere", -1, null, AccessKind.Public, new[] { "void D();" }));
            Assert.Equal(new[] { "A" }, CodePlacement.DefinedBefore(Cpp, "AMyActor", 6));
        }

        // ---------------------------------------------------------------- TextEditApplier

        [Fact]
        public void EditsApplyAndSkipChangedText()
        {
            string text = "int Old = 1;\nOld++;\n";
            var edits = new[]
            {
                new TextEdit { FilePath = "a", Line = 0, Column = 4, Length = 3, OldText = "Old", NewText = "New" },
                new TextEdit { FilePath = "a", Line = 1, Column = 0, Length = 3, OldText = "Old", NewText = "New" },
                new TextEdit { FilePath = "a", Line = 1, Column = 1, Length = 3, OldText = "Old", NewText = "New" }, // stale
                TextEdit.Insert("a", 2, "// end\n"),
            };
            string result = TextEditApplier.Apply(text, edits, out int skipped);
            Assert.Equal(1, skipped);
            Assert.Equal("int New = 1;\nNew++;\n// end\n", result);
        }

        // ---------------------------------------------------------------- IncludePlanner

        [Fact]
        public void UeIncludePathIsModuleRelative()
        {
            Assert.Equal("GameFramework/Actor.h", IncludePlanner.UeIncludePath(@"C:\UE\Engine\Source\Runtime\Engine\Classes\GameFramework\Actor.h"));
            Assert.Equal("Chars/Hero.h", IncludePlanner.UeIncludePath("/p/Source/MyGame/Public/Chars/Hero.h", "/p/Source/MyGame"));
            Assert.Equal("Misc/Util.h", IncludePlanner.UeIncludePath("/p/Source/MyGame/Misc/Util.h", "/p/Source/MyGame"));
            Assert.Equal("Util.h", IncludePlanner.UeIncludePath("/x/Util.h"));
        }

        [Fact]
        public void IncludeIsPlannedBeforeGeneratedHeader()
        {
            var p = IncludePlanner.Plan(Header, "Components/BoxComponent.h");
            Assert.Equal(2, p.Line);
            Assert.Equal("before-generated", p.Reason);
            Assert.Equal("#include \"Components/BoxComponent.h\"\n", p.Text);

            var c = IncludePlanner.Plan(Cpp, "Kismet/GameplayStatics.h");
            Assert.Equal(1, c.Line);
            Assert.Equal("include-group", c.Reason);

            var po = IncludePlanner.Plan("// Copyright\n#pragma once\n\nstruct F {};\n", "A.h");
            Assert.Equal("after-pragma-once", po.Reason);
            Assert.Equal(2, po.Line);

            Assert.True(IncludePlanner.AlreadyIncluded(Header, "CoreMinimal.h"));
            Assert.True(IncludePlanner.AlreadyIncluded("#include \"Public/Chars/Hero.h\"\n", "Chars/Hero.h"));
            Assert.False(IncludePlanner.AlreadyIncluded(Header, "Hero.h"));
        }

        // ---------------------------------------------------------------- RenamePlanner

        [Fact]
        public void IdentifierValidation()
        {
            Assert.True(RenamePlanner.IsValidIdentifier("NewName_2"));
            Assert.False(RenamePlanner.IsValidIdentifier("2Name"));
            Assert.False(RenamePlanner.IsValidIdentifier("class"));
            Assert.False(RenamePlanner.IsValidIdentifier("名字"));
            Assert.False(RenamePlanner.IsValidIdentifier(""));
        }

        [Fact]
        public void HitsBecomeLocationsProjectFirst()
        {
            var hits = new[]
            {
                new ReferenceHit { Location = new SourceLocation { FilePath = "/e/Engine.h", Line = 1, Column = 2 }, Origin = ItemOrigin.Engine, Confidence = ResolutionConfidence.Exact },
                new ReferenceHit { Location = new SourceLocation { FilePath = "/p/A.cpp", Line = 3, Column = 4 }, Origin = ItemOrigin.Project, Confidence = ResolutionConfidence.Exact },
                new ReferenceHit { Location = new SourceLocation { FilePath = "/p/A.cpp", Line = 3, Column = 4 }, Origin = ItemOrigin.Project, Confidence = ResolutionConfidence.Exact },
                new ReferenceHit { Location = new SourceLocation { FilePath = "/p/B.cpp", Line = 0, Column = 0 }, Origin = ItemOrigin.ProjectPlugin, Confidence = ResolutionConfidence.NameOnly },
            };
            var l = RenamePlanner.FromHits(hits, "Old");
            Assert.Equal(3, l.Count);
            Assert.Equal(new[] { "/p/A.cpp", "/p/B.cpp", "/e/Engine.h" }, l.Select(x => x.FilePath));
            Assert.True(l[0].Checked);
            Assert.False(l[1].Checked); // name-only: listed unchecked
            Assert.False(l[2].IsEditable);
            Assert.Equal(3, l[0].Length);

            l[1].Checked = true;
            l[2].Checked = true; // engine never edited
            var edits = RenamePlanner.Edits(l, "Old", "New");
            Assert.Equal(2, edits.Count);
            Assert.All(edits, e => Assert.Equal("Old", e.OldText));
        }

        [Fact]
        public void CoreRedirectLines()
        {
            Assert.Equal("+ClassRedirects=(OldName=\"/Script/MyGame.MyActor\",NewName=\"/Script/MyGame.Hero\")",
                RenamePlanner.RedirectLine(RedirectKind.Class, "MyGame", "AMyActor", "AHero"));
            Assert.Equal("+StructRedirects=(OldName=\"/Script/MyGame.Data\",NewName=\"/Script/MyGame.Info\")",
                RenamePlanner.RedirectLine(RedirectKind.Struct, "MyGame", "FData", "FInfo"));
            Assert.Equal("+EnumRedirects=(OldName=\"/Script/MyGame.EMode\",NewName=\"/Script/MyGame.EState\")",
                RenamePlanner.RedirectLine(RedirectKind.Enum, "MyGame", "EMode", "EState"));
            Assert.Equal("+PropertyRedirects=(OldName=\"/Script/MyGame.MyActor.Health\",NewName=\"HitPoints\")",
                RenamePlanner.RedirectLine(RedirectKind.Property, "MyGame", "Health", "HitPoints", "AMyActor"));
            Assert.Equal("+FunctionRedirects=(OldName=\"/Script/MyGame.MyActor.Fire\",NewName=\"Shoot\")",
                RenamePlanner.RedirectLine(RedirectKind.Function, "MyGame", "Fire", "Shoot", "AMyActor"));
            Assert.Null(RenamePlanner.RedirectLine(RedirectKind.Property, "MyGame", "Health", "HP"));
            Assert.Null(RenamePlanner.RedirectLine(RedirectKind.None, "MyGame", "A", "B"));

            Assert.Equal(RedirectKind.Class, RenamePlanner.RedirectFor(new SymbolInfo { Ue = UeFlags.UClass }));
            Assert.Equal(RedirectKind.Property, RenamePlanner.RedirectFor(new SymbolInfo { Ue = UeFlags.UProperty }));
            Assert.Equal(RedirectKind.None, RenamePlanner.RedirectFor(new SymbolInfo()));
        }

        [Fact]
        public void RedirectsAreAddedToIni()
        {
            string created = RenamePlanner.AddRedirects("[/Script/Engine.Engine]\r\nX=1\r\n", new[] { "+ClassRedirects=(A)" });
            Assert.Equal("[/Script/Engine.Engine]\r\nX=1\r\n\r\n[CoreRedirects]\r\n+ClassRedirects=(A)\r\n", created);

            string existing = "[CoreRedirects]\n+ClassRedirects=(A)\n\n[Other]\nY=2\n";
            string appended = RenamePlanner.AddRedirects(existing, new[] { "+ClassRedirects=(A)", "+ClassRedirects=(B)" });
            Assert.Equal("[CoreRedirects]\n+ClassRedirects=(A)\n+ClassRedirects=(B)\n\n[Other]\nY=2\n", appended);
            Assert.Same(existing, RenamePlanner.AddRedirects(existing, new[] { "+ClassRedirects=(A)" }));
            Assert.Equal("[CoreRedirects]\r\n+X\r\n", RenamePlanner.AddRedirects(string.Empty, new[] { "+X" }));
        }

        [Fact]
        public void ClassFilesAndIncludesAreRenamed()
        {
            Assert.Equal("Hero", RenamePlanner.RenamedFileBase("/p/MyActor.h", "AMyActor", "AHero"));
            Assert.Equal("AHero", RenamePlanner.RenamedFileBase("/p/AMyActor.h", "AMyActor", "AHero"));
            Assert.Null(RenamePlanner.RenamedFileBase("/p/Other.h", "AMyActor", "AHero"));

            string text = "#include \"MyActor.h\"\n#include \"Chars/MyActor.h\"\n#include \"XMyActor.h\"\n#include \"MyActor.generated.h\"\n";
            var rw = RenamePlanner.IncludeRewrites("/p/A.cpp", text, "MyActor", "Hero");
            Assert.Equal(new[] { 0, 1, 3 }, rw.Select(r => r.Line));
            var edits = RenamePlanner.Edits(rw, "AMyActor", "AHero");
            string result = TextEditApplier.Apply(text, edits, out int skipped);
            Assert.Equal(0, skipped);
            Assert.Equal("#include \"Hero.h\"\n#include \"Chars/Hero.h\"\n#include \"XMyActor.h\"\n#include \"Hero.generated.h\"\n", result);
        }

        // ---------------------------------------------------------------- hub and providers

        private static RefactorContext Context(FunctionSignature f = null, RefactoringContextResponse engine = null, string file = "/p/MyActor.h")
        {
            engine = engine ?? new RefactoringContextResponse();
            engine.Function = f ?? engine.Function;
            return new RefactorContext { FilePath = file, Line = 9, Engine = engine };
        }

        [Fact]
        public void ProvidersOfferCreateImplementationOnlyWhenMissing()
        {
            var f = Fn("A", "void", "");
            f.Location = new SourceLocation { FilePath = "/p/MyActor.h", Line = 9 };
            var hub = new ContextActionHub();
            foreach (var p in RefactorProviders.All) hub.Register(p);
            var items = hub.Collect(Context(f));
            Assert.Contains(items, i => i.ActionId == RefactorActionIds.CreateImplementation);

            var withDef = Context(f, new RefactoringContextResponse { Definitions = new List<SymbolInfo> { new SymbolInfo { Name = "A" } } });
            Assert.DoesNotContain(hub.Collect(withDef), i => i.ActionId == RefactorActionIds.CreateImplementation);

            var rpc = Fn("ServerFire", "void", "", ue: UeFlags.UFunction, specifiers: "Server, WithValidation");
            var half = Context(rpc, new RefactoringContextResponse { UeCounterpartDefinitions = new List<SymbolInfo> { new SymbolInfo { Name = "ServerFire_Implementation" } } });
            Assert.True(RefactorProviders.NeedsImplementation(half, out var ue));
            Assert.True(ue.Validate);

            var pure = Fn("P", "void", "", mods: FunctionModifiers.Pure | FunctionModifiers.Virtual);
            Assert.False(RefactorProviders.NeedsImplementation(Context(pure), out _));
            var bie = Fn("E", "void", "", ue: UeFlags.UFunction, specifiers: "BlueprintImplementableEvent");
            Assert.False(RefactorProviders.NeedsImplementation(Context(bie), out _));
        }

        [Fact]
        public void ProvidersOfferDeclarationIncludeAndRename()
        {
            var def = Fn("D", "void", "");
            def.IsDefinition = true;
            def.Location = new SourceLocation { FilePath = "/p/MyActor.cpp", Line = 3 };
            var cls = new ClassAtCaret { Symbol = new SymbolInfo { Name = "AMyActor", Origin = ItemOrigin.Project, Location = new SourceLocation { FilePath = "/p/MyActor.h", Line = 5 } }, Bases = new List<string> { "AActor" } };
            var c = Context(def, new RefactoringContextResponse { Class = cls }, "/p/MyActor.cpp");
            Assert.True(RefactorProviders.NeedsDeclaration(c));

            c.Engine.Type = new TypeAtCaret { HeaderPath = "/e/Engine/Classes/Components/BoxComponent.h", Include = IncludeState.Missing, FilesVisited = 20 };
            Assert.True(RefactorProviders.NeedsInclude(c, out var inc));
            Assert.Equal("Components/BoxComponent.h", inc);
            c.Engine.Type.Include = IncludeState.Transitive;
            Assert.False(RefactorProviders.NeedsInclude(c, out _));
            c.Engine.Type.Include = IncludeState.Unknown;
            c.Engine.Type.FilesVisited = 0;
            Assert.False(RefactorProviders.NeedsInclude(c, out _));

            c.Engine.Symbol = new SymbolInfo { Name = "Health", Kind = SymbolKind.Field, Origin = ItemOrigin.Project };
            c.Engine.Word = "Health";
            Assert.True(RefactorProviders.CanRename(c));
            c.Engine.Symbol.Origin = ItemOrigin.Engine;
            Assert.False(RefactorProviders.CanRename(c));
            c.Engine.Symbol = new SymbolInfo { Name = "Fire_Implementation", Kind = SymbolKind.Method, Origin = ItemOrigin.Project };
            c.Engine.Word = "Fire_Implementation";
            Assert.False(RefactorProviders.CanRename(c));

            var hub = new ContextActionHub();
            foreach (var p in RefactorProviders.All) hub.Register(p);
            var items = hub.Collect(c);
            Assert.Contains(items, i => i.ActionId == RefactorActionIds.ImplementVirtual);
            Assert.Equal(RefactorActionIds.CreateDeclaration, items[0].ActionId);
        }

        private sealed class Provider : IContextActionProvider
        {
            private readonly Func<RefactorContext, IEnumerable<ContextActionItem>> _get;
            public Provider(string id, Func<RefactorContext, IEnumerable<ContextActionItem>> get) { Id = id; _get = get; }
            public string Id { get; }
            public IEnumerable<ContextActionItem> GetActions(RefactorContext context) => _get(context);
        }

        [Fact]
        public void HubSortsDedupesAndIsolatesFailures()
        {
            var hub = new ContextActionHub();
            hub.Register(new Provider("a", c => new[] { new ContextActionItem("x.low", "Low", 1), new ContextActionItem("x.high", "High", 9) }));
            hub.Register(new Provider("b", c => new[] { new ContextActionItem("x.high", "High", 9), new ContextActionItem("x.empty", "", 5) }));
            hub.Register(new Provider("boom", c => throw new InvalidOperationException("boom")));
            Assert.Throws<InvalidOperationException>(() => hub.Register(new Provider("a", c => null)));
            var items = hub.Collect(new RefactorContext { FilePath = "/p/A.cpp" });
            Assert.Equal(new[] { "x.high", "x.low" }, items.Select(i => i.ActionId));
            Assert.Equal("a", items[0].Provider);
            Assert.Empty(hub.Collect(null));
            Assert.True(hub.Unregister("boom"));
            Assert.Equal(2, hub.Count);
        }

        // ---------------------------------------------------------------- commands and strings

        [Fact]
        public void RefactoringCommandsUseTheirIdRange()
        {
            var cmds = CommandCatalog.All.Where(c => c.Owner == CommandOwner.Refactoring).ToList();
            Assert.Equal(3, cmds.Count);
            foreach (var c in cmds) Assert.InRange(c.Id, 0x0700, 0x07FF);
            Assert.Contains(cmds, c => c.VaKeys == "Alt+Enter");
        }

        [Fact]
        public void RefactorStringsAreBilingual()
        {
            var saved = Loc.Language;
            try
            {
                Loc.Language = UiLanguage.Chinese;
                string zh = RefactorStrings.RenameSymbol("Health") + RefactorStrings.AddIncludeFor("A.h") + RefactorStrings.CmdRename;
                Loc.Language = UiLanguage.English;
                string en = RefactorStrings.RenameSymbol("Health") + RefactorStrings.AddIncludeFor("A.h") + RefactorStrings.CmdRename;
                Assert.NotEqual(zh, en);
                Assert.Contains("Rename", en);
                Assert.Contains("重命名", zh);
            }
            finally
            {
                Loc.Language = saved;
            }
        }
    }
}
