using System;
using System.IO;
using System.Linq;
using UEToVS.Host.Logic.Build;
using Xunit;

namespace UEToVS.Host.Logic.Tests
{
    public class BuildOutputParserTests
    {
        [Theory]
        [InlineData(@"C:\Proj\Source\MyGame\Private\MyActor.cpp(42): error C2065: 'Foo': undeclared identifier", @"C:\Proj\Source\MyGame\Private\MyActor.cpp", 42, 0, "C2065", "msvc")]
        [InlineData(@"1>C:\Proj\Source\MyGame\Private\MyActor.cpp(42,17): error C2039: 'Bar': is not a member of 'AMyActor'", @"C:\Proj\Source\MyGame\Private\MyActor.cpp", 42, 17, "C2039", "msvc")]
        [InlineData(@"  12>D:\UE 5.3\Engine\Source\Runtime\Core\Public\Foo.h(7,3,7,9): fatal error C1083: Cannot open include file: 'X.h': No such file or directory", @"D:\UE 5.3\Engine\Source\Runtime\Core\Public\Foo.h", 7, 3, "C1083", "msvc")]
        [InlineData(@"C:\Proj\Source\MyGame\Public\MyActor.h(25): Error: Unrecognized type 'FMissing' - type must be a UCLASS, USTRUCT, UENUM, or global delegate.", @"C:\Proj\Source\MyGame\Public\MyActor.h", 25, 0, null, "uht")]
        [InlineData(@"C:\Proj\Source\MyGame\MyGame.Build.cs(12,4): error CS0103: The name 'Foo' does not exist in the current context", @"C:\Proj\Source\MyGame\MyGame.Build.cs", 12, 4, "CS0103", "csc")]
        [InlineData(@"C:\Program Files\Microsoft Visual Studio\2022\Community\MSBuild\Microsoft\VC\v170\Microsoft.MakeFile.Targets(44,5): error MSB3073: The command ""Build.bat"" exited with code 6.", @"C:\Program Files\Microsoft Visual Studio\2022\Community\MSBuild\Microsoft\VC\v170\Microsoft.MakeFile.Targets", 44, 5, "MSB3073", "msbuild")]
        [InlineData(@"C:\Proj\Source\MyGame\Private\A.cpp(3): error : expected ';' after expression", @"C:\Proj\Source\MyGame\Private\A.cpp", 3, 0, null, "msvc")]
        public void ParsesMsvcStyleLines(string line, string file, int lineNo, int col, string code, string tool)
        {
            var d = BuildOutputParser.ParseLine(line);
            Assert.NotNull(d);
            Assert.Equal(file, d.File);
            Assert.Equal(lineNo, d.Line);
            Assert.Equal(col, d.Column);
            Assert.Equal(code, d.Code);
            Assert.Equal(tool, d.Tool);
            Assert.Equal(BuildSeverity.Error, d.Severity);
            Assert.False(string.IsNullOrEmpty(d.Message));
        }

        [Fact]
        public void WarningsAndNotes()
        {
            var w = BuildOutputParser.ParseLine(@"C:\P\A.cpp(10): warning C4996: 'FOld': Deprecated. Please update your code.");
            Assert.Equal(BuildSeverity.Warning, w.Severity);
            Assert.Equal("C4996", w.Code);
            Assert.StartsWith("'FOld': Deprecated", w.Message);
            var n = BuildOutputParser.ParseLine(@"C:\P\A.h(3): note: see declaration of 'AMyActor'");
            Assert.Equal(BuildSeverity.Info, n.Severity);
            Assert.Null(n.Code);
            var uhtWarn = BuildOutputParser.ParseLine(@"C:\P\A.h(30): Warning: Member variable declaration: Missing variable type");
            Assert.Equal(BuildSeverity.Warning, uhtWarn.Severity);
            Assert.Equal("uht", uhtWarn.Tool);
        }

        [Theory]
        [InlineData("/home/u/Proj/Source/MyGame/Private/MyActor.cpp:42:17: error: use of undeclared identifier 'Foo'", "/home/u/Proj/Source/MyGame/Private/MyActor.cpp", 42, 17, BuildSeverity.Error, null)]
        [InlineData(@"C:\Proj\Source\A.cpp:10:5: warning: unused variable 'x' [-Wunused-variable]", @"C:\Proj\Source\A.cpp", 10, 5, BuildSeverity.Warning, "-Wunused-variable")]
        [InlineData("/p/A.h:3: fatal error: 'Missing.h' file not found", "/p/A.h", 3, 0, BuildSeverity.Error, null)]
        [InlineData("/p/A.h:9:1: note: previous definition is here", "/p/A.h", 9, 1, BuildSeverity.Info, null)]
        public void ParsesClangLines(string line, string file, int lineNo, int col, BuildSeverity sev, string code)
        {
            var d = BuildOutputParser.ParseLine(line);
            Assert.NotNull(d);
            Assert.Equal(file, d.File);
            Assert.Equal(lineNo, d.Line);
            Assert.Equal(col, d.Column);
            Assert.Equal(sev, d.Severity);
            Assert.Equal(code, d.Code);
            Assert.Equal("clang", d.Tool);
        }

        [Theory]
        [InlineData("LINK : fatal error LNK1104: cannot open file 'UnrealEditor-MyGame.dll'", "LNK1104", "linker")]
        [InlineData("Module.MyGame.cpp.obj : error LNK2019: unresolved external symbol \"public: void __cdecl AMyActor::Foo(void)\" referenced in function", "LNK2019", "linker")]
        [InlineData("UnrealBuildTool : error : Unable to find plugin 'Missing'", null, "ubt")]
        [InlineData("clang: error: linker command failed with exit code 1 (use -v to see invocation)", null, "clang")]
        public void ParsesToolLinesWithoutLocation(string line, string code, string tool)
        {
            var d = BuildOutputParser.ParseLine(line);
            Assert.NotNull(d);
            Assert.Null(d.File);
            Assert.False(d.HasLocation);
            Assert.Equal(code, d.Code);
            Assert.Equal(tool, d.Tool);
            Assert.Equal(BuildSeverity.Error, d.Severity);
        }

        [Fact]
        public void ParsesUbtAndUhtSummaryLines()
        {
            var a = BuildOutputParser.ParseLine("ERROR: Expecting to find a type to be declared in a module rules named 'Foo'");
            Assert.Equal(BuildSeverity.Error, a.Severity);
            Assert.Equal("ubt", a.Tool);
            var b = BuildOutputParser.ParseLine("LogCompile: Error: Failed to generate code for MyGameEditor - error code: OtherCompilationError (5)");
            Assert.Equal(BuildSeverity.Error, b.Severity);
            Assert.Equal("uht", b.Tool);
            Assert.StartsWith("Failed to generate code", b.Message);
            var c = BuildOutputParser.ParseLine("Warning: Plugin 'X' does not list plugin 'Y' as a dependency");
            Assert.Equal(BuildSeverity.Warning, c.Severity);
        }

        [Fact]
        public void StripsUeLogPrefixesForLiveCodingLines()
        {
            var d = BuildOutputParser.ParseLine(@"[2026.11.12-10.00.00:123][  0]LogLiveCoding: Display: C:\P\Source\G\Private\A.cpp(12): error C2065: 'X': undeclared identifier");
            Assert.NotNull(d);
            Assert.Equal(@"C:\P\Source\G\Private\A.cpp", d.File);
            Assert.Equal(12, d.Line);
            Assert.Equal("C2065", d.Code);
            var e = BuildOutputParser.ParseLine("LogLiveCoding: Error: Live coding failed, please see Live console for more information");
            Assert.Equal(BuildSeverity.Error, e.Severity);
            Assert.Equal("livecoding", e.Tool);
            Assert.Null(BuildOutputParser.ParseLine("LogLiveCoding: Display: Starting Live Coding compile."));
        }

        [Theory]
        [InlineData("")]
        [InlineData("   ")]
        [InlineData("Building 3 actions with 16 processes...")]
        [InlineData("[1/3] Compile [x64] MyActor.cpp")]
        [InlineData("    3 Error(s)")]
        [InlineData("========== Build: 0 succeeded, 1 failed, 0 up-to-date, 0 skipped ==========")]
        [InlineData("Result: Failed (OtherCompilationError)")]
        [InlineData("Total execution time: 12.34 seconds")]
        [InlineData("Build FAILED.")]
        [InlineData("2 warnings and 1 error generated.")]
        [InlineData("1>------ Build started: Project: MyGame, Configuration: Development_Editor x64 ------")]
        [InlineData("See http://example.com/page(1): error docs")]
        public void IgnoresNonDiagnostics(string line) => Assert.Null(BuildOutputParser.ParseLine(line));

        [Fact]
        public void RelativePathsResolveAgainstBaseDirectory()
        {
            string baseDir = Path.Combine(Path.GetTempPath(), "proj");
            var d = BuildOutputParser.ParseLine(@"Source\A.cpp(3): error C2065: x", baseDir);
            Assert.Equal(Path.Combine(baseDir, @"Source\A.cpp"), d.File);
            Assert.Equal(@"C:\abs\A.cpp", BuildOutputParser.ParseLine(@"C:\abs\A.cpp(3): error C2065: x", baseDir).File);
            Assert.True(BuildOutputParser.IsRooted(@"C:\x"));
            Assert.True(BuildOutputParser.IsRooted(@"\\server\share\x"));
            Assert.True(BuildOutputParser.IsRooted("/x"));
            Assert.False(BuildOutputParser.IsRooted(@"x\y"));
        }

        [Fact]
        public void ParserDeduplicatesCountsAndSamples()
        {
            var p = new BuildOutputParser();
            p.AddText(string.Join("\r\n", new[]
            {
                "1>------ Build started ------",
                @"1>C:\P\A.cpp(10): error C2065: 'X': undeclared identifier",
                @"1>C:\P\A.cpp(10): error C2065: 'X': undeclared identifier", // UBT repeats it in the summary
                @"1>C:\P\A.cpp(12): warning C4996: old",
                @"1>C:\P\A.h(3): note: see declaration",
                "1>something went wrong: error happened in a weird format",
                "1>    1 Error(s)",
                "",
            }));
            Assert.Equal(3, p.Items.Count);
            Assert.Equal(1, p.Errors);
            Assert.Equal(1, p.Warnings);
            Assert.Equal(1, p.Notes);
            Assert.Equal(1, p.Duplicates);
            Assert.Equal(1, p.Unparsed);
            Assert.Single(p.UnparsedSamples);
            Assert.Contains("weird format", p.UnparsedSamples[0]);
            Assert.Equal(7, p.LinesSeen);
        }

        [Fact]
        public void ParserCapsItemsAndSamples()
        {
            var p = new BuildOutputParser();
            for (int i = 0; i < BuildOutputParser.MaxItems + 10; i++) p.AddLine(@"C:\P\A.cpp(" + (i + 1) + "): error C2065: x");
            for (int i = 0; i < BuildOutputParser.MaxSamples + 5; i++) p.AddLine("odd: error " + i + " " + new string('x', 500));
            Assert.Equal(BuildOutputParser.MaxItems, p.Items.Count);
            Assert.True(p.Truncated);
            Assert.Equal(BuildOutputParser.MaxSamples, p.UnparsedSamples.Count);
            Assert.All(p.UnparsedSamples, s => Assert.True(s.Length <= BuildOutputParser.MaxSampleLength + 1));
        }

        [Fact]
        public void RecordRoundTripsThroughJson()
        {
            var p = new BuildOutputParser();
            p.AddLine(@"C:\P\A.cpp(10,3): error C2065: 'X': undeclared identifier");
            p.AddLine("LINK : fatal error LNK1104: cannot open file 'x.dll'");
            p.AddLine("weird: error thing");
            var start = new DateTime(2026, 11, 12, 10, 0, 0, DateTimeKind.Utc);
            var r = BuildRecord.From(p, start, start.AddSeconds(12.5), "failed");
            string json = BuildRecordStore.Serialize(r);
            Assert.Contains("\"severity\": \"error\"", json);
            Assert.Contains("\"durationMs\": 12500", json);
            var back = BuildRecordStore.Deserialize(json);
            Assert.Equal("failed", back.Result);
            Assert.Equal(start, back.StartedUtc);
            Assert.Equal(12500, back.DurationMs);
            Assert.Equal(2, back.Errors);
            Assert.Equal(2, back.Items.Count);
            Assert.Equal(@"C:\P\A.cpp", back.Items[0].File);
            Assert.Equal(3, back.Items[0].Column);
            Assert.Equal("msvc", back.Items[0].Tool);
            Assert.Null(back.Items[1].File);
            Assert.Equal("LNK1104", back.Items[1].Code);
            Assert.Single(back.UnparsedSamples);
            Assert.Null(BuildRecordStore.Deserialize("{\"version\":99}"));
            Assert.Null(BuildRecordStore.Deserialize("not json"));
        }

        [Fact]
        public void RecordFileIsWrittenUnderDotVs()
        {
            string dir = Path.Combine(Path.GetTempPath(), "uetovs-build-" + Guid.NewGuid().ToString("N"));
            try
            {
                string path = BuildRecordStore.PathFor(dir);
                Assert.EndsWith(Path.Combine(".vs", "UEToVS", "build", "last-build.json"), path);
                var r = new BuildRecord { Result = "succeeded", StartedUtc = DateTime.UtcNow, FinishedUtc = DateTime.UtcNow };
                Assert.Null(BuildRecordStore.TryWrite(path, r));
                Assert.Null(BuildRecordStore.TryWrite(path, r)); // replace
                Assert.Equal("succeeded", BuildRecordStore.TryRead(path).Result);
                Assert.False(File.Exists(path + ".tmp"));
            }
            finally
            {
                try { Directory.Delete(dir, true); } catch { }
            }
        }

        [Fact]
        public void ErrorSetGroupsByFileAndOrdersNavigation()
        {
            var r = new BuildRecord();
            r.Items.Add(new BuildDiagnostic { File = @"C:\P\A.cpp", Line = 3, Severity = BuildSeverity.Warning, Message = "w" });
            r.Items.Add(new BuildDiagnostic { File = "C:/P/A.cpp", Line = 5, Severity = BuildSeverity.Error, Message = "e" });
            r.Items.Add(new BuildDiagnostic { File = @"C:\P\A.cpp", Line = 6, Severity = BuildSeverity.Info, Message = "n" });
            r.Items.Add(new BuildDiagnostic { Severity = BuildSeverity.Error, Message = "link" });
            var set = new BuildErrorSet(r);
            Assert.Equal(2, set.ForFile(@"c:\p\a.cpp").Count);
            Assert.Equal(3, set.ForFile(@"C:\P\A.cpp", includeNotes: true).Count);
            Assert.Empty(set.ForFile(@"C:\P\B.cpp"));
            var order = set.NavigationOrder();
            Assert.Equal(new[] { "e", "w" }, order.Select(d => d.Message).ToArray());
            Assert.Equal(0, BuildErrorSet.NextIndex(2, -1));
            Assert.Equal(1, BuildErrorSet.NextIndex(2, 0));
            Assert.Equal(0, BuildErrorSet.NextIndex(2, 1));
            Assert.Equal(-1, BuildErrorSet.NextIndex(0, 0));
        }

        [Theory]
        [InlineData("\tint32 Foo = Bar;", 14, 13, 3)]   // identifier at the column
        [InlineData("\tint32 Foo = Bar;", 12, 11, 1)]   // punctuation: one char
        [InlineData("\tint32 Foo = Bar;  ", 0, 1, 16)]  // no column: trimmed line
        [InlineData("\tint32 Foo;", 99, 1, 10)]         // column past the end: trimmed line
        [InlineData("   ", 2, 3, 0)]                    // blank: nothing
        public void SquiggleSpan(string line, int column, int start, int length)
        {
            var (s, l) = BuildDiagnosticSpan.Compute(line, column);
            Assert.Equal(start, s);
            Assert.Equal(length, l);
        }

        [Fact]
        public void ErrorListModelMergesRulesAndBuild()
        {
            var m = new ErrorListModel();
            Assert.True(m.SetRules(@"C:\P\A.cpp", new[]
            {
                new ErrorListEntry { File = @"C:\P\A.cpp", Line = 4, Severity = BuildSeverity.Warning, Code = "UE0007", Message = "tag" },
            }));
            Assert.False(m.SetRules(@"C:\P\A.cpp", new[]
            {
                new ErrorListEntry { File = @"C:\P\A.cpp", Line = 4, Severity = BuildSeverity.Warning, Code = "UE0007", Message = "tag" },
            })); // unchanged
            var r = new BuildRecord();
            r.Items.Add(new BuildDiagnostic { File = @"C:\P\B.cpp", Line = 10, Column = 2, Severity = BuildSeverity.Error, Code = "C2065", Message = "x", Tool = "msvc" });
            r.Items.Add(new BuildDiagnostic { File = @"C:\P\B.cpp", Line = 11, Severity = BuildSeverity.Info, Message = "note" });
            Assert.True(m.SetBuild(new BuildErrorSet(r)));
            var rows = m.Rows(true, true);
            Assert.Equal(2, rows.Count);
            Assert.Equal(BuildSeverity.Error, rows[0].Severity);
            Assert.Equal(9, rows[0].Line);
            Assert.Equal(1, rows[0].Column);
            Assert.Equal("UEToVS · msvc", rows[0].Tool);
            Assert.Equal(ErrorListOrigin.Build, rows[0].Origin);
            Assert.Single(m.Rows(true, false));
            Assert.Single(m.Rows(false, true));
            Assert.True(m.SetRules(@"C:\P\A.cpp", null));
            Assert.False(m.SetRules(@"C:\P\A.cpp", null));
            Assert.Equal(0, m.RuleFileCount);
            Assert.Equal("A.cpp", ErrorListModel.DisplayFile(@"C:\P\A.cpp".Replace('\\', Path.DirectorySeparatorChar)));
        }
    }
}
