#include "MyCharacter.h"
#include "Net/UnrealNetwork.h"

DEFINE_LOG_CATEGORY_STATIC(LogMyCharacter, Log, All);

namespace
{
	static int32 HelperCount = 0;
	float ComputeSpread(float Base) { return Base * 2.f; }
}

AMyCharacter::AMyCharacter()
	: Ammo(30)
{
	PrimaryActorTick.bCanEverTick = true;
	auto Lambda = [this](int32 X) { if (X > 0) { Ammo += X; } };
}

void AMyCharacter::Tick(float DeltaSeconds)
{
	Super::Tick(DeltaSeconds);
	for (int32 I = 0; I < 3; ++I) { UE_LOG(LogMyCharacter, Log, TEXT("tick {")); }
}

void AMyCharacter::Server_Fire_Implementation(const FVector& Target)
{
}

bool AMyCharacter::Server_Fire_Validate(const FVector& Target)
{
	return true;
}

void AMyCharacter::OnHit_Implementation(AActor* Other)
{
}
