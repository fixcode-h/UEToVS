using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
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
    /// <summary>A small UE-like tree for the v0.4.2 UE rules: engine stubs, a replicated game class, gameplay tags and /Game assets on disk.</summary>
    public sealed class UeRuleFixture : IDisposable
    {
        public static readonly Dictionary<string, string> Sources = new Dictionary<string, string>
        {
            ["Engine/CoreUObject.h"] = @"#pragma once
class UClass;
class COREUOBJECT_API UObject
{
public:
	virtual void GetLifetimeReplicatedProps(TArray<FLifetimeProperty>& OutLifetimeProps) const;
};
",
            ["Engine/Actor.h"] = @"#pragma once
#include ""CoreUObject.h""
struct FVector { float X; float Y; float Z; };
UCLASS()
class ENGINE_API AActor : public UObject
{
	GENERATED_BODY()
public:
	virtual void BeginPlay();
	UFUNCTION()
	void OnRep_Owner();
	void OnRep_Plain();
};
",
            ["Game/Weapon.h"] = @"#pragma once
#include ""Actor.h""
#include ""Weapon.generated.h""

UCLASS()
class MYGAME_API AWeapon : public AActor
{
	GENERATED_BODY()
public:
	UFUNCTION(Server, Reliable)
	void ServerFire();
	UFUNCTION(Client, Unreliable)
	void ClientHit();
	UFUNCTION(BlueprintNativeEvent, Category = ""Weapon"")
	void OnEquipped();
	UFUNCTION(BlueprintCallable, Category = ""Server"")
	void Reload();
	UFUNCTION(NetMulticast, Reliable)
	void MulticastBoom();
	void MulticastBoom(int32 Power);
	UFUNCTION(Server, Reliable, CustomThunk)
	void ServerThunk();
	void PlainHelper();

	UPROPERTY(ReplicatedUsing = OnRep_Ammo)
	int32 Ammo;
	UPROPERTY(Replicated)
	float Heat;
	UPROPERTY(Replicated)
	bool bLoaded;
	UPROPERTY(ReplicatedUsing = OnRep_Owner)
	int32 OwnerId;
	UFUNCTION()
	void OnRep_Ammo();
	UPROPERTY(EditAnywhere)
	int32 NotReplicated;
#if WITH_EDITORONLY_DATA
	UPROPERTY(Replicated)
	int32 EditorOnly;
#endif
};

UCLASS()
class MYGAME_API AOtherWeapon : public AActor
{
	GENERATED_BODY()
public:
	UPROPERTY(Replicated)
	int32 Charges;
};
",
        };

        public static readonly string[] AssetObjectPaths =
        {
            "/Game/Weapons/SM_Rifle.SM_Rifle",
            "/Game/Weapons/BP_Rifle.BP_Rifle",
            "/Game/Maps/L_Arena.L_Arena",
        };

        public UeRuleFixture()
        {
            Root = Path.Combine(Path.GetTempPath(), "uetovs-uerules", Guid.NewGuid().ToString("N"));
            Backend = new SegmentedIndexBackend();
            Backend.TryLoad(Path.Combine(Root, ".index"), "ue-rules");
            var parser = new SyntaxFileParser();
            foreach (var kv in Sources)
            {
                string path = PathOf(kv.Key);
                Directory.CreateDirectory(Path.GetDirectoryName(path));
                File.WriteAllText(path, kv.Value);
                bool engine = kv.Key.StartsWith("Engine/", StringComparison.Ordinal);
                var listed = new ListedFile { Path = path, Name = Path.GetFileName(path), Module = engine ? "Engine" : "MyGame", Origin = engine ? ItemOrigin.Engine : ItemOrigin.Project, Kind = SourceFileKind.Header };
                Files.Upsert(listed);
                var ctx = new FileContext { Path = path, Module = listed.Module, Origin = listed.Origin, Kind = listed.Kind };
                Backend.UpdateParsed(path, parser.Parse(ctx, File.ReadAllBytes(path), CancellationToken.None));
            }
            string ini = PathOf("Config/DefaultGameplayTags.ini");
            Directory.CreateDirectory(Path.GetDirectoryName(ini));
            File.WriteAllText(ini, "[/Script/GameplayTags.GameplayTagsSettings]\n+GameplayTagList=(Tag=\"Weapon.Fire.Auto\",DevComment=\"\")\n+GameplayTagList=(Tag=\"Weapon.Reload\",DevComment=\"\")\n");
            Files.Upsert(new ListedFile { Path = ini, Name = "DefaultGameplayTags.ini", Origin = ItemOrigin.Project, Kind = SourceFileKind.Config });
            foreach (var op in AssetObjectPaths)
            {
                string pkg = op.Substring(0, op.LastIndexOf('.'));
                string file = PathOf("Content" + pkg.Substring(5) + (pkg.Contains("/Maps/") ? ".umap" : ".uasset"));
                Directory.CreateDirectory(Path.GetDirectoryName(file));
                File.WriteAllText(file, string.Empty);
                Assets.Upsert(new AssetItem { Name = pkg.Substring(pkg.LastIndexOf('/') + 1), ObjectPath = op, FilePath = file, Origin = ItemOrigin.Project, ClassName = pkg.Contains("/Maps/") ? "World" : null });
            }
            // on disk but not (yet) in the table: the table lags behind the editor, must not be reported
            File.WriteAllText(PathOf("Content/Weapons/SM_JustSaved.uasset"), string.Empty);
            Directory.CreateDirectory(PathOf("Content/Empty"));
            Service = new IntelliSenseService(Backend, Files, () => Ready, assets: Assets);
        }

        public bool Ready { get; set; } = true;
        public string Root { get; }
        public SegmentedIndexBackend Backend { get; }
        public FileTable Files { get; } = new FileTable();
        public AssetTable Assets { get; } = new AssetTable();
        public IntelliSenseService Service { get; }

        public string PathOf(string relative) => Path.Combine(Root, relative.Replace('/', Path.DirectorySeparatorChar));

        public void Dispose()
        {
            Backend.Dispose();
            try { Directory.Delete(Root, true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }
    }

    /// <summary>
    /// UE0004…UE0010. Same convention as <see cref="DiagnosticsTests"/>: <c>/*@UE0004*/</c> right before the identifier (or string
    /// content) that must be reported; nothing else may be reported.
    /// </summary>
    public sealed class UeRuleDiagnosticsTests : IClassFixture<UeRuleFixture>
    {
        private static readonly Regex Marker = new Regex(@"/\*@(UE\d{4})\*/", RegexOptions.Compiled);
        private static long _version = 5000;
        private readonly UeRuleFixture _f;
        private readonly ITestOutputHelper _out;

        public UeRuleDiagnosticsTests(UeRuleFixture f, ITestOutputHelper output)
        {
            _f = f;
            _out = output;
        }

        private CodeDiagnosticsResponse Run(string relative, string text, int caretLine = 0, int caretColumn = 0, List<string> unsaved = null)
        {
            var req = new CodeDiagnosticsRequest
            {
                Document = new CaretRequest { FilePath = _f.PathOf(relative), Text = text, TextVersion = Interlocked.Increment(ref _version), Line = caretLine, Column = caretColumn },
                StartLine = 0,
                EndLine = 100000,
                UnsavedFiles = unsaved ?? new List<string>(),
            };
            using var sink = new CapturingLogSink();
            var resp = _f.Service.GetDiagnostics(req, CancellationToken.None);
            _out.WriteLine(sink.Dump(x => x.Event == "intellisense.diagnostics"));
            foreach (var d in resp.Items) _out.WriteLine($"{d.Code} {d.Line + 1}:{d.Column + 1} len {d.Length} {d.Message}");
            return resp;
        }

        private CodeDiagnosticsResponse Expect(string relative, string text, params string[] codes)
        {
            var expected = new List<(int Line, int Column, string Code)>();
            var lines = text.Replace("\r\n", "\n").Split('\n');
            for (int l = 0; l < lines.Length; l++)
                foreach (Match m in Marker.Matches(lines[l]))
                    expected.Add((l, m.Index + m.Length, m.Groups[1].Value));
            var resp = Run(relative, text);
            var only = new HashSet<string>(codes);
            var actual = resp.Items.Where(d => only.Count == 0 || only.Contains(d.Code)).Select(d => (d.Line, d.Column, d.Code)).ToList();
            var missing = expected.Except(actual).ToList();
            var extra = actual.Except(expected).ToList();
            Assert.True(missing.Count == 0 && extra.Count == 0,
                "missing: " + string.Join("; ", missing.Select(x => $"{x.Code} {x.Line + 1}:{x.Column + 1}")) +
                " | unexpected: " + string.Join("; ", extra.Select(x => $"{x.Code} {x.Line + 1}:{x.Column + 1}")));
            return resp;
        }

        // ================================================================== UE0004

        [Fact]
        public void GeneratedBody_HandWritten_Reported()
        {
            var resp = Expect("Game/Private/Weapon.cpp", @"#include ""Weapon.h""
void AWeapon::/*@UE0004*/ServerFire() {}
void AWeapon::ServerFire_Implementation() {}
void AWeapon::/*@UE0004*/ClientHit() {}
void AWeapon::/*@UE0004*/OnEquipped() {}
void AWeapon::OnEquipped_Implementation() {}
void AWeapon::Reload() {}                 // BlueprintCallable, Category=""Server"": not an RPC
void AWeapon::MulticastBoom(int32 Power) {} // overloaded name: uncertain
void AWeapon::ServerThunk() {}            // CustomThunk
void AWeapon::PlainHelper() {}
void AWeapon::NotDeclared() {}
void Free() {}
", "UE0004");
            var d = resp.Items.First(x => x.Code == "UE0004");
            Assert.Equal(new[] { "ServerFire", "Server", "AWeapon" }, d.Args);
            Assert.Equal(CodeDiagnosticSeverity.Error, d.Severity);
        }

        [Fact]
        public void GeneratedBody_InlineInClass_Reported_AndUnsavedHeaderSkipped()
        {
            Expect("Game/Inline.h", @"#pragma once
#include ""Actor.h""
UCLASS()
class AInline : public AActor
{
	GENERATED_BODY()
public:
	UFUNCTION(Server, Reliable)
	void /*@UE0004*/ServerGo() {}
	UFUNCTION(BlueprintCallable)
	void Fine() {}
};
", "UE0004");
            var resp = Run("Game/Private/Weapon2.cpp", "#include \"Weapon.h\"\nvoid AWeapon::ServerFire() {}\n", unsaved: new List<string> { _f.PathOf("Game/Weapon.h") });
            Assert.DoesNotContain(resp.Items, x => x.Code == "UE0004");
        }

        // ================================================================== UE0005

        [Fact]
        public void RepNotify_MissingOrNotUFunction_Reported()
        {
            var resp = Expect("Game/Rep.h", @"#pragma once
#include ""Actor.h""
UCLASS()
class ARep : public AActor
{
	GENERATED_BODY()
public:
	UPROPERTY(ReplicatedUsing = OnRep_Good)
	int32 Good;
	UPROPERTY(ReplicatedUsing = OnRep_Missing)
	int32 /*@UE0005*/Bad;
	UPROPERTY(ReplicatedUsing = ""OnRep_Plain"")
	int32 /*@UE0005*/NotUFunction;
	UPROPERTY(ReplicatedUsing = OnRep_Owner)
	int32 Inherited;
	UPROPERTY(Replicated, meta = (ReplicatedUsing = Whatever))
	int32 InMeta;
	UFUNCTION()
	void OnRep_Good();
};
", "UE0005");
            var bad = resp.Items.Where(x => x.Code == "UE0005").ToList();
            Assert.Equal("missing", bad[0].Args[3]);
            Assert.Equal("not-ufunction", bad[1].Args[3]);
        }

        [Fact]
        public void RepNotify_UncertainClass_NotReported()
        {
            Expect("Game/Rep2.h", @"#pragma once
#include ""Actor.h""
UCLASS()
class ARep2 : public AUnindexedBase
{
	GENERATED_BODY()
public:
	UPROPERTY(ReplicatedUsing = OnRep_Missing)
	int32 Bad;
};
template<class T> class TNot {};
", "UE0005");
        }

        // ================================================================== UE0006

        [Fact]
        public void Registrations_Missing_Reported()
        {
            var resp = Expect("Game/Private/WeaponNet.cpp", @"#include ""Weapon.h""
void AWeapon::/*@UE0006*/GetLifetimeReplicatedProps(TArray<FLifetimeProperty>& OutLifetimeProps) const
{
	Super::GetLifetimeReplicatedProps(OutLifetimeProps);
	FDoRepLifetimeParams Params;
	Params.Condition = COND_OwnerOnly;
	DOREPLIFETIME(AWeapon, Ammo);
	DOREPLIFETIME_WITH_PARAMS_FAST(AWeapon, Heat, Params);
	DISABLE_REPLICATED_PROPERTY(AWeapon, OwnerId);
}
", "UE0006");
            var d = resp.Items.Single(x => x.Code == "UE0006");
            Assert.Equal(new[] { "bLoaded", "AWeapon" }, d.Args);
        }

        [Theory]
        [InlineData("for (int32 i = 0; i < 1; ++i) { DOREPLIFETIME(AWeapon, Ammo); }")]
        [InlineData("RegisterMore(OutLifetimeProps);")]
        [InlineData("MY_REPLICATION_MACRO(AWeapon);")]
        [InlineData("#if WITH_SOMETHING\n\tDOREPLIFETIME(AWeapon, bLoaded);\n#endif")]
        [InlineData("DISABLE_ALL_CLASS_REPLICATED_PROPERTIES(AWeapon, EFieldIteratorFlags::IncludeSuper);")]
        [InlineData("DOREPLIFETIME(AWeapon, PROP_NAME(bLoaded));")]
        public void Registrations_Uncertain_NotReported(string statement)
        {
            Expect("Game/Private/WeaponNet2.cpp", @"#include ""Weapon.h""
void AWeapon::GetLifetimeReplicatedProps(TArray<FLifetimeProperty>& OutLifetimeProps) const
{
	Super::GetLifetimeReplicatedProps(OutLifetimeProps);
	" + statement + @"
}
", "UE0006");
        }

        [Fact]
        public void Registrations_AllRegistered_NotReported()
        {
            Expect("Game/Private/Other.cpp", @"#include ""Weapon.h""
void AOtherWeapon::GetLifetimeReplicatedProps(TArray<FLifetimeProperty>& OutLifetimeProps) const
{
	Super::GetLifetimeReplicatedProps(OutLifetimeProps);
	DOREPLIFETIME_CONDITION(AOtherWeapon, Charges, COND_OwnerOnly);
}
", "UE0006");
        }

        // ================================================================== UE0007

        [Fact]
        public void GameplayTags_UnknownChild_Warned()
        {
            var resp = Run("Game/Private/Tags.cpp", @"#include ""Weapon.h""
void Use()
{
	FGameplayTag A = FGameplayTag::RequestGameplayTag(FName(""Weapon.Fire.Auto""));
	FGameplayTag B = FGameplayTag::RequestGameplayTag(FName(""Weapon.Fire""));
	FGameplayTag C = FGameplayTag::RequestGameplayTag(FName(""Weapon.Fire.Burst""));
	FGameplayTag D = FGameplayTag::RequestGameplayTag(FName(""Weapon.Reloadd""));
	FGameplayTag E = FGameplayTag::RequestGameplayTag(FName(""Unknown.Root.Tag""));
	FGameplayTag F = FGameplayTag::RequestGameplayTag(FName(""Weapon.Nope""), false);
	FGameplayTag G = FGameplayTag::RequestGameplayTag(FName(""Weapon.%s""));
	UE_LOG(LogTemp, Log, TEXT(""Weapon.Fire.Burst""));
}
");
            Assert.Equal(new[] { "Weapon.Fire.Burst", "Weapon.Reloadd" }, resp.Items.Where(x => x.Code == "UE0007").Select(x => x.Args[0]));
            Assert.Equal(new[] { 5, 6 }, resp.Items.Where(x => x.Code == "UE0007").Select(x => x.Line));
            Assert.All(resp.Items.Where(x => x.Code == "UE0007"), x => Assert.Equal(CodeDiagnosticSeverity.Warning, x.Severity));
        }

        // ================================================================== UE0008

        [Fact]
        public void AssetPaths_Missing_Warned()
        {
            var resp = Run("Game/Private/Assets.cpp", @"#include ""Weapon.h""
void Load()
{
	static ConstructorHelpers::FObjectFinder<UStaticMesh> A(TEXT(""/Game/Weapons/SM_Rifle.SM_Rifle""));
	static ConstructorHelpers::FObjectFinder<UStaticMesh> B(TEXT(""/Game/Weapons/SM_Rilfe.SM_Rilfe""));
	static ConstructorHelpers::FClassFinder<AActor> C(TEXT(""/Game/Weapons/BP_Rifle""));
	FSoftObjectPath D(TEXT(""/Game/Maps/L_Arena.L_Arena""));
	FSoftObjectPath E(TEXT(""/Game/Weapons/SM_JustSaved.SM_JustSaved""));
	FSoftObjectPath F(TEXT(""/Game/Empty""));
	FSoftObjectPath G(TEXT(""/Game/Weapons""));
	FString H = FString(TEXT(""/Game/Weapons/"")) + Name;
	FString I = TEXT(""/Game/Weapons/SM_"") + Name;
	FString J = FString::Printf(TEXT(""/Game/Weapons/%s""), *Name);
	FSoftObjectPath K(TEXT(""/MyPlugin/Nope.Nope""));
	FSoftObjectPath L(TEXT(""/Game/Nope/Missing.Missing:Sub""));
}
");
            Assert.Equal(new[] { "/Game/Weapons/SM_Rilfe", "/Game/Nope/Missing" }, resp.Items.Where(x => x.Code == "UE0008").Select(x => x.Args[0]));
            var first = resp.Items.First(x => x.Code == "UE0008");
            Assert.Equal(4, first.Line);
            Assert.Equal("/Game/Weapons/SM_Rilfe.SM_Rilfe".Length, first.Length);
        }

        [Fact]
        public void AssetPaths_NotReady_NotReported()
        {
            _f.Ready = false;
            try
            {
                var resp = Run("Game/Private/Assets2.cpp", "void Load()\n{\n\tFSoftObjectPath B(TEXT(\"/Game/Weapons/Missing.Missing\"));\n\tFGameplayTag C = FGameplayTag::RequestGameplayTag(FName(\"Weapon.Fire.Burst\"));\n}\n");
                Assert.Empty(resp.Items);
            }
            finally
            {
                _f.Ready = true;
            }
        }

        [Fact]
        public void StringRules_CaretInsideLiteral_NotReported()
        {
            string text = "void Load()\n{\n\tFSoftObjectPath B(TEXT(\"/Game/Weapons/Missing.Missing\"));\n}\n";
            Assert.Empty(Run("Game/Private/Assets3.cpp", text, 2, 30).Items);
            Assert.Single(Run("Game/Private/Assets3.cpp", text).Items);
        }

        // ================================================================== UE0009 / UE0010

        [Fact]
        public void ReflectedTypes_Unsupported_Reported()
        {
            var resp = Expect("Game/Types.h", @"#pragma once
#include ""Actor.h""
UCLASS()
class ATypes : public AActor
{
	GENERATED_BODY()
public:
	UPROPERTY()
	TSharedPtr<FVector> /*@UE0009*/Shared;
	UPROPERTY()
	FVector* /*@UE0009*/VecPtr;
	UPROPERTY()
	int32* /*@UE0009*/IntPtr;
	UPROPERTY()
	AActor* Fine;
	UPROPERTY()
	TObjectPtr<AActor> AlsoFine;
	UPROPERTY()
	uint32 NotBlueprintButFine;
	TSharedPtr<FVector> NotReflected;

	UFUNCTION(BlueprintCallable)
	void Call(int32 A, uint32 /*@UE0010*/B, const FVector& C, FVector* /*@UE0010*/D, AActor* E, const uint16& /*@UE0010*/F);
	UFUNCTION(Server, Reliable)
	void ServerSend(uint32 Fine32, TSharedPtr<int32> /*@UE0010*/Bad);
	UFUNCTION(BlueprintPure)
	int32 Pure(int8 /*@UE0010*/Small) const;
	UFUNCTION(BlueprintCallable, CustomThunk)
	void Thunk(FVector* Anything);
	void NotUFunction(FVector* P);
};
", "UE0009", "UE0010");
            Assert.Contains(resp.Items, x => x.Code == "UE0010" && x.Args[3] == "blueprint");
            Assert.Contains(resp.Items, x => x.Code == "UE0010" && x.Args[3] == "pointer");
            Assert.Contains(resp.Items, x => x.Code == "UE0010" && x.Args[3] == "unsupported");
        }

        [Fact]
        public void UeRules_Logged()
        {
            using var sink = new CapturingLogSink();
            Run("Game/Private/Log2.cpp", "#include \"Weapon.h\"\nvoid AWeapon::ServerFire() {}\n");
            Assert.Contains(sink.Events, x => x.Event == "intellisense.diagnostics" && x.Data.Any(kv => kv.Key == "ueRules" && (kv.Value as string ?? string.Empty).Contains("UE0004=1")));
        }
    }

    public sealed class UhtRulesTests
    {
        [Fact]
        public void Specifiers_TopLevelKeys()
        {
            var s = UhtRules.Specifiers("Server, Reliable, Category=\"Server, Weapon\", meta=(ReplicatedUsing=X), ReplicatedUsing = OnRep_A");
            Assert.True(UhtRules.HasKey(s, "Server"));
            Assert.False(UhtRules.HasKey(s, "Weapon"));
            Assert.Equal("OnRep_A", UhtRules.Value(s, "ReplicatedUsing"));
            Assert.Equal("Server", UhtRules.GeneratedBodySpecifier(UhtRules.Specifiers("Category=Server, Server")));
            Assert.Null(UhtRules.GeneratedBodySpecifier(UhtRules.Specifiers("BlueprintCallable, Category=Server")));
        }

        [Fact]
        public void Parameters_Split()
        {
            var p = UhtRules.Parameters("int32 A, TMap<int32, FString> B = {}, const FVector& C, FVector*, uint32");
            Assert.Equal(5, p.Count);
            Assert.Equal("TMap<int32,FString>", p[1].Type);
            Assert.Equal("B", p[1].Name);
            Assert.Equal("FVector&", p[2].Type);
            Assert.Null(p[3].Name);
            Assert.Equal("FVector*", p[3].Type);
            Assert.Equal("uint32", p[4].Type);
        }

        [Theory]
        [InlineData("TSharedPtr<FX>", "unsupported")]
        [InlineData("std::vector<int>", "unsupported")]
        [InlineData("const FVector*", "pointer")]
        [InlineData("float *", "pointer")]
        [InlineData("AActor*", null)]
        [InlineData("UObject*", null)]
        [InlineData("TObjectPtr<UObject>", null)]
        [InlineData("FVector", null)]
        [InlineData("const FVector&", null)]
        [InlineData("TArray<FVector*>", null)]
        public void ReflectedTypeProblem(string type, string expected) => Assert.Equal(expected, UhtRules.ReflectedTypeProblem(type));

        [Fact]
        public void TagsAndPaths()
        {
            Assert.True(UhtRules.IsPlainTag("A.B_c.D1"));
            Assert.False(UhtRules.IsPlainTag("A..B"));
            Assert.False(UhtRules.IsPlainTag("A.%s"));
            Assert.Equal("/Game/A/B", UhtRules.GamePackagePath("/Game/A/B.B_C"));
            Assert.Equal("/Game/A/B", UhtRules.GamePackagePath("/Game/A/B.B:Sub"));
            Assert.Null(UhtRules.GamePackagePath("/Game/A/"));
            Assert.Null(UhtRules.GamePackagePath("/Game/A/%s"));
            Assert.Null(UhtRules.GamePackagePath("/MyPlugin/A"));
        }
    }
}
