#include "Weapon.h"
#include "MyCharacter.h"

void AWeapon::/*@def:AWeapon.Fire*/Fire()
{
	if (/*@caret:AWeapon.Ammo*/Ammo <= 0)
	{
		return;
	}
	--Ammo;
	const float Damage = /*@caret:AWeapon.Data*/Data./*@caret:FWeaponData.DamageAt*/DamageAt(100.f);
	if (/*@caret:AWeapon.Mode*/Mode == /*@caret:EWeaponMode*/EWeaponMode::/*@caret:EWeaponMode.Single*/Single)
	{
		/*@caret:AActor.Destroy*/Destroy();
	}
}

void AWeapon::/*@def:AWeapon.Interact*/Interact(AActor* By)
{
	if (AMyCharacter* Character = /*@caret:Cast*/Cast<AMyCharacter>(By))
	{
		Character->/*@caret:AMyCharacter.Equip.Weapon*/Equip(this);
		Character->/*@caret:AMyCharacter.Health*/Health -= 1;
		Character->/*@caret:AMyCharacter.GetHealthComponent*/GetHealthComponent()->/*@caret:UHealthComponent.Health*/Health += 10.f;
	}
	if (/*@caret:IInteractable.CanInteract*/CanInteract(By))
	{
		Fire();
	}
}

void AShotgun::/*@def:AWeapon.Fire.overrides*//*@def:AShotgun.Fire*/Fire()
{
	for (int32 /*@def:local.I*/I = 0; /*@caret:local.I*/I < /*@caret:AShotgun.Pellets*/Pellets; ++I)
	{
		/*@caret:AWeapon*/AWeapon::/*@caret:AWeapon.Fire*/Fire();
	}
	/*@caret:Super.AWeapon*/Super::/*@caret:AWeapon.Fire*/Fire();
	/*@caret:AWeapon.Ammo*/Ammo = 0;
}

void ARifle::/*@def:AWeapon.Fire.overrides*//*@def:ARifle.Fire*/Fire()
{
	Super::/*@caret:AWeapon.Fire*/Fire();
	/*@caret:AWeapon.Data*/Data.Damage *= 2.f;
}
