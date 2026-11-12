using System.IO;
using System.Linq;
using UEToVS.Core.Protocol;
using UEToVS.Indexer.Parsing;
using Xunit;

namespace UEToVS.Indexer.Tests
{
    public class ScannerTests
    {
        private static ScannedSymbol[] ScanSample(string name) =>
            CppSymbolScanner.Scan(File.ReadAllText(Path.Combine(System.AppContext.BaseDirectory, "Samples", name))).ToArray();

        private static ScannedSymbol One(ScannedSymbol[] all, string name, SymbolKind? kind = null) =>
            Assert.Single(all, s => s.Name == name && (kind == null || s.Kind == kind));

        [Fact]
        public void HeaderDeclarations()
        {
            var s = ScanSample("MyCharacter.h");

            var cls = One(s, "AMyCharacter", SymbolKind.Class);
            Assert.Equal(UeFlags.UClass, cls.Ue);
            Assert.Equal("Blueprintable", cls.Specifiers);
            Assert.Equal(28, cls.Line);

            var st = One(s, "FWeaponData", SymbolKind.Struct);
            Assert.Equal(UeFlags.UStruct, st.Ue);

            var e = One(s, "EWeaponState", SymbolKind.Enum);
            Assert.Equal(UeFlags.UEnum, e.Ue);
            Assert.Equal("EWeaponState", One(s, "Reloading", SymbolKind.EnumValue).Container);
            One(s, "Idle", SymbolKind.EnumValue);
            One(s, "Firing", SymbolKind.EnumValue);

            var del = One(s, "FOnHealthChanged", SymbolKind.Delegate);
            Assert.True((del.Ue & UeFlags.UDelegate) != 0);

            var fire = One(s, "Server_Fire", SymbolKind.Method);
            Assert.Equal("AMyCharacter", fire.Container);
            Assert.Equal(UeFlags.UFunction, fire.Ue);
            Assert.Contains("WithValidation", fire.Specifiers);
            Assert.False(fire.IsDefinition);
            Assert.Contains("void Server_Fire(const FVector& Target)", fire.Signature);

            One(s, "Tick", SymbolKind.Method);
            One(s, "OnHit", SymbolKind.Method);
            Assert.True(One(s, "FindComp", SymbolKind.Method).IsDefinition);
            One(s, "AMyCharacter", SymbolKind.Method); // ctor

            var damage = One(s, "Damage", SymbolKind.Field);
            Assert.Equal("FWeaponData", damage.Container);
            Assert.Equal(UeFlags.UProperty, damage.Ue);
            Assert.Equal(UeFlags.UProperty, One(s, "Ammo", SymbolKind.Field).Ue);
            Assert.Equal(UeFlags.None, One(s, "bIsAiming", SymbolKind.Field).Ue);
            One(s, "Callbacks", SymbolKind.Field);
            One(s, "OnHealthChanged", SymbolKind.Field);
            One(s, "Sockets", SymbolKind.Field);

            // GENERATED_BODY and access specifiers produce nothing
            Assert.DoesNotContain(s, x => x.Name == "GENERATED_BODY" || x.Name == "public");
        }

        [Fact]
        public void SourceDefinitions()
        {
            var s = ScanSample("MyCharacter.cpp");
            var impl = One(s, "Server_Fire_Implementation");
            Assert.Equal(SymbolKind.Method, impl.Kind);
            Assert.Equal("AMyCharacter", impl.Container);
            Assert.True(impl.IsDefinition);
            One(s, "Server_Fire_Validate");
            One(s, "OnHit_Implementation");
            Assert.Equal("AMyCharacter", One(s, "Tick").Container);
            One(s, "LogMyCharacter", SymbolKind.Variable);
            One(s, "ComputeSpread", SymbolKind.Function);
            One(s, "HelperCount", SymbolKind.Variable);
            // nothing from inside function bodies
            Assert.DoesNotContain(s, x => x.Name == "Lambda" || x.Name == "I" || x.Name == "UE_LOG");
        }

        [Theory]
        [InlineData("class ENGINE_API AActor : public UObject { };", "AActor", SymbolKind.Class)]
        [InlineData("struct alignas(16) FVector4 final { float X; };", "FVector4", SymbolKind.Struct)]
        [InlineData("template<typename T> class TArray { };", "TArray", SymbolKind.Class)]
        [InlineData("template<> struct TIsPODType<FFoo> { enum { Value = true }; };", "TIsPODType", SymbolKind.Struct)]
        [InlineData("namespace UE::Math { struct FBox { }; }", "FBox", SymbolKind.Struct)]
        [InlineData("typedef TMap<FName, int32> FNameMap;", "FNameMap", SymbolKind.Typedef)]
        [InlineData("using FIntMap = TMap<int32, int32>;", "FIntMap", SymbolKind.Typedef)]
        [InlineData("#define CHECK_SOMETHING(x) (x)\n", "CHECK_SOMETHING", SymbolKind.Macro)]
        [InlineData("CORE_API extern bool GIsEditor;", "GIsEditor", SymbolKind.Variable)]
        [InlineData("FORCEINLINE float Lerp(float A, float B, float T) { return A + (B - A) * T; }", "Lerp", SymbolKind.Function)]
        [InlineData("bool operator==(const FFoo& A, const FFoo& B);", "operator==", SymbolKind.Function)]
        [InlineData("DECLARE_LOG_CATEGORY_EXTERN(LogTemp, Log, All);", "LogTemp", SymbolKind.Variable)]
        [InlineData("DECLARE_DELEGATE_RetVal_OneParam(bool, FOnFoo, int32);", "FOnFoo", SymbolKind.Delegate)]
        [InlineData("DECLARE_DYNAMIC_MULTICAST_DELEGATE_TwoParams(FOnBar, int32, A, float, B);", "FOnBar", SymbolKind.Delegate)]
        [InlineData("FFoo& operator=(const FFoo& Other);", "operator=", SymbolKind.Function)]
        public void Snippets(string code, string name, SymbolKind kind)
        {
            var s = CppSymbolScanner.Scan(code);
            Assert.Contains(s, x => x.Name == name && x.Kind == kind);
        }

        [Fact]
        public void ElaboratedTypesAndBareMacros()
        {
            var s = CppSymbolScanner.Scan(@"class AChar {
public:
	FORCEINLINE class USkeletalMeshComponent* GetMesh() const { return Mesh; }
	class UMovement* Movement;
#if WITH_EDITORONLY_DATA
	PRAGMA_DISABLE_DEPRECATION_WARNINGS
	UPROPERTY()
	TArray<int32> Old_DEPRECATED;
	PRAGMA_ENABLE_DEPRECATION_WARNINGS
#endif
	UPROPERTY()
	int32 After;
};
class UForward;
class ENGINE_API UForward2;
");
            Assert.Equal("AChar", Assert.Single(s, x => x.Name == "GetMesh" && x.Kind == SymbolKind.Method).Container);
            Assert.Single(s, x => x.Name == "Movement" && x.Kind == SymbolKind.Field);
            Assert.Equal(UeFlags.UProperty, Assert.Single(s, x => x.Name == "Old_DEPRECATED").Ue);
            Assert.Equal(UeFlags.UProperty, Assert.Single(s, x => x.Name == "After").Ue);
            Assert.DoesNotContain(s, x => x.Name == "const" || x.Name == "UPROPERTY" || x.Name.StartsWith("PRAGMA"));
            Assert.DoesNotContain(s, x => x.Name.StartsWith("UForward"));
        }

        [Fact]
        public void QualifiedNamespaceContainer()
        {
            var s = CppSymbolScanner.Scan("namespace UE::Math { struct FBox { void Grow(); }; }");
            Assert.Equal("UE::Math::FBox", Assert.Single(s, x => x.Name == "Grow").Container);
        }

        [Fact]
        public void NestedClassAndStatics()
        {
            var s = CppSymbolScanner.Scan("class A { public: struct B { int X; }; static const int Y = 3; void F() const; };");
            Assert.Equal("A::B", Assert.Single(s, x => x.Name == "X").Container);
            Assert.Equal("A", Assert.Single(s, x => x.Name == "Y").Container);
            Assert.Equal("A", Assert.Single(s, x => x.Name == "F").Container);
        }

        [Fact]
        public void StringsAndCommentsWithBracesDoNotBreakScopes()
        {
            var s = CppSymbolScanner.Scan("// {\n/* } */ const char* S = \"{\"; class A { void F(); };\nchar C = '}';\nstruct B {};");
            Assert.Contains(s, x => x.Name == "A");
            Assert.Contains(s, x => x.Name == "B" && x.Container == null);
        }

        [Fact]
        public void LineContinuationInMacro()
        {
            var s = CppSymbolScanner.Scan("#define M(x) \\\n  do { x; } \\\n  while(0)\nclass After {};");
            var a = Assert.Single(s, x => x.Name == "After");
            Assert.Equal(3, a.Line);
        }
    }
}
