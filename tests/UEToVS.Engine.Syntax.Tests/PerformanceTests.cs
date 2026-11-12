using System;
using System.Diagnostics;
using System.Linq;
using System.Text;
using UEToVS.Contracts;
using Xunit;
using Xunit.Abstractions;

namespace UEToVS.Engine.Syntax.Tests
{
    public class PerformanceTests
    {
        private readonly ITestOutputHelper _out;

        public PerformanceTests(ITestOutputHelper output) => _out = output;

        internal static byte[] SyntheticHeader(int targetBytes)
        {
            var sb = new StringBuilder(targetBytes + 4096);
            sb.Append("#pragma once\n#include \"CoreMinimal.h\"\n\n");
            int i = 0;
            while (sb.Length < targetBytes)
            {
                sb.Append($@"/** Fires when health changes for actor {i}. */
DECLARE_DYNAMIC_MULTICAST_DELEGATE_TwoParams(FOnHealthChanged{i}, float, OldHealth, float, NewHealth);

UENUM(BlueprintType)
enum class EMode{i} : uint8
{{
	Idle UMETA(DisplayName = ""Idle""),
	Running,
	Dead = 7,
}};

/**
 * Synthetic actor number {i}.
 */
UCLASS(Blueprintable, BlueprintType, meta = (ShortTooltip = ""Synthetic""))
class MYGAME_API AActor{i} : public ACharacter, public IInterface{i % 7}
{{
	GENERATED_BODY()

public:
	AActor{i}(const FObjectInitializer& ObjectInitializer = FObjectInitializer::Get());

	virtual void BeginPlay() override;
	virtual void Tick(float DeltaSeconds) override;

	UFUNCTION(BlueprintCallable, Category = ""Combat|Damage"")
	float ApplyDamage(float Amount, AActor* Causer, const FHitResult& Hit);

	UFUNCTION(Server, Reliable, WithValidation)
	void Server_Fire(const FVector& Origin, const FRotator& Direction);

	FORCEINLINE int32 GetAmmo() const {{ return Ammo; }}

	template <typename T>
	T* FindComp() const {{ return FindComponentByClass<T>(); }}

#if WITH_EDITOR
	virtual void PostEditChangeProperty(FPropertyChangedEvent& Event) override;
#endif

protected:
	UPROPERTY(EditAnywhere, BlueprintReadWrite, Category = ""Stats"", meta = (ClampMin = ""0""))
	float Health = 100.f;

	UPROPERTY(VisibleAnywhere, BlueprintReadOnly)
	int32 Ammo = 30;

	UPROPERTY(BlueprintAssignable)
	FOnHealthChanged{i} OnHealthChanged;

	TMap<FName, TArray<TWeakObjectPtr<AActor>>> Targets;
	uint8 bIsAiming : 1;

private:
	void UpdateState(EMode{i} NewMode)
	{{
		if (NewMode != Mode && Health > 0.f)
		{{
			Mode = NewMode;
			OnHealthChanged.Broadcast(Health, Health - 1.f);
			for (int32 Index = 0; Index < Targets.Num(); ++Index)
			{{
				UE_LOG(LogTemp, Log, TEXT(""Target %d""), Index);
			}}
		}}
	}}

	EMode{i} Mode = EMode{i}::Idle;
}};

");
                i++;
            }
            return Encoding.UTF8.GetBytes(sb.ToString());
        }

        [Fact]
        public void SyntheticTwoMegabyteHeaderParsesCompletely()
        {
            var bytes = SyntheticHeader(2 * 1024 * 1024);
            int classes = Encoding.UTF8.GetString(bytes).Split("UCLASS(").Length - 1;

            // Warm up so tiered JIT reaches optimized code, then take the best of several runs.
            bool bench = Environment.GetEnvironmentVariable("UETOVS_BENCH") == "1";
            int warmups = bench ? 30 : 2, runs = bench ? 10 : 2;
            FileSummary s = null;
            for (int r = 0; r < warmups; r++) s = CppParser.Parse(bytes);
            double best = double.MaxValue;
            for (int r = 0; r < runs; r++)
            {
                var sw = Stopwatch.StartNew();
                s = CppParser.Parse(bytes);
                best = Math.Min(best, sw.Elapsed.TotalSeconds);
            }
            double mbPerSec = bytes.Length / (1024.0 * 1024) / best;
            _out.WriteLine($"{bytes.Length / 1024} KB, {s.Declarations.Count} declarations, {s.Occurrences.Count} occurrences, {mbPerSec:F1} MB/s (best of {runs})");

            Assert.Equal(classes, s.Declarations.Count(d => d.Kind == SymbolKind.Class && (d.Ue & UeFlags.UClass) != 0));
            Assert.Equal(classes, s.Declarations.Count(d => d.Name == "UpdateState" && d.IsDefinition));
            Assert.Equal(classes, s.Declarations.Count(d => d.Name == "PostEditChangeProperty" && d.IsConditional));
            Assert.Empty(s.Diagnostics);

            // The throughput target (> 50 MB/s single-threaded) is only meaningful for an optimized, warmed-up build:
            // run with UETOVS_BENCH=1 and -c Release to enforce it.
            if (bench)
                Assert.True(mbPerSec > 50, $"throughput {mbPerSec:F1} MB/s below 50 MB/s");
        }
    }
}
