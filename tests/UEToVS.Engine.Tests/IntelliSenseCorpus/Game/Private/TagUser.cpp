// String context completion carets (see Gadget.h for the marker syntax).
#include "TagUser.h"

UE_DEFINE_GAMEPLAY_TAG(TAG_Local_Only, "Local.OnlyHere");

void ATagUser::Setup(ATagUser* Other)
{
	FGameplayTag A = FGameplayTag::RequestGameplayTag(FName("/*@complete:tag.root*/"));
	FGameplayTag B = FGameplayTag::RequestGameplayTag(FName("Ability./*@complete:tag.child*/"));
	FGameplayTag C = RequestGameplayTag(TEXT("Ability.Fi/*@complete:tag.prefix*/"));
	FGameplayTag D = RequestGameplayTag(TEXT("Status./*@complete:tag.native*/"));
	FGameplayTag E = RequestGameplayTag(TEXT("Lo/*@complete:tag.doc*/"));
	static ConstructorHelpers::FObjectFinder<UStaticMesh> Mesh(TEXT("/Game/Props//*@complete:asset.mesh*/"));
	static ConstructorHelpers::FClassFinder<APawn> PawnClass(TEXT("/Game/Props//*@complete:asset.class*/"));
	UObject* Any = LoadObject<UObject>(nullptr, TEXT("//*@complete:asset.mount*/"));
	UWorld* Map = LoadObject<UWorld>(nullptr, TEXT("/Game/Maps//*@complete:asset.world*/"));
	FSoftObjectPath Soft(TEXT("/Game//*@complete:asset.folders*/"));
	const TCHAR* Raw = TEXT("/Game/Props/SM_/*@complete:asset.anywhere*/");
	const TCHAR* Unix = TEXT("/usr/bin//*@complete:none.unixpath*/");
	FScriptDelegate Del;
	Del.BindUFunction(this, FName("On/*@complete:ufn.this*/"));
	FTimerDelegate Timer;
	Timer.BindUFunction(Other, TEXT("/*@complete:ufn.other*/"));
	ATagListener* Listener = nullptr;
	Timer.BindUFunction(Listener, FName("Ha/*@complete:ufn.listener*/"));
	UFunction* Fn = Listener->FindFunction(TEXT("/*@complete:ufn.find*/"));
	FString S;
	GConfig->GetString(TEXT("/Script/MyGame/*@complete:ini.section*/"), TEXT("Key"), S, GGameIni);
	bool bUse = false;
	GConfig->GetBool(TEXT("/Script/MyGame.GadgetSettings"), TEXT("/*@complete:ini.key*/"), bUse, GGameIni);
	GConfig->GetInt(TEXT("SystemSettings"), TEXT("r./*@complete:ini.dotkey*/"), Count, GEngineIni);
	UE_LOG(LogTemp, Log, TEXT("Ability./*@complete:none.log*/"));
	FName Plain = FName("Abil/*@complete:none.fname*/");
	OnCharged.Execute(1, /*@sig:delegate.execute*/);
	OnSignal.ExecuteIfBound(/*@sig:delegate.dynexecute*/);
	OnCountChanged.Broadcast(/*@sig:delegate.native*/);
}
