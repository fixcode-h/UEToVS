using System;
using System.Collections.Generic;
using System.Linq;
using UEToVS.Contracts;
using UEToVS.Core.Localization;
using UEToVS.Host.Logic.IntelliSense;
using UEToVS.Host.Logic.Settings;
using UEToVS.Host.Logic.Unreal;
using Xunit;

namespace UEToVS.Host.Logic.Tests
{
    public class CodeDiagnosticsLogicTests : IDisposable
    {
        private readonly UiLanguage _saved = Loc.Language;

        public void Dispose() => Loc.Language = _saved;

        private static CodeDiagnostic D(string code, params string[] args) => new CodeDiagnostic { Code = code, Args = args.ToList(), Message = "fallback" };

        [Fact]
        public void Messages_AreLocalizedFromCodeAndArgs()
        {
            Loc.Language = UiLanguage.English;
            Assert.Equal("'AMyActor' has no member named 'Fier'", CodeDiagnosticText.Message(D(CodeDiagnosticCodes.UnknownMember, "Fier", "AMyActor")));
            Assert.Contains("module UMG", CodeDiagnosticText.Message(D(CodeDiagnosticCodes.MissingModuleDependency, "Blueprint/UserWidget.h", "UMG", "MyGame")));
            Assert.Contains("MyGame.Build.cs", CodeDiagnosticText.Message(D(CodeDiagnosticCodes.MissingModuleDependency, "Blueprint/UserWidget.h", "UMG", "MyGame")));
            Assert.Equal("UCLASS type 'AFoo' is missing GENERATED_BODY() (UHT will fail)", CodeDiagnosticText.Message(D(CodeDiagnosticCodes.MissingGeneratedBody, "UCLASS", "AFoo")));
            Assert.Equal("fallback", CodeDiagnosticText.Message(D("UE9999")));
            Assert.Equal("UE0001: 'T' has no member named 'X' (UEToVS)", CodeDiagnosticText.ToolTip(D(CodeDiagnosticCodes.UnknownMember, "X", "T")));
            // missing args never throw
            Assert.Contains("?", CodeDiagnosticText.Message(D(CodeDiagnosticCodes.UnknownMember)));

            Loc.Language = UiLanguage.Chinese;
            Assert.Equal("“AMyActor” 没有名为 “Fier” 的成员", CodeDiagnosticText.Message(D(CodeDiagnosticCodes.UnknownMember, "Fier", "AMyActor")));
            Assert.Contains("GENERATED_BODY()", CodeDiagnosticText.Message(D(CodeDiagnosticCodes.MissingGeneratedBody, "USTRUCT", "FData")));
            Assert.Contains("UMG", CodeDiagnosticText.Message(D(CodeDiagnosticCodes.MissingModuleDependency, "A.h", "UMG", "MyGame")));
        }

        [Fact]
        public void UeRuleMessages_Localized()
        {
            Loc.Language = UiLanguage.English;
            Assert.Contains("ServerFire_Implementation", CodeDiagnosticText.Message(D(CodeDiagnosticCodes.GeneratedFunctionBody, "ServerFire", "Server", "AW")));
            Assert.Contains("must be a UFUNCTION", CodeDiagnosticText.Message(D(CodeDiagnosticCodes.MissingRepNotify, "Ammo", "OnRep_Ammo", "AW", "not-ufunction")));
            Assert.Contains("does not exist in AW", CodeDiagnosticText.Message(D(CodeDiagnosticCodes.MissingRepNotify, "Ammo", "OnRep_Ammo", "AW", "missing")));
            Assert.Contains("AW::GetLifetimeReplicatedProps", CodeDiagnosticText.Message(D(CodeDiagnosticCodes.UnregisteredReplicatedProperty, "Heat", "AW")));
            Assert.Contains("'A.B'", CodeDiagnosticText.Message(D(CodeDiagnosticCodes.UnknownGameplayTag, "A.B")));
            Assert.Equal("Asset '/Game/X' does not exist", CodeDiagnosticText.Message(D(CodeDiagnosticCodes.UnknownAssetPath, "/Game/X")));
            Assert.Contains("TSharedPtr<F>", CodeDiagnosticText.Message(D(CodeDiagnosticCodes.UnsupportedPropertyType, "P", "TSharedPtr<F>")));
            Assert.Contains("Blueprint", CodeDiagnosticText.Message(D(CodeDiagnosticCodes.UnsupportedParameterType, "B", "uint32", "Call", "blueprint")));
            Assert.Contains("?", CodeDiagnosticText.Message(D(CodeDiagnosticCodes.UnsupportedParameterType)));
            Loc.Language = UiLanguage.Chinese;
            Assert.Contains("_Implementation", CodeDiagnosticText.Message(D(CodeDiagnosticCodes.GeneratedFunctionBody, "ServerFire", "Server", "AW")));
            Assert.Contains("DOREPLIFETIME", CodeDiagnosticText.Message(D(CodeDiagnosticCodes.UnregisteredReplicatedProperty, "Heat", "AW")));
        }

        [Fact]
        public void Plan_ViewportPlusMargin_AndCoverage()
        {
            var r = CodeDiagnosticsPlan.Range(100, 140, 1000);
            Assert.Equal(new LineRange(50, 190), r);
            Assert.Equal(new LineRange(0, 70), CodeDiagnosticsPlan.Range(0, 20, 71));
            Assert.True(CodeDiagnosticsPlan.Range(0, 0, 0).IsEmpty);

            Assert.False(CodeDiagnosticsPlan.NeedsRequest(r, 7, 120, 160, 7));
            Assert.True(CodeDiagnosticsPlan.NeedsRequest(r, 7, 120, 200, 7));   // scrolled out of the covered lines
            Assert.True(CodeDiagnosticsPlan.NeedsRequest(r, 7, 120, 160, 8));   // edited
            Assert.True(CodeDiagnosticsPlan.NeedsRequest(new LineRange(0, -1), 7, 0, 10, 7));
        }

        [Fact]
        public void Includes_ToSquiggles_OnThePath()
        {
            var missing = new List<MissingDependency>
            {
                new MissingDependency { Include = "Blueprint/UserWidget.h", Line = 2, Module = "UMG", OwnerModule = "MyGame", OwnerBuildCsPath = "C:/G/MyGame.Build.cs" },
                new MissingDependency { Include = "Gone.h", Line = 9, Module = "X", OwnerModule = "MyGame", OwnerBuildCsPath = "C:/G/MyGame.Build.cs" },
            };
            var lines = new Dictionary<int, string> { [2] = "#include \"Blueprint/UserWidget.h\"", [9] = "// edited away" };
            var list = IncludeDiagnostics.From(missing, l => lines.TryGetValue(l, out var t) ? t : null, _ => "PublicDependencyModuleNames.AddRange(new[] { \"Core\" });", out int uncertain);
            var d = Assert.Single(list);
            Assert.Equal(1, uncertain);
            Assert.Equal(CodeDiagnosticCodes.MissingModuleDependency, d.Code);
            Assert.Equal(CodeDiagnosticSeverity.Warning, d.Severity);
            Assert.Equal(2, d.Line);
            Assert.Equal("#include \"".Length, d.Column);
            Assert.Equal("Blueprint/UserWidget.h".Length, d.Length);
            Assert.Equal(new[] { "Blueprint/UserWidget.h", "UMG", "MyGame" }, d.Args);

            // raw include paths in the owner Build.cs: the header may be reachable anyway → not reported
            var none = IncludeDiagnostics.From(missing, l => lines.TryGetValue(l, out var t) ? t : null, _ => "PrivateIncludePaths.Add(\"X\");", out uncertain);
            Assert.Empty(none);
            Assert.Equal(2, uncertain);
            Assert.Empty(IncludeDiagnostics.From(null, null, null, out _));
        }

        [Fact]
        public void IncludeKey_ChangesOnlyWithIncludes()
        {
            var a = IncludeDependencyChecker.ParseIncludes("#include \"A.h\"\nint x;\n#include <B/C.h>\n");
            var b = IncludeDependencyChecker.ParseIncludes("#include \"A.h\"\nint y = 2;\n#include <B/C.h>\n");
            var c = IncludeDependencyChecker.ParseIncludes("#include \"A.h\"\n#include <B/C.h>\n");
            Assert.Equal(IncludeDiagnostics.Key(a), IncludeDiagnostics.Key(b));
            Assert.NotEqual(IncludeDiagnostics.Key(a), IncludeDiagnostics.Key(c)); // lines moved: positions must be redone
            Assert.Equal(string.Empty, IncludeDiagnostics.Key(null));
        }

        [Fact]
        public void Setting_RoundTrips()
        {
            var schema = HostSettingsSchema.Build(8);
            var d = schema.Single(x => x.Key == "diagnostics.enabled");
            Assert.Equal(HostSettingsSchema.IntelliSense, d.Category);
            Assert.Equal("true", d.Default);
            Assert.True(new HostSettings().DiagnosticsEnabled);
            var off = HostSettingsSchema.Codec.FromValues(new Dictionary<string, string> { ["diagnostics.enabled"] = "false" }, schema, new HostSettings());
            Assert.False(off.DiagnosticsEnabled);
            Assert.Equal("false", HostSettingsSchema.Codec.ToValues(off)["diagnostics.enabled"]);
        }
    }
}
