using System.Linq;
using UEToVS.Contracts;
using Xunit;
using static UEToVS.Engine.Syntax.Tests.TestUtil;

namespace UEToVS.Engine.Syntax.Tests
{
    public class DeclarationTests
    {
        [Theory]
        [InlineData("class ENGINE_API AActor : public UObject { };", "AActor", SymbolKind.Class)]
        [InlineData("struct alignas(16) FVector4 final { float X; };", "FVector4", SymbolKind.Struct)]
        [InlineData("union FBits { int32 I; float F; };", "FBits", SymbolKind.Union)]
        [InlineData("template<typename T> class TArray { };", "TArray", SymbolKind.Class)]
        [InlineData("template<> struct TIsPODType<FFoo> { enum { Value = true }; };", "TIsPODType", SymbolKind.Struct)]
        [InlineData("namespace UE::Math { struct FBox { }; }", "FBox", SymbolKind.Struct)]
        [InlineData("typedef TMap<FName, int32> FNameMap;", "FNameMap", SymbolKind.Typedef)]
        [InlineData("using FIntMap = TMap<int32, int32>;", "FIntMap", SymbolKind.Typedef)]
        [InlineData("#define CHECK_SOMETHING(x) (x)\n", "CHECK_SOMETHING", SymbolKind.Macro)]
        [InlineData("CORE_API extern bool GIsEditor;", "GIsEditor", SymbolKind.Variable)]
        [InlineData("FORCEINLINE float Lerp(float A, float B, float T) { return A + (B - A) * T; }", "Lerp", SymbolKind.Function)]
        [InlineData("bool operator==(const FFoo& A, const FFoo& B);", "operator==", SymbolKind.Function)]
        [InlineData("FFoo& operator=(const FFoo& Other);", "operator=", SymbolKind.Function)]
        [InlineData("namespace N { int32 Counter = 0; }", "Counter", SymbolKind.Variable)]
        [InlineData("typedef void (*FCallback)(int32, float);", "FCallback", SymbolKind.Typedef)]
        [InlineData("enum ELegacy { LA, LB };", "LB", SymbolKind.EnumValue)]
        [InlineData("template <typename T> using TPtr = TSharedPtr<T, ESPMode::ThreadSafe>;", "TPtr", SymbolKind.Typedef)]
        [InlineData("namespace Alias = Some::Deep::Ns;", "Alias", SymbolKind.Typedef)]
        public void Snippets(string code, string name, SymbolKind kind)
        {
            One(code, name, kind);
        }

        [Fact]
        public void NestedNamespacesAndClassesBuildContainers()
        {
            var s = Parse("namespace UE { namespace Math { struct FBox { void Grow(); struct FInner { int X; }; }; } }\nnamespace A::B { class C { enum class E { V }; }; }");
            Assert.Equal("UE::Math::FBox", One(s, "Grow").Container);
            Assert.Equal("UE::Math::FBox::FInner", One(s, "X").Container);
            Assert.Equal("UE::Math", One(s, "FBox").Container);
            var math = One(s, "Math", SymbolKind.Namespace);
            Assert.Equal("UE", math.Container);
            Assert.Equal("A", One(s, "B", SymbolKind.Namespace).Container);
            Assert.Equal("A::B::C::E", One(s, "V").Container);
            Assert.False(math.Body.IsNone);
        }

        [Fact]
        public void AnonymousNamespaceIsTransparent()
        {
            var s = Parse("namespace { static int32 Helper = 0; float Compute(float B) { return B; } }");
            Assert.Null(One(s, "Helper").Container);
            Assert.Null(One(s, "Compute").Container);
            Assert.DoesNotContain(s.Declarations, d => d.Kind == SymbolKind.Namespace);
        }

        [Fact]
        public void OutOfLineDefinitionsResolveContainer()
        {
            var s = Parse("namespace Game {\nvoid AFoo::Bar(int32 X) const {}\nAFoo::AFoo() {}\nAFoo::~AFoo() {}\nint32 AFoo::Count = 0;\nFFoo& FFoo::operator+=(const FFoo& O) { return *this; }\ntemplate<typename T> void TBox<T>::Put(T V) {}\n}\nvoid Game::FreeFn() {}");
            var bar = One(s, "Bar", SymbolKind.Method);
            Assert.Equal("Game::AFoo", bar.Container);
            Assert.True(bar.IsDefinition);
            Assert.True((bar.Modifiers & DeclModifiers.Const) != 0);
            Assert.Equal("void AFoo::Bar(int32 X) const", bar.Signature);
            Assert.Equal("Game::AFoo", One(s, "AFoo", SymbolKind.Constructor).Container);
            Assert.Equal("Game::AFoo", One(s, "~AFoo", SymbolKind.Destructor).Container);
            Assert.Equal("Game::AFoo", One(s, "Count", SymbolKind.Field).Container);
            Assert.Equal("Game::FFoo", One(s, "operator+=", SymbolKind.Method).Container);
            var put = One(s, "Put", SymbolKind.Method);
            Assert.Equal("Game::TBox", put.Container);
            Assert.Equal("typename T", put.TemplateParameters);
            var free = One(s, "FreeFn");
            Assert.Equal(SymbolKind.Function, free.Kind); // qualifier is a known namespace
            Assert.Equal("Game", free.Container);
        }

        [Fact]
        public void FunctionDetails()
        {
            var s = Parse("class A {\npublic:\n\tvirtual const FString& GetName(int32 Index, bool bX = false) const = 0;\n\tstatic TArray<int32> Make() noexcept;\n\tauto Trailing() -> TOptional<int32>;\n\tA& operator=(A&&) = default;\n\tA(const A&) = delete;\n\texplicit operator bool() const;\n\tbool operator()(int32 X) const;\n\tvoid* operator new(size_t S);\n};");
            var g = One(s, "GetName");
            Assert.Equal(SymbolKind.Method, g.Kind);
            Assert.Equal("const FString&", g.Type);
            Assert.Equal("int32 Index, bool bX = false", g.Parameters);
            Assert.Equal(DeclModifiers.Virtual | DeclModifiers.Const | DeclModifiers.Pure, g.Modifiers);
            Assert.Equal("virtual const FString& GetName(int32 Index, bool bX = false) const = 0", g.Signature);
            Assert.False(g.IsDefinition);
            var m = One(s, "Make");
            Assert.Equal("TArray<int32>", m.Type);
            Assert.True((m.Modifiers & (DeclModifiers.Static | DeclModifiers.Noexcept)) == (DeclModifiers.Static | DeclModifiers.Noexcept));
            Assert.Equal("TOptional<int32>", One(s, "Trailing").Type);
            Assert.True((One(s, "operator=").Modifiers & DeclModifiers.Defaulted) != 0);
            Assert.True((One(s, "A", SymbolKind.Constructor).Modifiers & DeclModifiers.Deleted) != 0);
            Assert.True((One(s, "operator bool").Modifiers & DeclModifiers.Explicit) != 0);
            One(s, "operator()");
            One(s, "operator new");
        }

        [Fact]
        public void ConstructorsWithInitializerLists()
        {
            const string code = "class FThing {\npublic:\n\tFThing() : A(1), B{2}, C(MakeC(3)) { Init(); }\n\tFThing(int X) : FThing() {}\n\t~FThing();\n\tint A; int B; FC C;\n};\nFThing::~FThing() { Cleanup(); }";
            var s = Parse(code);
            var ctors = s.Declarations.Where(d => d.Kind == SymbolKind.Constructor).ToArray();
            Assert.Equal(2, ctors.Length);
            Assert.All(ctors, c => Assert.True(c.IsDefinition));
            Assert.Equal(2, s.Declarations.Count(d => d.Kind == SymbolKind.Destructor));
            Assert.Equal(OccurrenceRole.Write, Assert.Single(Occ(s, "A")).Role);
            Assert.Equal(OccurrenceRole.Write, Assert.Single(Occ(s, "B")).Role);
            Assert.Equal(OccurrenceRole.Call, Assert.Single(Occ(s, "MakeC")).Role);
            Assert.Single(Occ(s, "Init"));
            Assert.Single(Occ(s, "Cleanup"));
            One(s, "C", SymbolKind.Field);
        }

        [Fact]
        public void EnumsWithValuesAndUnderlyingTypes()
        {
            var s = Parse("enum class EState : uint8 { Idle, Busy = 3, Dead = Busy + 1 };\nenum EOld { OldA = 1 << 2, OldB };\nnamespace ENs { enum Type { X, Y }; }\nenum class EFwd : int32;");
            var e = One(s, "EState", SymbolKind.Enum);
            Assert.Equal("uint8", e.Value);
            Assert.True((e.Modifiers & DeclModifiers.Scoped) != 0);
            Assert.Equal("enum class EState : uint8", e.Signature);
            var busy = One(s, "Busy");
            Assert.Equal("3", busy.Value);
            Assert.Equal("EState", busy.Container);
            Assert.Equal("Busy + 1", One(s, "Dead").Value);
            Assert.Equal("1 << 2", One(s, "OldA").Value);
            Assert.Equal("ENs::Type", One(s, "Y").Container);
            Assert.DoesNotContain(s.Declarations, d => d.Name == "EFwd");
            Assert.Equal(s.Declarations.ToList().IndexOf(e), busy.Parent);
        }

        [Fact]
        public void ForwardDeclarationsOptIn()
        {
            const string code = "class UForward;\nclass ENGINE_API UForward2;\nstruct FFwd;\nenum class EFwd : uint8;";
            Assert.Empty(Parse(code).Declarations);
            var s = Parse(code, new ParseOptions { IncludeForwardDeclarations = true });
            Assert.Equal(4, s.Declarations.Count);
            Assert.All(s.Declarations, d => Assert.True((d.Modifiers & DeclModifiers.Forward) != 0));
        }

        [Fact]
        public void FieldsBitfieldsArraysAndMultipleDeclarators()
        {
            var s = Parse("struct S {\n\tuint8 bA : 1, bB : 1;\n\tfloat Values[4] = {0};\n\tint32 X = 1, *Y = nullptr, &Z = X;\n\tmutable FCriticalSection Lock;\n\tstatic constexpr int32 N = 4;\n\tvoid (*Callback)(int32);\n\tTFunction<void(int32, float)> Fn = [](int32, float) { return; };\n\tint32 Last;\n};");
            foreach (var n in new[] { "bA", "bB", "Values", "X", "Y", "Z", "Lock", "N", "Callback", "Fn", "Last" })
                Assert.Equal("S", One(s, n, SymbolKind.Field).Container);
            Assert.Equal("int32*", One(s, "Y").Type);
            Assert.Equal("int32&", One(s, "Z").Type);
            Assert.True((One(s, "N").Modifiers & (DeclModifiers.Static | DeclModifiers.Constexpr)) == (DeclModifiers.Static | DeclModifiers.Constexpr));
            Assert.Equal("void (*Callback)(int32)", One(s, "Callback").Signature);
        }

        [Fact]
        public void ElaboratedTypeSpecifiersAreNotClasses()
        {
            var s = Parse("class AChar {\npublic:\n\tFORCEINLINE class USkeletalMeshComponent* GetMesh() const { return Mesh; }\n\tclass UMovement* Movement;\n\tstruct FHit Trace(enum ECollision Channel);\n};");
            Assert.Equal("AChar", One(s, "GetMesh", SymbolKind.Method).Container);
            Assert.Equal("class UMovement*", One(s, "Movement", SymbolKind.Field).Type);
            One(s, "Trace", SymbolKind.Method);
            Assert.DoesNotContain(s.Declarations, d => d.Name == "USkeletalMeshComponent" || d.Name == "UMovement" || d.Name == "FHit");
        }

        [Fact]
        public void TemplatesAndSpecializations()
        {
            var s = Parse("template <typename T, int32 N = 4>\nclass TFixed : public TBase<T, N> {\n\ttemplate <typename U> void Push(U&& V);\n};\ntemplate <> class TFixed<int, 1> {};\ntemplate <typename T> FORCEINLINE T Max(T A, T B) { return A > B ? A : B; }");
            var cls = s.Declarations.Where(d => d.Name == "TFixed").ToArray();
            Assert.Equal(2, cls.Length);
            Assert.Equal("typename T, int32 N = 4", cls[0].TemplateParameters);
            Assert.Equal(new[] { "TBase<T, N>" }, cls[0].Bases);
            Assert.Equal("typename U", One(s, "Push").TemplateParameters);
            var max = One(s, "Max", SymbolKind.Function);
            Assert.True((max.Modifiers & DeclModifiers.Template) != 0);
            Assert.Equal("T", max.Type);
        }

        [Fact]
        public void AccessLevels()
        {
            var s = Parse("class C { int A; public: int B; protected: int D; private: int E; };\nstruct S { int F; };");
            Assert.Equal(AccessLevel.Private, One(s, "A").Access);
            Assert.Equal(AccessLevel.Public, One(s, "B").Access);
            Assert.Equal(AccessLevel.Protected, One(s, "D").Access);
            Assert.Equal(AccessLevel.Private, One(s, "E").Access);
            Assert.Equal(AccessLevel.Public, One(s, "F").Access);
        }

        [Fact]
        public void TypedefStructAndTrailingDeclarators()
        {
            var s = Parse("typedef struct tagPOINT { long x; long y; } POINT, *PPOINT;\nstruct FConfig { int A; } GConfig, GOther;\nstruct { int Anon; } GAnonInstance;");
            One(s, "tagPOINT", SymbolKind.Struct);
            One(s, "POINT", SymbolKind.Typedef);
            One(s, "PPOINT", SymbolKind.Typedef);
            Assert.Equal("FConfig", One(s, "GConfig", SymbolKind.Variable).Type);
            One(s, "GOther", SymbolKind.Variable);
            Assert.Null(One(s, "Anon").Container);
            One(s, "GAnonInstance", SymbolKind.Variable);
        }

        [Fact]
        public void LinkageSpecificationIsTransparent()
        {
            var s = Parse("extern \"C\" { int CFunc(int); struct CS { int v; }; }\nextern \"C\" void Single();");
            Assert.Null(One(s, "CFunc", SymbolKind.Function).Container);
            Assert.Equal("CS", One(s, "v").Container);
            One(s, "Single", SymbolKind.Function);
        }

        [Fact]
        public void StatementsAtFileScopeAreNotDeclarations()
        {
            var s = Parse("static_assert(sizeof(int) == 4, \"x\");\nusing namespace std;\nusing Base::Member;\nfriend class X;\nint After;");
            Assert.Equal(new[] { "After" }, s.Declarations.Select(d => d.Name).ToArray());
        }

        [Fact]
        public void DirectInitVariablesVsFunctionDeclarations()
        {
            var s = Parse("static FAutoConsoleVariableRef CVarX(TEXT(\"x\"), GX, TEXT(\"help\"));\nFVector Origin(0, 0, 0);\nFVector MakeOrigin(float Scale);");
            One(s, "CVarX", SymbolKind.Variable);
            One(s, "Origin", SymbolKind.Variable);
            One(s, "MakeOrigin", SymbolKind.Function);
        }

        [Fact]
        public void PositionsAndDocComments()
        {
            const string code = "/** The hero. */\nUCLASS()\nclass AHero : public AActor\n{\n\tGENERATED_BODY()\n\n\t/// Health in points.\n\t/// Never negative.\n\tUPROPERTY()\n\tfloat Health;\n\n\tint32 Undocumented; // trailing\n};";
            var s = Parse(code);
            var bytes = System.Text.Encoding.UTF8.GetBytes(code);
            var hero = One(s, "AHero");
            Assert.Equal((2, 6), (hero.Line, hero.Column));
            Assert.Equal("The hero.", CppParser.GetDocCommentText(bytes, hero.DocComment));
            var health = One(s, "Health");
            Assert.Equal((9, 7), (health.Line, health.Column));
            Assert.Equal("Health in points.\nNever negative.", CppParser.GetDocCommentText(bytes, health.DocComment));
            Assert.True(One(s, "Undocumented").DocComment.IsNone);
            Assert.Equal("Health", System.Text.Encoding.UTF8.GetString(bytes, health.Offset, health.NameLength));
            Assert.False(hero.Body.IsNone);
            Assert.Equal((byte)'{', bytes[hero.Body.Start]);
            Assert.Equal((byte)'}', bytes[hero.Body.End - 1]);
        }

        [Fact]
        public void FunctionBodyRangeAndParentIndex()
        {
            const string code = "struct A { void F() { int x = 1; } int G; };";
            var s = Parse(code);
            var f = One(s, "F");
            Assert.Equal(code.IndexOf("{ int x"), f.Body.Start);
            Assert.Equal(code.IndexOf("} int G") + 1, f.Body.End);
            Assert.Equal(s.Declarations.ToList().IndexOf(One(s, "A")), f.Parent);
        }
    }
}
