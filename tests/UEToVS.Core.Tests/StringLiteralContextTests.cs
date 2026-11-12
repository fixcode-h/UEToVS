using UEToVS.Core.Unreal;
using Xunit;

namespace UEToVS.Core.Tests
{
    public sealed class StringLiteralContextTests
    {
        /// <summary>'|' marks the caret.</summary>
        private static StringLiteralSite At(string lineWithCaret)
        {
            int caret = lineWithCaret.IndexOf('|');
            return StringLiteralContext.Analyze(lineWithCaret.Remove(caret, 1), caret);
        }

        [Theory]
        [InlineData("FGameplayTag T = FGameplayTag::RequestGameplayTag(FName(\"Ability.Fi|\"));", "Fi", "Ability.Fi")]
        [InlineData("auto T = RequestGameplayTag(TEXT(\"Abi|\"));", "Abi", "Abi")]
        [InlineData("UE_DEFINE_GAMEPLAY_TAG(TAG_Fire, \"Ability.|\");", "", "Ability.")]
        [InlineData("UE_DEFINE_GAMEPLAY_TAG_COMMENT(TAG_Fire, \"Ab|\", \"comment\");", "Ab", "Ab")]
        [InlineData("UPROPERTY(EditAnywhere, meta = (Categories = \"Ability.|\"))", "", "Ability.")]
        [InlineData("\tRequestGameplayTag(\"Status.Stun|", "Stun", "Status.Stun")]
        public void GameplayTag(string line, string fragment, string typed)
        {
            var s = At(line);
            Assert.True(s.InString);
            Assert.Equal(StringLiteralKind.GameplayTag, s.Kind);
            Assert.Equal(typed, s.Typed);
            string clean = line.Replace("|", string.Empty);
            Assert.Equal(fragment, clean.Substring(s.FragmentStart, s.FragmentLength).Substring(0, fragment.Length));
        }

        [Fact]
        public void GameplayTag_CommentArgumentIsNotATag()
        {
            var s = At("UE_DEFINE_GAMEPLAY_TAG_COMMENT(TAG_Fire, \"Ability.Fire\", \"Fires|\");");
            Assert.True(s.InString);
            Assert.Equal(StringLiteralKind.None, s.Kind);
        }

        [Fact]
        public void AssetPath_FinderWithTemplateArgument()
        {
            var s = At("static ConstructorHelpers::FObjectFinder<UStaticMesh> Mesh(TEXT(\"/Game/Props/SM_|\"));");
            Assert.Equal(StringLiteralKind.AssetPath, s.Kind);
            Assert.Equal("FObjectFinder", s.Callee);
            Assert.Equal("UStaticMesh", s.TemplateArgument);
            Assert.False(s.WantsClass);
            Assert.Equal("/Game/Props/SM_", s.Typed);
        }

        [Theory]
        [InlineData("UStaticMesh* M = LoadObject<UStaticMesh>(nullptr, TEXT(\"/Game/|\"));", "LoadObject", "UStaticMesh", false)]
        [InlineData("FSoftObjectPath P(TEXT(\"/Ga|\"));", "FSoftObjectPath", null, false)]
        [InlineData("FSoftObjectPath P = FSoftObjectPath(\"|\");", "FSoftObjectPath", null, false)]
        [InlineData("static ConstructorHelpers::FClassFinder<APawn> PawnClass(TEXT(\"/Game/BP_|\"));", "FClassFinder", "APawn", true)]
        [InlineData("auto C = StaticLoadClass(UObject::StaticClass(), nullptr, TEXT(\"/Game/|\"));", "StaticLoadClass", null, true)]
        public void AssetPath_Calls(string line, string callee, string template, bool wantsClass)
        {
            var s = At(line);
            Assert.Equal(StringLiteralKind.AssetPath, s.Kind);
            Assert.Equal(callee, s.Callee);
            Assert.Equal(template, s.TemplateArgument);
            Assert.Equal(wantsClass, s.WantsClass);
        }

        [Fact]
        public void AssetPath_SegmentFragment()
        {
            string line = "LoadObject<UTexture2D>(nullptr, TEXT(\"/Game/UI/Ic|ons\"));";
            var s = At(line);
            string clean = line.Replace("|", string.Empty);
            Assert.Equal("Icons", clean.Substring(s.FragmentStart, s.FragmentLength));
        }

        [Fact]
        public void AssetPath_MountPrefixAnywhere()
        {
            var s = At("const TCHAR* Path = TEXT(\"/Game/Maps/|\");");
            Assert.Equal(StringLiteralKind.AssetPath, s.Kind);
            Assert.Null(s.Callee);
            Assert.Equal(StringLiteralKind.None, At("const TCHAR* Path = TEXT(\"/Script/Engine.|\");").Kind);
        }

        [Fact]
        public void UFunction_BindUFunctionTarget()
        {
            string line = "Delegate.BindUFunction(Owner, FName(\"On|\"));";
            var s = At(line);
            Assert.Equal(StringLiteralKind.UFunctionName, s.Kind);
            Assert.Equal(1, s.ArgumentIndex);
            Assert.Equal("Owner", line.Replace("|", "").Substring(s.TargetStart, s.TargetEnd - s.TargetStart));
            Assert.Equal("Delegate", s.Receiver);
        }

        [Fact]
        public void UFunction_FindFunctionReceiver()
        {
            var s = At("UFunction* F = Target->FindFunction(TEXT(\"Do|\"));");
            Assert.Equal(StringLiteralKind.UFunctionName, s.Kind);
            Assert.Equal("Target", s.Receiver);
            Assert.True(s.ReceiverOperatorColumn > 0);
            Assert.Equal(-1, s.TargetStart);
        }

        [Fact]
        public void Ini_SectionAndKey()
        {
            var sec = At("GConfig->GetString(TEXT(\"/Script/Eng|\"), TEXT(\"Key\"), Value, GGameIni);");
            Assert.Equal(StringLiteralKind.IniSection, sec.Kind);
            Assert.Equal("GGameIni", sec.IniFile);
            Assert.Equal("/Script/Eng", sec.Typed);
            // the whole literal is replaced
            Assert.Equal("/Script/Eng".Length, sec.FragmentLength);

            var key = At("GConfig->GetBool(TEXT(\"/Script/MyGame.Settings\"), TEXT(\"bUse|\"), bValue, GGameIni);");
            Assert.Equal(StringLiteralKind.IniKey, key.Kind);
            Assert.Equal("/Script/MyGame.Settings", key.IniSection);
            Assert.Equal("Game", StringLiteralContext.IniBaseName(key.IniFile));
        }

        [Theory]
        [InlineData("UE_LOG(LogTemp, Log, TEXT(\"Hello |\"));")]
        [InlineData("FString S = TEXT(\"Ability.|\");")]
        [InlineData("Print(\"abc|\");")]
        [InlineData("Obj->SetName(FName(\"Ab|\"));")]
        [InlineData("GLog->Logf(TEXT(\"%s|\"), *Name);")]
        public void PlainStrings_NoContext(string line)
        {
            var s = At(line);
            Assert.True(s.InString);
            Assert.Equal(StringLiteralKind.None, s.Kind);
        }

        [Theory]
        [InlineData("RequestGameplayTag(TEXT(\"A\")); // \"x|")]
        [InlineData("RequestGameplayTag(TEXT(\"A\")|);")]
        [InlineData("char C = '|';")]
        public void OutsideStrings(string line)
        {
            var s = At(line);
            Assert.False(s.InString);
            Assert.Equal(StringLiteralKind.None, s.Kind);
        }

        [Fact]
        public void EscapesAndEarlierLiterals()
        {
            var s = At("Use(TEXT(\"a\\\"b\"), RequestGameplayTag(TEXT(\"X.|\")));");
            Assert.Equal(StringLiteralKind.GameplayTag, s.Kind);
            Assert.Equal("X.", s.Typed);
        }
    }
}
