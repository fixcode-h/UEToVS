using System.IO;
using System.Linq;
using System.Text;
using UEToVS.Contracts;
using Xunit;
using static UEToVS.Engine.Syntax.Tests.TestUtil;

namespace UEToVS.Engine.Syntax.Tests
{
    public class UeMacroTests
    {
        [Fact]
        public void KnowledgeBaseLoadsAndClassifies()
        {
            var kb = UeMacroKnowledge.Default;
            Assert.NotEqual("empty", kb.Version);
            Assert.Equal(MacroRole.Reflection, kb.Lookup("UCLASS").Role);
            Assert.Equal(UeFlags.UProperty, kb.Lookup("UPROPERTY").Flag);
            Assert.Equal(MacroRole.GeneratedBody, kb.Lookup("GENERATED_UCLASS_BODY").Role);
            Assert.True(kb.Lookup("GENERATED_UCLASS_BODY").Legacy);
            Assert.Equal(1, kb.Lookup("DECLARE_DELEGATE_RetVal_TwoParams").NameArg);
            Assert.Equal(0, kb.Lookup("DECLARE_DELEGATE_TwoParams").NameArg);
            Assert.Equal(MacroRole.Attribute, kb.Lookup("FORCEINLINE").Role);
            Assert.True(kb.IsExportMacro("ENGINE_API"));
            Assert.True(kb.IsExportMacro("MYGAME2_API"));
            Assert.False(kb.IsExportMacro("Foo_API"));
            Assert.False(kb.IsExportMacro("_API"));
            Assert.Contains(kb.GetInjectedMembers(GeneratedFamily.UClass), m => m.Name == "StaticClass");
        }

        [Fact]
        public void KnowledgeBaseHasSpecifierTables()
        {
            var kb = UeMacroKnowledge.Default;
            foreach (var m in new[] { "UCLASS", "UPROPERTY", "UFUNCTION", "USTRUCT", "UENUM", "UINTERFACE", "UMETA", "UPARAM" })
                Assert.NotEmpty(kb.GetSpecifiers(m));
            Assert.True(kb.GetSpecifiers("UPROPERTY").Count >= 40);
            Assert.True(kb.GetSpecifiers("UFUNCTION").Count >= 20);
            Assert.True(kb.GetMetaKeys("UPROPERTY").Count >= 40);
            Assert.True(kb.GetMetaKeys("UFUNCTION").Count >= 25);
            var edit = kb.FindSpecifier("UPROPERTY", "editanywhere", false);
            Assert.Equal("EditAnywhere", edit.Name);
            Assert.Contains("VisibleAnywhere", edit.Conflicts);
            Assert.Equal("=\"Category\"", kb.FindSpecifier("UFUNCTION", "Category", false).Value);
            Assert.True(kb.FindSpecifier("UPROPERTY", "ClampMin", true).IsMeta);
            Assert.Null(kb.FindSpecifier("UPROPERTY", "ClampMin", false));
            Assert.True(kb.IsKnownSpecifier("BlueprintCallable"));
            Assert.False(kb.IsKnownSpecifier("NotASpecifier"));
            Assert.NotNull(kb.GetMacroDoc("GENERATED_BODY"));
        }

        [Fact]
        public void KnowledgeBaseCanBeReplaced()
        {
            const string json = "{ \"version\": \"custom\", \"reflection\": [ { \"name\": \"MYCLASS\", \"flag\": \"UClass\" } ] }";
            var kb = UeMacroKnowledge.Load(new MemoryStream(Encoding.UTF8.GetBytes(json)));
            Assert.Equal("custom", kb.Version);
            var s = Parse("MYCLASS(Abstract) class AThing {};", new ParseOptions { Knowledge = kb });
            var c = One(s, "AThing", SymbolKind.Class);
            Assert.Equal(UeFlags.UClass, c.Ue);
            Assert.Equal("Abstract", c.Specifiers);
            Assert.Throws<InvalidDataException>(() => UeMacroKnowledge.Load(new MemoryStream(Encoding.UTF8.GetBytes("{\"reflection\":[{\"name\":\"X\",\"flag\":\"Nope\"}]}"))));
        }

        [Fact]
        public void UClassWithGeneratedBodyInjectsMembers()
        {
            var s = Parse("UCLASS(Blueprintable, meta=(ShortTooltip=\"x\"))\nclass MYGAME_API AHero final : public ACharacter, public IInteractable\n{\n\tGENERATED_BODY()\npublic:\n\tvoid Jump();\n};");
            var c = One(s, "AHero", SymbolKind.Class);
            Assert.Equal(UeFlags.UClass | UeFlags.GeneratedBody, c.Ue);
            Assert.Equal("Blueprintable, meta=(ShortTooltip=\"x\")", c.Specifiers);
            Assert.Equal(new[] { "ACharacter", "IInteractable" }, c.Bases);
            Assert.True((c.Modifiers & DeclModifiers.Final) != 0);
            Assert.Equal("class AHero final : public ACharacter, public IInteractable", c.Signature);
            var injected = s.Declarations.Where(d => d.IsImplicit).ToArray();
            Assert.Contains(injected, d => d.Name == "StaticClass" && d.Kind == SymbolKind.Method && d.Container == "AHero" && d.Signature == "static UClass* StaticClass()");
            var super = Assert.Single(injected, d => d.Name == "Super");
            Assert.Equal("ACharacter", super.Type);
            Assert.Equal(3, super.Line);
            Assert.Contains(injected, d => d.Name == "ThisClass");
            Assert.Equal(AccessLevel.Public, One(s, "Jump").Access);
        }

        [Fact]
        public void GeneratedUClassBodyDeclaresObjectInitializerConstructorAndPublicAccess()
        {
            var s = Parse("UCLASS()\nclass UOld : public UObject\n{\n\tGENERATED_UCLASS_BODY()\n\tint32 Value;\n};");
            var ctor = Assert.Single(s.Declarations, d => d.IsImplicit && d.Kind == SymbolKind.Constructor);
            Assert.Equal("UOld(const FObjectInitializer& ObjectInitializer)", ctor.Signature);
            Assert.Equal(AccessLevel.Public, One(s, "Value").Access);
        }

        [Fact]
        public void UStructGeneratedBodyInjectsStaticStruct()
        {
            var s = Parse("USTRUCT(BlueprintType)\nstruct FStats\n{\n\tGENERATED_USTRUCT_BODY()\n\tUPROPERTY() float Hp = 1.f;\n};");
            Assert.Equal(UeFlags.UStruct | UeFlags.GeneratedBody, One(s, "FStats").Ue);
            Assert.Contains(s.Declarations, d => d.IsImplicit && d.Name == "StaticStruct");
            Assert.DoesNotContain(s.Declarations, d => d.IsImplicit && d.Name == "Super"); // no base
            Assert.Equal(UeFlags.UProperty, One(s, "Hp", SymbolKind.Field).Ue);
        }

        [Fact]
        public void InterfacePair()
        {
            var s = Parse("UINTERFACE(MinimalAPI)\nclass UInteractable : public UInterface { GENERATED_BODY() };\nclass IInteractable\n{\n\tGENERATED_BODY()\npublic:\n\tUFUNCTION(BlueprintNativeEvent)\n\tvoid Interact(AActor* By);\n};");
            Assert.Equal(UeFlags.UInterface | UeFlags.GeneratedBody, One(s, "UInteractable").Ue);
            Assert.Equal(UeFlags.GeneratedBody, One(s, "IInteractable").Ue);
            Assert.Contains(s.Declarations, d => d.IsImplicit && d.Container == "IInteractable" && d.Name == "UClassType" && d.Signature == "typedef UInteractable UClassType");
            Assert.Equal(UeFlags.UFunction, One(s, "Interact").Ue);
        }

        [Fact]
        public void InjectedMembersCanBeDisabled()
        {
            var s = Parse("UCLASS() class UA : public UObject { GENERATED_BODY() };", new ParseOptions { EmitInjectedMembers = false });
            Assert.DoesNotContain(s.Declarations, d => d.IsImplicit);
            Assert.True((One(s, "UA").Ue & UeFlags.GeneratedBody) != 0);
        }

        [Theory]
        [InlineData("DECLARE_DELEGATE(FSimple);", "FSimple", UeFlags.None, DeclModifiers.None)]
        [InlineData("DECLARE_DELEGATE_OneParam(FOnOne, int32);", "FOnOne", UeFlags.None, DeclModifiers.None)]
        [InlineData("DECLARE_DELEGATE_RetVal_OneParam(bool, FOnRet, int32);", "FOnRet", UeFlags.None, DeclModifiers.None)]
        [InlineData("DECLARE_MULTICAST_DELEGATE_TwoParams(FOnMulti, int32, float);", "FOnMulti", UeFlags.None, DeclModifiers.Multicast)]
        [InlineData("DECLARE_TS_MULTICAST_DELEGATE_OneParam(FOnTs, int32);", "FOnTs", UeFlags.None, DeclModifiers.Multicast)]
        [InlineData("DECLARE_DYNAMIC_DELEGATE_OneParam(FOnDyn, int32, Value);", "FOnDyn", UeFlags.UDelegate, DeclModifiers.Dynamic)]
        [InlineData("DECLARE_DYNAMIC_DELEGATE_RetVal_OneParam(int32, FOnDynRet, int32, Value);", "FOnDynRet", UeFlags.UDelegate, DeclModifiers.Dynamic)]
        [InlineData("DECLARE_DYNAMIC_MULTICAST_DELEGATE_TwoParams(FOnBar, int32, A, float, B);", "FOnBar", UeFlags.UDelegate, DeclModifiers.Dynamic | DeclModifiers.Multicast)]
        [InlineData("DECLARE_DYNAMIC_MULTICAST_SPARSE_DELEGATE_OneParam(FOnSparse, UMyComp, OnSparse, int32, V);", "FOnSparse", UeFlags.UDelegate, DeclModifiers.Dynamic | DeclModifiers.Multicast)]
        [InlineData("DECLARE_EVENT_OneParam(FMyOwner, FOnEvt, int32);", "FOnEvt", UeFlags.None, DeclModifiers.Event | DeclModifiers.Multicast)]
        [InlineData("DECLARE_DERIVED_EVENT(FOwner, FBase::FEvt, FDerivedEvt);", "FDerivedEvt", UeFlags.None, DeclModifiers.Event | DeclModifiers.Multicast)]
        public void DelegateFamilies(string code, string name, UeFlags ue, DeclModifiers traits)
        {
            var d = One(code, name, SymbolKind.Delegate);
            Assert.Equal(ue, d.Ue);
            Assert.Equal(traits, d.Modifiers & (DeclModifiers.Multicast | DeclModifiers.Dynamic | DeclModifiers.Event));
            Assert.StartsWith("DECLARE_", d.Signature);
        }

        [Fact]
        public void DelegateDetailsAndUDelegateSpecifiers()
        {
            var s = Parse("UDELEGATE(BlueprintAuthorityOnly)\nDECLARE_DYNAMIC_DELEGATE_RetVal_OneParam(bool, FCheck, AActor*, Who);\nclass A { DECLARE_DELEGATE(FInner); };");
            var check = One(s, "FCheck");
            Assert.Equal("bool", check.Type);
            Assert.Equal("AActor*, Who", check.Parameters);
            Assert.Equal("BlueprintAuthorityOnly", check.Specifiers);
            Assert.Equal("A", One(s, "FInner").Container);
        }

        [Theory]
        [InlineData("DECLARE_LOG_CATEGORY_EXTERN(LogGame, Log, All);", "LogGame", false)]
        [InlineData("DEFINE_LOG_CATEGORY(LogGame);", "LogGame", true)]
        [InlineData("DEFINE_LOG_CATEGORY_STATIC(LogLocal, Verbose, All);", "LogLocal", true)]
        [InlineData("DECLARE_CYCLE_STAT(TEXT(\"Tick\"), STAT_MyTick, STATGROUP_Game);", "STAT_MyTick", true)]
        [InlineData("DECLARE_STATS_GROUP(TEXT(\"Game\"), STATGROUP_Game, STATCAT_Advanced);", "STATGROUP_Game", true)]
        public void LogCategoriesAndStats(string code, string name, bool definition)
        {
            var d = One(code, name, SymbolKind.Variable);
            Assert.Equal(definition, d.IsDefinition);
        }

        [Fact]
        public void AttributeLikeMacrosAreIgnored()
        {
            var s = Parse(@"class A {
public:
	UE_DEPRECATED(5.1, ""Use Other"")
	FORCEINLINE UE_NODISCARD int32 GetX() const { return X; }
	FORCENOINLINE static void Slow();
	UE_DEPRECATED(5.0, ""x"") UPROPERTY() int32 Old;
	MS_ALIGN(16) struct FAligned { float V[4]; } GCC_ALIGN(16);
	int32 X;
};");
            var get = One(s, "GetX", SymbolKind.Method);
            Assert.Equal("int32 GetX() const", get.Signature);
            Assert.Equal("int32", get.Type);
            Assert.True((One(s, "Slow").Modifiers & DeclModifiers.Static) != 0);
            Assert.Equal(UeFlags.UProperty, One(s, "Old", SymbolKind.Field).Ue);
            One(s, "FAligned", SymbolKind.Struct);
            Assert.Equal("A", One(s, "X").Container);
            Assert.DoesNotContain(s.Declarations, d => d.Name.StartsWith("UE_") || d.Name.StartsWith("FORCE"));
        }

        [Fact]
        public void ExportMacrosInEveryPosition()
        {
            var s = Parse("class ENGINE_API AActor : public UObject {};\nstruct CORE_API FVector2 {};\nENGINE_API void GlobalFn();\nextern ENGINE_API int32 GCount;\nclass UE_API UThing {};");
            One(s, "AActor", SymbolKind.Class);
            One(s, "FVector2", SymbolKind.Struct);
            Assert.Equal("void GlobalFn()", One(s, "GlobalFn", SymbolKind.Function).Signature);
            Assert.False(One(s, "GCount", SymbolKind.Variable).IsDefinition);
            One(s, "UThing", SymbolKind.Class);
        }

        [Fact]
        public void ReflectionMacroOnSeparateLinesWithPragmaMacrosBetween()
        {
            var s = Parse("class AChar {\n#if WITH_EDITORONLY_DATA\n\tPRAGMA_DISABLE_DEPRECATION_WARNINGS\n\tUPROPERTY()\n\tTArray<int32> Old_DEPRECATED;\n\tPRAGMA_ENABLE_DEPRECATION_WARNINGS\n#endif\n\tUPROPERTY()\n\tint32 After;\n};");
            var old = One(s, "Old_DEPRECATED");
            Assert.Equal(UeFlags.UProperty, old.Ue);
            Assert.True(old.IsConditional);
            Assert.Equal(UeFlags.UProperty, One(s, "After").Ue);
            Assert.DoesNotContain(s.Declarations, d => d.Name.StartsWith("PRAGMA"));
        }

        [Fact]
        public void UFunctionSpecifiersAndUParamInParameters()
        {
            var s = Parse("class A {\n\tUFUNCTION(BlueprintCallable, Category=\"Inventory|Items\", meta=(DisplayName=\"Add Item\"))\n\tbool AddItem(UPARAM(ref) FItem& Item, int32 Count = 1);\n};");
            var f = One(s, "AddItem", SymbolKind.Method);
            Assert.Equal(UeFlags.UFunction, f.Ue);
            Assert.Equal("BlueprintCallable, Category=\"Inventory|Items\", meta=(DisplayName=\"Add Item\")", f.Specifiers);
            Assert.Equal("UPARAM(ref) FItem& Item, int32 Count = 1", f.Parameters);
        }

        [Fact]
        public void PureVirtualMacroIsABody()
        {
            var s = Parse("class A { virtual int32 Get() const PURE_VIRTUAL(A::Get, return 0;); int32 Next; };");
            var g = One(s, "Get");
            Assert.True((g.Modifiers & DeclModifiers.Pure) != 0);
            Assert.True(g.IsDefinition);
            One(s, "Next", SymbolKind.Field);
        }

        [Fact]
        public void UnknownStatementMacrosDoNotBreakParsing()
        {
            var s = Parse("IMPLEMENT_PRIMARY_GAME_MODULE(FDefaultGameModuleImpl, MyGame, \"MyGame\");\nSOME_REGISTRATION(Foo)\nvoid AfterMacro();\nDEFINE_SOMETHING(X) { int y = 0; }\nint Tail;");
            One(s, "AfterMacro", SymbolKind.Function);
            One(s, "Tail", SymbolKind.Variable);
            Assert.DoesNotContain(s.Declarations, d => d.Name == "y");
        }

        [Fact]
        public void TextMacroIsNotAReference()
        {
            var s = Parse("void F() { UE_LOG(LogTemp, Log, TEXT(\"{ not a brace\")); FString S = TEXT(\"x\"); }\nint After;");
            Assert.Empty(Occ(s, "TEXT"));
            Assert.Single(Occ(s, "UE_LOG"));
            One(s, "After");
        }
    }
}
