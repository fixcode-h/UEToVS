// Engine stand-in (Origin = Engine): just enough of CoreUObject / Engine for the navigation corpus.
#pragma once

struct /*@def:FVector*/FVector
{
	float /*@def:FVector.X*/X;
	float Y;
	float Z;

	FVector() : X(0), Y(0), Z(0) {}
	FVector(float InX, float InY, float InZ) : X(InX), Y(InY), Z(InZ) {}

	float /*@def:FVector.Size*/Size() const;
	FVector GetSafeNormal() const;

	static const FVector /*@def:FVector.ZeroVector*/ZeroVector;
};

class /*@def:UObject*/UObject
{
public:
	virtual ~UObject();
	virtual void /*@def:UObject.PostInitProperties*/PostInitProperties();
	class UWorld* GetWorld() const;
	FString GetName() const;
};

class /*@def:UClass*/UClass : public UObject
{
};

template<typename T>
T* /*@def:Cast*/Cast(UObject* Src);

template<typename T>
T* NewObject(UObject* Outer);
