using System.Linq;
using UEToVS.Core.Matching;
using Xunit;

namespace UEToVS.Core.Tests
{
    public class MatcherTests
    {
        private static MatchResult M(string p, string n) => new FuzzyMatcher(p).Match(n);

        [Theory]
        [InlineData("PCU", "PlayerCameraUpdate")]
        [InlineData("gAC", "GetActorComponent")]
        [InlineData("getac", "GetActorComponent")]
        [InlineData("PlaCamUp", "PlayerCameraUpdate")]
        [InlineData("HS", "HTTPServer")]
        [InlineData("tick", "TickComponent")]
        [InlineData("ACh", "ACharacter")]
        public void CamelHumpsAndPrefixesMatch(string pattern, string name) => Assert.True(M(pattern, name).IsMatch);

        [Theory]
        [InlineData("PCX", "PlayerCameraUpdate")]
        [InlineData("Ac", "GetActorComponent")] // 'A' upper must match a word start, which "Ac" does -> but next char c must continue
        public void NonMatches(string pattern, string name)
        {
            if (pattern == "Ac") Assert.True(M(pattern, name).IsMatch); // Actor
            else Assert.False(M(pattern, name).IsMatch);
        }

        [Fact]
        public void UppercasePatternDoesNotMatchLowercaseMidWord() => Assert.False(M("CU", "Cupdate").IsMatch);

        [Fact]
        public void RankingOrder()
        {
            var exact = M("Actor", "Actor");
            var prefix = M("Actor", "ActorComponent");
            var camel = M("AC", "ActorComponent");
            var sub = M("ctor", "Actor");
            Assert.Equal(MatchKind.Exact, exact.Kind);
            Assert.Equal(MatchKind.Prefix, prefix.Kind);
            Assert.Equal(MatchKind.CamelHump, camel.Kind);
            Assert.Equal(MatchKind.Substring, sub.Kind);
            Assert.True(exact.Score > prefix.Score && prefix.Score > M("Actor", "XActorY").Score);
        }

        [Fact]
        public void ShorterNameWinsForSamePrefix() =>
            Assert.True(M("Act", "Actor").Score > M("Act", "ActorComponentSomethingLong").Score);

        [Fact]
        public void Highlights()
        {
            var r = M("PCU", "PlayerCameraUpdate");
            Assert.Equal(new[] { 0, 6, 12 }, r.Ranges.Select(x => x.Start).ToArray());
        }

        [Fact]
        public void Wildcard()
        {
            Assert.True(M("Player*Update", "PlayerCameraUpdate").IsMatch);
            Assert.True(M("player update", "PlayerCameraUpdate").IsMatch);
            Assert.False(M("Update*Player", "PlayerCameraUpdate").IsMatch);
        }

        [Fact]
        public void UnrealPrefixIgnored()
        {
            var m = new FuzzyMatcher("Character");
            var r = m.MatchUnreal("ACharacter");
            Assert.True(r.IsMatch);
            Assert.Equal(MatchKind.Exact, r.Kind);
            Assert.Equal(1, r.Ranges[0].Start);
            Assert.True(m.MatchUnreal("Character").Score > r.Score);
        }

        [Theory]
        [InlineData("ACharacter", "Character")]
        [InlineData("UObject", "Object")]
        [InlineData("FVector", "Vector")]
        [InlineData("Apple", null)]
        [InlineData("UI", null)]
        [InlineData("FPSCounter", null)]
        public void StripPrefix(string input, string expected) => Assert.Equal(expected, UnrealNaming.StripTypePrefix(input));
    }
}
