// String context completion corpus: UFUNCTIONs for BindUFunction / FindFunction name strings.
#pragma once

#include "Actor.h"
#include "TagUser.generated.h"

DECLARE_DELEGATE_TwoParams(FOnTagCharged, int32, bool);
DECLARE_DYNAMIC_DELEGATE_OneParam(FOnTagSignal, float, Strength);
DECLARE_MULTICAST_DELEGATE_OneParam(FOnTagCountChanged, int32);

UCLASS()
class MYGAME_API ATagUser : public AActor
{
	GENERATED_BODY()

public:
	UFUNCTION()
	void OnTagAdded(int32 Count);

	UFUNCTION(BlueprintCallable, Category = "Tags")
	void OnTagRemoved();

	/** Not a UFUNCTION: BindUFunction cannot call it. */
	void OnPlainHelper();

	void Setup(ATagUser* Other);

	FOnTagCharged OnCharged;
	FOnTagSignal OnSignal;
	FOnTagCountChanged OnCountChanged;
};

UCLASS()
class MYGAME_API ATagListener : public AActor
{
	GENERATED_BODY()

public:
	UFUNCTION()
	void HandleSignal(float Value);
};
