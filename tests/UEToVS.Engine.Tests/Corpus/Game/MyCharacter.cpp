// Fill out your copyright notice in the Description page of Project Settings.

#include "MyCharacter.h" //@caret:include.MyCharacter
#include "HealthComponent.h"
#include "Weapon.h"

AMyCharacter::/*@def:AMyCharacter.ctor*/AMyCharacter()
{
	/*@caret:AMyCharacter.HealthComp*/HealthComp = CreateDefaultSubobject<UHealthComponent>(TEXT("Health"));
	/*@caret:AActor.CustomTimeDilation*/CustomTimeDilation = 1.f;
	/*@caret:AMyCharacter.Health*/Health = 3;
}

void AMyCharacter::/*@caret:AMyCharacter.BeginPlay.decl:toggle*//*@def:AMyCharacter.BeginPlay*/BeginPlay()
{
	/*@caret:Super.ACharacter*/Super::/*@caret:AActor.BeginPlay*/BeginPlay();

	HealthComp->/*@caret:UHealthComponent.OnHealthChanged*/OnHealthChanged.AddDynamic(this, &AMyCharacter::OnHit);
	/*@caret:AMyCharacter.HealthComp*/HealthComp->/*@caret:UHealthComponent.Health*/Health = 100.f;
	this->/*@caret:AMyCharacter.Health*/Health = 5;

	auto* Movement = /*@caret:ACharacter.GetCharacterMovement*/GetCharacterMovement();
	UHealthComponent* Comp = /*@caret:AActor.FindComponentByClass*/FindComponentByClass<UHealthComponent>();
	if (Comp)
	{
		Comp->/*@caret:UHealthComponent.Heal*/Heal(10.f);
		Comp->/*@caret:UActorComponent.Activate*/Activate();
	}

	auto* Typed = Cast<UHealthComponent>(GetOwner());
	Typed->/*@caret:UHealthComponent.MaxHealth*/MaxHealth = 200.f;
}

void AMyCharacter::/*@def:AMyCharacter.Tick*/Tick(float DeltaSeconds)
{
	Super::/*@caret:AActor.Tick*/Tick(DeltaSeconds);
	const FVector /*@def:local.Location*/Location = /*@caret:AActor.GetActorLocation*/GetActorLocation();
	const float Speed = /*@caret:local.Location*/Location./*@caret:FVector.Size*/Size();
	if (Speed > DefaultWeaponData./*@caret:FWeaponData.Range*/Range)
	{
		/*@caret:AMyCharacter.Jump*/Jump();
	}
	for (AWeapon* /*@def:local.Item*/Item : /*@caret:AMyCharacter.Inventory*/Inventory)
	{
		/*@caret:local.Item*/Item->/*@caret:AWeapon.Fire*/Fire();
	}
	DamageHistory./*@caret:TRingBuffer.Push*/Push(DeltaSeconds);
	const FVector Zero = FVector::/*@caret:FVector.ZeroVector*/ZeroVector;
}

void AMyCharacter::/*@def:AMyCharacter.Jump*/Jump()
{
	/*@caret:Super.ACharacter*/Super::/*@caret:ACharacter.Jump*/Jump();
}

void AMyCharacter::/*@caret:AMyCharacter.OnHit.decl:toggle*//*@def:AMyCharacter.OnHit_Implementation*/OnHit_Implementation(float /*@def:param.OnHit.Damage*/Damage, AActor* Instigator)
{
	/*@caret:AMyCharacter.HealthComp*/HealthComp->/*@caret:UHealthComponent.ApplyDamage*/ApplyDamage(/*@caret:param.OnHit.Damage*/Damage);
	if (HealthComp->/*@caret:UHealthComponent.IsDead*/IsDead())
	{
		/*@caret:AMyCharacter.OnDied*/OnDied.Broadcast(Instigator);
		/*@caret:AMyCharacter.MulticastPlayEffect.impl*/MulticastPlayEffect();
	}
	/*@caret:AMyCharacter.ClientShowHit.impl*/ClientShowHit(Damage);
	CurrentWeapon->/*@caret:AWeapon.Data*/Data./*@caret:FWeaponData.Damage*/Damage += 1.f;
}

void AMyCharacter::/*@def:AMyCharacter.ServerFire.impls*/ServerFire_Implementation(const FVector& Direction)
{
	if (/*@caret:AMyCharacter.CurrentWeapon*/CurrentWeapon)
	{
		CurrentWeapon->/*@caret:AWeapon.Fire*/Fire();
	}
}

bool AMyCharacter::/*@caret:AMyCharacter.ServerFire.decl:decl*//*@def:AMyCharacter.ServerFire.impls*/ServerFire_Validate(const FVector& Direction)
{
	return !Direction.IsNearlyZero();
}

void AMyCharacter::/*@def:AMyCharacter.ClientShowHit.impl*/ClientShowHit_Implementation(float Damage)
{
	UE_LOG(LogTemp, Log, TEXT("Hit %f"), Damage);
}

void AMyCharacter::/*@def:AMyCharacter.MulticastPlayEffect.impl*/MulticastPlayEffect_Implementation()
{
}

void AMyCharacter::/*@caret:AMyCharacter.Equip.Weapon.decl:toggle*//*@def:AMyCharacter.Equip.Weapon*/Equip(AWeapon* /*@def:param.Equip.Weapon*/Weapon)
{
	/*@caret:AMyCharacter.CurrentWeapon*/CurrentWeapon = /*@caret:param.Equip.Weapon*/Weapon;
}

void AMyCharacter::/*@caret:AMyCharacter.Equip.Slot.decl:toggle*//*@def:AMyCharacter.Equip.Slot*/Equip(int32 Slot, bool bForce)
{
	if (Inventory.IsValidIndex(Slot))
	{
		Equip(Inventory[Slot]);
		Inventory[Slot]->/*@caret:AWeapon.Fire*/Fire();
	}
}
