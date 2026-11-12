using System;
using System.Linq;
using System.Threading;
using UEToVS.Contracts;
using UEToVS.Engine.Indexing;
using UEToVS.Engine.IntelliSense;
using UEToVS.Engine.Navigation;
using UEToVS.Engine.Unreal;
using Xunit;
using Xunit.Abstractions;

namespace UEToVS.Engine.Tests
{
    /// <summary>v0.4.5 UE0011 (deprecated API, <see cref="DeprecationInfo"/>) and UE0012 (UE naming conventions).</summary>
    public sealed class DeprecationAndNamingTests : IClassFixture<EditorFeatureFixture>
    {
        private static long _version = 19000;
        private readonly EditorFeatureFixture _f;
        private readonly ITestOutputHelper _out;

        public DeprecationAndNamingTests(EditorFeatureFixture f, ITestOutputHelper output)
        {
            _f = f;
            _out = output;
        }

        private CodeDiagnosticsResponse Run(string text, bool deprecated = true, bool naming = false, string file = "MyActor.cpp")
        {
            string path;
            if (file.EndsWith(".h", StringComparison.Ordinal))
            {
                path = _f.PathOf("Game/" + file);
                if (!_f.Files.TryGet(path, out _))
                    _f.Files.Upsert(new ListedFile { Path = path, Name = file, Module = "MyGame", Origin = ItemOrigin.Project, Kind = SourceFileKind.Header });
            }
            else path = _f.ProjectCpp(file);
            var req = new CodeDiagnosticsRequest
            {
                Document = new CaretRequest { FilePath = path, Text = text, TextVersion = Interlocked.Increment(ref _version) },
                StartLine = 0,
                EndLine = 100000,
                CheckDeprecated = deprecated,
                CheckNaming = naming,
            };
            var resp = _f.Service.GetDiagnostics(req, CancellationToken.None);
            foreach (var d in resp.Items) _out.WriteLine($"{d.Code} {d.Line + 1}:{d.Column + 1} len {d.Length} [{string.Join("|", d.Args)}] {d.Message}");
            return resp;
        }

        [Theory]
        [InlineData("Use NewThing instead.", "NewThing")]
        [InlineData("Please use FreshValue instead", "FreshValue")]
        [InlineData("Use the new function UKismetSystemLibrary::DoIt() instead", "DoIt")]
        [InlineData("GetFoo() should be used instead", "GetFoo")]
        [InlineData("Replaced by SetBar.", "SetBar")]
        [InlineData("已弃用，请使用 NewApi", "NewApi")]
        [InlineData("This is gone; there is no replacement.", null)]
        [InlineData("Use this instead", null)]
        [InlineData(null, null)]
        public void ReplacementFromMessage(string message, string expected) => Assert.Equal(expected, DeprecationInfo.ReplacementFrom(message));

        [Fact]
        public void Specifiers_DeprecatedFunctionMeta()
        {
            var d = DeprecationInfo.FromSpecifiers("BlueprintCallable, meta = (DeprecatedFunction, DeprecationMessage = \"Use Better instead\")");
            Assert.NotNull(d);
            Assert.Equal("Use Better instead", d.Message);
            Assert.Equal("Better", d.Replacement);
            Assert.Null(DeprecationInfo.FromSpecifiers("BlueprintCallable"));
        }

        [Fact]
        public void Declarations_AreRecognised()
        {
            var doc = DocumentModel.Parse("x.h", 1, EditorFeatureFixture.Sources["Engine/Actor.h"]);
            DeprecationInfo Of(string name) => DeprecationInfo.Of(doc, doc.Decls.ToList().FindIndex(x => x.Name == name));
            var old = Of("OldThing");
            Assert.NotNull(old);
            Assert.Equal("5.1", old.Version);
            Assert.Equal("Use NewThing instead.", old.Message);
            Assert.Equal("NewThing", old.Replacement);
            Assert.Equal("UE_DEPRECATED", old.Macro);
            Assert.Null(Of("NewThing"));
            Assert.Null(Of("SetLifeSpan"));
            Assert.Equal(DeprecationInfo.MacroAttribute, Of("Legacy").Macro);
            Assert.Equal("Modern", Of("Legacy").Replacement);
            Assert.Equal("UE_DEPRECATED_FORGAME", Of("StaleValue").Macro);
            Assert.Null(Of("Gone").Replacement);
            Assert.Equal(DeprecationInfo.MacroSuffix, DeprecationInfo.Of(DocumentModel.Parse("z.h", 1, "struct FA { int32 Old_DEPRECATED; };"), 1)?.Macro);
            // class form: class UE_DEPRECATED(5.0, "...") FOld
            var cls = DocumentModel.Parse("y.h", 1, "class UE_DEPRECATED(5.0, \"Use FNew instead\") FOld { };\nclass FNew { };\n");
            var info = DeprecationInfo.Of(cls, cls.Decls.ToList().FindIndex(x => x.Name == "FOld"));
            Assert.NotNull(info);
            Assert.Equal("FNew", info.Replacement);
            Assert.Null(DeprecationInfo.Of(cls, cls.Decls.ToList().FindIndex(x => x.Name == "FNew")));
        }

        [Fact]
        public void Uses_AreReported_WithReplacementOnlyWhenMechanical()
        {
            string text = "#include \"MyActor.h\"\nvoid AMyActor::Run()\n{\n\tOldThing(1);\n\tGone();\n\tLegacy();\n\tGetOwner()->OldThing(2);\n\tint32 V = StaleValue;\n\tNewThing(3);\n}\n";
            var r = Run(text);
            var items = r.Items.Where(d => d.Code == CodeDiagnosticCodes.DeprecatedApi).ToList();
            Assert.All(items, d => Assert.Equal(CodeDiagnosticSeverity.Warning, d.Severity));
            var byName = items.GroupBy(d => d.Args[0]).ToDictionary(g => g.Key, g => g.ToList());
            Assert.Equal(2, byName["OldThing"].Count);
            Assert.All(byName["OldThing"], d => Assert.Equal("NewThing", d.Args[3]));
            Assert.Equal("5.1", byName["OldThing"][0].Args[1]);
            Assert.Equal(string.Empty, byName["Gone"].Single().Args[3]);
            Assert.Equal("Modern", byName["Legacy"].Single().Args[3]);
            Assert.Equal("FreshValue", byName["StaleValue"].Single().Args[3]);
            Assert.False(byName.ContainsKey("NewThing"));
            var first = byName["OldThing"].OrderBy(d => d.Line).First();
            Assert.Equal(3, first.Line);
            Assert.Equal(1, first.Column);
            Assert.Equal("OldThing".Length, first.Length);
        }

        [Fact]
        public void Off_ReportsNothing()
        {
            string text = "void AMyActor::Run()\n{\n\tOldThing(1);\n}\n";
            Assert.DoesNotContain(Run(text, deprecated: false).Items, d => d.Code == CodeDiagnosticCodes.DeprecatedApi);
        }

        [Fact]
        public void Naming_TypesEnumsAndBools()
        {
            string text = "#pragma once\n#include \"Actor.h\"\n" +
                          "UCLASS()\nclass MYGAME_API UBadActor : public AActor\n{\n\tGENERATED_BODY()\npublic:\n\tbool Visible;\n\tbool bOk;\n\tint32 Count;\n};\n" +
                          "UCLASS()\nclass MYGAME_API AGoodActor : public AActor\n{\n\tGENERATED_BODY()\n};\n" +
                          "UCLASS()\nclass MYGAME_API Thing : public UObject\n{\n\tGENERATED_BODY()\n};\n" +
                          "struct Inventory { };\nstruct FFine { };\n" +
                          "enum class Mode : uint8 { A, B };\nenum class EMode2 : uint8 { A };\n" +
                          "template <typename T> class Box { };\n" +
                          "class FUnknownBase : public SomethingNotIndexed { };\n" +
                          "class IMyInterface\n{\n\tGENERATED_BODY()\n};\n" +
                          "class MyInterface2\n{\n\tGENERATED_BODY()\n};\n";
            var r = Run(text, deprecated: false, naming: true, file: "Naming.h");
            var items = r.Items.Where(d => d.Code == CodeDiagnosticCodes.NamingConvention).ToDictionary(d => d.Args[0], d => d);
            Assert.All(items.Values, d => Assert.Equal(CodeDiagnosticSeverity.Info, d.Severity));
            Assert.Equal("ABadActor", items["UBadActor"].Args[1]);
            Assert.Equal("bVisible", items["Visible"].Args[1]);
            Assert.Equal("UThing", items["Thing"].Args[1]);
            Assert.Equal("FInventory", items["Inventory"].Args[1]);
            Assert.Equal("EMode", items["Mode"].Args[1]);
            Assert.Equal("TBox", items["Box"].Args[1]);
            Assert.Equal("IMyInterface2", items["MyInterface2"].Args[1]);
            foreach (var ok in new[] { "bOk", "Count", "AGoodActor", "FFine", "EMode2", "FUnknownBase", "IMyInterface" }) Assert.False(items.ContainsKey(ok), ok);
            Assert.DoesNotContain(Run(text, deprecated: false, naming: false, file: "Naming.h").Items, d => d.Code == CodeDiagnosticCodes.NamingConvention);
        }

        [Fact]
        public void QuickInfo_ShowsDeprecation()
        {
            string text = "void AMyActor::Run()\n{\n\tOldThing(1);\n}\n";
            var qi = _f.Service.GetQuickInfo(new CaretRequest { FilePath = _f.ProjectCpp("MyActor.cpp"), Text = text, TextVersion = Interlocked.Increment(ref _version), Line = 2, Column = 3 },
                CancellationToken.None);
            Assert.True(qi.IsDeprecated);
            Assert.Equal("5.1", qi.DeprecatedVersion);
            Assert.Equal("Use NewThing instead.", qi.DeprecationMessage);
        }
    }
}
