using System;
using System.Collections.Generic;
using System.Linq;
using UEToVS.Contracts;
using UEToVS.Core.Localization;
using UEToVS.Host.Logic.Build;
using UEToVS.Host.Logic.Editing;
using UEToVS.Host.Logic.IntelliSense;
using UEToVS.Host.Logic.Localization;
using UEToVS.Host.Logic.Refactoring;
using UEToVS.Host.Logic.Settings;
using Xunit;

namespace UEToVS.Host.Logic.Tests
{
    /// <summary>v0.11.0 host side of the header / syntax / name rules, the engine fixes and the background UBT runs.</summary>
    public class CodeChecksV011Tests : IDisposable
    {
        private readonly UiLanguage _saved = Loc.Language;

        public void Dispose() => Loc.Language = _saved;

        private static CodeDiagnostic D(string code, params string[] args) => new CodeDiagnostic { Code = code, Args = args.ToList(), Message = "fallback" };

        [Fact]
        public void New_rules_have_texts_and_names_in_both_languages()
        {
            var samples = new[]
            {
                D(CodeDiagnosticCodes.IncludeNotFound, "Actor.h", "GameFramework/Actor.h"),
                D(CodeDiagnosticCodes.IncludeNotFound, "Nope.h", ""),
                D(CodeDiagnosticCodes.IncludeOfPrivateHeader, "EnginePrivate.h", "Engine"),
                D(CodeDiagnosticCodes.GeneratedInclude, "missing", "Bad.generated.h", ""),
                D(CodeDiagnosticCodes.GeneratedInclude, "name", "Bad.generated.h", "Old.generated.h"),
                D(CodeDiagnosticCodes.GeneratedInclude, "not-last", "Bad.generated.h", "CoreMinimal.h"),
                D(CodeDiagnosticCodes.IncludeSpelling, "LegacyThing.h", "Sub/LegacyThing.h"),
                D(CodeDiagnosticCodes.SyntaxError, "missing-semicolon-after-type", "FA"),
                D(CodeDiagnosticCodes.SyntaxError, "unbalanced-paren", "("),
                D(CodeDiagnosticCodes.MissingInclude, "UGameplayStatics", "Kismet/GameplayStatics.h", "missing"),
                D(CodeDiagnosticCodes.MissingInclude, "UGameplayStatics", "Kismet/GameplayStatics.h", "incomplete"),
                D(CodeDiagnosticCodes.UnknownIdentifier, "Healht", "Health|Heal"),
                D(CodeDiagnosticCodes.UnknownIdentifier, "Zzz", ""),
            };
            foreach (var lang in new[] { UiLanguage.English, UiLanguage.Chinese })
            {
                Loc.Language = lang;
                foreach (var d in samples)
                {
                    string m = CodeDiagnosticText.Message(d);
                    Assert.NotEqual("fallback", m);
                    Assert.DoesNotContain("{", m);
                }
                for (int i = 17; i <= 23; i++) Assert.NotEqual("UE00" + i, HeavyFeatureStrings.RuleName("UE00" + i));
            }
            Loc.Language = UiLanguage.English;
            Assert.Equal("Identifier 'Healht' is undefined; did you mean 'Health'?", CodeDiagnosticText.Message(samples[11]));
            Assert.Equal("Cannot open include file 'Actor.h'; write 'GameFramework/Actor.h'", CodeDiagnosticText.Message(samples[0]));
        }

        [Fact]
        public void Disabled_rules_follow_the_switches()
        {
            var s = new HostSettings();
            Assert.Empty(s.DisabledDiagnosticRules());
            Assert.True(s.UbtIncludeExport);
            Assert.False(s.SaveCompileCheck); // user's call 2026-11-26: the save check is off by default
            s.IncludeChecks = false;
            s.UnknownIdentifierCheck = false;
            Assert.Equal(new[] { "UE0017", "UE0018", "UE0019", "UE0020", "UE0023" }, s.DisabledDiagnosticRules());
            Assert.True(EditorFeatureSettings.TrySet(s, EditorFeatureSettings.SaveCheckKey, "true"));
            Assert.True(s.SaveCompileCheck);
            var values = new Dictionary<string, string>();
            EditorFeatureSettings.ToValues(s, values);
            Assert.Equal("false", values[EditorFeatureSettings.IncludesKey]);
            Assert.Equal("true", values[EditorFeatureSettings.AutoIncludeKey]);
        }

        [Fact]
        public void Engine_fixes_are_offered_and_planned_from_the_caret_diagnostic()
        {
            var d = new CodeDiagnostic
            {
                Code = CodeDiagnosticCodes.UnknownIdentifier, Line = 1, Column = 1, Length = 6, Args = new List<string> { "Healht", "Health|Heal" },
                Fixes = new List<CodeDiagnosticFix>
                {
                    new CodeDiagnosticFix { Kind = "replace", Args = { "Health" } },
                    new CodeDiagnosticFix { Kind = "replace", Args = { "Heal" } },
                },
            };
            var items = new EngineDiagnosticFixProvider().GetActions(new RefactorContext { IsProjectFile = true, Diagnostics = new List<CodeDiagnostic> { d } }).ToList();
            Assert.Equal(2, items.Count);
            Assert.True(items[0].Priority > items[1].Priority);
            var fix = (UeRuleFix)items[0].Argument;
            string text = "{\n\tHealht = 3;\n}\n";
            var edits = EngineDiagnosticFixPlanner.Plan("a.cpp", text, fix, out string problem);
            Assert.Null(problem);
            Assert.Equal("Healht", edits[0].OldText);
            Assert.Equal("Health", edits[0].NewText);

            // insert-line: after the last #include
            var gen = new UeRuleFix { Code = CodeDiagnosticCodes.GeneratedInclude, Variant = "insert-line", Target = "#include \"Bad.generated.h\"" };
            edits = EngineDiagnosticFixPlanner.Plan("Bad.h", "#pragma once\n#include \"CoreMinimal.h\"\nUCLASS()\n", gen, out problem);
            Assert.Equal(2, edits[0].Line);
            Assert.Equal("#include \"Bad.generated.h\"\n", edits[0].NewText);
            Assert.Null(EngineDiagnosticFixPlanner.Plan("Bad.h", "#include \"Bad.generated.h\"\n", gen, out problem));
            Assert.Equal("nothing", problem);

            // not a project file: nothing
            Assert.Empty(new EngineDiagnosticFixProvider().GetActions(new RefactorContext { IsProjectFile = false, Diagnostics = new List<CodeDiagnostic> { d } }));
        }

        [Fact]
        public void Background_ubt_plan()
        {
            Assert.Equal("MyGameEditor", BackgroundUbtPlan.PickTarget(new[] { @"C:\P\Source\MyGame.Target.cs", @"C:\P\Source\MyGameEditor.Target.cs", @"C:\P\Source\MyGameServer.Target.cs" }, "MyGame"));
            Assert.Equal("OtherEditor", BackgroundUbtPlan.PickTarget(new[] { "MyGame.Target.cs", "OtherEditor.Target.cs" }, "MyGame"));
            Assert.Equal("MyGame", BackgroundUbtPlan.PickTarget(new[] { "MyGame.Target.cs" }, "MyGame"));
            Assert.Null(BackgroundUbtPlan.PickTarget(new[] { "MyGame.Build.cs" }, "MyGame"));
            Assert.Equal("-Mode=JsonExport MyGameEditor Win64 Development -Project=\"C:\\P\\MyGame.uproject\" -OutputFile=\"C:\\o.json\"",
                BackgroundUbtPlan.ExportArgs("MyGameEditor", @"C:\P\MyGame.uproject", @"C:\o.json"));
            Assert.Contains("-SingleFile=\"C:\\P\\A.cpp\"", BackgroundUbtPlan.SingleFileArgs("MyGameEditor", @"C:\P\MyGame.uproject", @"C:\P\A.cpp"));
            var t = new DateTime(2026, 11, 26, 0, 0, 0, DateTimeKind.Utc);
            Assert.True(BackgroundUbtPlan.ExportOutOfDate(null, new[] { t }));
            Assert.True(BackgroundUbtPlan.ExportOutOfDate(t, new[] { t.AddMinutes(1) }));
            Assert.False(BackgroundUbtPlan.ExportOutOfDate(t, new[] { t.AddMinutes(-1) }));
            Assert.True(BackgroundUbtPlan.IsCompilable(@"C:\A\B.cpp"));
            Assert.False(BackgroundUbtPlan.IsCompilable(@"C:\A\B.h"));
            Assert.True(BackgroundUbtPlan.IsLiveCodingRefusal("Unable to build while Live Coding is active. Exit the editor and game, or press Ctrl+Alt+F11"));
            Assert.False(BackgroundUbtPlan.IsLiveCodingRefusal("error C2065: 'X': undeclared identifier"));
        }
    }
}
