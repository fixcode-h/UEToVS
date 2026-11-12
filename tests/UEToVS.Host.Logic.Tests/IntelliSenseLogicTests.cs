using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using UEToVS.Contracts;
using UEToVS.Core.Localization;
using UEToVS.Core.Matching;
using UEToVS.Host.Logic.IntelliSense;
using UEToVS.Host.Logic.Localization;
using UEToVS.Host.Logic.Settings;
using Xunit;

namespace UEToVS.Host.Logic.Tests
{
    public class CompletionTriggerTests
    {
        private static TriggerResult Typed(string lineWithCaret, bool onIdentifier = true)
        {
            int caret = lineWithCaret.IndexOf('|');
            string line = lineWithCaret.Remove(caret, 1);
            char typed = caret > 0 ? line[caret - 1] : '\0';
            return CompletionTriggers.Evaluate(line, caret, typed, false, onIdentifier);
        }

        private static TriggerResult Explicit(string lineWithCaret)
        {
            int caret = lineWithCaret.IndexOf('|');
            return CompletionTriggers.Evaluate(lineWithCaret.Remove(caret, 1), caret, '\0', true);
        }

        [Theory]
        [InlineData("Actor.|", CompletionContextKind.Member, '.')]
        [InlineData("GetOwner()->|", CompletionContextKind.Member, '>')]
        [InlineData("Items[0].|", CompletionContextKind.Member, '.')]
        [InlineData("FMath::|", CompletionContextKind.Scope, ':')]
        [InlineData("  ::|", CompletionContextKind.Scope, ':')]
        [InlineData("TArray<int32>::|", CompletionContextKind.Scope, ':')]
        public void PunctuationTriggers(string line, CompletionContextKind hint, char trigger)
        {
            var r = Typed(line);
            Assert.True(r.Start, line);
            Assert.Equal(hint, r.Hint);
            Assert.Equal(trigger, r.TriggerChar);
        }

        [Theory]
        [InlineData("float X = 1.|")]
        [InlineData("a - >|")]
        [InlineData("x >|")]
        [InlineData("// Actor.|")]
        [InlineData("Log(\"a.|")]
        [InlineData("x = a:|")]
        [InlineData("x = a:::|")]
        [InlineData("Foo(|")]
        [InlineData("int a1|")]
        [InlineData("/* Actor.|")]
        public void NoTrigger(string line) => Assert.False(Typed(line).Start, line);

        [Fact]
        public void IdentifierStartTriggersOnlyAtWordStart()
        {
            var r = Typed("  A|");
            Assert.True(r.Start);
            Assert.Equal(CompletionContextKind.General, r.Hint);
            Assert.Equal(2, r.WordStart);
            Assert.Equal(1, r.WordLength);
            Assert.False(Typed("  Ac|").Start);       // session already filters
            Assert.False(Typed("  3|").Start);        // numbers never
            Assert.False(Typed("  A|", onIdentifier: false).Start);
            Assert.Equal(CompletionContextKind.Member, Typed("Actor->G|").Hint);
            Assert.Equal(CompletionContextKind.Override, Typed("virtual B|").Hint);
        }

        [Theory]
        [InlineData("#include \"|", '"')]
        [InlineData("#include <|", '<')]
        [InlineData("  #  include \"GameFramework/|", '/')]
        public void IncludeTriggers(string line, char trigger)
        {
            var r = Typed(line);
            Assert.True(r.Start, line);
            Assert.Equal(CompletionContextKind.Include, r.Hint);
            Assert.Equal(trigger, r.TriggerChar);
        }

        [Fact]
        public void IncludeFragmentIsAfterTheLastSlash()
        {
            var r = Explicit("#include \"GameFramework/Act|or.h\"");
            Assert.Equal(CompletionContextKind.Include, r.Hint);
            Assert.Equal("#include \"GameFramework/".Length, r.WordStart);
            Assert.Equal("Actor.h".Length, r.WordLength);
            Assert.False(Typed("#include \"Foo.h\" .|").Start); // after the closing quote: plain code, '.' after a quote is not a member
            Assert.True(Typed("#include \"G|").Start);
        }

        [Theory]
        [InlineData("UPROPERTY(|", ',')]
        [InlineData("UFUNCTION(BlueprintCallable,|", ',')]
        [InlineData("UPROPERTY(EditAnywhere, |", ',')]
        [InlineData("UPROPERTY(meta=(|", '(')]
        [InlineData("UCLASS(Blueprintable, meta = (DisplayName = \"X\",|", ',')]
        public void SpecifierTriggers(string line, char trigger)
        {
            var r = Typed(line);
            Assert.True(r.Start, line);
            Assert.Equal(CompletionContextKind.Specifier, r.Hint);
            if (trigger == '(') Assert.Equal('(', r.TriggerChar);
        }

        [Fact]
        public void SpecifierIdentifierTypingAndNonReflectionCalls()
        {
            var r = Typed("UPROPERTY(Edit, B|");
            Assert.True(r.Start);
            Assert.Equal(CompletionContextKind.Specifier, r.Hint);
            Assert.False(Typed("Foo(a,|").Start);
            Assert.False(Typed("UPROPERTY(Category = |").Start);
            Assert.True(CompletionTriggers.IsInSpecifierList("UFUNCTION(meta=(Keywords=\"a\"), |", 31, out var macro));
            Assert.Equal("UFUNCTION", macro);
            Assert.False(CompletionTriggers.IsInSpecifierList("UPROPERTY() int32 X; Foo(", 25, out _));
        }

        [Fact]
        public void ExplicitInvokeWorksAnywhereButCommentsAndStrings()
        {
            Assert.True(Explicit("    |").Start);
            var r = Explicit("Actor->Get|Owner");
            Assert.Equal(CompletionContextKind.Member, r.Hint);
            Assert.Equal(7, r.WordStart);
            Assert.Equal(8, r.WordLength);
            Assert.False(Explicit("x; // |").Start);
            Assert.False(Explicit("TEXT(\"|\")").Start);
        }

        [Fact]
        public void LineContextScan()
        {
            Assert.Equal(LineContext.Code, CompletionTriggers.ContextAt("a /* b */ c", 11));
            Assert.Equal(LineContext.BlockComment, CompletionTriggers.ContextAt("a /* b", 6));
            Assert.Equal(LineContext.String, CompletionTriggers.ContextAt("x = \"a\\\" b", 10));
            Assert.Equal(LineContext.CharLiteral, CompletionTriggers.ContextAt("c = '", 5));
            Assert.Equal(LineContext.Code, CompletionTriggers.ContextAt("int x = 1'000; y", 16));
            Assert.Equal(LineContext.LineComment, CompletionTriggers.ContextAt("x // y", 6));
            Assert.Equal(LineContext.IncludePath, CompletionTriggers.ContextAt("#include <Core", 14));
        }

        [Fact]
        public void UeOnlyPolicyBeforeTakeover()
        {
            Assert.True(CompletionPolicy.ProvidesAll(CompletionScope.Auto, true));
            Assert.False(CompletionPolicy.ProvidesAll(CompletionScope.Auto, false));
            Assert.True(CompletionPolicy.ProvidesAll(CompletionScope.All, false));
            Assert.False(CompletionPolicy.ProvidesAll(CompletionScope.UeOnly, true));

            Assert.False(CompletionPolicy.Participates(CompletionContextKind.General, false));
            Assert.True(CompletionPolicy.Participates(CompletionContextKind.Specifier, false));
            Assert.True(CompletionPolicy.Participates(CompletionContextKind.General, true));

            var plain = new CompletionItem { Label = "Tick" };
            var reflected = new CompletionItem { Label = "Health", Ue = UeFlags.UProperty };
            Assert.False(CompletionPolicy.Keep(plain, CompletionContextKind.Member, false));
            Assert.True(CompletionPolicy.Keep(reflected, CompletionContextKind.Member, false));
            Assert.True(CompletionPolicy.Keep(plain, CompletionContextKind.Specifier, false));
            Assert.True(CompletionPolicy.Keep(plain, CompletionContextKind.Include, false));
            Assert.False(CompletionPolicy.Keep(reflected, CompletionContextKind.General, false));
            Assert.True(CompletionPolicy.Keep(plain, CompletionContextKind.General, true));
            Assert.False(CompletionPolicy.Keep(new CompletionItem(), CompletionContextKind.General, true));
        }
    }

    public class CompletionRankingTests
    {
        private static RankCandidate C(string text, int engine = 0, bool inherited = false, bool keyword = false, bool foreign = false) =>
            new RankCandidate { FilterText = text, EngineScore = engine, Inherited = inherited, Keyword = keyword, Foreign = foreign };

        private static List<string> Order(IReadOnlyList<RankCandidate> items, string pattern, RecentCompletions recent = null) =>
            CompletionRanker.Rank(items, pattern, recent).Select(r => items[r.Index].FilterText).ToList();

        [Fact]
        public void ExactThenPrefixThenCamelThenSubstring()
        {
            var items = new[] { C("GetActorLocation"), C("TargetActor"), C("GetActor"), C("GetActorForwardVector"), C("Get"), C("Unrelated") };
            var order = Order(items, "GetActor");
            Assert.Equal("GetActor", order[0]);
            Assert.Equal(new[] { "GetActorLocation", "GetActorForwardVector" }.OrderBy(s => s.Length).ToList(), order.Skip(1).Take(2).ToList());
            Assert.DoesNotContain("Unrelated", order);
            Assert.DoesNotContain("Get", order);

            var camel = Order(new[] { C("SetActorLocation"), C("GetActorLocation"), C("Glacier") }, "GAL");
            Assert.Equal("GetActorLocation", camel[0]);
            Assert.Single(camel);
        }

        [Fact]
        public void UePrefixIsOptional()
        {
            var order = Order(new[] { C("AActor"), C("UActorComponent"), C("FVector") }, "Actor");
            Assert.Equal(new[] { "AActor", "UActorComponent" }, order);
        }

        [Fact]
        public void OwnMembersBeforeInheritedBeforeKeywordsThenRecentThenEngine()
        {
            var items = new[] { C("Health", 0, inherited: true), C("HealthMax", 0), C("H_keyword", 0, keyword: true), C("Hit", 50) };
            Assert.Equal(new[] { "Hit", "HealthMax", "Health", "H_keyword" }, Order(items, "H"));

            var recent = new RecentCompletions(10);
            recent.Record("HealthMax");
            Assert.Equal("HealthMax", Order(new[] { C("Hit", 50), C("HealthMax", 0) }, "H", recent)[0]);
            Assert.Equal("Hit", Order(new[] { C("Hit", 50), C("HealthMax", 0) }, "H")[0]);
        }

        [Fact]
        public void EmptyPatternKeepsEverythingAndRanges()
        {
            var items = new[] { C("b"), C("a", 3), C("c", foreign: true) };
            var r = CompletionRanker.Rank(items, "", null);
            Assert.Equal(3, r.Count);
            Assert.Equal("a", items[r[0].Index].FilterText);
            Assert.Equal("c", items[r[2].Index].FilterText);
            var m = CompletionRanker.Rank(new[] { C("BeginPlay") }, "bp", null, 5);
            Assert.Equal(MatchKind.CamelHump, m[0].Kind);
            Assert.Equal(2, m[0].Ranges.Count);
            Assert.Single(CompletionRanker.Rank(items, "", null, 1));
        }

        [Fact]
        public void RecentIsBoundedLru()
        {
            var r = new RecentCompletions(3);
            foreach (var s in new[] { "a", "b", "c", "d" }) r.Record(s);
            Assert.Equal(3, r.Count);
            Assert.Equal(0, r.Rank("a"));
            Assert.True(r.Rank("d") > r.Rank("c"));
            r.Record("b");
            Assert.True(r.Rank("b") > r.Rank("d"));
            Assert.Equal(0, r.Rank(null));
        }
    }

    public class AccessorFixTests
    {
        [Fact]
        public void AppliesOnlyToTheExpectedAccessor()
        {
            var fix = new AccessorFix { Line = 3, Column = 9, Length = 1, Replacement = "->" };
            Assert.True(AccessorFixes.TryApply(fix, "    Actor.Get", out var line));
            Assert.Equal("    Actor->Get", line);
            Assert.Equal(1, AccessorFixes.Delta(fix));
            Assert.False(AccessorFixes.TryApply(fix, "    Actor:Get", out var same));
            Assert.Equal("    Actor:Get", same);
            Assert.False(AccessorFixes.IsValid(fix, "short"));
            Assert.False(AccessorFixes.IsValid(new AccessorFix { Column = 9, Length = 1, Replacement = "." }, "    Actor.Get"));
            Assert.False(AccessorFixes.IsValid(new AccessorFix { Column = 9, Length = 1, Replacement = "::" }, "    Actor.Get"));
            Assert.False(AccessorFixes.IsValid(null, "x"));

            var back = new AccessorFix { Line = 0, Column = 3, Length = 2, Replacement = "." };
            Assert.True(AccessorFixes.TryApply(back, "Vec->X", out var v));
            Assert.Equal("Vec.X", v);
            Assert.Equal(-1, AccessorFixes.Delta(back));
            Assert.True(AccessorFixes.PrecedesWord(back, 0, 5));
            Assert.False(AccessorFixes.PrecedesWord(back, 1, 5));
            Assert.False(AccessorFixes.PrecedesWord(back, 0, 4));
        }
    }

    public class SemanticClassificationTests : IDisposable
    {
        private readonly UiLanguage _saved = Loc.Language;

        public void Dispose() => Loc.Language = _saved;

        [Fact]
        public void EveryKindHasAUniqueUEToVSClassification()
        {
            var kinds = Enum.GetValues(typeof(SemanticKind)).Cast<SemanticKind>().Where(k => k != SemanticKind.None).ToList();
            Assert.Equal(kinds.Count, SemanticClassifications.All.Count);
            foreach (var k in kinds) Assert.NotNull(SemanticClassifications.Get(k));
            Assert.Equal(SemanticClassifications.All.Count, SemanticClassifications.All.Select(i => i.Name).Distinct().Count());
            foreach (var i in SemanticClassifications.All)
            {
                Assert.StartsWith("UEToVS ", i.Name);
                Loc.Language = UiLanguage.English;
                Assert.StartsWith("UEToVS ", i.DisplayName);
                string en = i.DisplayName;
                Loc.Language = UiLanguage.Chinese;
                Assert.NotEqual(en, i.DisplayName);
            }
            // Name constants are the ones in the table (MEF attributes use the constants).
            var constants = typeof(SemanticClassificationNames).GetFields(BindingFlags.Public | BindingFlags.Static).Select(f => (string)f.GetValue(null)).ToList();
            Assert.Equal(constants.OrderBy(x => x), SemanticClassifications.All.Select(i => i.Name).OrderBy(x => x));
            Assert.Null(SemanticClassifications.NameFor(SemanticKind.None));
            Assert.Null(SemanticClassifications.NameFor(SemanticKind.Class, SemanticModifiers.Unresolved));
            Assert.Equal(SemanticClassificationNames.Method, SemanticClassifications.NameFor(SemanticKind.Method, SemanticModifiers.Static));
        }

        [Fact]
        public void DefaultsFollowTheVaTable()
        {
            Assert.Equal(0xFF4EC9B0u, SemanticClassifications.Get(SemanticKind.Class).Dark);
            Assert.Equal(0xFF2B91AFu, SemanticClassifications.Get(SemanticKind.UeType).Light);
            Assert.Equal(0xFFDCDCAAu, SemanticClassifications.Get(SemanticKind.VirtualMethod).Dark);
            Assert.Equal(0xFF9CDCFEu, SemanticClassifications.Get(SemanticKind.Field).Dark); // VA: fields like locals
            Assert.Equal(0xFFBD63C5u, SemanticClassifications.Get(SemanticKind.Macro).Dark);
            Assert.Equal(0xFFB8D7A3u, SemanticClassifications.Get(SemanticKind.EnumValue).Dark);
            Assert.Equal(0xFFC586C0u, SemanticClassifications.Get(SemanticKind.UeMacro).Dark);
            Assert.Equal(0xFFA31515u, SemanticClassifications.Get(SemanticKind.UeSpecifier).Light);
        }

        [Fact]
        public void ThemeFollowNeverTramplesUserColours()
        {
            var info = SemanticClassifications.Get(SemanticKind.Class);
            Assert.Equal(info.Light, SemanticClassifications.TargetColor(info, false, info.Dark, null, null));   // our dark default → light
            Assert.Equal(info.Dark, SemanticClassifications.TargetColor(info, true, null, null, null));          // nothing yet
            Assert.Null(SemanticClassifications.TargetColor(info, true, info.Dark, null, null));                 // already right
            Assert.Null(SemanticClassifications.TargetColor(info, false, 0xFF123456, null, null));               // user customized
            Assert.Equal(info.Light, SemanticClassifications.TargetColor(info, false, 0xFF123456, null, 0xFF123456)); // we applied it (old override)
            Assert.Equal(0xFFABCDEFu, SemanticClassifications.TargetColor(info, false, 0xFF123456, 0xFFABCDEF, null)); // override wins
        }

        [Fact]
        public void OverrideSettingParsesKindsAndGroups()
        {
            var errors = new List<string>();
            var o = SemanticClassifications.ParseOverrides("Functions=#112233; Method=#445566;\n class = #778899 ; Bogus=#000000; Field=red; =x", errors);
            Assert.Equal(0xFF112233u, o[SemanticKind.Function]);
            Assert.Equal(0xFF112233u, o[SemanticKind.VirtualMethod]);
            Assert.Equal(0xFF445566u, o[SemanticKind.Method]); // kind beats group
            Assert.Equal(0xFF778899u, o[SemanticKind.Class]);
            Assert.False(o.ContainsKey(SemanticKind.Field));
            Assert.Equal(3, errors.Count);
            string text = SemanticClassifications.FormatOverrides(new Dictionary<SemanticKind, uint> { [SemanticKind.Macro] = 0xFF010203, [SemanticKind.Class] = 0x80FFFFFF });
            Assert.Equal("Class=#80FFFFFF; Macro=#010203", text);
            Assert.Equal(2, SemanticClassifications.ParseOverrides(text).Count);
            Assert.Empty(SemanticClassifications.ParseOverrides(null));
        }
    }

    public class VaColorImportTests
    {
        private const string Sample = @"<UserSettings>
  <ApplicationIdentity version=""17.0""/>
  <Category name=""Environment_Group"" RegisteredName=""Environment_Group"">
    <Category name=""Environment_FontsAndColors"" Category=""{1EDA5DD4-927A-43a7-810E-7FD247D0DA1D}"">
      <FontsAndColors Version=""2.0"">
        <Categories>
          <Category GUID=""{A27B4E24-A735-4D1D-B8E7-9716E1E3D8E0}"" FontIsDefault=""Yes"">
            <Items>
              <Item Name=""Plain Text"" Foreground=""0x00DCDCDC"" Background=""0x001E1E1E"" BoldFont=""No""/>
              <Item Name=""VA Class"" Foreground=""0x0000D7FF"" Background=""0x02000000"" BoldFont=""No"" />
              <Item Name=""VA Method"" Foreground=""0x000080FF"" Background=""0x02000000"" BoldFont=""Yes"" />
              <Item Name=""VA Variable"" Foreground=""0x006BB7BD"" Background=""0x02000000"" BoldFont=""No"" />
              <Item Name=""VA Macro"" Foreground=""0x00C563BD"" Background=""0x02000000"" BoldFont=""No"" />
              <Item Name=""VA Enum Member"" Foreground=""0x001E77B9"" Background=""0x02000000"" BoldFont=""No"" />
              <Item Name=""VA X Macro / Enum"" Foreground=""0x00A000A0"" Background=""0x02000000"" BoldFont=""No"" />
              <Item Name=""VA Namespace"" Foreground=""0x01000000"" Background=""0x02000000"" BoldFont=""No"" />
              <Item Name=""VA Brace Matching"" Foreground=""0x00DCDCDC"" Background=""0x02000000"" BoldFont=""Yes"" />
            </Items>
          </Category>
        </Categories>
      </FontsAndColors>
    </Category>
  </Category>
</UserSettings>";

        [Fact]
        public void ColorRefIsBgr()
        {
            Assert.Equal(0xFFFFD700u, VaColorImport.FromColorRef("0x0000D7FF")); // gold
            Assert.Null(VaColorImport.FromColorRef("0x01000000"));
            Assert.Null(VaColorImport.FromColorRef("0x02000000"));
            Assert.Null(VaColorImport.FromColorRef("junk"));
            Assert.Equal(0xFF0000FFu, VaColorImport.FromColorRef("16711680")); // decimal 0x00FF0000 = blue
            Assert.Equal(0x0000D7FFu, VaColorImport.ToColorRef(0xFFFFD700));
            Assert.Equal("VA Class", VaColorImport.Normalize("VA X Class"));
        }

        [Fact]
        public void MapsVaItemsToUEToVSKinds()
        {
            var items = VaColorImport.Parse(Sample);
            Assert.Equal(9, items.Count);
            Assert.All(items, i => Assert.Equal("{A27B4E24-A735-4D1D-B8E7-9716E1E3D8E0}", i.Category));
            var plan = VaColorImport.Map(items);
            uint Fg(SemanticKind k) => plan.Assignments.Single(a => a.Kind == k).Foreground;
            Assert.Equal(0xFFFFD700u, Fg(SemanticKind.Class));
            Assert.Equal(0xFFFFD700u, Fg(SemanticKind.UeType));
            Assert.Equal(0xFFFF8000u, Fg(SemanticKind.Method));
            Assert.True(plan.Assignments.Single(a => a.Kind == SemanticKind.Method).Bold);
            Assert.Equal(0xFFBDB76Bu, Fg(SemanticKind.Field));
            Assert.Equal(0xFFBD63C5u, Fg(SemanticKind.Macro));      // "VA Macro" beats "VA X Macro / Enum"
            Assert.Equal(0xFFB9771Eu, Fg(SemanticKind.EnumValue));  // "VA Enum Member" beats it too
            Assert.DoesNotContain(plan.Assignments, a => a.Kind == SemanticKind.Namespace); // automatic colour
            Assert.DoesNotContain(plan.Assignments, a => a.Kind == SemanticKind.UeSpecifier);
            Assert.Contains("VA Namespace", plan.WithoutColor);
            Assert.Contains("VA Brace Matching", plan.Ignored);
            Assert.Equal(8, plan.VaItems.Count);
            Assert.All(plan.Assignments, a => Assert.Equal(SemanticClassifications.NameFor(a.Kind), a.ClassificationName));
        }

        [Fact]
        public void OldVaXFileAndNoVaFile()
        {
            var plan = VaColorImport.Map(VaColorImport.Parse(
                "<UserSettings><Category GUID=\"{x}\"><Items><Item Name=\"VA X Macro / Enum\" Foreground=\"0x00A000A0\"/><Item Name=\"VA X Class\" Foreground=\"0x00AAB220\"/></Items></Category></UserSettings>"));
            Assert.Equal(0xFFA000A0u, plan.Assignments.Single(a => a.Kind == SemanticKind.EnumValue).Foreground);
            Assert.Equal(0xFFA000A0u, plan.Assignments.Single(a => a.Kind == SemanticKind.Macro).Foreground);
            Assert.Equal(0xFF20B2AAu, plan.Assignments.Single(a => a.Kind == SemanticKind.Struct).Foreground);
            Assert.True(VaColorImport.Map(VaColorImport.Parse("<UserSettings/>")).IsEmpty);
            Assert.ThrowsAny<System.Xml.XmlException>(() => VaColorImport.Parse("<!DOCTYPE x [<!ENTITY e \"a\">]><x>&e;</x>"));
        }
    }

    public class SemanticStoreTests
    {
        private static SemanticSpan S(int line, int col, SemanticKind k = SemanticKind.Class) => new SemanticSpan { Line = line, Column = col, Length = 3, Kind = k };

        [Fact]
        public void StoreKeepsOneVersionAndMergesCoverage()
        {
            var store = new SemanticLineStore();
            store.Apply(5, new LineRange(0, 9), new[] { S(1, 4), S(1, 0), S(20, 0), S(2, 0, SemanticKind.None) });
            Assert.Equal(2, store.SpanCount); // out-of-range and None dropped
            Assert.Equal(0, store.Line(1)[0].Column); // sorted
            store.Apply(5, new LineRange(10, 19), new[] { S(15, 0) });
            Assert.Single(store.Covered);
            Assert.True(store.Covers(5, new LineRange(3, 18)));
            Assert.False(store.Covers(6, new LineRange(3, 4)));
            store.Apply(5, new LineRange(1, 1), new SemanticSpan[0]); // re-classify a line: replaced
            Assert.Empty(store.Line(1));
            store.Apply(6, new LineRange(0, 4), new[] { S(0, 0) });   // new version resets
            Assert.Equal(6, store.Version);
            Assert.Empty(store.Line(15));
            Assert.Equal(5, store.CoveredLines);
            store.Trim(new LineRange(2, 3));
            Assert.Equal(0, store.SpanCount);
            Assert.Equal(new LineRange(2, 3), store.Covered.Single());
        }

        [Fact]
        public void PlannerRequestsOnlyMissingVisibleLines()
        {
            var store = new SemanticLineStore();
            Assert.Equal(new LineRange(0, 90), VisibleRangePlanner.Plan(10, 40, 1000, 1, store, 50));
            store.Apply(1, new LineRange(0, 90), null);
            Assert.Null(VisibleRangePlanner.Plan(10, 40, 1000, 1, store, 50));
            Assert.Equal(new LineRange(91, 110), VisibleRangePlanner.Plan(30, 60, 1000, 1, store, 50)); // scrolled down a bit
            Assert.Equal(new LineRange(0, 90), VisibleRangePlanner.Plan(10, 40, 1000, 2, store, 50));   // edited: whole window
            Assert.Equal(new LineRange(950, 999), VisibleRangePlanner.Plan(990, 1010, 1000, 2, store, 40));
            Assert.Null(VisibleRangePlanner.Plan(0, 10, 0, 1, store));
            var big = VisibleRangePlanner.Plan(1000, 1100, 5000, 1, null, 500, 300).Value;
            Assert.Equal(300, big.Count);
            Assert.True(big.Contains(1050));
        }

        [Fact]
        public void InFlightRequestsMerge()
        {
            var a = new LineRange(0, 90);
            Assert.Equal(new LineRange(0, 110), VisibleRangePlanner.Merge(a, 1, new LineRange(91, 110), 1));
            Assert.Equal(new LineRange(91, 110), VisibleRangePlanner.Merge(a, 0, new LineRange(91, 110), 1));   // other version
            Assert.Equal(new LineRange(500, 520), VisibleRangePlanner.Merge(a, 1, new LineRange(500, 520), 1)); // far apart
            Assert.Equal(new LineRange(95, 700), VisibleRangePlanner.Merge(a, 1, new LineRange(95, 700), 1, 600)); // too big
            Assert.Equal(new LineRange(5, 6), VisibleRangePlanner.Merge(null, 1, new LineRange(5, 6), 1));
        }
    }

    public class SignatureAndQuickInfoTests : IDisposable
    {
        private readonly UiLanguage _saved = Loc.Language;

        public void Dispose() => Loc.Language = _saved;

        [Theory]
        [InlineData("", 0)]
        [InlineData("a", 0)]
        [InlineData("a, b", 1)]
        [InlineData("Foo(1, 2), b", 1)]
        [InlineData("TEXT(\"a,b\"), ',', x", 2)]
        [InlineData("{1, 2}, [3, 4], ", 2)]
        [InlineData("a) + b", -1)]
        public void ActiveParameterCountsTopLevelCommas(string text, int expected) => Assert.Equal(expected, SignatureHelpModel.ActiveParameter(text));

        [Fact]
        public void FindOpenParen()
        {
            string t = "Foo(a, Bar(b), \"(\" ";
            Assert.Equal(3, SignatureHelpModel.FindOpenParen(t, t.Length));
            Assert.Equal(-1, SignatureHelpModel.FindOpenParen("Foo(a); x", 9));
            Assert.Equal(10, SignatureHelpModel.FindOpenParen("Foo(a, Bar(", 11));
            Assert.Equal(3, SignatureHelpModel.FindOpenParen("Foo(a, // ( comment\n b", 22));
        }

        [Fact]
        public void ParameterRangesFromEngineOrLabel()
        {
            var sig = new SignatureInfo { Label = "void SetActorLocation(const FVector& NewLocation, bool bSweep = false, TArray<int, X> Y)" };
            var r = SignatureHelpModel.ParameterRanges(sig);
            Assert.Equal(3, r.Count);
            Assert.Equal("const FVector& NewLocation", sig.Label.Substring(r[0].Start, r[0].Length));
            Assert.Equal("bool bSweep = false", sig.Label.Substring(r[1].Start, r[1].Length));
            Assert.Equal("TArray<int, X> Y", sig.Label.Substring(r[2].Start, r[2].Length));
            sig.Parameters.Add(new SignatureParameter { Name = "NewLocation", LabelStart = 37, LabelLength = 11 });
            sig.Parameters.Add(new SignatureParameter { Name = "bSweep", LabelStart = 55, LabelLength = 6 });
            sig.Parameters.Add(new SignatureParameter { Name = "Y", LabelStart = 87, LabelLength = 1 });
            Assert.Equal((37, 11), SignatureHelpModel.ParameterRanges(sig)[0]);
            sig.Parameters[2].LabelStart = 500; // invalid → fallback to splitting
            Assert.Equal(r, SignatureHelpModel.ParameterRanges(sig));
            Assert.Empty(SignatureHelpModel.ParameterRanges(new SignatureInfo { Label = "void F()" }));
        }

        [Fact]
        public void PickSignatureByArgumentCount()
        {
            var resp = new SignatureHelpResponse
            {
                Signatures =
                {
                    new SignatureInfo { Label = "void F(int a)" },
                    new SignatureInfo { Label = "void F(int a, int b)" },
                    new SignatureInfo { Label = "void F(const TCHAR* Fmt, ...)" },
                },
                ActiveSignature = 0,
            };
            Assert.Equal(0, SignatureHelpModel.PickSignature(resp, 0));
            Assert.Equal(1, SignatureHelpModel.PickSignature(resp, 1));
            Assert.Equal(2, SignatureHelpModel.PickSignature(resp, 5));
            Assert.Equal(-1, SignatureHelpModel.PickSignature(new SignatureHelpResponse(), 0));
        }

        [Fact]
        public void DeclarationTokensFollowSemanticColouring()
        {
            var sym = new SymbolInfo { Name = "BeginPlay", Kind = SymbolKind.Method };
            var t = DeclarationTokenizer.Tokenize("virtual void AMyActor::BeginPlay(const FVector& Loc, int32 Count = 3) override", sym);
            DeclToken Tok(string text) => t.First(x => x.Text == text);
            Assert.Equal(DeclTokenClass.Keyword, Tok("virtual").Class);
            Assert.Equal(DeclTokenClass.Keyword, Tok("int32").Class);
            Assert.Equal(SemanticKind.Class, Tok("AMyActor").Kind);
            Assert.Equal(SemanticKind.Method, Tok("BeginPlay").Kind);
            Assert.Equal(SemanticKind.Class, Tok("FVector").Kind);
            Assert.Equal(SemanticKind.Parameter, Tok("Loc").Kind);
            Assert.Equal(SemanticKind.Parameter, Tok("Count").Kind);
            Assert.Equal(DeclTokenClass.Number, Tok("3").Class);
            Assert.Equal(DeclTokenClass.Punctuation, Tok("::").Class);
            Assert.Equal("virtual void AMyActor::BeginPlay(const FVector& Loc, int32 Count = 3) override", string.Concat(t.Select(x => x.Text)));

            var m = DeclarationTokenizer.Tokenize("#define UE_LOG(Cat, Verb, Fmt, ...) FMsg::Logf(TEXT(\"x\"))");
            Assert.Equal(DeclTokenClass.Keyword, m[0].Class);
            Assert.Equal(SemanticKind.UeMacro, m.First(x => x.Text == "UE_LOG").Kind);
            Assert.Equal(SemanticKind.Class, m.First(x => x.Text == "FMsg").Kind);
            Assert.Equal(SemanticKind.Method, m.First(x => x.Text == "Logf").Kind);
            Assert.Equal(DeclTokenClass.String, m.First(x => x.Text == "\"x\"").Class);

            Assert.Equal(SemanticKind.UeType, DeclarationTokenizer.KindOf(new SymbolInfo { Kind = SymbolKind.Class, Ue = UeFlags.UClass }));
            Assert.Equal(SemanticKind.Field, DeclarationTokenizer.KindOf(new SymbolInfo { Kind = SymbolKind.Field }));
            Assert.True(DeclarationTokenizer.IsUePrefixedType("TArray"));
            Assert.False(DeclarationTokenizer.IsUePrefixedType("FOO"));
            Assert.False(DeclarationTokenizer.IsUePrefixedType("Foo"));
        }

        [Fact]
        public void QuickInfoModelSections()
        {
            Loc.Language = UiLanguage.English;
            var r = new QuickInfoResponse
            {
                Symbol = new SymbolInfo
                {
                    Name = "Fire", Kind = SymbolKind.Method, Ue = UeFlags.UFunction, Module = "MyGame",
                    Location = new SourceLocation { FilePath = @"C:\P\Source\Weapon.h", Line = 41 },
                },
                Declaration = "void AWeapon::Fire(float Power)",
                Documentation = "/**\n * Fires the weapon.\n * @param Power strength\n */",
                Reflection = "UFUNCTION(BlueprintCallable, Server, Reliable, Category=\"Combat\", meta=(Keywords=\"a,b\"))",
                MacroExpansion = string.Join("\n", Enumerable.Range(0, 500).Select(i => "line" + i)),
                OverloadCount = 2,
                Confidence = ResolutionConfidence.NameOnly,
            };
            var m = QuickInfoModel.Build(r);
            Assert.Equal("(+2 overloads)", m.Overloads);
            Assert.Equal(new[] { "UFUNCTION" }, m.Badges);
            Assert.Equal("MyGame · Weapon.h:42", m.Location);
            Assert.Equal("Fires the weapon.\n@param Power strength", m.Documentation);
            Assert.Equal(new[] { "Blueprint callable", "Server RPC", "Reliable" }, m.ReflectionTags);
            Assert.True(m.MacroExpansionTruncated);
            Assert.Equal(QuickInfoModel.MaxMacroLines, m.MacroExpansionLines);
            Assert.NotNull(m.ConfidenceNote);
            Assert.Contains(m.Declaration, x => x.Kind == SemanticKind.Method && x.Text == "Fire");

            var empty = QuickInfoModel.Build(new QuickInfoResponse());
            Assert.True(empty.IsEmpty);
            var sigOnly = QuickInfoModel.Build(new QuickInfoResponse { Symbol = new SymbolInfo { Name = "X", Kind = SymbolKind.Field, Signature = "int32 X" } });
            Assert.Equal(SemanticKind.Field, sigOnly.Declaration.Last().Kind);
            Assert.Null(QuickInfoModel.CleanDocumentation(" /** */ "));
        }

        [Fact]
        public void StringsExistInBothLanguages()
        {
            var props = typeof(IntelliSenseStrings).GetProperties(BindingFlags.Public | BindingFlags.Static).Where(p => p.PropertyType == typeof(string)).ToArray();
            Assert.True(props.Length > 10);
            foreach (var p in props)
            {
                Loc.Language = UiLanguage.Chinese;
                string zh = (string)p.GetValue(null);
                Loc.Language = UiLanguage.English;
                string en = (string)p.GetValue(null);
                Assert.False(string.IsNullOrWhiteSpace(zh), p.Name);
                Assert.NotEqual(zh, en);
                Assert.False(en.Any(c => c >= 0x4E00 && c <= 0x9FFF), p.Name + " (en) contains Chinese");
            }
            Loc.Language = UiLanguage.Chinese;
            Assert.Equal("(+3 个重载)", IntelliSenseStrings.Overloads(3));
            Assert.Null(IntelliSenseStrings.SpecifierTag("Category"));
        }

        [Fact]
        public void NewSettingsDefaults()
        {
            var s = new HostSettings();
            Assert.True(s.IntelliSenseTakeover); // ADR-08: on since M3
            Assert.True(s.CompletionEnabled);
            Assert.Equal(CompletionScope.Auto, s.CompletionScope);
            Assert.True(s.CompletionAccessorFix);
            Assert.True(s.SemanticColoring);
            Assert.Equal(50, s.SemanticColoringMarginLines);
            Assert.Equal(string.Empty, s.SemanticColorOverrides);
        }
    }
}
