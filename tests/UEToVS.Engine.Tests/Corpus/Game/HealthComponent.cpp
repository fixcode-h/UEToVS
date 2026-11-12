#include "HealthComponent.h"

/*@caret:UHealthComponent*/UHealthComponent::/*@caret:UHealthComponent.ctor.decl:toggle*//*@def:UHealthComponent.ctor*/UHealthComponent()
{
	/*@caret:UHealthComponent.MaxHealth*/MaxHealth = 120.f;
	/*@caret:UHealthComponent.Health*/Health = MaxHealth;
}

void UHealthComponent::/*@def:UHealthComponent.BeginPlay*/BeginPlay()
{
	Super::/*@caret:UActorComponent.BeginPlay*/BeginPlay();
	/*@caret:UActorComponent.Activate*/Activate();
	AActor* OwnerActor = /*@caret:UActorComponent.GetOwner*/GetOwner();
	if (OwnerActor)
	{
		OwnerActor->/*@caret:AActor.Tick*/Tick(0.f);
	}
}

void UHealthComponent::/*@def:UHealthComponent.ApplyDamage*/ApplyDamage(float Amount)
{
	const float /*@def:local.Old*/Old = Health;
	Health = FMath::Max(0.f, Health - Amount);
	/*@caret:UHealthComponent.OnHealthChanged*/OnHealthChanged.Broadcast(Health, Health - /*@caret:local.Old*/Old);
	if (/*@caret:UHealthComponent.IsDead*/IsDead())
	{
		GetOwner()->/*@caret:AActor.Destroy*/Destroy();
	}
}

void UHealthComponent::/*@caret:UHealthComponent.Heal.decl:toggle*//*@def:UHealthComponent.Heal*/Heal(float /*@def:param.Heal.Amount*/Amount)
{
	/*@caret:UHealthComponent.ApplyDamage*/ApplyDamage(-/*@caret:param.Heal.Amount*/Amount);
}
