#pragma once

#include "Actor.h"
#include "WeaponTypes.h"
#include "Weapon.generated.h"

UINTERFACE(MinimalAPI, Blueprintable)
class /*@def:UInteractable*/UInteractable : public UInterface
{
	GENERATED_BODY()
};

class MYGAME_API /*@def:IInteractable*/IInteractable
{
	GENERATED_BODY()

public:
	virtual void /*@def:IInteractable.Interact*/Interact(AActor* By) = 0;
	virtual bool /*@def:IInteractable.CanInteract*/CanInteract(AActor* By) const { return true; }
};

UCLASS(Abstract)
class MYGAME_API /*@caret:AWeapon.derived:impl*//*@def:AWeapon*//*@def:Super.AWeapon*/AWeapon : public /*@caret:AActor*/AActor, public /*@caret:IInteractable*/IInteractable
{
	GENERATED_BODY()

public:
	virtual void /*@caret:AWeapon.Fire.overrides:impl*//*@def:AWeapon.Fire.decl*/Fire();
	virtual void /*@def:AWeapon.Interact.decl*/Interact(AActor* By) override;

	UPROPERTY(EditDefaultsOnly, Category = "Weapon")
	/*@caret:FWeaponData*/FWeaponData /*@def:AWeapon.Data*/Data;

	UPROPERTY(EditDefaultsOnly, Category = "Weapon")
	/*@caret:EWeaponMode*/EWeaponMode /*@def:AWeapon.Mode*/Mode = EWeaponMode::/*@caret:EWeaponMode.Burst*/Burst;

protected:
	int32 /*@def:AWeapon.Ammo*/Ammo = 30;
};

UCLASS()
class MYGAME_API /*@def:AShotgun*//*@def:AWeapon.derived*/AShotgun : public /*@caret:AWeapon*/AWeapon
{
	GENERATED_BODY()

public:
	virtual void /*@def:AShotgun.Fire.decl*/Fire() override;

	UPROPERTY(EditDefaultsOnly)
	int32 /*@def:AShotgun.Pellets*/Pellets = 8;
};

UCLASS()
class MYGAME_API /*@def:ARifle*//*@def:AWeapon.derived*/ARifle : public AWeapon
{
	GENERATED_BODY()

public:
	virtual void /*@def:ARifle.Fire.decl*/Fire() override;
};
