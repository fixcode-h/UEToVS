using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using UEToVS.Contracts;
using UEToVS.Engine.Indexing;
using UEToVS.Engine.UeCode;
using UEToVS.Engine.Unreal;
using Xunit;

namespace UEToVS.Engine.Tests
{
    public class UeCodeScannerTests
    {
        [Fact]
        public void FindsAutomationTestsOfEveryMacroFamily()
        {
            const string src = @"
#include ""Misc/AutomationTest.h""
IMPLEMENT_SIMPLE_AUTOMATION_TEST(FSimpleTest, ""Project.Feature.Simple"", EAutomationTestFlags::EditorContext | EAutomationTestFlags::EngineFilter)
IMPLEMENT_COMPLEX_AUTOMATION_TEST(FComplexTest,
    ""Project.Feature.Complex"",
    EAutomationTestFlags::EditorContext)
IMPLEMENT_CUSTOM_SIMPLE_AUTOMATION_TEST(FCustomTest, FMyBase, ""Project.Custom"", EAutomationTestFlags::EditorContext)
BEGIN_DEFINE_SPEC(FMySpec, TEXT(""Project.Spec.Mine""), EAutomationTestFlags::ProductFilter)
END_DEFINE_SPEC(FMySpec)
DEFINE_SPEC(FOtherSpec, ""Project.Spec.Other"", EAutomationTestFlags::ProductFilter)
// IMPLEMENT_SIMPLE_AUTOMATION_TEST(FCommented, ""Project.Commented"", 0)
/* DEFINE_SPEC(FBlock, ""Project.Block"", 0) */
const TCHAR* S = TEXT(""IMPLEMENT_SIMPLE_AUTOMATION_TEST(FInString, \""Project.InString\"", 0)"");
#define IMPLEMENT_SIMPLE_AUTOMATION_TEST(TClass, PrettyName, TFlags) IMPLEMENT_SIMPLE_AUTOMATION_TEST_PRIVATE(TClass, FAutomationTestBase, PrettyName, TFlags)
";
            var r = UeMacroScanner.Scan(src);
            Assert.Equal(new[] { "Project.Feature.Simple", "Project.Feature.Complex", "Project.Custom", "Project.Spec.Mine", "Project.Spec.Other" },
                r.Tests.Select(t => t.Name));
            var complex = r.Tests[1];
            Assert.Equal("FComplexTest", complex.ClassName);
            Assert.Equal(AutomationTestKind.Complex, complex.Kind);
            Assert.Equal(3, complex.Line);
            Assert.Equal(0, complex.Column);
            Assert.Equal(AutomationTestKind.Spec, r.Tests[3].Kind);
            Assert.Equal("FCustomTest", r.Tests[2].ClassName);
        }

        [Fact]
        public void FindsLogUsagesWithDecodedFormats()
        {
            const string src = "void F()\n{\n" +
                "    UE_LOG(LogMyGame, Warning, TEXT(\"Loaded %s in %.2f ms\"), *Name, Ms);\n" +
                "    UE_CLOG(bFailed, LogMyGame, Error, TEXT(\"Line one\\nTab\\there \\\"quoted\\\" 100%%\"));\n" +
                "    UE_LOG(LogMyGame, Log, TEXT(\"part one, \") TEXT(\"part two\"));\n" +
                "    UE_LOG(LogMyGame, Verbose, FormatVariable, 1);\n" +
                "    UE_LOGFMT(LogMyGame, Display, \"Hello {Name}\", Name);\n" +
                "    UE_LOG(LogTemp, Log, TEXT(\"Lambda %d\"), [&]() { int X = 1; return X; }());\n" +
                "}\n";
            var r = UeMacroScanner.Scan(src);
            var sites = r.LogSites;
            Assert.Equal(6, sites.Count);
            Assert.All(sites, s => Assert.Equal(LogSiteKind.Usage, s.Kind));
            Assert.Equal("Loaded %s in %.2f ms", sites[0].Format);
            Assert.Equal("Warning", sites[0].Verbosity);
            Assert.Equal(2, sites[0].Line);
            Assert.Equal(4, sites[0].Column);
            Assert.Equal("UE_CLOG", sites[1].Macro);
            Assert.Equal("LogMyGame", sites[1].Category);
            Assert.Equal("Error", sites[1].Verbosity);
            Assert.Equal("Line one\nTab\there \"quoted\" 100%%", sites[1].Format);
            Assert.Equal("part one, part two", sites[2].Format);
            Assert.Null(sites[3].Format);
            Assert.Equal("Hello {Name}", sites[4].Format);
            Assert.Equal("UE_LOGFMT", sites[4].Macro);
            Assert.Equal("LogTemp", sites[5].Category);
            Assert.Equal("Lambda %d", sites[5].Format);
        }

        [Fact]
        public void FindsCategoryDeclarationsAndDefinitionsAtTheCategoryName()
        {
            const string src = "#pragma once\n" +
                "DECLARE_LOG_CATEGORY_EXTERN(LogMyGame, Log, All);\n" +
                "DEFINE_LOG_CATEGORY(LogMyGame);\n" +
                "DEFINE_LOG_CATEGORY_STATIC(LogLocal, Warning, All);\n" +
                "class FFoo { DECLARE_LOG_CATEGORY_CLASS(LogFoo, Log, All); };\n" +
                "DEFINE_LOG_CATEGORY_CLASS(FFoo, LogFoo);\n" +
                "#define DECLARE_LOG_CATEGORY_EXTERN(CategoryName, DefaultVerbosity, CompileTimeVerbosity) extern int CategoryName;\n";
            var sites = UeMacroScanner.Scan(src).LogSites;
            Assert.Equal(5, sites.Count);
            Assert.Equal((LogSiteKind.Declaration, "LogMyGame", 1, 28), (sites[0].Kind, sites[0].Category, sites[0].Line, sites[0].Column));
            Assert.Equal((LogSiteKind.Definition, "LogMyGame", 2, 20), (sites[1].Kind, sites[1].Category, sites[1].Line, sites[1].Column));
            Assert.Equal(LogSiteKind.StaticDefinition, sites[2].Kind);
            Assert.Equal(("LogFoo", LogSiteKind.Declaration), (sites[3].Category, sites[3].Kind));
            Assert.Equal(("LogFoo", LogSiteKind.Definition), (sites[4].Category, sites[4].Kind));
        }

        [Fact]
        public void DecodesEscapesAndRejectsNonLiterals()
        {
            string Decode(string s) => UeMacroScanner.DecodeLiteral(s, 0, s.Length);
            Assert.Equal("a\\b", Decode("TEXT(\"a\\\\b\")"));
            Assert.Equal("ab", Decode("L\"a\" \"b\""));
            Assert.Equal("A", Decode("TEXT(\"\\x41\")"));
            Assert.Equal("é", Decode("TEXT(\"\\u00e9\")"));
            Assert.Null(Decode("*Name"));
            Assert.Null(Decode("TEXT(\"a\") + Suffix"));
            Assert.Null(Decode(""));
        }

        [Fact]
        public void IgnoresFilesWithoutMacrosAndUnbalancedCalls()
        {
            Assert.True(UeMacroScanner.Scan("int main() { return 0; }").IsEmpty);
            Assert.True(UeMacroScanner.Scan("UE_LOG(LogX, Log, TEXT(\"never closed\")").IsEmpty);
            Assert.False(UeMacroScanner.MightContain("plain text"));
        }

        [Fact]
        public async Task ServiceScansProjectFilesOnceAndRescansChangedFiles()
        {
            string dir = Path.Combine(Path.GetTempPath(), "uetovs-uecode-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(dir);
            try
            {
                string a = Path.Combine(dir, "A.cpp"), b = Path.Combine(dir, "B.cpp"), eng = Path.Combine(dir, "Engine.cpp");
                File.WriteAllText(a, "IMPLEMENT_SIMPLE_AUTOMATION_TEST(FA, \"Project.A\", 0)\nvoid F() { UE_LOG(LogA, Log, TEXT(\"hi %d\"), 1); }\n");
                File.WriteAllText(b, "// nothing here\n");
                File.WriteAllText(eng, "IMPLEMENT_SIMPLE_AUTOMATION_TEST(FE, \"Engine.E\", 0)\n");
                FileEntry Entry(string p, ItemOrigin o)
                {
                    var fi = new FileInfo(p);
                    return new FileEntry(new ListedFile { Path = p, Name = fi.Name, Origin = o, Kind = SourceFileKind.Source, Size = fi.Length, LastWriteUtcTicks = fi.LastWriteTimeUtc.Ticks });
                }
                var files = new[] { Entry(a, ItemOrigin.Project), Entry(b, ItemOrigin.ProjectPlugin), Entry(eng, ItemOrigin.Engine) };
                var svc = new UeCodeService(() => files, _ => Task.FromResult(true));

                var tests = await svc.FindAutomationTestsAsync(new AutomationTestsRequest(), CancellationToken.None);
                Assert.Equal(new[] { "Project.A" }, tests.Tests.Select(t => t.Name));
                Assert.Equal(2, tests.FilesRead);
                Assert.Equal(a, tests.Tests[0].Location.FilePath);

                var warm = await svc.FindLogSitesAsync(new LogSitesRequest { Category = "LogA" }, CancellationToken.None);
                Assert.Equal(0, warm.FilesRead);
                Assert.Equal("hi %d", Assert.Single(warm.Sites).Format);

                var withEngine = await svc.FindAutomationTestsAsync(new AutomationTestsRequest { IncludeEngine = true }, CancellationToken.None);
                Assert.Equal(new[] { "Engine.E", "Project.A" }, withEngine.Tests.Select(t => t.Name).OrderBy(n => n));

                // Edited on disk without a listing update (missed watcher event): the file with hits is re-read.
                File.WriteAllText(a, "\n\nIMPLEMENT_SIMPLE_AUTOMATION_TEST(FA, \"Project.A2\", 0)\n");
                File.SetLastWriteTimeUtc(a, DateTime.UtcNow.AddMinutes(1));
                var after = await svc.FindAutomationTestsAsync(new AutomationTestsRequest(), CancellationToken.None);
                Assert.Equal("Project.A2", Assert.Single(after.Tests).Name);
                Assert.Equal(2, after.Tests[0].Location.Line);
            }
            finally
            {
                Directory.Delete(dir, true);
            }
        }
    }
}
