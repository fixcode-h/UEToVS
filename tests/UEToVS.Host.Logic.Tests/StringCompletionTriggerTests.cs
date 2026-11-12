using UEToVS.Contracts;
using UEToVS.Host.Logic.IntelliSense;
using Xunit;

namespace UEToVS.Host.Logic.Tests
{
    /// <summary>When string context completion (tags, asset paths, UFUNCTION names, ini) opens, and its applicable span.</summary>
    public class StringCompletionTriggerTests
    {
        private static TriggerResult Typed(string lineWithCaret)
        {
            int caret = lineWithCaret.IndexOf('|');
            string line = lineWithCaret.Remove(caret, 1);
            char typed = caret > 0 ? line[caret - 1] : '\0';
            return CompletionTriggers.Evaluate(line, caret, typed, false, true);
        }

        private static TriggerResult Explicit(string lineWithCaret)
        {
            int caret = lineWithCaret.IndexOf('|');
            return CompletionTriggers.Evaluate(lineWithCaret.Remove(caret, 1), caret, '\0', true);
        }

        [Theory]
        [InlineData("RequestGameplayTag(TEXT(\"|", CompletionContextKind.GameplayTag)]
        [InlineData("RequestGameplayTag(TEXT(\"|\"))", CompletionContextKind.GameplayTag)]
        [InlineData("RequestGameplayTag(FName(\"Ability.|\"))", CompletionContextKind.GameplayTag)]
        [InlineData("RequestGameplayTag(FName(\"Ability.F|\"))", CompletionContextKind.GameplayTag)]
        [InlineData("UPROPERTY(meta = (Categories = \"A.B,|\"))", CompletionContextKind.GameplayTag)]
        [InlineData("FObjectFinder<UStaticMesh> M(TEXT(\"/Game/|\"));", CompletionContextKind.AssetPath)]
        [InlineData("const TCHAR* P = TEXT(\"/Game/Maps/|\");", CompletionContextKind.AssetPath)]
        [InlineData("Del.BindUFunction(this, FName(\"O|\"));", CompletionContextKind.UFunctionName)]
        [InlineData("GConfig->GetString(TEXT(\"/|\"), TEXT(\"K\"), S, GGameIni);", CompletionContextKind.IniKey)]
        [InlineData("GConfig->GetString(TEXT(\"S|\"), TEXT(\"K\"), S, GGameIni);", CompletionContextKind.IniKey)]
        public void Opens(string line, CompletionContextKind hint)
        {
            var r = Typed(line);
            Assert.True(r.Start, line);
            Assert.Equal(hint, r.Hint);
            Assert.True(CompletionPolicy.Participates(r.Hint, false));
        }

        [Theory]
        [InlineData("UE_LOG(LogTemp, Log, TEXT(\"Ability.|\"));")]
        [InlineData("FString S = TEXT(\"a|\");")]
        [InlineData("RequestGameplayTag(FName(\"Ability.Fi|\"))")] // second character of a segment: the open session filters
        public void StaysClosed(string line) => Assert.False(Typed(line).Start, line);

        // "/usr/bin/" in a plain string does open a session (the host cannot know the mount points): the engine answers it
        // with an empty list, so nothing is shown (Engine StringCompletionTests.AssetPath_AnyStringStartingWithAMount).

        [Fact]
        public void SpanIsTheSegment()
        {
            var r = Explicit("RequestGameplayTag(TEXT(\"Ability.Fi|re\"))");
            Assert.Equal(CompletionContextKind.GameplayTag, r.Hint);
            Assert.Equal("RequestGameplayTag(TEXT(\"Ability.".Length, r.WordStart);
            Assert.Equal(4, r.WordLength);

            var ini = Explicit("GConfig->GetInt(TEXT(\"SystemSettings\"), TEXT(\"r.Sha|dow\"), V, GEngineIni);");
            Assert.Equal(CompletionContextKind.IniKey, ini.Hint);
            Assert.Equal("GConfig->GetInt(TEXT(\"SystemSettings\"), TEXT(\"".Length, ini.WordStart);
            Assert.Equal("r.Shadow".Length, ini.WordLength);

            Assert.False(Explicit("TEXT(\"plain|\")").Start);
        }

        [Fact]
        public void CommitCharacters()
        {
            Assert.True(CompletionPolicy.CommitsInString(CompletionContextKind.GameplayTag, '.'));
            Assert.False(CompletionPolicy.CommitsInString(CompletionContextKind.GameplayTag, '/'));
            Assert.True(CompletionPolicy.CommitsInString(CompletionContextKind.AssetPath, '/'));
            Assert.False(CompletionPolicy.CommitsInString(CompletionContextKind.AssetPath, '.'));
            Assert.True(CompletionPolicy.CommitsInString(CompletionContextKind.IniKey, '"'));
            Assert.False(CompletionPolicy.CommitsInString(CompletionContextKind.UFunctionName, '('));
            Assert.Null(CompletionPolicy.CommitsInString(CompletionContextKind.Member, '.'));
            Assert.True(CompletionPolicy.Keep(new CompletionItem { Label = "Fire" }, CompletionContextKind.GameplayTag, false));
        }
    }
}
