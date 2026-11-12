// Copyright Epic-style header: USTRUCT / UENUM / namespaces / templates. //@deffile:include.WeaponTypes
#pragma once

#include "CoreTypes.h"
#include "WeaponTypes.generated.h"

UENUM(BlueprintType)
enum class /*@def:EWeaponMode*/EWeaponMode : uint8
{
	/*@def:EWeaponMode.Single*/Single UMETA(DisplayName = "Single"),
	/*@def:EWeaponMode.Burst*/Burst,
	Auto
};

USTRUCT(BlueprintType)
struct /*@def:FWeaponData*/FWeaponData
{
	GENERATED_BODY()

	UPROPERTY(EditAnywhere, BlueprintReadWrite, Category = "Weapon")
	float /*@def:FWeaponData.Damage*/Damage = 10.f;

	UPROPERTY(EditAnywhere, BlueprintReadWrite, Category = "Weapon")
	float /*@def:FWeaponData.Range*/Range = 1000.f;

	UPROPERTY(EditAnywhere)
	EWeaponMode /*@def:FWeaponData.Mode*/Mode = EWeaponMode::/*@caret:EWeaponMode.Single*/Single;

	float /*@def:FWeaponData.DamageAt*/DamageAt(float Distance) const
	{
		return Distance > /*@caret:FWeaponData.Range*/Range ? 0.f : /*@caret:FWeaponData.Damage*/Damage;
	}
};

namespace /*@def:Game*/Game
{
namespace /*@def:Game.Weapons*/Weapons
{
	struct /*@def:Game.Weapons.FSpread*/FSpread
	{
		float /*@def:Game.Weapons.FSpread.Angle*/Angle = 2.f;
		float /*@def:Game.Weapons.FSpread.Compute.decl*/Compute(int32 Shots) const;
	};

	float /*@def:Game.Weapons.DefaultSpread.decl*/DefaultSpread();

	/** Clamp helper that exists both here and globally. */
	float /*@def:Game.Weapons.Clamp01.decl*/Clamp01(float V);
}
}

/** Global helper with the same name as the namespaced one. */
float /*@def:Clamp01.decl*/Clamp01(float V);

template<typename T>
class /*@def:TRingBuffer*/TRingBuffer
{
public:
	void /*@def:TRingBuffer.Push.decl*/Push(const T& Value);
	T /*@def:TRingBuffer.Pop*/Pop();
	int32 /*@def:TRingBuffer.Num*/Num() const { return /*@caret:TRingBuffer.Count*/Count; }

private:
	T* Items = nullptr;
	int32 /*@def:TRingBuffer.Count*/Count = 0;
};
