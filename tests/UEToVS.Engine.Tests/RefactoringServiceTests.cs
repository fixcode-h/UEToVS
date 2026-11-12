using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using UEToVS.Contracts;
using UEToVS.Engine.Indexing;
using UEToVS.Engine.Navigation;
using UEToVS.Engine.Refactoring;
using Xunit;

namespace UEToVS.Engine.Tests
{
    public sealed class RefactoringServiceTests
    {
        private const string BaseH = @"#pragma once
#include ""CoreMinimal.h""
class ENGINE_API AActor : public UObject
{
public:
	virtual void BeginPlay();
	virtual void Tick(float DeltaSeconds);
	virtual float TakeDamage(float Amount, class AController* By) const;
	virtual void Sealed() final;
	void NotVirtual();
	UFUNCTION(BlueprintNativeEvent)
	void OnHit(int32 Damage);
};
";

        private const string CharH = @"#pragma once
#include ""Actor.h""
class ACharacter : public AActor
{
public:
	virtual void Tick(float DeltaSeconds) override;
	virtual void Jump();
};
";

        private const string MyH = @"#pragma once
#include ""Character.h""
#include ""MyActor.generated.h""
UCLASS()
class AMyActor : public ACharacter
{
	GENERATED_BODY()
public:
	virtual void BeginPlay() override;
	UFUNCTION(Server, Reliable, WithValidation)
	void ServerFire(const FVector& Dir, int32 Count = 1);
	void Local();
	FHitResult Hit;
};
";

        private const string MyCpp = @"#include ""MyActor.h""

void AMyActor::BeginPlay()
{
	Super::BeginPlay();
}

void AMyActor::Extra(int A)
{
}
";

        [Fact]
        public void Context_DeclarationInHeader_ReportsSignatureAndDefinitions()
        {
            using var p = new Project(("Actor.h", BaseH), ("Character.h", CharH), ("MyActor.h", MyH), ("MyActor.cpp", MyCpp), ("HitResult.h", "#pragma once\nstruct FHitResult { int X; };\n"));
            var (l, c) = p.Find("MyActor.h", "ServerFire");
            var r = p.Service.GetContext(new RefactoringContextRequest { Caret = p.Caret("MyActor.h", l, c + 2) }, CancellationToken.None);
            Assert.NotNull(r.Function);
            Assert.Equal("ServerFire", r.Function.Name);
            Assert.Equal("AMyActor", r.Function.Container);
            Assert.Equal("const FVector& Dir, int32 Count = 1", r.Function.Parameters);
            Assert.Equal("void", r.Function.ReturnType);
            Assert.Contains("Server", r.Function.Specifiers);
            Assert.False(r.Function.IsDefinition);
            Assert.Empty(r.Definitions);
            Assert.Equal("AMyActor", r.Class.Symbol.Name);
            Assert.True(r.Class.BodyEndLine > r.Class.BodyStartLine);

            // BeginPlay has a definition in the .cpp
            (l, c) = p.Find("MyActor.h", "BeginPlay");
            r = p.Service.GetContext(new RefactoringContextRequest { Caret = p.Caret("MyActor.h", l, c + 1) }, CancellationToken.None);
            Assert.Equal(FunctionModifiers.Virtual | FunctionModifiers.Override, r.Function.Modifiers);
            Assert.Single(r.Definitions);
        }

        [Fact]
        public void Context_DefinitionWithoutDeclaration_FindsOwningClass()
        {
            using var p = new Project(("Actor.h", BaseH), ("Character.h", CharH), ("MyActor.h", MyH), ("MyActor.cpp", MyCpp));
            var (l, c) = p.Find("MyActor.cpp", "Extra");
            var r = p.Service.GetContext(new RefactoringContextRequest { Caret = p.Caret("MyActor.cpp", l, c + 1) }, CancellationToken.None);
            Assert.Equal("Extra", r.Function.Name);
            Assert.True(r.Function.IsDefinition);
            Assert.Empty(r.Declarations);
            Assert.NotNull(r.Class);
            Assert.EndsWith("MyActor.h", r.Class.Symbol.Location.FilePath);
            Assert.True(r.Class.BodyEndLine > 0);
        }

        [Fact]
        public void Context_TypeAtCaret_IncludeStates()
        {
            using var p = new Project(("Actor.h", BaseH), ("Character.h", CharH), ("MyActor.h", MyH), ("HitResult.h", "#pragma once\nstruct FHitResult { int X; };\n"),
                ("User.cpp", "#include \"MyActor.h\"\nvoid F() { ACharacter* C = nullptr; AActor* A = nullptr; FHitResult H; }\n"));
            var (l, c) = p.Find("User.cpp", "ACharacter");
            var r = p.Service.GetContext(new RefactoringContextRequest { Caret = p.Caret("User.cpp", l, c + 1), CheckIncludes = true }, CancellationToken.None);
            Assert.Equal(IncludeState.Transitive, r.Type.Include);
            Assert.Equal("MyActor.h", r.Type.IncludedVia);
            (l, c) = p.Find("User.cpp", "AActor*");
            r = p.Service.GetContext(new RefactoringContextRequest { Caret = p.Caret("User.cpp", l, c + 1), CheckIncludes = true }, CancellationToken.None);
            Assert.Equal(IncludeState.Transitive, r.Type.Include); // two levels down
            (l, c) = p.Find("User.cpp", "FHitResult");
            r = p.Service.GetContext(new RefactoringContextRequest { Caret = p.Caret("User.cpp", l, c + 1), CheckIncludes = true }, CancellationToken.None);
            Assert.Equal(IncludeState.Missing, r.Type.Include);
            Assert.EndsWith("HitResult.h", r.Type.HeaderPath);
            // without the flag: no walk
            r = p.Service.GetContext(new RefactoringContextRequest { Caret = p.Caret("User.cpp", l, c + 1) }, CancellationToken.None);
            Assert.Equal(IncludeState.Unknown, r.Type.Include);
        }

        [Fact]
        public void IncludeMatches_SuffixOnPathBoundary()
        {
            Assert.True(RefactoringService.IncludeMatches("GameFramework/Actor.h", "/e/engine/classes/gameframework/actor.h"));
            Assert.True(RefactoringService.IncludeMatches("Actor.h", "/e/engine/classes/gameframework/actor.h"));
            Assert.False(RefactoringService.IncludeMatches("MyActor.h", "/e/actor.h"));
            Assert.False(RefactoringService.IncludeMatches("Actor.h", "/e/myactor.h"));
        }

        [Fact]
        public void Overridable_ListsBaseVirtualsNotYetOverridden()
        {
            using var p = new Project(("Actor.h", BaseH), ("Character.h", CharH), ("MyActor.h", MyH));
            var (l, c) = p.Find("MyActor.h", "void Local");
            var r = p.Service.GetOverridableMethods(new OverridableMethodsRequest { Caret = p.Caret("MyActor.h", l, c) }, CancellationToken.None);
            var names = r.Methods.Select(m => m.Name).ToList();
            Assert.Equal("AMyActor", r.Type.Name);
            Assert.Contains("Tick", names);
            Assert.Contains("Jump", names);
            Assert.Contains("TakeDamage", names);
            Assert.Contains("OnHit_Implementation", names);
            Assert.DoesNotContain("BeginPlay", names);   // already overridden
            Assert.DoesNotContain("Sealed", names);      // final
            Assert.DoesNotContain("NotVirtual", names);
            Assert.Equal(1, names.Count(n => n == "Tick")); // most derived declaration only
            Assert.Equal("ACharacter", r.Methods.First(m => m.Name == "Tick").DeclaringType);
            Assert.True(names.IndexOf("Jump") < names.IndexOf("TakeDamage")); // nearest base first
            Assert.True(r.AlreadyOverridden >= 1);
            var td = r.Methods.First(m => m.Name == "TakeDamage");
            Assert.Equal("float", td.ReturnType);
            Assert.True((td.Modifiers & FunctionModifiers.Const) != 0);
        }

        // ------------------------------------------------------------------ fixture

        // ---- v0.4.5 refactoring batch 2: expression type, locals, override chain

        private const string LocalsCpp = @"#include ""MyActor.h""

void AMyActor::Local()
{
	const int32 Count = 3;
	FHitResult Copy = Hit;
	for (int32 I = 0; I < Count; ++I)
	{
		float Scale = I * 2.f;
	}
	BeginPlay();
}
";

        [Fact]
        public void Context_IncludeLocals_ReturnsEnclosingFunctionAndLocals()
        {
            using var p = new Project(("Actor.h", BaseH), ("Character.h", CharH), ("MyActor.h", MyH), ("MyActor.cpp", LocalsCpp));
            var (l, c) = p.Find("MyActor.cpp", "BeginPlay();");
            var r = p.Service.GetContext(new RefactoringContextRequest { Caret = p.Caret("MyActor.cpp", l, c), IncludeLocals = true }, CancellationToken.None);
            Assert.NotNull(r.EnclosingFunction);
            Assert.Equal("Local", r.EnclosingFunction.Name);
            Assert.Equal("AMyActor", r.EnclosingFunction.Container);
            Assert.True(r.EnclosingFunction.EndLine > l);
            var count = Assert.Single(r.Locals, x => x.Name == "Count");
            Assert.Equal("const int32", count.Type);
            Assert.Equal(4, count.Line);
            Assert.False(count.IsParameter);
            var scale = Assert.Single(r.Locals, x => x.Name == "Scale");
            Assert.Equal("float", scale.Type);
            Assert.True(scale.ScopeEndLine < l, "Scale goes out of scope at the loop's closing brace");
            // without the flag nothing is computed
            r = p.Service.GetContext(new RefactoringContextRequest { Caret = p.Caret("MyActor.cpp", l, c) }, CancellationToken.None);
            Assert.Null(r.EnclosingFunction);
            Assert.Empty(r.Locals);
        }

        [Fact]
        public void Context_Selection_TypesTheExpressionEndingThere()
        {
            using var p = new Project(("Actor.h", BaseH), ("Character.h", CharH), ("MyActor.h", MyH), ("MyActor.cpp", LocalsCpp),
                ("HitResult.h", "#pragma once\nstruct FHitResult { int X; };\n"));
            var (l, c) = p.Find("MyActor.cpp", "Hit;");
            var r = p.Service.GetContext(new RefactoringContextRequest
            {
                Caret = p.Caret("MyActor.cpp", l, c), SelectionStartLine = l, SelectionStartColumn = c, SelectionEndLine = l, SelectionEndColumn = c + 3,
            }, CancellationToken.None);
            Assert.Equal("FHitResult", r.ExpressionType);
            // a selection ending inside a token is not typed
            r = p.Service.GetContext(new RefactoringContextRequest
            {
                Caret = p.Caret("MyActor.cpp", l, c), SelectionStartLine = l, SelectionStartColumn = c, SelectionEndLine = l, SelectionEndColumn = c + 2,
            }, CancellationToken.None);
            Assert.Null(r.ExpressionType);
        }

        [Fact]
        public void Context_SignatureFamily_ListsBaseDeclarationsAndOverrides()
        {
            const string Derived = @"#pragma once
#include ""MyActor.h""
class AMyChild : public AMyActor
{
public:
	virtual void BeginPlay() override;
	void ServerFire_Implementation(const FVector& Dir, int32 Count);
};
";
            using var p = new Project(("Actor.h", BaseH), ("Character.h", CharH), ("MyActor.h", MyH), ("MyActor.cpp", MyCpp), ("MyChild.h", Derived));
            var (l, c) = p.Find("MyActor.h", "BeginPlay");
            var r = p.Service.GetContext(new RefactoringContextRequest { Caret = p.Caret("MyActor.h", l, c + 1), IncludeSignatureFamily = true }, CancellationToken.None);
            Assert.Contains(r.BaseDeclarations, s => s.Container == "AActor" && s.Name == "BeginPlay");
            Assert.Contains(r.Overrides, s => s.Container == "AMyChild" && s.Name == "BeginPlay");
            (l, c) = p.Find("MyActor.h", "ServerFire");
            r = p.Service.GetContext(new RefactoringContextRequest { Caret = p.Caret("MyActor.h", l, c + 1), IncludeSignatureFamily = true }, CancellationToken.None);
            Assert.Empty(r.BaseDeclarations);
            Assert.Contains(r.Overrides, s => s.Container == "AMyChild" && s.Name == "ServerFire_Implementation");
        }

        private sealed class Project : IDisposable
        {
            private readonly string _root = Path.Combine(Path.GetTempPath(), "uetovs-refactor", Guid.NewGuid().ToString("N"));
            private readonly SegmentedIndexBackend _backend = new SegmentedIndexBackend();

            public Project(params (string Name, string Text)[] files)
            {
                Directory.CreateDirectory(_root);
                _backend.TryLoad(Path.Combine(_root, ".index"), "refactor");
                var table = new FileTable();
                var parser = new SyntaxFileParser();
                foreach (var (name, text) in files)
                {
                    string path = Path.Combine(_root, name);
                    File.WriteAllText(path, text);
                    var kind = name.EndsWith(".h") ? Unreal.SourceFileKind.Header : Unreal.SourceFileKind.Source;
                    var listed = new Unreal.ListedFile { Path = path, Name = name, Module = "Mini", Origin = ItemOrigin.Project, Kind = kind };
                    table.Upsert(listed);
                    _backend.UpdateParsed(path, parser.Parse(new FileContext { Path = path, Module = "Mini", Origin = ItemOrigin.Project, Kind = kind },
                        Encoding.UTF8.GetBytes(text), CancellationToken.None));
                }
                Service = new RefactoringService(_backend, table, new DocumentCache());
            }

            public RefactoringService Service { get; }

            public string PathOf(string name) => Path.Combine(_root, name);

            public (int Line, int Column) Find(string name, string text)
            {
                var lines = File.ReadAllLines(PathOf(name));
                for (int i = 0; i < lines.Length; i++)
                {
                    int c = lines[i].IndexOf(text, StringComparison.Ordinal);
                    if (c >= 0) return (i, c);
                }
                throw new InvalidOperationException(text);
            }

            public CaretRequest Caret(string name, int line, int column) => new CaretRequest { FilePath = PathOf(name), Line = line, Column = column };

            public void Dispose()
            {
                _backend.Dispose();
                try { Directory.Delete(_root, true); } catch (IOException) { }
            }
        }
    }
}
