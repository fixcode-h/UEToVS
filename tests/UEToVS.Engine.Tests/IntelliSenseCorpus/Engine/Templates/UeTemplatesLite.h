// Engine stand-in (Origin = Engine): UE smart pointers / TOptional with their real template parameter names (ObjectType,
// OptionalType, InterfaceType...), so generic template instantiation is exercised against declarations from the index
// rather than the built-in rule tables (v0.4.5).
#pragma once

#include "CoreTypes.h"

enum class ESPMode : unsigned char
{
	NotThreadSafe = 0,
	ThreadSafe = 1,
};

template<typename OptionalType>
struct TOptional
{
	bool IsSet() const;
	OptionalType& GetValue();
	const OptionalType& Get(const OptionalType& DefaultValue) const;
	OptionalType* GetPtrOrNull();
	void Reset();
};

template<class ObjectType, ESPMode InMode = ESPMode::ThreadSafe>
class TSharedRef;

template<class ObjectType, ESPMode InMode = ESPMode::ThreadSafe>
class TSharedPtr
{
public:
	ObjectType* Get() const;
	ObjectType* operator->() const;
	bool IsValid() const;
	void Reset();
	TSharedRef<ObjectType, InMode> ToSharedRef() const;
	int32 GetSharedReferenceCount() const;
	bool IsUnique() const;
};

template<class ObjectType, ESPMode InMode>
class TSharedRef
{
public:
	ObjectType& Get() const;
	ObjectType* operator->() const;
	TSharedPtr<ObjectType, InMode> ToSharedPtr() const;
	bool IsUnique() const;
	int32 GetSharedReferenceCount() const;
};

template<class ObjectType, ESPMode InMode = ESPMode::ThreadSafe>
class TWeakPtr
{
public:
	TSharedPtr<ObjectType, InMode> Pin() const;
	bool IsValid() const;
	void Reset();
};

template<typename T>
struct TDefaultDelete
{
	void operator()(T* Ptr) const;
};

template<typename T, typename Deleter = TDefaultDelete<T>>
class TUniquePtr
{
public:
	T* Get() const;
	T* operator->() const;
	T* Release();
	bool IsValid() const;
	void Reset(T* InPtr = nullptr);
};

struct FWeakObjectPtr
{
	bool IsValid() const;
};

template<class T, class TWeakObjectPtrBase = FWeakObjectPtr>
struct TWeakObjectPtr
{
	T* Get() const;
	T* operator->() const;
	bool IsValid() const;
	void Reset();
	bool IsStale() const;
};

template<class T>
class TSubclassOf
{
public:
	UClass* Get() const;
	T* GetDefaultObject() const;
};

template<typename T>
class TSoftObjectPtr
{
public:
	T* Get() const;
	T* LoadSynchronous() const;
	bool IsValid() const;
	bool IsNull() const;
	bool IsPending() const;
	void Reset();
};

template<class InterfaceType>
class TScriptInterface
{
public:
	InterfaceType* GetInterface() const;
	UObject* GetObject() const;
	InterfaceType* operator->() const;
};
