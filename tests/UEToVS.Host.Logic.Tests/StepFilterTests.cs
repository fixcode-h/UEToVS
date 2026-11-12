using System;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using UEToVS.Host.Logic.Editing;
using Xunit;

namespace UEToVS.Host.Logic.Tests
{
    /// <summary>v0.4.5 debug step filter: the bundled file is valid, every Name compiles, deployment decisions.</summary>
    public sealed class StepFilterTests
    {
        private static readonly XNamespace Ns = "http://schemas.microsoft.com/vstudio/debugger/natstepfilter/2010";

        private static string Bundled => File.ReadAllText(Path.Combine(AppContext.BaseDirectory, StepFilterPlan.FileName));

        [Fact]
        public void StepFilter_IsValidXmlWithCompilingRegexes()
        {
            var doc = XDocument.Parse(Bundled);
            Assert.Equal(Ns + "StepFilter", doc.Root.Name);
            var functions = doc.Root.Elements(Ns + "Function").ToList();
            Assert.True(functions.Count >= 10);
            foreach (var f in functions)
            {
                string name = (string)f.Element(Ns + "Name");
                Assert.False(string.IsNullOrWhiteSpace(name));
                var re = new Regex("^(" + name + ")$"); // throws when it does not compile
                Assert.NotNull(re);
                Assert.Contains((string)f.Element(Ns + "Action"), new[] { "NoStepInto", "StepInto" });
            }
            Assert.True(StepFilterPlan.IsOurs(Bundled));
        }

        [Theory]
        [InlineData("TArray<int,TSizedDefaultAllocator<32> >::operator[]", true)]
        [InlineData("TMap<FName,int,FDefaultSetAllocator,TDefaultMapHashableKeyFuncs<FName,int,0> >::operator[]", true)]
        [InlineData("FString::FString", true)]
        [InlineData("FName::FName", true)]
        [InlineData("Cast<AActor,UObject>", true)]
        [InlineData("IsValid", true)]
        [InlineData("TObjectPtr<AActor>::Get", true)]
        [InlineData("UE::Assert::Private::ExecCheckImplInternal", true)]
        [InlineData("AMyActor::BeginPlay", false)]
        [InlineData("TArray<int,TSizedDefaultAllocator<32> >::Add", false)]
        public void StepFilter_Matches(string function, bool skipped)
        {
            var names = XDocument.Parse(Bundled).Root.Elements(Ns + "Function").Select(f => (string)f.Element(Ns + "Name"));
            Assert.Equal(skipped, names.Any(n => Regex.IsMatch(function, "^(" + n + ")$")));
        }

        [Fact]
        public void StepFilter_IsInTheVsix()
        {
            string csproj = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "src", "UEToVS.Host", "UEToVS.Host.csproj"));
            Assert.Contains("Resources\\UEToVS.natstepfilter\" IncludeInVSIX=\"true\"", csproj);
        }

        [Fact]
        public void Targets_PreferExistingFolders()
        {
            string docs = Path.Combine("D", "Docs");
            Assert.Equal(new[] { Path.Combine(docs, "Visual Studio 2022", "Visualizers", StepFilterPlan.FileName) }, StepFilterPlan.Targets(docs, 17, _ => false));
            var both = StepFilterPlan.Targets(docs, 18, _ => true);
            Assert.Equal(2, both.Count);
            var second = StepFilterPlan.Targets(docs, 18, d => d.EndsWith("2026", StringComparison.Ordinal));
            Assert.Equal(Path.Combine(docs, "Visual Studio 2026", "Visualizers", StepFilterPlan.FileName), Assert.Single(second));
            Assert.Empty(StepFilterPlan.Targets(docs, 0, _ => true));
            Assert.Empty(StepFilterPlan.Targets(null, 17, _ => true));
        }

        [Fact]
        public void Decide()
        {
            string ours = "<!-- " + StepFilterPlan.Marker + " v1 -->";
            string ours2 = "<!-- " + StepFilterPlan.Marker + " v2 -->";
            Assert.Equal(StepFilterAction.Write, StepFilterPlan.Decide(true, null, ours));
            Assert.Equal(StepFilterAction.None, StepFilterPlan.Decide(true, ours.Replace("\n", "\r\n"), ours));
            Assert.Equal(StepFilterAction.Write, StepFilterPlan.Decide(true, ours, ours2));
            Assert.Equal(StepFilterAction.None, StepFilterPlan.Decide(true, "<user's own/>", ours));
            Assert.Equal(StepFilterAction.Delete, StepFilterPlan.Decide(false, ours, ours));
            Assert.Equal(StepFilterAction.None, StepFilterPlan.Decide(false, "<user's own/>", ours));
            Assert.Equal(StepFilterAction.None, StepFilterPlan.Decide(false, null, ours));
        }
    }
}
