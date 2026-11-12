using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using UEToVS.Contracts;
using UEToVS.Engine.Indexing;
using UEToVS.Engine.IntelliSense;
using UEToVS.Engine.Unreal;
using Xunit;
using Xunit.Abstractions;

namespace UEToVS.Engine.Tests
{
    /// <summary>
    /// A small self-contained UE-like tree (engine headers with Origin = Engine, game headers with Origin = Project), indexed with
    /// the real parser. Test sources are sent as unsaved buffers (overlay) so every case is independent.
    /// </summary>
    public sealed class DiagnosticsFixture : IDisposable
    {
        public static readonly Dictionary<string, string> Files = new Dictionary<string, string>
        {
            ["Engine/CoreUObject.h"] = @"#pragma once
class UClass;
class FString { public: int Len() const; };
class UObjectBase
{
public:
	UClass* GetClass() const;
};
class UObjectBaseUtility : public UObjectBase
{
public:
	bool IsValidLowLevel() const;
	FString GetName() const;
};
class COREUOBJECT_API UObject : public UObjectBaseUtility
{
public:
	class UWorld* GetWorld() const;
	virtual void PostInitProperties();
};
UCLASS()
class COREUOBJECT_API UClass : public UObject
{
	GENERATED_BODY()
public:
	UObject* GetDefaultObject() const;
};
template<typename T> class TSharedPtr { public: T* Get() const; bool IsValid() const; };
template<typename T> class TWeakPtr { public: TSharedPtr<T> Pin() const; };
template<typename T> class TObjectPtr { public: T* Get() const; };
template<typename T> class TArray { public: int Num() const; };
",
            ["Engine/Actor.h"] = @"#pragma once
#include ""CoreUObject.h""
struct FVector
{
	float X;
	float Y;
	float Z;
	float Size() const;
};
UCLASS()
class ENGINE_API AActor : public UObject
{
	GENERATED_BODY()
public:
	virtual void BeginPlay();
	FVector GetActorLocation() const;
	AActor* GetOwner() const;
	template<class T> T* FindComponentByClass() const;
	UPROPERTY()
	TObjectPtr<AActor> Owner;
	float CustomTimeDilation;
};
template<typename T> class TBaseThing { public: T Value; };
class FUsesTemplateBase : public TBaseThing<int> { public: int Own; };
class FMacroInjected
{
	DECLARE_MAGIC_MEMBERS(Magic)
public:
	int Known;
};
struct FWithUnion
{
	union { float F; int I; };
	int Tag;
};
",
            ["Game/MyActor.h"] = @"#pragma once
#include ""Actor.h""
#include ""MyActor.generated.h""

DECLARE_MULTICAST_DELEGATE_OneParam(FOnFired, int32);

UCLASS()
class MYGAME_API AMyActor : public AActor
{
	GENERATED_BODY()
public:
	void Fire();
	void Use(struct FMyData& D);
	int32 Ammo;
	UPROPERTY()
	TObjectPtr<AMyActor> Buddy;
	AMyActor* GetBuddy() const;
	UFUNCTION(Server, Reliable)
	void ServerFire();
	int32 Pick(int32 I) const;
	struct FPlain Pick() const;
	FOnFired OnFired;
};
class FHasUnknownBase : public FUnindexedBase { public: int A; };
USTRUCT()
struct FMyData
{
	GENERATED_BODY()
	int32 Value;
};
struct FPlain
{
	int P;
	AMyActor* Owner;
};
using FAliasData = FMyData;
namespace Game
{
	struct FDup { int A; };
}
namespace Other
{
	struct FDup { int B; };
}
",
        };

        public DiagnosticsFixture()
        {
            Root = Path.Combine(Path.GetTempPath(), "uetovs-diag-corpus", Guid.NewGuid().ToString("N"));
            Backend = new SegmentedIndexBackend();
            Backend.TryLoad(Path.Combine(Root, ".index"), "diag-corpus");
            var parser = new SyntaxFileParser();
            foreach (var kv in Files)
            {
                string path = PathOf(kv.Key);
                Directory.CreateDirectory(Path.GetDirectoryName(path));
                File.WriteAllText(path, kv.Value);
                bool engine = kv.Key.StartsWith("Engine/", StringComparison.Ordinal);
                var listed = new ListedFile
                {
                    Path = path,
                    Name = Path.GetFileName(path),
                    Module = engine ? "Engine" : "MyGame",
                    Origin = engine ? ItemOrigin.Engine : ItemOrigin.Project,
                    Kind = SourceFileKind.Header,
                };
                Table.Upsert(listed);
                var ctx = new FileContext { Path = path, Module = listed.Module, Origin = listed.Origin, Kind = listed.Kind };
                Backend.UpdateParsed(path, parser.Parse(ctx, File.ReadAllBytes(path), CancellationToken.None));
            }
            // an engine source file (diagnostics never run on engine code)
            string enginePath = PathOf("Engine/Private/EngineThing.cpp");
            Table.Upsert(new ListedFile { Path = enginePath, Name = "EngineThing.cpp", Module = "Engine", Origin = ItemOrigin.Engine, Kind = SourceFileKind.Source });
            Service = new IntelliSenseService(Backend, Table);
        }

        public string Root { get; }
        public SegmentedIndexBackend Backend { get; }
        public FileTable Table { get; } = new FileTable();
        public IntelliSenseService Service { get; }

        public string PathOf(string relative) => Path.Combine(Root, relative.Replace('/', Path.DirectorySeparatorChar));

        public void Dispose()
        {
            Backend.Dispose();
            try { Directory.Delete(Root, true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }
    }

    /// <summary>
    /// Squiggle rules: each source marks the identifiers that must be reported with <c>/*@UE0001*/</c> (code of the rule) right before
    /// them; nothing else in the file may be reported (the "must not report" cases are the point: false positives are bugs).
    /// </summary>
    public sealed class DiagnosticsTests : IClassFixture<DiagnosticsFixture>
    {
        private static readonly Regex Marker = new Regex(@"/\*@(UE\d{4})\*/", RegexOptions.Compiled);
        private static long _version = 1000;
        private readonly DiagnosticsFixture _f;
        private readonly ITestOutputHelper _out;

        public DiagnosticsTests(DiagnosticsFixture f, ITestOutputHelper output)
        {
            _f = f;
            _out = output;
        }

        private CodeDiagnosticsResponse Run(string relative, string text, int caretLine = 0, int caretColumn = 0)
        {
            var req = new CodeDiagnosticsRequest
            {
                Document = new CaretRequest { FilePath = _f.PathOf(relative), Text = text, TextVersion = Interlocked.Increment(ref _version), Line = caretLine, Column = caretColumn },
                StartLine = 0,
                EndLine = 100000,
            };
            using var sink = new CapturingLogSink();
            var resp = _f.Service.GetDiagnostics(req, CancellationToken.None);
            _out.WriteLine(sink.Dump(x => x.Event == "intellisense.diagnostics" && x.Data != null && Equals(x.Data.FirstOrDefault(kv => kv.Key == "version").Value, req.Document.TextVersion)));
            foreach (var d in resp.Items) _out.WriteLine($"{d.Code} {d.Line + 1}:{d.Column + 1} len {d.Length} {d.Message}");
            _out.WriteLine($"checked {resp.Checked}, uncertain {resp.Uncertain}, {resp.ElapsedMs} ms");
            return resp;
        }

        /// <summary>Asserts that exactly the marked identifiers are reported, with the marked code.</summary>
        private CodeDiagnosticsResponse Expect(string relative, string text)
        {
            var expected = new List<(int Line, int Column, string Code, string Word)>();
            var lines = text.Replace("\r\n", "\n").Split('\n');
            for (int l = 0; l < lines.Length; l++)
            {
                foreach (Match m in Marker.Matches(lines[l]))
                {
                    int c = m.Index + m.Length;
                    int e = c;
                    while (e < lines[l].Length && (char.IsLetterOrDigit(lines[l][e]) || lines[l][e] == '_')) e++;
                    expected.Add((l, c, m.Groups[1].Value, lines[l].Substring(c, e - c)));
                }
            }
            var resp = Run(relative, text);
            var actual = resp.Items.Select(d => (Line: d.Line, Column: d.Column, Code: d.Code, Word: lines[d.Line].Substring(d.Column, d.Length))).ToList();
            var missing = expected.Except(actual).ToList();
            var extra = actual.Except(expected).ToList();
            Assert.True(missing.Count == 0 && extra.Count == 0,
                "missing: " + string.Join("; ", missing.Select(x => $"{x.Code} '{x.Word}' {x.Line + 1}:{x.Column + 1}")) +
                " | unexpected (false positives): " + string.Join("; ", extra.Select(x => $"{x.Code} '{x.Word}' {x.Line + 1}:{x.Column + 1}")));
            return resp;
        }

        // ================================================================== UE0001 unknown member: reported

        [Fact]
        public void UnknownMember_Reported_OnFullyKnownTypes()
        {
            var resp = Expect("Game/Private/MyActor.cpp", @"#include ""MyActor.h""
void AMyActor::Fire()
{
	this->/*@UE0001*/Missing();
	Buddy->/*@UE0001*/Nope = 1;
	GetBuddy()->/*@UE0001*/Nope2();
	AMyActor* A = nullptr;
	A->/*@UE0001*/Nope3;
	FPlain P;
	P./*@UE0001*/Q = 2;
	P.Owner->/*@UE0001*/Nope4();
	GetOwner()->/*@UE0001*/NotOnActor();
	this->Buddy->/*@UE0001*/Chain();
}
void AMyActor::Use(FMyData& D)
{
	D./*@UE0001*/Valu = 3;
}
");
            Assert.Equal(CodeDiagnosticSeverity.Error, resp.Items[0].Severity);
            Assert.Equal("UE0001", resp.Items[0].Code);
            Assert.Equal(new[] { "Missing", "AMyActor" }, resp.Items[0].Args);
            Assert.Contains("Missing", resp.Items[0].Message);
        }

        // ================================================================== UE0001: must not report

        [Fact]
        public void UnknownMember_KnownMembers_NotReported()
        {
            Expect("Game/Private/MyActor.cpp", @"#include ""MyActor.h""
void AMyActor::Fire()
{
	this->Ammo = 1;
	Buddy->Fire();
	GetBuddy()->Ammo++;
	GetOwner()->BeginPlay();
	AMyActor* A = nullptr;
	A->GetClass();             // base of base of base
	A->GetName().Len();        // chain through an engine type
	A->StaticClass();          // GENERATED_BODY injected
	A->GetActorLocation().X = 1;
	A->CustomTimeDilation = 2;
	A->Owner->BeginPlay();
	FPlain P;
	P.P = 1;
	P.Owner->Fire();
	Super::BeginPlay();
	AMyActor::StaticClass();
	OnFired.Broadcast(1);      // delegate: not a record
	this->ServerFire_Implementation();
	GetClass()->GetDefaultObject();
}
void AMyActor::Use(FMyData& D)
{
	D.Value = 3;
	D.StaticStruct();
}
");
        }

        [Fact]
        public void UnknownMember_Uncertain_NotReported()
        {
            var resp = Expect("Game/Private/Uncertain.cpp", @"#include ""MyActor.h""
#define SOME_MACRO(x) 0
template<class T> void Generic(T* t) { t->Whatever(); }
template<class T> struct TBox { T* Ptr; void Go() { Ptr->Anything(); this->Nope(); } };
void AMyActor::Fire()
{
	auto X = GetBuddy();
	X->Nope();                            // auto without a known factory
	SOME_MACRO(Buddy->Nope);               // macro argument
	FHasUnknownBase H;
	H.Zzz = 1;                             // unindexed base
	FUsesTemplateBase U;
	U.Value = 1;                           // template base
	U.Zzz = 1;
	FMacroInjected M;
	M.Magic = 1;                           // members declared by an unknown macro
	FWithUnion W;
	W.F = 1.f;                             // anonymous union members
	FAliasData AD;
	AD.Zzz = 1;                            // alias
	FNotExist N;
	N.Zzz = 1;                             // unknown type
	FDup Dup;
	Dup.C = 1;                             // ambiguous type name
	TSharedPtr<FPlain> SP;
	SP.Zzz();                              // wrapper API
	TWeakPtr<FPlain> WP;
	WP->Zzz;                               // no operator->
	TArray<AMyActor*> Arr;
	Arr.Zzz();                             // container
	Pick()->Zzz;                           // overloads with different return types
	auto L = [](FPlain* Buddy) { Buddy->Zzz = 1; };   // lambda parameter shadows the field
	Unknown()->Zzz();                      // free / unknown function
	Game::Thing.Zzz = 1;                   // qualified
#if 0
	this->InDeadCode();
#endif
	this->ServerFire_Validate();
	this->execServerFire();
}
");
            Assert.True(resp.Uncertain >= 15, "uncertain " + resp.Uncertain);
        }

        [Fact]
        public void UnknownMember_SmartPointerArrow_Reported()
        {
            Expect("Game/Private/Smart.cpp", @"#include ""MyActor.h""
void AMyActor::Fire()
{
	TSharedPtr<FPlain> SP;
	SP->/*@UE0001*/Zzz = 1;
	SP->P = 1;
	Buddy.Get();
}
");
        }

        [Fact]
        public void UnknownMember_WordBeingTyped_NotReported()
        {
            string text = "#include \"MyActor.h\"\nvoid AMyActor::Fire()\n{\n\tthis->Amm\n}\n";
            var resp = Run("Game/Private/Typing.cpp", text, 3, 10);
            Assert.Empty(resp.Items);
            resp = Run("Game/Private/Typing.cpp", text, 0, 0);
            Assert.Single(resp.Items);
        }

        [Fact]
        public void UnsavedDeclaringHeader_NotReported()
        {
            string text = "#include \"MyActor.h\"\nvoid AMyActor::Fire()\n{\n\tthis->JustAdded();\n}\n";
            var req = new CodeDiagnosticsRequest
            {
                Document = new CaretRequest { FilePath = _f.PathOf("Game/Private/Unsaved.cpp"), Text = text, TextVersion = 3 },
                StartLine = 0,
                EndLine = 10,
                UnsavedFiles = new List<string> { _f.PathOf("Game/MyActor.h").ToUpperInvariant() },
            };
            Assert.Empty(_f.Service.GetDiagnostics(req, CancellationToken.None).Items);
            req.UnsavedFiles = new List<string> { _f.PathOf("Game/Other.h") };
            Assert.Single(_f.Service.GetDiagnostics(req, CancellationToken.None).Items);
        }

        [Fact]
        public void EngineFiles_NeverDiagnosed()
        {
            var resp = Run("Engine/Private/EngineThing.cpp", "#include \"MyActor.h\"\nvoid AMyActor::Fire()\n{\n\tthis->Missing();\n}\n");
            Assert.Empty(resp.Items);
        }

        [Fact]
        public void RangeOnly()
        {
            string text = "#include \"MyActor.h\"\nvoid AMyActor::Fire()\n{\n\tthis->Missing();\n\n\n\n\n\tthis->Missing2();\n}\n";
            var req = new CodeDiagnosticsRequest { Document = new CaretRequest { FilePath = _f.PathOf("Game/Private/Range.cpp"), Text = text, TextVersion = 7 }, StartLine = 6, EndLine = 9 };
            var resp = _f.Service.GetDiagnostics(req, CancellationToken.None);
            Assert.Single(resp.Items);
            Assert.Equal(8, resp.Items[0].Line);
            Assert.Equal(7, resp.TextVersion);
            Assert.Equal(6, resp.StartLine);
        }

        // ================================================================== UE0003 GENERATED_BODY missing

        [Fact]
        public void MissingGeneratedBody_Reported()
        {
            var resp = Expect("Game/Public/Broken.h", @"#pragma once
#include ""MyActor.h""
#include ""Broken.generated.h""
UCLASS()
class MYGAME_API /*@UE0003*/ABroken : public AActor
{
public:
	void X();
};
USTRUCT(BlueprintType)
struct /*@UE0003*/FBrokenData
{
	int32 V;
};
");
            Assert.Equal(new[] { "UCLASS", "ABroken" }, resp.Items[0].Args);
            Assert.Equal(new[] { "USTRUCT", "FBrokenData" }, resp.Items[1].Args);
        }

        [Fact]
        public void MissingGeneratedBody_NotReported()
        {
            Expect("Game/Public/Fine.h", @"#pragma once
#include ""MyActor.h""
#include ""Fine.generated.h""
UCLASS()
class MYGAME_API AFine : public AActor
{
	GENERATED_BODY()
};
UCLASS()
class MYGAME_API ALegacy : public AActor
{
	GENERATED_UCLASS_BODY()
};
USTRUCT()
struct FFineData
{
	GENERATED_USTRUCT_BODY()
	int32 V;
};
UCLASS()
class MYGAME_API AWrapped : public AActor
{
	MY_GENERATED_WRAPPER()
};
struct FNotReflected
{
	int32 V;
};
#if 0
UCLASS()
class ADead : public AActor
{
};
#endif
");
            // .cpp files are not seen by UHT
            var resp = Run("Game/Private/InCpp.cpp", "UCLASS()\nclass AInCpp : public AActor\n{\n};\n");
            Assert.Empty(resp.Items);
        }

        [Fact]
        public void Logs_DiagnosticsEvent()
        {
            using var sink = new CapturingLogSink();
            Run("Game/Private/Log.cpp", "#include \"MyActor.h\"\nvoid AMyActor::Fire()\n{\n\tthis->Missing();\n\tFNotExist N;\n\tN.Zzz = 1;\n}\n");
            string dump = sink.Dump(x => x.Event == "intellisense.diagnostics");
            Assert.Contains("UE0001=1", dump);
            Assert.Contains("uncertain=", dump);
            Assert.Contains("unknown-type", dump);
        }
    }
}
