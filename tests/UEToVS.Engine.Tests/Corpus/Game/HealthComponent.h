#pragma once

#include "CoreTypes.h"
#include "Actor.h"
#include "HealthComponent.generated.h"

DECLARE_DYNAMIC_MULTICAST_DELEGATE_TwoParams(/*@def:FOnHealthChanged*/FOnHealthChanged, float, NewHealth, float, Delta);

UCLASS(ClassGroup = (Custom), meta = (BlueprintSpawnableComponent))
class MYGAME_API /*@def:UHealthComponent*/UHealthComponent : public /*@caret:UActorComponent*/UActorComponent
{
	GENERATED_BODY()

public:
	/*@def:UHealthComponent.ctor.decl*/UHealthComponent();

	UFUNCTION(BlueprintCallable, Category = "Health")
	void /*@caret:UHealthComponent.ApplyDamage:toggle*//*@def:UHealthComponent.ApplyDamage.decl*/ApplyDamage(float Amount);

	UFUNCTION(BlueprintCallable, Category = "Health")
	void /*@def:UHealthComponent.Heal.decl*/Heal(float Amount);

	/** Same member name as AMyCharacter::Health: navigation must not mix them up. */
	UPROPERTY(EditAnywhere, BlueprintReadOnly, Category = "Health")
	float /*@def:UHealthComponent.Health*/Health = 100.f;

	UPROPERTY(EditAnywhere, Category = "Health")
	float /*@def:UHealthComponent.MaxHealth*/MaxHealth = 100.f;

	UPROPERTY(BlueprintAssignable)
	/*@caret:FOnHealthChanged*/FOnHealthChanged /*@def:UHealthComponent.OnHealthChanged*/OnHealthChanged;

	bool /*@def:UHealthComponent.IsDead*/IsDead() const { return /*@caret:UHealthComponent.Health*/Health <= 0.f; }

protected:
	virtual void /*@caret:UHealthComponent.BeginPlay*//*@def:UHealthComponent.BeginPlay.decl*/BeginPlay() override;
};
