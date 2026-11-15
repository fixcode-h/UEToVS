using UEToVS.Contracts;
using UEToVS.Host.Logic.Navigation;
using Xunit;

namespace UEToVS.Host.Logic.Tests
{
    public class LogCategoryTextTests
    {
        private static LogSiteInfo Site(string format, string verbosity = "Log", string cat = "LogMy", string macro = "UE_LOG", int line = 1, string file = "A.cpp") =>
            new LogSiteInfo { Kind = LogSiteKind.Usage, Category = cat, Verbosity = verbosity, Format = format, Macro = macro, Location = new SourceLocation { FilePath = file, Line = line } };

        [Fact]
        public void LogCategoryCandidatesAndTitles()
        {
            Assert.True(LogCategoryText.IsCandidate("LogMyGame", null));
            Assert.False(LogCategoryText.IsCandidate("Logger", null));
            Assert.True(LogCategoryText.IsCandidate("MyCategory", "    UE_LOG(MyCategory, Log, TEXT(\"x\"));"));
            Assert.False(LogCategoryText.IsCandidate("Warning", "    UE_LOG(MyCategory, Warning, TEXT(\"x\"));"));
            Assert.False(LogCategoryText.IsCandidate("Foo", "int Foo = 1;"));
            Assert.Equal("UE_LOG(LogMy, Warning, \"a\\nb\")", LogCategoryText.Title(Site("a\nb", "Warning")));
            Assert.Equal("DEFINE_LOG_CATEGORY(LogMy)", LogCategoryText.Title(new LogSiteInfo { Kind = LogSiteKind.Definition, Macro = "DEFINE_LOG_CATEGORY", Category = "LogMy" }));
            Assert.True(LogCategoryText.KindRank(LogSiteKind.Declaration) < LogCategoryText.KindRank(LogSiteKind.Usage));
        }
    }
}
