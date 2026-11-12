#pragma once

#include "CoreTypes.h"

class UActorComponent;

UCLASS()
class ENGINE_API /*@def:AActor*//*@def:Super.AActor*/AActor : public UObject
{
	GENERATED_BODY()
public:
	AActor();

	virtual void /*@def:AActor.BeginPlay*/BeginPlay();
	virtual void /*@def:AActor.Tick*/Tick(float DeltaSeconds);
	virtual void /*@def:AActor.EndPlay*/EndPlay();

	FVector /*@def:AActor.GetActorLocation*/GetActorLocation() const;
	bool /*@def:AActor.Destroy*/Destroy();
	AActor* /*@def:AActor.GetOwner*/GetOwner() const;

	template<class T>
	T* /*@def:AActor.FindComponentByClass*/FindComponentByClass() const;

	UPROPERTY()
	TObjectPtr<AActor> /*@def:AActor.Owner*/Owner;

	float /*@def:AActor.CustomTimeDilation*/CustomTimeDilation;
};

UCLASS()
class ENGINE_API /*@def:APawn*/APawn : public AActor
{
	GENERATED_BODY()
public:
	virtual void /*@def:APawn.PossessedBy*/PossessedBy(class AController* NewController);
	class AController* GetController() const;
};

UCLASS()
class ENGINE_API /*@def:ACharacter*//*@def:Super.ACharacter*/ACharacter : public APawn
{
	GENERATED_BODY()
public:
	virtual void /*@def:ACharacter.Jump*/Jump();
	virtual void /*@def:ACharacter.Landed*/Landed(const FHitResult& Hit);
	class UCharacterMovementComponent* /*@def:ACharacter.GetCharacterMovement*/GetCharacterMovement() const;
};

UCLASS()
class ENGINE_API /*@def:UActorComponent*//*@def:Super.UActorComponent*/UActorComponent : public UObject
{
	GENERATED_BODY()
public:
	virtual void /*@def:UActorComponent.BeginPlay*/BeginPlay();
	virtual void TickComponent(float DeltaTime);
	AActor* /*@def:UActorComponent.GetOwner*/GetOwner() const;
	void /*@def:UActorComponent.Activate*/Activate(bool bReset = false);
};
