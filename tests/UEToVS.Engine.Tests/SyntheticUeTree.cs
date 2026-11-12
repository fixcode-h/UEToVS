using System;
using System.IO;
using System.Text;

namespace UEToVS.Engine.Tests
{
    /// <summary>
    /// Generates a large UE-shaped tree for indexing benchmarks: an engine with N runtime modules (Public headers,
    /// Classes headers, Private .cpp), a ThirdParty folder, Config .ini files, an Intermediate folder that must be
    /// skipped, and a game project with its own modules. File sizes vary deterministically (seeded) between ~1 KB and
    /// ~40 KB, roughly like real engine sources.
    /// </summary>
    public sealed class SyntheticUeTree : IDisposable
    {
        public SyntheticUeTree(int engineFiles, int projectFiles, int seed = 1234, string root = null)
        {
            Root = root ?? Path.Combine(Path.GetTempPath(), "uetovs-bench", Guid.NewGuid().ToString("N"));
            EngineRoot = Path.Combine(Root, "UE");
            ProjectRoot = Path.Combine(Root, "Game");
            ProjectFile = Path.Combine(ProjectRoot, "BenchGame.uproject");
            var rnd = new Random(seed);

            Write("UE/Engine/Build/Build.version", "{\"MajorVersion\":5,\"MinorVersion\":4,\"PatchVersion\":2,\"Changelist\":777,\"CompatibleChangelist\":700,\"BranchName\":\"++UE5+Release-5.4\"}");
            for (int i = 0; i < 40; i++) Write($"UE/Engine/Config/Base{i}.ini", "[Core.System]\nPaths=../../Content\n");
            for (int i = 0; i < 200; i++) Write($"UE/Engine/Intermediate/Build/Gen{i}.generated.h", "class FGenerated {};\n");

            const int filesPerModule = 80;
            int modules = Math.Max(1, engineFiles / filesPerModule);
            int written = 0;
            for (int m = 0; m < modules && written < engineFiles; m++)
            {
                string mod = "Mod" + m;
                string dir = $"UE/Engine/Source/Runtime/{mod}";
                Write($"{dir}/{mod}.Build.cs", $"public class {mod} : ModuleRules {{ public {mod}(ReadOnlyTargetRules T) : base(T) {{ }} }}");
                for (int f = 0; f < filesPerModule && written < engineFiles; f++, written++)
                {
                    int size = RandomSize(rnd);
                    int r = f % 10;
                    if (r < 4) Write($"{dir}/Public/{mod}Type{f}.h", Header(mod, f, size));
                    else if (r < 5) Write($"{dir}/Classes/{mod}Class{f}.h", Header(mod, f, size));
                    else if (r < 6) Write($"{dir}/Private/{mod}Internal{f}.h", Header(mod, f, size));
                    else Write($"{dir}/Private/{mod}Type{f}.cpp", Source(mod, f, size));
                }
            }
            Write("UE/Engine/Source/ThirdParty/zlib/zlib.Build.cs", "public class zlib : ModuleRules { public zlib(ReadOnlyTargetRules T) : base(T) { } }");
            for (int i = 0; i < 20; i++) Write($"UE/Engine/Source/ThirdParty/zlib/include/z{i}.h", "struct z_stream_" + i + " { int avail_in; };\n");

            Write("Game/BenchGame.uproject", "{\n  \"FileVersion\": 3,\n  \"EngineAssociation\": \"../UE\",\n  \"Modules\": [ { \"Name\": \"BenchGame\", \"Type\": \"Runtime\" } ]\n}");
            Write("Game/Config/DefaultEngine.ini", "[/Script/Engine.Engine]\n");
            Write("Game/Source/BenchGame/BenchGame.Build.cs", "public class BenchGame : ModuleRules { public BenchGame(ReadOnlyTargetRules T) : base(T) { } }");
            for (int f = 0; f < projectFiles; f++)
            {
                int size = RandomSize(rnd);
                if (f % 2 == 0) Write($"Game/Source/BenchGame/Public/Game{f}.h", Header("BenchGame", f, size));
                else Write($"Game/Source/BenchGame/Private/Game{f}.cpp", Source("BenchGame", f, size));
            }
            for (int i = 0; i < 50; i++) Write($"Game/Intermediate/Build/Game{i}.generated.h", "class FGen {};\n");
            for (int i = 0; i < 30; i++) Write($"Game/Content/Blueprints/BP_Thing{i}.uasset", "");
            Write("Game/Content/Maps/L_Bench.umap", "");
        }

        public string Root { get; }
        public string EngineRoot { get; }
        public string ProjectRoot { get; }
        public string ProjectFile { get; }

        /// <summary>Skewed: mostly 2–15 KB, a tail up to ~40 KB, a few large ones (~120 KB).</summary>
        private static int RandomSize(Random rnd)
        {
            double u = rnd.NextDouble();
            if (u < 0.01) return 100_000 + rnd.Next(40_000);
            if (u < 0.15) return 15_000 + rnd.Next(25_000);
            return 1_500 + rnd.Next(13_500);
        }

        public static string Header(string mod, int n, int targetBytes)
        {
            var sb = new StringBuilder(targetBytes + 2048);
            string api = mod.ToUpperInvariant() + "_API";
            sb.Append("#pragma once\n#include \"CoreMinimal.h\"\n#include \"UObject/Object.h\"\n#include \"").Append(mod).Append("Type").Append(n).Append(".generated.h\"\n\n");
            int i = 0;
            while (sb.Length < targetBytes)
            {
                string cls = $"U{mod}Obj{n}_{i}";
                sb.Append($@"DECLARE_DYNAMIC_MULTICAST_DELEGATE_OneParam(F{mod}Changed{n}_{i}, int32, NewValue);

UENUM(BlueprintType)
enum class E{mod}State{n}_{i} : uint8
{{
	None,
	Active UMETA(DisplayName = ""Active""),
	Done = 3,
}};

USTRUCT(BlueprintType)
struct {api} F{mod}Data{n}_{i}
{{
	GENERATED_BODY()

	UPROPERTY(EditAnywhere, BlueprintReadWrite)
	float Weight = 1.f;

	UPROPERTY()
	TArray<FName> Tags;
}};

/** Synthetic class {i} of file {n}. */
UCLASS(BlueprintType, Blueprintable)
class {api} {cls} : public UObject
{{
	GENERATED_BODY()

public:
	UFUNCTION(BlueprintCallable, Category = ""{mod}"")
	int32 Compute(int32 Input, const FString& Label) const;

	UFUNCTION(BlueprintPure)
	bool IsReady() const {{ return bReady && Count > 0; }}

	virtual void PostInitProperties() override;
	static {cls}* Get(UObject* Outer);

	template <typename T>
	T* As() {{ return Cast<T>(this); }}

#if WITH_EDITOR
	virtual void PostEditChangeProperty(FPropertyChangedEvent& Event) override;
#endif

protected:
	UPROPERTY(EditAnywhere, Category = ""{mod}"")
	int32 Count = 0;

	UPROPERTY(BlueprintAssignable)
	F{mod}Changed{n}_{i} OnChanged;

	TMap<FName, F{mod}Data{n}_{i}> Entries;
	uint8 bReady : 1;
}};

");
                i++;
            }
            return sb.ToString();
        }

        public static string Source(string mod, int n, int targetBytes)
        {
            var sb = new StringBuilder(targetBytes + 2048);
            sb.Append("#include \"").Append(mod).Append("Type").Append(n).Append(".h\"\n#include \"Engine/World.h\"\n\n");
            sb.Append("DEFINE_LOG_CATEGORY_STATIC(Log").Append(mod).Append(n).Append(", Log, All);\n\n");
            int i = 0;
            while (sb.Length < targetBytes)
            {
                string cls = $"U{mod}Obj{n}_{i}";
                sb.Append($@"int32 {cls}::Compute(int32 Input, const FString& Label) const
{{
	int32 Result = Input;
	for (int32 Index = 0; Index < Count; ++Index)
	{{
		Result += Index * 3;
		if (Result > 1000) {{ UE_LOG(Log{mod}{n}, Verbose, TEXT(""%s overflow %d""), *Label, Result); break; }}
	}}
	return Result;
}}

void {cls}::PostInitProperties()
{{
	Super::PostInitProperties();
	bReady = true;
	OnChanged.Broadcast(Count);
}}

{cls}* {cls}::Get(UObject* Outer)
{{
	return NewObject<{cls}>(Outer);
}}

static void Helper{n}_{i}(TArray<int32>& Out)
{{
	Out.Reserve(16);
	for (int32 V : {{ 1, 2, 3 }}) Out.Add(V);
}}

");
                i++;
            }
            return sb.ToString();
        }

        public string Write(string relative, string content)
        {
            string path = Path.Combine(Root, relative.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(path));
            File.WriteAllText(path, content);
            return path;
        }

        public void Dispose()
        {
            try
            {
                if (Directory.Exists(Root)) Directory.Delete(Root, true);
            }
            catch (Exception)
            {
                // temp folder; best effort
            }
        }
    }
}
