// IntelliSense corpus: completion, parameter info, Quick Info and colouring carets (see Gadget.h for the marker syntax).
#include "Gadgets/Gadget.h"
#include "Gad/*@complete:include.gad*/"
#include <HealthCo/*@complete:include.health*/>

AGadget::AGadget()
{
	Charges = 3;
}

bool AGadget::Use(float Power, bool bForce)
{
	const float Scaled = Power * GADGET_SCALE;
	int32 LocalCount = 0;
	Cha/*@complete:general.member*/;
	Sca/*@complete:general.local*/;
	Pow/*@complete:general.param*/;
	Clamp/*@complete:general.global*/;
	retu/*@complete:general.keyword*/;
	UPROP/*@complete:general.snippet*/;
	Gadget/*@complete:general.unreal*/;
	Health->/*@complete:member.arrow*/;
	Health./*@complete:member.dotfix*/;
	SharedData->Dam/*@complete:member.shared*/;
	SharedData./*@complete:member.sharedapi*/;
	LastUser->GetActorL/*@complete:member.weak*/;
	Linked[0]->/*@complete:member.index*/;
	Linked./*@complete:member.tarray*/;
	Linked.Last()->Char/*@complete:member.chain.last*/;
	ByName.FindRef(TEXT("x"))->/*@complete:member.map*/;
	GetOwner()->GetActorLoc/*@complete:member.getowner*/;
	Health->GetOwner()->Dest/*@complete:member.chain*/;
	auto* Found = FindComponentByClass<UHealthComponent>();
	Found->He/*@complete:member.auto*/;
	FVector Loc = GetActorLocation();
	Loc->/*@complete:member.arrowfix*/;
	Loc.Si/*@complete:member.value*/;
	OnUsed./*@complete:member.delegate*/;
	OnGadgetHealth./*@complete:member.dyndelegate*/;
	this->Cool/*@complete:member.this*/;
	EWeaponMode::/*@complete:scope.enum*/;
	Super::/*@complete:scope.super*/;
	AGadget::/*@complete:scope.class*/;
	Game::Weapons::/*@complete:scope.namespace*/;
	// Cha/*@complete:none.comment*/
	const TCHAR* Text = TEXT("Cha/*@complete:none.string*/");
	return LocalCount > 0;
}

void AGadget::ServerUse()
{
	Use(1.f, /*@sig:use.second*/);
	Use(this, 2.f, /*@sig:use.third*/);
	Use(GADGET_MUL(1, 2), /*@sig:use.nested*/);
	Use(TEXT("a,b"), /*@sig:use.string*/);
	Use([](int A, int B) { return A + B; }(1, 2), /*@sig:use.lambda*/);
	Use(TMap<int, float>().Num(), /*@sig:use.template*/);
	Use(/*@sig:use.first*/);
	Health->ApplyDamage(/*@sig:member*/);
	OnGadgetHealth.Broadcast(1.f, /*@sig:delegate*/);
	FVector(1.f, /*@sig:ctor*/);
	Recharge(2, /*@sig:defaults*/);
}

void AGadget::Tick(float DeltaSeconds)
{
	Super::Tick(DeltaSeconds);
	Use/*@quick:use*/(1.f);
	Use/*@quick:use.target*/(this, 1.f);
	Charges/*@quick:charges*/ = GADGET_MUL/*@quick:macro.mul*/(2, 3);
	GADGET_LOG/*@quick:macro.va*/(TEXT("x"), 1);
	GADGET_SELF/*@quick:macro.self*/;
	AGadget/*@quick:class*/* Self = this;
	UE_LOG/*@quick:uemacro*/(LogTemp, Log, TEXT("t"));
	OnUsed.Broadcast(LocalHelper/*@quick:unknown*/);
}

int32 AGadget::/*@cls:StaticMethod+Declaration*/CountAll()
{
	/*@cls:UeType*/AGadget* /*@cls:LocalVariable+Declaration*/G = /*@cls:Function*/Cast</*@cls:UeType*/AGadget>(nullptr);
	/*@cls:UeMacro*/UE_LOG(LogTemp, Log, /*@cls:UeMacro*/TEXT("n"));
	int32 N = /*@cls:Macro*/GADGET_SCALE;
	/*@cls:UeType*/EWeaponMode M = /*@cls:UeType*/EWeaponMode::/*@cls:EnumValue*/Burst;
	G->/*@cls:Field*/Charges = 1;
	G->/*@cls:Method*/Use(1.f);
	G->/*@cls:VirtualMethod*/BeginPlay();
	/*@cls:Namespace*/Game::Weapons::/*@cls:Function*/DefaultSpread();
	Game::Weapons::/*@cls:Struct*/FSpread S;
	/*@cls:UeType*/FWeaponData D;
	/*@cls:Delegate*/FOnGadgetUsed Del;
	return /*@cls:LocalVariable*/G ? /*@cls:LocalVariable*/N : 0;
}

void AGadget::/*@cls:VirtualMethod+Declaration*/Recharge(int32 /*@cls:Parameter+Declaration*/Amount, bool bFull) const
{
	/*@cls:Parameter*/Amount++;
}
