// IntelliSense corpus (v0.4.5): user class templates, nested typedef / using aliases, alias templates, operator->,
// function templates with explicit arguments. Carets are in GameTemplates.cpp.
#pragma once

#include "Gadgets/Gadget.h"
#include "HealthComponent.h"
#include "Templates/UeTemplatesLite.h"

class IGadgetInterface
{
public:
	virtual void Ping();
	virtual int32 PingCount() const;
};

template<typename T>
struct TGameBox
{
	using ValueType = T;
	typedef T* PointerType;

	ValueType Get() const;
	PointerType GetPtr() const;
	template<typename U>
	U* As() const;

	T Value;
};

template<typename T, int32 N = 4>
class TGameRing : public TGameBox<T*>
{
public:
	T& At(int32 Index);
	TGameBox<T> Front() const;
	int32 Capacity() const;
};

template<typename T>
class TGamePtr
{
public:
	T* operator->() const;
	T* Get() const;
	bool IsSet() const;
};

template<typename K, typename V>
struct TGamePair
{
	K Key;
	V Value;
	const V& GetValue() const;
};

template<typename T>
using TGameBoxAlias = TGameBox<T>;

using FGadgetBox = TGameBox<AGadget>;
using FGadgetPtr = TGamePtr<AGadget>;
typedef TSharedPtr<FWeaponData> FWeaponDataPtr;

template<typename T>
T* MakeGameThing(int32 Seed);

template<typename T>
TGameBox<T> BoxOf(const T& In);

struct FTemplateUser
{
	TGameBox<AGadget*> PtrBox;
	TGameRing<UHealthComponent> Ring;
	TGamePtr<AGadget> Ptr;
	FGadgetPtr AliasPtr;
	FGadgetBox AliasBox;
	TGameBoxAlias<UHealthComponent*> AliasTemplate;
	TGamePair<FName, AGadget*> Pair;
	TOptional<FVector> MaybeLoc;
	TOptional<AGadget*> MaybeGadget;
	TSharedPtr<FWeaponData> Shared;
	TSharedRef<FWeaponData> SharedRef;
	TWeakPtr<FWeaponData> WeakShared;
	FWeaponDataPtr SharedAlias;
	TUniquePtr<FWeaponData> Unique;
	TWeakObjectPtr<AGadget> Weak;
	TSoftObjectPtr<AGadget> Soft;
	TSubclassOf<AGadget> GadgetClass;
	TScriptInterface<IGadgetInterface> Iface;
	TGameBox<TGameBox<AGadget*>> Nested;

	void Run(AActor* Actor);
};
