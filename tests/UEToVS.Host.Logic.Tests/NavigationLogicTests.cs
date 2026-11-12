using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using UEToVS.Contracts;
using UEToVS.Core.Localization;
using UEToVS.Host.Logic.Localization;
using UEToVS.Host.Logic.Navigation;
using Xunit;

namespace UEToVS.Host.Logic.Tests
{
    public class CaretRequestBuilderTests
    {
        [Fact]
        public void ComputesZeroBasedColumnFromLineStart()
        {
            var r = CaretRequestBuilder.Build(@"C:\P\A.cpp", 9, 100, 30, 112, false, () => "x", 500, 7);
            Assert.Equal(9, r.Line);
            Assert.Equal(12, r.Column);
            Assert.Null(r.Text);
            Assert.Equal(7, r.TextVersion);
        }

        [Fact]
        public void SendsTextOnlyWhenDirty()
        {
            int calls = 0;
            var clean = CaretRequestBuilder.Build("a.h", 0, 0, 5, 1, false, () => { calls++; return "dirty"; }, 5, 1);
            Assert.Null(clean.Text);
            Assert.Equal(0, calls);
            var dirty = CaretRequestBuilder.Build("a.h", 0, 0, 5, 1, true, () => { calls++; return "dirty"; }, 5, 1);
            Assert.Equal("dirty", dirty.Text);
            Assert.Equal(1, calls);
        }

        [Fact]
        public void SkipsHugeDirtyBuffersAndClampsColumn()
        {
            var r = CaretRequestBuilder.Build("a.h", 2, 50, 4, 80, true, () => throw new InvalidOperationException(), CaretRequestBuilder.MaxInlineChars + 1, 1);
            Assert.Null(r.Text);
            Assert.Equal(4, r.Column); // clamped to the line length
            Assert.Equal(0, CaretRequestBuilder.Build("a.h", 0, 10, 4, 5, false, null, 0, 0).Column);
            Assert.Null(CaretRequestBuilder.Build(null, 0, 0, 0, 0, false, null, 0, 0));
        }

        [Fact]
        public void SameCaretComparesPathCaseInsensitivelyAndVersion()
        {
            var a = new CaretRequest { FilePath = @"C:\A.cpp", Line = 1, Column = 2, TextVersion = 3 };
            var b = new CaretRequest { FilePath = @"c:\a.CPP", Line = 1, Column = 2, TextVersion = 3 };
            Assert.True(CaretRequestBuilder.SameCaret(a, b));
            b.TextVersion = 4;
            Assert.False(CaretRequestBuilder.SameCaret(a, b));
            var d = CaretRequestBuilder.Describe(new CaretRequest { FilePath = "x", Text = "secret" });
            Assert.DoesNotContain("secret", d.Values);
            Assert.Equal(true, d["dirty"]);
        }
    }

    public class IdentifierFinderTests
    {
        [Theory]
        [InlineData("    Foo->BeginPlay();", 10, "BeginPlay")]
        [InlineData("    Foo->BeginPlay();", 4, "Foo")]
        [InlineData("UCLASS(BlueprintType)", 2, "UCLASS")]
        [InlineData("x = AMyActor::StaticClass();", 16, "StaticClass")]
        [InlineData("int _private_1 = 0;", 6, "_private_1")]
        public void FindsIdentifierUnderMouse(string line, int col, string expected)
        {
            Assert.Equal(expected, IdentifierFinder.WordAt(line, col, false));
        }

        [Theory]
        [InlineData("return Value;", 2)]          // keyword
        [InlineData("x = 1234;", 5)]              // number
        [InlineData("// Foo bar", 4)]             // comment
        [InlineData("a = 1; // Foo", 10)]         // trailing comment
        [InlineData("s = \"Hello World\";", 8)]   // string
        [InlineData("c = 'x';", 5)]               // char literal
        [InlineData("a + b", 2)]                  // operator
        [InlineData("#include \"Foo.h\"", 3)]     // directive name
        [InlineData("#define FOO 1", 3)]
        public void NoHitOutsideIdentifiers(string line, int col)
        {
            Assert.False(IdentifierFinder.TryFind(line, col, false, out _));
        }

        [Fact]
        public void BlockCommentClosedBeforeIsNotAComment()
        {
            Assert.Equal("Foo", IdentifierFinder.WordAt("/* x */ Foo();", 9, false));
            Assert.Null(IdentifierFinder.WordAt("/* Foo */", 4, false));
            Assert.Equal("Bar", IdentifierFinder.WordAt("int n = 1'000; Bar();", 16, false)); // digit separator
        }

        [Fact]
        public void CaretModeAcceptsPositionRightAfterWord()
        {
            Assert.Null(IdentifierFinder.WordAt("Foo;", 3, false));
            Assert.Equal("Foo", IdentifierFinder.WordAt("Foo;", 3, true));
            Assert.Equal("Foo", IdentifierFinder.WordAt("Foo", 3, true));
            Assert.Null(IdentifierFinder.WordAt("Foo", 4, true));
        }

        [Fact]
        public void IncludePathIsOneSpan()
        {
            Assert.True(IdentifierFinder.TryFind("#include \"GameFramework/Actor.h\"", 15, false, out var span));
            Assert.Equal(10, span.Start);
            Assert.Equal("GameFramework/Actor.h".Length, span.Length);
            Assert.True(IdentifierFinder.TryFind("  #  include <vector>", 15, false, out span));
            Assert.Equal("vector".Length, span.Length);
        }
    }

    public class CtrlClickGestureTests
    {
        private static readonly LineSpan Word = new LineSpan(4, 6);

        [Fact]
        public void CtrlClickOnWordNavigates()
        {
            var g = new CtrlClickGesture();
            Assert.Equal(CtrlClickAction.Hover, g.OnMove(KeyMods.Ctrl, true, 10, 10));
            Assert.Equal(CtrlClickAction.Arm, g.OnLeftDown(KeyMods.Ctrl, true, 3, Word, 10, 10));
            Assert.Equal(CtrlClickAction.Navigate, g.OnLeftUp(KeyMods.Ctrl, true, 3, Word, 11, 10));
            Assert.False(g.IsArmed);
        }

        [Theory]
        [InlineData(KeyMods.None)]
        [InlineData(KeyMods.Ctrl | KeyMods.Alt)]   // multi-caret
        [InlineData(KeyMods.Ctrl | KeyMods.Shift)] // extend selection
        public void OtherModifiersAreLeftToVs(KeyMods mods)
        {
            var g = new CtrlClickGesture();
            Assert.Equal(CtrlClickAction.None, g.OnLeftDown(mods, true, 0, Word, 0, 0));
            Assert.Equal(CtrlClickAction.ClearHover, g.OnMove(mods, true, 0, 0));
        }

        [Fact]
        public void DragCancels()
        {
            var g = new CtrlClickGesture();
            g.OnLeftDown(KeyMods.Ctrl, true, 0, Word, 0, 0);
            Assert.Equal(CtrlClickAction.Cancel, g.OnMove(KeyMods.Ctrl, true, 30, 0));
            Assert.Equal(CtrlClickAction.None, g.OnLeftUp(KeyMods.Ctrl, true, 0, Word, 30, 0));
        }

        [Fact]
        public void ReleaseOnAnotherWordOrWithoutCtrlCancels()
        {
            var g = new CtrlClickGesture();
            g.OnLeftDown(KeyMods.Ctrl, true, 0, Word, 0, 0);
            Assert.Equal(CtrlClickAction.Cancel, g.OnLeftUp(KeyMods.Ctrl, true, 0, new LineSpan(20, 3), 0, 0));
            g.OnLeftDown(KeyMods.Ctrl, true, 0, Word, 0, 0);
            Assert.Equal(CtrlClickAction.Cancel, g.OnLeftUp(KeyMods.None, true, 0, Word, 0, 0));
        }

        [Fact]
        public void DisabledAndDoubleClickDoNothing()
        {
            var g = new CtrlClickGesture { Enabled = false };
            Assert.Equal(CtrlClickAction.None, g.OnLeftDown(KeyMods.Ctrl, true, 0, Word, 0, 0));
            Assert.Equal(CtrlClickAction.ClearHover, g.OnMove(KeyMods.Ctrl, true, 0, 0));
            g.Enabled = true;
            Assert.Equal(CtrlClickAction.None, g.OnLeftDown(KeyMods.Ctrl, true, 0, Word, 0, 0, clickCount: 2));
            Assert.Equal(CtrlClickAction.None, g.OnLeftDown(KeyMods.Ctrl, false, 0, Word, 0, 0));
        }
    }

    public class RequestGateTests
    {
        [Fact]
        public void BeginCancelsPreviousAndOnlyLatestIsCurrent()
        {
            using (var gate = new LatestRequestGate())
            {
                var (g1, t1) = gate.Begin();
                var (g2, t2) = gate.Begin();
                Assert.True(t1.IsCancellationRequested);
                Assert.False(t2.IsCancellationRequested);
                Assert.False(gate.IsCurrent(g1));
                Assert.True(gate.IsCurrent(g2));
                gate.Cancel();
                Assert.True(t2.IsCancellationRequested);
                Assert.False(gate.IsCurrent(g2));
            }
        }

        [Fact]
        public async Task DebouncerRunsOnceAfterQuietPeriod()
        {
            int runs = 0;
            using (var d = new Debouncer(TimeSpan.FromMilliseconds(80), ct => { Interlocked.Increment(ref runs); return Task.CompletedTask; }))
            {
                for (int i = 0; i < 5; i++)
                {
                    d.Trigger();
                    await Task.Delay(10);
                }
                await Task.Delay(400);
                Assert.Equal(1, runs);
                d.Trigger();
                d.Cancel();
                await Task.Delay(250);
                Assert.Equal(1, runs);
            }
        }

        [Fact]
        public async Task DebouncerReportsErrors()
        {
            Exception seen = null;
            using (var d = new Debouncer(TimeSpan.FromMilliseconds(1), ct => throw new InvalidOperationException("boom")))
            {
                d.Error += e => seen = e;
                d.Trigger();
                for (int i = 0; i < 50 && seen == null; i++) await Task.Delay(10);
            }
            Assert.IsType<InvalidOperationException>(seen);
        }
    }

    public class NavigationOutcomeTests : IDisposable
    {
        private readonly UiLanguage _saved = Loc.Language;

        public void Dispose() => Loc.Language = _saved;

        private static NavigationTarget T(string file, int line, ItemOrigin origin = ItemOrigin.Project, NavigationTargetRole role = NavigationTargetRole.Definition) =>
            new NavigationTarget { Role = role, Symbol = new SymbolInfo { Name = "Foo", Location = new SourceLocation { FilePath = file, Line = line }, Origin = origin } };

        [Fact]
        public void SingleTargetJumpsSeveralChoose()
        {
            var one = NavigationOutcome.Decide(EngineCallStatus.Ok, new NavigationResponse { Word = "Foo", IndexReady = true, Targets = { T("a.cpp", 1) } }, NavigationKind.Toggle);
            Assert.Equal(NavigationDecision.Jump, one.Decision);
            Assert.Null(one.Message);
            var two = NavigationOutcome.Decide(EngineCallStatus.Ok, new NavigationResponse { Word = "Foo", IndexReady = true, Targets = { T("a.cpp", 1), T("b.cpp", 2) } }, NavigationKind.Toggle);
            Assert.Equal(NavigationDecision.Choose, two.Decision);
        }

        [Fact]
        public void RankDropsDuplicatesAndMissingLocationsAndPutsProjectFirst()
        {
            var ranked = NavigationOutcome.Rank(new[]
            {
                T(@"E:\UE\Engine\Actor.h", 5, ItemOrigin.Engine),
                T(@"C:\P\A.cpp", 1),
                T(@"c:\p\a.cpp", 1),
                new NavigationTarget { Symbol = new SymbolInfo { Name = "NoLoc" } },
                null,
                T(@"C:\P\B.cpp", 3),
            });
            Assert.Equal(3, ranked.Count);
            Assert.Equal(@"C:\P\A.cpp", ranked[0].Symbol.Location.FilePath);
            Assert.Equal(@"C:\P\B.cpp", ranked[1].Symbol.Location.FilePath);
            Assert.Equal(ItemOrigin.Engine, ranked[2].Symbol.Origin);
        }

        [Fact]
        public void MessagesExplainWhy()
        {
            Loc.Language = UiLanguage.English;
            Assert.Equal(NavStrings.EngineUnavailable, NavigationOutcome.Decide(EngineCallStatus.Unavailable, null, NavigationKind.Toggle).Message);
            Assert.Null(NavigationOutcome.Decide(EngineCallStatus.Canceled, null, NavigationKind.Toggle).Message);
            Assert.Equal(NavStrings.NotOnIdentifier, NavigationOutcome.Decide(EngineCallStatus.Ok, new NavigationResponse { Word = "" }, NavigationKind.Toggle).Message);
            var m = NavigationOutcome.Decide(EngineCallStatus.Ok, new NavigationResponse { Word = "Foo", Reason = "not-found", IndexReady = false }, NavigationKind.Definition).Message;
            Assert.Contains("'Foo'", m);
            Assert.Contains("name not in the index", m);
            Assert.Contains("indexing is not finished", m);
            Assert.Contains("implementation", NavigationOutcome.Decide(EngineCallStatus.Ok, new NavigationResponse { Word = "Foo", IndexReady = true }, NavigationKind.Implementation).Message);
            Assert.Equal("custom-code", NavStrings.ReasonText("custom-code"));
        }
    }

    public class ReferenceResultsTests : IDisposable
    {
        private readonly UiLanguage _saved = Loc.Language;

        public void Dispose() => Loc.Language = _saved;

        private static ReferenceHit H(string file, int line, int col, ReferenceKind kind = ReferenceKind.Read, ItemOrigin origin = ItemOrigin.Project,
                                      ResolutionConfidence conf = ResolutionConfidence.Exact) =>
            new ReferenceHit { Location = new SourceLocation { FilePath = file, Line = line, Column = col }, Kind = kind, Origin = origin, Confidence = conf, Length = 3 };

        private static readonly ReferenceHit[] Sample =
        {
            H(@"C:\P\B.cpp", 20, 4, ReferenceKind.Write),
            H(@"C:\P\B.cpp", 3, 1),
            H(@"C:\P\A.h", 7, 2, ReferenceKind.Declaration),
            H(@"E:\UE\Engine\Actor.cpp", 1, 1, ReferenceKind.Call, ItemOrigin.Engine),
            H(@"C:\P\A.h", 9, 0, ReferenceKind.Read, ItemOrigin.Project, ResolutionConfidence.NameOnly),
            H(@"c:\p\b.cpp", 3, 1), // duplicate position
        };

        [Fact]
        public void GroupsByFileSortsAndDeduplicates()
        {
            var v = ReferenceResults.Build(Sample, new ReferenceFilter());
            Assert.Equal(new[] { "A.h", "B.cpp" }, v.Groups.Select(g => g.FileName));
            Assert.Equal(new[] { 3, 20 }, v.Groups[1].Hits.Select(h => h.Location.Line));
            Assert.Equal(1, v.Groups[1].WriteCount);
            Assert.Equal(4, v.Shown);
            Assert.Equal(1, v.Hidden); // engine hit
        }

        [Fact]
        public void FiltersWritesEngineAndUncertain()
        {
            var writes = ReferenceResults.Build(Sample, new ReferenceFilter { WritesOnly = true });
            Assert.Single(writes.Groups);
            Assert.Equal(1, writes.Shown);
            var engine = ReferenceResults.Build(Sample, new ReferenceFilter { IncludeEngine = true });
            Assert.Equal(3, engine.Groups.Count);
            Assert.Equal("Actor.cpp", engine.Groups.Last().FileName); // engine files after project files
            var sure = ReferenceResults.Build(Sample, new ReferenceFilter { ShowUncertain = false });
            Assert.Equal(3, sure.Shown);
            Assert.True(ReferenceResults.IsUncertain(Sample[4]));
        }

        [Fact]
        public void MatchRangeIsClamped()
        {
            Assert.Equal((4, 3), ReferenceResults.MatchRange(new ReferenceHit { LineText = "int Foo = 1;", LineTextColumn = 4, Length = 3 }));
            Assert.Equal((10, 2), ReferenceResults.MatchRange(new ReferenceHit { LineText = "int Foo = 1;", LineTextColumn = 10, Length = 9 }));
            Assert.Null(ReferenceResults.MatchRange(new ReferenceHit { LineText = "abc", LineTextColumn = 9, Length = 1 }));
            Assert.Null(ReferenceResults.MatchRange(new ReferenceHit { LineText = null, Length = 1 }));
        }

        [Fact]
        public void DisplayLineStripsIndentAndShiftsMatch()
        {
            var h = new ReferenceHit { Location = new SourceLocation { FilePath = "a", Line = 41 }, LineText = "\t    Health = 10;  ", LineTextColumn = 5, Length = 6 };
            string s = ReferenceResults.DisplayLine(h, out int start, out int len);
            Assert.Equal("42  Health = 10;", s);
            Assert.Equal("Health", s.Substring(start, len));
            var noMatch = new ReferenceHit { Location = new SourceLocation { FilePath = "a", Line = 0 }, LineText = "x", LineTextColumn = 7, Length = 2 };
            Assert.Equal("1  x", ReferenceResults.DisplayLine(noMatch, out _, out len));
            Assert.Equal(0, len);
            Assert.Equal(string.Empty, ReferenceResults.DisplayLine(null, out _, out _));
        }

        [Fact]
        public void KindTextPrefersWriteAndSummaryCounts()
        {
            Loc.Language = UiLanguage.English;
            Assert.Equal("Write", ReferenceResults.KindText(ReferenceKind.Read | ReferenceKind.Write));
            Assert.Equal("Call", ReferenceResults.KindText(ReferenceKind.Call | ReferenceKind.Read));
            Assert.Null(ReferenceResults.KindText(ReferenceKind.None));
            var v = ReferenceResults.Build(Sample, new ReferenceFilter());
            string s = ReferenceResults.Summary(new ReferencesResponse { ElapsedMs = 12.4, Truncated = true, IndexReady = true }, v);
            Assert.Contains("4 references", s);
            Assert.Contains("2 files", s);
            Assert.Contains("truncated", s);
            Assert.Contains("1 hidden", s);
        }
    }

    public class ScopeModelTests
    {
        private static SymbolInfo S(string name, SymbolKind kind, string container = null, int line = 0, UeFlags ue = UeFlags.None) =>
            new SymbolInfo { Name = name, Kind = kind, Container = container, Ue = ue, Location = new SourceLocation { FilePath = "f.cpp", Line = line } };

        private static CaretContextResponse Cpp() => new CaretContextResponse
        {
            Scopes = { S("Game", SymbolKind.Namespace), S("BeginPlay", SymbolKind.Method, "AMyActor", 10) },
            FileSymbols =
            {
                S("Helper", SymbolKind.Function, null, 2),
                S("BeginPlay", SymbolKind.Method, "AMyActor", 10),
                S("Tick", SymbolKind.Method, "AMyActor", 20),
                S("Run", SymbolKind.Method, "UOther", 30),
                S("FLocal", SymbolKind.Struct, null, 40),
                S("Value", SymbolKind.Field, "FLocal", 41),
            },
        };

        [Fact]
        public void OutOfLineDefinitionsYieldTheirClass()
        {
            var ctx = Cpp();
            Assert.Null(ScopeModel.CurrentType(ctx));
            Assert.Equal("BeginPlay", ScopeModel.CurrentFunction(ctx).Name);
            Assert.Equal("AMyActor", ScopeModel.CurrentTypeKey(ctx));
            var types = ScopeModel.TypeEntries(ctx);
            Assert.Equal(new[] { "", "AMyActor", "UOther", "FLocal" }, types.Select(t => t.Name));
            Assert.True(types[0].IsGlobal);
            Assert.Equal(10, types[1].Location.Line);
            Assert.Equal(new[] { "BeginPlay", "Tick" }, ScopeModel.MembersOf(ctx, "AMyActor").Select(m => m.Name));
            Assert.Equal(new[] { "Helper" }, ScopeModel.MembersOf(ctx, "").Select(m => m.Name));
            Assert.Equal(new[] { "Value" }, ScopeModel.MembersOf(ctx, "FLocal").Select(m => m.Name));
        }

        [Fact]
        public void HeaderTypeIsCurrentTypeAndDeclaredTypeWinsOverContainerEntry()
        {
            var ctx = new CaretContextResponse
            {
                Scopes = { S("AMyActor", SymbolKind.Class, null, 5) },
                FileSymbols = { S("Tick", SymbolKind.Method, "AMyActor", 8), S("AMyActor", SymbolKind.Class, null, 5), S("Inner", SymbolKind.Struct, "AMyActor", 12) },
            };
            Assert.Equal("AMyActor", ScopeModel.CurrentTypeKey(ctx));
            var types = ScopeModel.TypeEntries(ctx);
            Assert.Equal(2, types.Count);
            Assert.NotNull(types[0].Symbol);
            Assert.Equal(5, types[0].Location.Line);
            Assert.Equal("AMyActor::Inner", types[1].Key);
        }

        [Fact]
        public void FileMemberListSkipsMacrosNamespacesAndDuplicates()
        {
            var list = ScopeModel.FileMemberList(new[]
            {
                S("Game", SymbolKind.Namespace), S("UE_LOG", SymbolKind.Macro), S("A", SymbolKind.Class, null, 1), S("A", SymbolKind.Class, null, 1), S("F", SymbolKind.Method, "A", 2), null,
            });
            Assert.Equal(new[] { "A", "F" }, list.Select(s => s.Name));
        }

        [Fact]
        public void TypeMemberListPutsOwnMembersFirst()
        {
            var r = new MembersResponse
            {
                Type = S("AMyActor", SymbolKind.Class),
                Members = { S("GetOwner", SymbolKind.Method, "AActor"), S("Tick", SymbolKind.Method, "AMyActor"), S("Rename", SymbolKind.Method, "UObject"), S("Health", SymbolKind.Field, "AMyActor") },
            };
            var list = ScopeModel.TypeMemberList(r);
            Assert.Equal(new[] { "Tick", "Health", "GetOwner", "Rename" }, list.Select(s => s.Name));
            Assert.False(ScopeModel.IsInherited(list[0], r.Type));
            Assert.True(ScopeModel.IsInherited(list[2], r.Type));
            Assert.Empty(ScopeModel.TypeMemberList(null));
        }
    }

    public class SymbolPresentationTests
    {
        [Fact]
        public void GlyphsAndBadges()
        {
            Assert.Equal(UEToVS.UI.Logic.SymbolKind.Method, SymbolPresentation.GlyphFor(SymbolKind.Constructor));
            Assert.Equal(UEToVS.UI.Logic.SymbolKind.Interface, SymbolPresentation.GlyphFor(new SymbolInfo { Kind = SymbolKind.Class, Ue = UeFlags.UInterface }));
            Assert.Equal(UEToVS.UI.Logic.SymbolKind.Struct, SymbolPresentation.GlyphFor(SymbolKind.Union));
            foreach (SymbolKind k in Enum.GetValues(typeof(SymbolKind)))
                if (k != SymbolKind.Unknown) Assert.NotEqual(UEToVS.UI.Logic.SymbolKind.Unknown, SymbolPresentation.GlyphFor(k));
            Assert.Equal("UFUNCTION", SymbolPresentation.UeBadge(UeFlags.UFunction | UeFlags.UMeta).Value.Text);
            Assert.Equal(UEToVS.UI.Logic.BadgeKind.UClass, SymbolPresentation.UeBadge(UeFlags.UClass | UeFlags.GeneratedBody).Value.Kind);
            Assert.Null(SymbolPresentation.UeBadge(UeFlags.GeneratedBody));
        }

        [Fact]
        public void Texts()
        {
            Assert.Equal("AMyActor::BeginPlay", SymbolPresentation.QualifiedName(new SymbolInfo { Name = "BeginPlay", Container = "AMyActor" }));
            Assert.Equal("Foo", SymbolPresentation.QualifiedName(new SymbolInfo { Name = "Foo" }));
            Assert.Equal("Actor.h:121", SymbolPresentation.ShortLocation(new SourceLocation { FilePath = "/ue/Actor.h", Line = 120 }));
            Assert.Equal(string.Empty, SymbolPresentation.ShortLocation(null));
            Assert.Equal("Engine", SymbolPresentation.ModuleAndOrigin("Engine", ItemOrigin.Project));
            Assert.Contains(" · ", SymbolPresentation.ModuleAndOrigin("Engine", ItemOrigin.Engine));
        }
    }

    public class HighlightColorsTests
    {
        [Theory]
        [InlineData("#264F78", 0xFF264F78u)]
        [InlineData("80ADD6FF", 0x80ADD6FFu)]
        [InlineData(" #5a3d1e ", 0xFF5A3D1Eu)]
        public void Parses(string text, uint expected)
        {
            Assert.True(HighlightColors.TryParse(text, out uint v));
            Assert.Equal(expected, v);
        }

        [Fact]
        public void FallsBackToThemeDefaults()
        {
            Assert.False(HighlightColors.TryParse("red", out _));
            Assert.Equal(HighlightColors.DarkWrite, HighlightColors.Resolve("", true, true));
            Assert.Equal(HighlightColors.LightRead, HighlightColors.Resolve("#12", false, false));
            Assert.Equal(0xFF010203u, HighlightColors.Resolve("#010203", false, true));
            Assert.Equal("#264F78", HighlightColors.Format(HighlightColors.DarkRead));
            Assert.Equal("#80ADD6FF", HighlightColors.Format(0x80ADD6FF));
            Assert.True(HighlightColors.IsDark(30, 30, 30));
            Assert.False(HighlightColors.IsDark(255, 255, 255));
        }
    }

    public class NavStringsTests : IDisposable
    {
        private readonly UiLanguage _saved = Loc.Language;

        public void Dispose() => Loc.Language = _saved;

        [Fact]
        public void EveryStringExistsInBothLanguagesAndDiffers()
        {
            var props = typeof(NavStrings).GetProperties(BindingFlags.Public | BindingFlags.Static).Where(p => p.PropertyType == typeof(string)).ToArray();
            Assert.True(props.Length > 40);
            Loc.Language = UiLanguage.Chinese;
            var zh = props.ToDictionary(p => p.Name, p => (string)p.GetValue(null));
            Loc.Language = UiLanguage.English;
            var en = props.ToDictionary(p => p.Name, p => (string)p.GetValue(null));
            foreach (var key in zh.Keys)
            {
                Assert.False(string.IsNullOrWhiteSpace(zh[key]), key);
                Assert.False(string.IsNullOrWhiteSpace(en[key]), key);
                Assert.NotEqual(zh[key], en[key]);
                Assert.DoesNotMatch("[\u4e00-\u9fff]", en[key]);
            }
            Assert.Contains("12", NavStrings.ReferencesSummary(12, 3, 4, false));
        }
    }
}
