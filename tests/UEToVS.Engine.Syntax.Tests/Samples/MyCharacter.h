#pragma once
#include "CoreMinimal.h"
#include "GameFramework/Character.h"
#include "MyCharacter.generated.h"

DECLARE_DYNAMIC_MULTICAST_DELEGATE_OneParam(FOnHealthChanged, float, NewHealth);

UENUM(BlueprintType)
enum class EWeaponState : uint8
{
	Idle UMETA(DisplayName = "Idle"),
	Firing,
	Reloading = 5,
};

USTRUCT(BlueprintType)
struct MYGAME_API FWeaponData
{
	GENERATED_BODY()

	UPROPERTY(EditAnywhere, BlueprintReadWrite)
	float Damage = 10.f;

	UPROPERTY(EditAnywhere)
	TArray<FName> Sockets;
};

UCLASS(Blueprintable)
class MYGAME_API AMyCharacter : public ACharacter
{
	GENERATED_BODY()

public:
	AMyCharacter();

	virtual void Tick(float DeltaSeconds) override;

	UFUNCTION(Server, Reliable, WithValidation)
	void Server_Fire(const FVector& Target);

	UFUNCTION(BlueprintNativeEvent, Category = "Combat")
	void OnHit(AActor* Other);

	UPROPERTY(BlueprintAssignable)
	FOnHealthChanged OnHealthChanged;

	template <typename T>
	T* FindComp() const { return FindComponentByClass<T>(); }

protected:
	UPROPERTY(VisibleAnywhere, BlueprintReadOnly, Category = "Combat", meta = (AllowPrivateAccess = "true"))
	int32 Ammo = 30;

	uint8 bIsAiming : 1;

private:
	TMap<FName, TFunction<void(int32)>> Callbacks;
};
