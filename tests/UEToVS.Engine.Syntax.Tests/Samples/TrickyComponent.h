#pragma once
#include "CoreMinimal.h"
#include UE_INLINE_GENERATED_CPP_BY_NAME(Tricky)

class UWorld;
struct FHitResult;

namespace ETrickyMode
{
	enum Type : int
	{
		None,
		Fast = 1 << 0,
	};
}

template<typename T, typename = typename TEnableIf<TIsPointer<T>::Value>::Type>
FORCEINLINE T* AsPtr(T InValue) { return InValue; }

template <typename KeyType, typename ValueType, bool bAllowDuplicates = false>
struct TPairX
{
	KeyType Key;
	ValueType Value;
	bool operator<(const TPairX& Other) const { return Key < Other.Key; }
	bool operator>(const TPairX& Other) const { return Other < *this; }
	friend uint32 GetTypeHash(const TPairX& P) { return 0; }
};

UCLASS(Abstract, Config = Game, meta = (BlueprintSpawnableComponent))
class ENGINE_API UTrickyComponent : public UActorComponent, public FTickableGameObject
{
	GENERATED_BODY()

	friend class FTrickyDetails;

public:
	UTrickyComponent(const FObjectInitializer& ObjectInitializer = FObjectInitializer::Get());

	UE_DEPRECATED(5.2, "Use NewThing instead.")
	UFUNCTION(BlueprintCallable, meta = (DeprecatedFunction, DeprecationMessage = "Use NewThing"))
	void OldThing();

	virtual void TickComponent(float DeltaTime, enum ELevelTick TickType, FActorComponentTickFunction* ThisTickFunction) override;
	virtual TStatId GetStatId() const override { RETURN_QUICK_DECLARE_CYCLE_STAT(UTrickyComponent, STATGROUP_Tickables); }
	virtual bool IsTickable() const PURE_VIRTUAL(UTrickyComponent::IsTickable, return false;);

	template<class T>
	T* GetOwnerAs() const { return Cast<T>(GetOwner()); }

	const TCHAR* GetLabel(const TCHAR* Default = TEXT("none")) const;

	UFUNCTION(BlueprintPure, Category = "Tricky", meta = (WorldContext = "WorldContextObject"))
	static UTrickyComponent* Find(const UObject* WorldContextObject, TSubclassOf<AActor> Class = nullptr);

	DECLARE_EVENT_OneParam(UTrickyComponent, FOnTricky, int32);
	FOnTricky& OnTricky() { return TrickyEvent; }

#if WITH_EDITOR
	virtual void PostEditChangeChainProperty(struct FPropertyChangedChainEvent& PropertyChangedEvent) override;
#if WITH_EDITORONLY_DATA
	UPROPERTY(EditAnywhere, Category = "Editor")
	TObjectPtr<UTexture2D> EditorIcon;
#endif // WITH_EDITORONLY_DATA
#endif

protected:
	UPROPERTY(EditAnywhere, BlueprintReadWrite, Category = "Tricky", meta = (EditCondition = "bEnabled && Mode != ETrickyMode::None", ClampMin = 0))
	TArray<TPair<FName, TSoftObjectPtr<UObject>>> Lookup;

	UPROPERTY(Transient)
	uint8 bEnabled : 1 = true;

	FOnTricky TrickyEvent;
	TFunction<bool(const FHitResult&, float)> Filter = [](const FHitResult& Hit, float Dist) -> bool { return Dist > 0.f; };
	FDelegateHandle Handles[4];
	mutable FCriticalSection Guard;

private:
	void Internal(TMap<FName, TArray<int32>>& Out) const;
	UFUNCTION()
	void OnRep_Value(int32 OldValue);
	ETrickyMode::Type Mode = ETrickyMode::None;
};

inline bool operator==(const FHitResult& A, const FHitResult& B) { return &A == &B; }
