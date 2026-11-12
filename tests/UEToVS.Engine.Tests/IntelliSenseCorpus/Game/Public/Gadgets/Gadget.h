// IntelliSense corpus (completion / Quick Info / parameter info / colouring).
// Markers: @complete / @quick / @sig with an id (block comment) put the caret at the marker start;
// @cls with a SemanticKind (optionally +Declaration) names the expected colour of the identifier that follows.
#pragma once

#include "Actor.h"
#include "HealthComponent.h"
#include "WeaponTypes.h"
#include "GadgetMacros.h"
#include "Gadget.generated.h"

DECLARE_MULTICAST_DELEGATE_OneParam(FOnGadgetUsed, int32);

/**
 * A gadget the player can carry.
 * Supports charges.
 */
UCLASS(Blueprintable, /*@complete:spec.uclass*/)
class MYGAME_API AGadget : public AActor
{
	GENERATED_BODY()

public:
	AGadget();

	/** Uses the gadget once.
	 *  @param Power how hard to use it
	 *  @param bForce ignore the cooldown
	 */
	UFUNCTION(BlueprintCallable, Category = "Gadget")
	bool Use(float /*@cls:Parameter+Declaration*/Power, bool bForce = false);

	/// Uses the gadget on a target.
	bool Use(AActor* Target, float Power, int32 Times = 1);

	// Number of charges left.
	UPROPERTY(/*@cls:UeSpecifier*/EditAnywhere, BlueprintReadWrite, Category = "Gadget", meta = (/*@cls:UeSpecifier*/ClampMin = "0", UIMax = "10"))
	int32 /*@cls:Field+Declaration*/Charges = 3;

	UPROPERTY(VisibleAnywhere, /*@complete:spec.uproperty*/)
	TObjectPtr<UHealthComponent> Health;

	UPROPERTY(EditAnywhere, meta = (/*@complete:spec.meta*/))
	float Cooldown = 1.f;

	UPROPERTY(EditAnywhere, BlueprintReadOnly, Bl/*@complete:spec.conflict*/)
	float Weight = 2.f;

	UFUNCTION(Server, /*@complete:spec.ufunction*/)
	void ServerUse();

	UFUNCTION(BlueprintNativeEvent)
	void OnActivated(int32 Level);

	TArray<AGadget*> Linked;
	TSharedPtr<FWeaponData> SharedData;
	TWeakObjectPtr<AActor> LastUser;
	TMap<FName, AGadget*> ByName;
	/*@cls:Delegate*/FOnGadgetUsed OnUsed;
	FOnHealthChanged OnGadgetHealth;

	static int32 /*@cls:StaticMethod+Declaration*/CountAll();
	virtual void Tick(float DeltaSeconds) override;
	virtual void /*@cls:VirtualMethod+Declaration*/Recharge(int32 Amount, bool bFull = false) const;
	virtual void Sealed() final;
};

template<typename TElement>
struct TGadgetBox
{
	/*@cls:TemplateParameter*/TElement Value;
};

UCLASS()
class MYGAME_API AGadgetChild : public AGadget
{
	GENERATED_BODY()

public:
	virtual void Tick(float DeltaSeconds) override;
	virtual /*@complete:override*/
};
