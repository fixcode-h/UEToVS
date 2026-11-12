// Fill out your copyright notice in the Description page of Project Settings. //@deffile:include.MyCharacter

#pragma once

#include "CoreMinimal.h"
#include "Actor.h"
#include "WeaponTypes.h"
#include "MyCharacter.generated.h"

class UHealthComponent;
class AWeapon;

DECLARE_DYNAMIC_MULTICAST_DELEGATE_OneParam(/*@def:FOnCharacterDied*/FOnCharacterDied, AActor*, Killer);

UCLASS(Blueprintable, Config = Game)
class MYGAME_API /*@def:AMyCharacter*/AMyCharacter : public /*@caret:ACharacter*/ACharacter
{
	/*@caret:none.GENERATED_BODY*/GENERATED_BODY()

public:
	/*@def:AMyCharacter.ctor.decl*/AMyCharacter();

	/** Blueprint can override; C++ default lives in OnHit_Implementation. */
	UFUNCTION(BlueprintNativeEvent, BlueprintCallable, Category = "Combat")
	void /*@caret:AMyCharacter.OnHit_Implementation*//*@def:AMyCharacter.OnHit.decl*/OnHit(float Damage, AActor* Instigator);

	UFUNCTION(Server, Reliable, WithValidation)
	void /*@caret:AMyCharacter.ServerFire.impls*//*@def:AMyCharacter.ServerFire.decl*/ServerFire(const FVector& Direction);

	UFUNCTION(Client, Unreliable)
	void /*@def:AMyCharacter.ClientShowHit.decl*/ClientShowHit(float Damage);

	UFUNCTION(NetMulticast, Unreliable)
	void /*@def:AMyCharacter.MulticastPlayEffect.decl*/MulticastPlayEffect();

	UFUNCTION(BlueprintImplementableEvent)
	void /*@def:AMyCharacter.OnLevelUp*/OnLevelUp(int32 NewLevel);

	virtual void /*@def:AMyCharacter.BeginPlay.decl*/BeginPlay() override;
	virtual void /*@def:AMyCharacter.Tick.decl*/Tick(float DeltaSeconds) override;
	virtual void /*@def:AMyCharacter.Jump.decl*/Jump() override;

	/** Overloads: navigation picks the matching one. */
	void /*@caret:AMyCharacter.Equip.Weapon*//*@def:AMyCharacter.Equip.Weapon.decl*/Equip(AWeapon* Weapon);
	void /*@caret:AMyCharacter.Equip.Slot*//*@def:AMyCharacter.Equip.Slot.decl*/Equip(int32 Slot, bool bForce = false);

	/*@caret:UHealthComponent*/UHealthComponent* /*@def:AMyCharacter.GetHealthComponent*/GetHealthComponent() const { return /*@caret:AMyCharacter.HealthComp*/HealthComp; }

	UPROPERTY(BlueprintAssignable, Category = "Combat")
	FOnCharacterDied /*@def:AMyCharacter.OnDied*/OnDied;

protected:
	UPROPERTY(VisibleAnywhere, BlueprintReadOnly, Category = "Components")
	TObjectPtr<UHealthComponent> /*@def:AMyCharacter.HealthComp*/HealthComp;

	UPROPERTY(EditAnywhere, Category = "Combat")
	TObjectPtr<AWeapon> /*@def:AMyCharacter.CurrentWeapon*/CurrentWeapon;

	UPROPERTY(EditAnywhere, Category = "Combat")
	FWeaponData /*@def:AMyCharacter.DefaultWeaponData*/DefaultWeaponData;

	/** Same name as UHealthComponent::Health. */
	UPROPERTY(EditAnywhere, Category = "Stats")
	int32 /*@def:AMyCharacter.Health*/Health = 3;

	TArray<AWeapon*> /*@def:AMyCharacter.Inventory*/Inventory;

	TRingBuffer<float> /*@def:AMyCharacter.DamageHistory*/DamageHistory;
};
