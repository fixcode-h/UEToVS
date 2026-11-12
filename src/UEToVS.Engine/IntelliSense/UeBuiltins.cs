using System;
using System.Collections.Generic;
using UEToVS.Contracts;

namespace UEToVS.Engine.IntelliSense
{
    /// <summary>A member of a built-in API table (containers, smart pointers, delegates) used when the index has no declaration.</summary>
    public sealed class BuiltinMember
    {
        public BuiltinMember(string name, string signature, string returns = null, bool isField = false)
        {
            Name = name;
            Signature = signature;
            Returns = returns;
            IsField = isField;
        }

        public string Name { get; }

        /// <summary>Display signature with <c>{T}</c> / <c>{K}</c> / <c>{V}</c> placeholders for template arguments.</summary>
        public string Signature { get; }

        /// <summary>Return type template (<c>{T}*</c>, <c>{V}&amp;</c>...) used for chained inference; null = not useful for chains.</summary>
        public string Returns { get; }

        public bool IsField { get; }
    }

    /// <summary>
    /// Static knowledge the completion engine needs when the index cannot answer: C++ keywords, UE snippets, the APIs of UE
    /// containers / smart pointers / delegates (engine headers are often not indexed, or are templates whose members we do
    /// not instantiate) and the return types of very common engine getters.
    /// </summary>
    public static class UeBuiltins
    {
        public static readonly string[] Keywords =
        {
            "alignas", "alignof", "auto", "bool", "break", "case", "catch", "char", "class", "const", "constexpr", "consteval", "const_cast",
            "continue", "decltype", "default", "delete", "do", "double", "dynamic_cast", "else", "enum", "explicit", "extern", "false", "final",
            "float", "for", "friend", "goto", "if", "inline", "int", "long", "mutable", "namespace", "new", "noexcept", "nullptr", "operator",
            "override", "private", "protected", "public", "reinterpret_cast", "return", "short", "signed", "sizeof", "static", "static_assert",
            "static_cast", "struct", "switch", "template", "this", "thread_local", "throw", "true", "try", "typedef", "typename", "union",
            "unsigned", "using", "virtual", "void", "volatile", "while",
            // UE fundamental types read like keywords in completion lists
            "int8", "int16", "int32", "int64", "uint8", "uint16", "uint32", "uint64", "TCHAR", "ANSICHAR", "UTF8CHAR", "SIZE_T",
        };

        /// <summary>(label, insert text, detail): UE macro snippets offered in general completion.</summary>
        public static readonly (string Label, string Insert, string Detail)[] Snippets =
        {
            ("UPROPERTY", "UPROPERTY()", "UE reflected property"),
            ("UFUNCTION", "UFUNCTION()", "UE reflected function"),
            ("UCLASS", "UCLASS()", "UE reflected class"),
            ("USTRUCT", "USTRUCT()", "UE reflected struct"),
            ("UENUM", "UENUM()", "UE reflected enum"),
            ("UINTERFACE", "UINTERFACE()", "UE interface (UObject side)"),
            ("UMETA", "UMETA()", "Enum value metadata"),
            ("UPARAM", "UPARAM()", "Parameter specifiers"),
            ("GENERATED_BODY", "GENERATED_BODY()", "UHT boilerplate"),
            ("UE_LOG", "UE_LOG(LogTemp, Log, TEXT(\"\"));", "UE_LOG(Category, Verbosity, Format, ...)"),
            ("TEXT", "TEXT(\"\")", "TCHAR string literal"),
            ("LOCTEXT", "LOCTEXT(\"\", \"\")", "Localized FText"),
            ("NSLOCTEXT", "NSLOCTEXT(\"\", \"\", \"\")", "Localized FText with namespace"),
            ("check", "check()", "Assert (not in shipping)"),
            ("checkf", "checkf(, TEXT(\"\"))", "Assert with message"),
            ("checkNoEntry", "checkNoEntry();", "Assert unreachable"),
            ("verify", "verify()", "Assert; expression always evaluated"),
            ("ensure", "ensure()", "Non-fatal assert"),
            ("ensureMsgf", "ensureMsgf(, TEXT(\"\"))", "Non-fatal assert with message"),
            ("ensureAlways", "ensureAlways()", "Non-fatal assert, reports every time"),
            ("DECLARE_LOG_CATEGORY_EXTERN", "DECLARE_LOG_CATEGORY_EXTERN(Log, Log, All);", "Log category declaration"),
            ("DEFINE_LOG_CATEGORY", "DEFINE_LOG_CATEGORY();", "Log category definition"),
            ("DECLARE_DYNAMIC_MULTICAST_DELEGATE", "DECLARE_DYNAMIC_MULTICAST_DELEGATE();", "Blueprint-assignable event"),
            ("DECLARE_DYNAMIC_MULTICAST_DELEGATE_OneParam", "DECLARE_DYNAMIC_MULTICAST_DELEGATE_OneParam(, , );", "Blueprint-assignable event"),
            ("DECLARE_DELEGATE", "DECLARE_DELEGATE();", "Native single-cast delegate"),
            ("DECLARE_MULTICAST_DELEGATE", "DECLARE_MULTICAST_DELEGATE();", "Native multicast delegate"),
            ("TRACE_CPUPROFILER_EVENT_SCOPE", "TRACE_CPUPROFILER_EVENT_SCOPE();", "Insights CPU scope"),
            ("SCOPE_CYCLE_COUNTER", "SCOPE_CYCLE_COUNTER();", "Stat scope"),
            ("GetLifetimeReplicatedProps", "GetLifetimeReplicatedProps(TArray<FLifetimeProperty>& OutLifetimeProps) const", "Replication registration"),
            ("DOREPLIFETIME", "DOREPLIFETIME(, );", "Register a replicated property"),
            ("IsValid", "IsValid()", "Object is non-null and not pending kill"),
            ("Cast", "Cast<>()", "UObject cast"),
            ("CastChecked", "CastChecked<>()", "UObject cast, asserts on failure"),
            ("NewObject", "NewObject<>()", "Create a UObject"),
            ("CreateDefaultSubobject", "CreateDefaultSubobject<>(TEXT(\"\"))", "Create a default subobject (constructor only)"),
            ("MakeShared", "MakeShared<>()", "Create a TSharedRef"),
            ("MakeUnique", "MakeUnique<>()", "Create a TUniquePtr"),
        };

        // {T} = first template argument, {K}/{V} = TMap key/value
        private static readonly Dictionary<string, BuiltinMember[]> Apis = new Dictionary<string, BuiltinMember[]>(StringComparer.Ordinal)
        {
            ["TArray"] = new[]
            {
                M("Num", "int32 Num() const", "int32"), M("IsEmpty", "bool IsEmpty() const", "bool"), M("IsValidIndex", "bool IsValidIndex(int32 Index) const", "bool"),
                M("Add", "int32 Add(const {T}& Item)", "int32"), M("AddUnique", "int32 AddUnique(const {T}& Item)", "int32"),
                M("Emplace", "int32 Emplace(Args&&... Args)", "int32"), M("AddDefaulted", "int32 AddDefaulted()", "int32"),
                M("AddZeroed", "int32 AddZeroed(int32 Count = 1)", "int32"), M("Append", "void Append(const TArray<{T}>& Source)"),
                M("Insert", "int32 Insert(const {T}& Item, int32 Index)", "int32"), M("Remove", "int32 Remove(const {T}& Item)", "int32"),
                M("RemoveSingle", "int32 RemoveSingle(const {T}& Item)", "int32"), M("RemoveAt", "void RemoveAt(int32 Index, int32 Count = 1)"),
                M("RemoveAtSwap", "void RemoveAtSwap(int32 Index, int32 Count = 1)"), M("RemoveAll", "int32 RemoveAll(Predicate Pred)", "int32"),
                M("RemoveSwap", "int32 RemoveSwap(const {T}& Item)", "int32"),
                M("Contains", "bool Contains(const {T}& Item) const", "bool"), M("ContainsByPredicate", "bool ContainsByPredicate(Predicate Pred) const", "bool"),
                M("Find", "int32 Find(const {T}& Item) const", "int32"), M("FindLast", "int32 FindLast(const {T}& Item) const", "int32"),
                M("IndexOfByKey", "int32 IndexOfByKey(const KeyType& Key) const", "int32"), M("IndexOfByPredicate", "int32 IndexOfByPredicate(Predicate Pred) const", "int32"),
                M("FindByKey", "{T}* FindByKey(const KeyType& Key)", "{T}*"), M("FindByPredicate", "{T}* FindByPredicate(Predicate Pred)", "{T}*"),
                M("FilterByPredicate", "TArray<{T}> FilterByPredicate(Predicate Pred) const", "TArray<{T}>"),
                M("Last", "{T}& Last(int32 IndexFromTheEnd = 0)", "{T}&"), M("Top", "{T}& Top()", "{T}&"), M("Pop", "{T} Pop()", "{T}"),
                M("Push", "void Push(const {T}& Item)"), M("GetData", "{T}* GetData()", "{T}*"),
                M("Empty", "void Empty(int32 Slack = 0)"), M("Reset", "void Reset(int32 NewSize = 0)"), M("Reserve", "void Reserve(int32 Number)"),
                M("SetNum", "void SetNum(int32 NewNum)"), M("SetNumZeroed", "void SetNumZeroed(int32 NewNum)"), M("Init", "void Init(const {T}& Element, int32 Number)"),
                M("Shrink", "void Shrink()"), M("Sort", "void Sort()"), M("StableSort", "void StableSort()"), M("Swap", "void Swap(int32 A, int32 B)"),
                M("Max", "int32 Max() const", "int32"), M("GetAllocatedSize", "SIZE_T GetAllocatedSize() const", "SIZE_T"),
                M("CreateIterator", "TIterator CreateIterator()"), M("CreateConstIterator", "TConstIterator CreateConstIterator() const"),
            },
            ["TMap"] = new[]
            {
                M("Add", "{V}& Add(const {K}& Key, const {V}& Value)", "{V}&"), M("Emplace", "{V}& Emplace({K} Key, {V} Value)", "{V}&"),
                M("Find", "{V}* Find(const {K}& Key)", "{V}*"), M("FindRef", "{V} FindRef(const {K}& Key) const", "{V}"),
                M("FindChecked", "{V}& FindChecked(const {K}& Key)", "{V}&"), M("FindOrAdd", "{V}& FindOrAdd(const {K}& Key)", "{V}&"),
                M("FindKey", "const {K}* FindKey(const {V}& Value) const", "{K}*"),
                M("Contains", "bool Contains(const {K}& Key) const", "bool"), M("Remove", "int32 Remove(const {K}& Key)", "int32"),
                M("RemoveAndCopyValue", "bool RemoveAndCopyValue(const {K}& Key, {V}& OutRemovedValue)", "bool"),
                M("Num", "int32 Num() const", "int32"), M("IsEmpty", "bool IsEmpty() const", "bool"), M("Empty", "void Empty(int32 Slack = 0)"),
                M("Reset", "void Reset()"), M("Reserve", "void Reserve(int32 Number)"),
                M("GenerateKeyArray", "int32 GenerateKeyArray(TArray<{K}>& OutArray) const", "int32"),
                M("GenerateValueArray", "int32 GenerateValueArray(TArray<{V}>& OutArray) const", "int32"),
                M("GetKeys", "int32 GetKeys(TArray<{K}>& OutKeys) const", "int32"), M("KeySort", "void KeySort(Predicate Pred)"),
                M("ValueSort", "void ValueSort(Predicate Pred)"), M("CreateIterator", "TIterator CreateIterator()"),
            },
            ["TSet"] = new[]
            {
                M("Add", "FSetElementId Add(const {T}& Item)"), M("Emplace", "FSetElementId Emplace(Args&&... Args)"),
                M("Find", "{T}* Find(const {T}& Key)", "{T}*"), M("Contains", "bool Contains(const {T}& Key) const", "bool"),
                M("Remove", "int32 Remove(const {T}& Key)", "int32"), M("Num", "int32 Num() const", "int32"), M("IsEmpty", "bool IsEmpty() const", "bool"),
                M("Empty", "void Empty(int32 Slack = 0)"), M("Reset", "void Reset()"), M("Array", "TArray<{T}> Array() const", "TArray<{T}>"),
                M("Union", "TSet<{T}> Union(const TSet<{T}>& Other) const", "TSet<{T}>"), M("Intersect", "TSet<{T}> Intersect(const TSet<{T}>& Other) const", "TSet<{T}>"),
                M("Difference", "TSet<{T}> Difference(const TSet<{T}>& Other) const", "TSet<{T}>"),
            },
            ["TSharedPtr"] = new[]
            {
                M("IsValid", "bool IsValid() const", "bool"), M("Get", "{T}* Get() const", "{T}*"), M("Reset", "void Reset()"),
                M("ToSharedRef", "TSharedRef<{T}> ToSharedRef() const", "TSharedRef<{T}>"), M("GetSharedReferenceCount", "int32 GetSharedReferenceCount() const", "int32"),
                M("IsUnique", "bool IsUnique() const", "bool"),
            },
            ["TSharedRef"] = new[]
            {
                M("Get", "{T}& Get() const", "{T}&"), M("ToSharedPtr", "TSharedPtr<{T}> ToSharedPtr() const", "TSharedPtr<{T}>"),
                M("ToWeakPtr", "TWeakPtr<{T}> ToWeakPtr() const", "TWeakPtr<{T}>"), M("IsUnique", "bool IsUnique() const", "bool"),
                M("GetSharedReferenceCount", "int32 GetSharedReferenceCount() const", "int32"),
            },
            ["TWeakPtr"] = new[]
            {
                M("IsValid", "bool IsValid() const", "bool"), M("Pin", "TSharedPtr<{T}> Pin() const", "TSharedPtr<{T}>"), M("Reset", "void Reset()"),
                M("HasSameObject", "bool HasSameObject(const void* Other) const", "bool"),
            },
            ["TUniquePtr"] = new[]
            {
                M("IsValid", "bool IsValid() const", "bool"), M("Get", "{T}* Get() const", "{T}*"), M("Reset", "void Reset({T}* InPtr = nullptr)"),
                M("Release", "{T}* Release()", "{T}*"),
            },
            ["TWeakObjectPtr"] = new[]
            {
                M("IsValid", "bool IsValid(bool bEvenIfPendingKill = false) const", "bool"), M("Get", "{T}* Get() const", "{T}*"),
                M("Reset", "void Reset()"), M("IsStale", "bool IsStale() const", "bool"), M("IsExplicitlyNull", "bool IsExplicitlyNull() const", "bool"),
                M("GetEvenIfUnreachable", "{T}* GetEvenIfUnreachable() const", "{T}*"),
            },
            ["TObjectPtr"] = new[]
            {
                M("Get", "{T}* Get() const", "{T}*"), M("IsNull", "bool IsNull() const", "bool"), M("IsResolved", "bool IsResolved() const", "bool"),
            },
            ["TSoftObjectPtr"] = new[]
            {
                M("IsValid", "bool IsValid() const", "bool"), M("IsNull", "bool IsNull() const", "bool"), M("IsPending", "bool IsPending() const", "bool"),
                M("Get", "{T}* Get() const", "{T}*"), M("LoadSynchronous", "{T}* LoadSynchronous() const", "{T}*"), M("Reset", "void Reset()"),
                M("ToSoftObjectPath", "const FSoftObjectPath& ToSoftObjectPath() const", "FSoftObjectPath"), M("ToString", "FString ToString() const", "FString"),
                M("GetAssetName", "FString GetAssetName() const", "FString"), M("GetLongPackageName", "FString GetLongPackageName() const", "FString"),
            },
            ["TSoftClassPtr"] = new[]
            {
                M("IsValid", "bool IsValid() const", "bool"), M("IsNull", "bool IsNull() const", "bool"), M("IsPending", "bool IsPending() const", "bool"),
                M("Get", "UClass* Get() const", "UClass*"), M("LoadSynchronous", "UClass* LoadSynchronous() const", "UClass*"),
                M("ToSoftObjectPath", "const FSoftObjectPath& ToSoftObjectPath() const", "FSoftObjectPath"),
            },
            ["TSubclassOf"] = new[]
            {
                M("Get", "UClass* Get() const", "UClass*"), M("GetDefaultObject", "{T}* GetDefaultObject() const", "{T}*"),
            },
            ["TOptional"] = new[]
            {
                M("IsSet", "bool IsSet() const", "bool"), M("GetValue", "{T}& GetValue()", "{T}&"), M("Get", "const {T}& Get(const {T}& DefaultValue) const", "{T}"),
                M("Reset", "void Reset()"), M("Emplace", "{T}& Emplace(Args&&... Args)", "{T}&"),
            },
            ["TScriptInterface"] = new[]
            {
                M("GetObject", "UObject* GetObject() const", "UObject*"), M("GetInterface", "{T}* GetInterface() const", "{T}*"),
                M("SetObject", "void SetObject(UObject* InObjectPointer)"), M("SetInterface", "void SetInterface({T}* InInterfacePointer)"),
            },
        };

        private static readonly BuiltinMember[] DynamicMulticast =
        {
            M("AddDynamic", "void AddDynamic(UserClass* Object, FuncName Function)"), M("AddUniqueDynamic", "void AddUniqueDynamic(UserClass* Object, FuncName Function)"),
            M("RemoveDynamic", "void RemoveDynamic(UserClass* Object, FuncName Function)"), M("IsAlreadyBound", "bool IsAlreadyBound(UserClass* Object, FuncName Function) const", "bool"),
            M("Broadcast", "void Broadcast({Params}) const"), M("Clear", "void Clear()"), M("IsBound", "bool IsBound() const", "bool"),
            M("RemoveAll", "void RemoveAll(const void* InUserObject)"), M("Contains", "bool Contains(const UObject* InObject, FName InFunctionName) const", "bool"),
        };

        private static readonly BuiltinMember[] DynamicSingle =
        {
            M("BindDynamic", "void BindDynamic(UserClass* Object, FuncName Function)"), M("BindUFunction", "void BindUFunction(UObject* InObject, const FName& InFunctionName)"),
            M("Execute", "{Ret} Execute({Params}) const", "{Ret}"), M("ExecuteIfBound", "bool ExecuteIfBound({Params}) const", "bool"),
            M("IsBound", "bool IsBound() const", "bool"), M("Unbind", "void Unbind()"), M("Clear", "void Clear()"),
        };

        private static readonly BuiltinMember[] NativeMulticast =
        {
            M("AddUObject", "FDelegateHandle AddUObject(UserClass* InUserObject, FuncPtr InFunc, VarTypes... Vars)", "FDelegateHandle"),
            M("AddRaw", "FDelegateHandle AddRaw(UserClass* InUserObject, FuncPtr InFunc, VarTypes... Vars)", "FDelegateHandle"),
            M("AddSP", "FDelegateHandle AddSP(const TSharedRef<UserClass>& InUserObjectRef, FuncPtr InFunc, VarTypes... Vars)", "FDelegateHandle"),
            M("AddLambda", "FDelegateHandle AddLambda(FunctorType&& InFunctor, VarTypes... Vars)", "FDelegateHandle"),
            M("AddWeakLambda", "FDelegateHandle AddWeakLambda(UserClass* InUserObject, FunctorType&& InFunctor)", "FDelegateHandle"),
            M("AddStatic", "FDelegateHandle AddStatic(FuncPtr InFunc, VarTypes... Vars)", "FDelegateHandle"),
            M("AddUFunction", "FDelegateHandle AddUFunction(UObject* InUserObject, const FName& InFunctionName, VarTypes... Vars)", "FDelegateHandle"),
            M("Broadcast", "void Broadcast({Params}) const"), M("Remove", "bool Remove(FDelegateHandle Handle)", "bool"),
            M("RemoveAll", "int32 RemoveAll(const void* InUserObject)", "int32"), M("Clear", "void Clear()"), M("IsBound", "bool IsBound() const", "bool"),
            M("IsBoundToObject", "bool IsBoundToObject(const void* InUserObject) const", "bool"),
        };

        private static readonly BuiltinMember[] NativeSingle =
        {
            M("BindUObject", "void BindUObject(UserClass* InUserObject, FuncPtr InFunc, VarTypes... Vars)"),
            M("BindRaw", "void BindRaw(UserClass* InUserObject, FuncPtr InFunc, VarTypes... Vars)"),
            M("BindSP", "void BindSP(const TSharedRef<UserClass>& InUserObjectRef, FuncPtr InFunc, VarTypes... Vars)"),
            M("BindLambda", "void BindLambda(FunctorType&& InFunctor, VarTypes... Vars)"),
            M("BindWeakLambda", "void BindWeakLambda(UserClass* InUserObject, FunctorType&& InFunctor)"),
            M("BindStatic", "void BindStatic(FuncPtr InFunc, VarTypes... Vars)"),
            M("BindUFunction", "void BindUFunction(UObject* InUserObject, const FName& InFunctionName, VarTypes... Vars)"),
            M("Execute", "{Ret} Execute({Params}) const", "{Ret}"), M("ExecuteIfBound", "bool ExecuteIfBound({Params}) const", "bool"),
            M("IsBound", "bool IsBound() const", "bool"), M("Unbind", "void Unbind()"), M("GetHandle", "FDelegateHandle GetHandle() const", "FDelegateHandle"),
        };

        /// <summary>Return types of very common engine getters, used only when the index cannot resolve the call.</summary>
        public static readonly IReadOnlyDictionary<string, string> WellKnownReturns = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["GetOwner"] = "AActor*", ["GetWorld"] = "UWorld*", ["GetGameInstance"] = "UGameInstance*", ["GetPawn"] = "APawn*",
            ["GetController"] = "AController*", ["GetPlayerController"] = "APlayerController*", ["GetFirstPlayerController"] = "APlayerController*",
            ["GetRootComponent"] = "USceneComponent*", ["GetCharacterMovement"] = "UCharacterMovementComponent*", ["GetMesh"] = "USkeletalMeshComponent*",
            ["GetCapsuleComponent"] = "UCapsuleComponent*", ["GetMovementComponent"] = "UPawnMovementComponent*", ["GetActorLocation"] = "FVector",
            ["GetActorRotation"] = "FRotator", ["GetActorTransform"] = "FTransform", ["GetActorForwardVector"] = "FVector", ["GetActorRightVector"] = "FVector",
            ["GetActorUpVector"] = "FVector", ["GetComponentLocation"] = "FVector", ["GetComponentRotation"] = "FRotator",
            ["GetComponentTransform"] = "FTransform", ["GetVelocity"] = "FVector", ["GetName"] = "FString", ["GetFName"] = "FName",
            ["GetClass"] = "UClass*", ["GetOuter"] = "UObject*", ["GetTimerManager"] = "FTimerManager&", ["GetAuthGameMode"] = "AGameModeBase*",
            ["GetGameState"] = "AGameStateBase*", ["GetPlayerState"] = "APlayerState*", ["GetInstigator"] = "APawn*",
            ["GetInstigatorController"] = "AController*", ["GetNetOwner"] = "const AActor*", ["GetLevel"] = "ULevel*",
            ["GetHUD"] = "AHUD*", ["GetLocalPlayer"] = "ULocalPlayer*", ["GetCameraManager"] = "APlayerCameraManager*",
            ["GetAnimInstance"] = "UAnimInstance*", ["GetSkeletalMeshAsset"] = "USkeletalMesh*", ["GetStaticMesh"] = "UStaticMesh*",
            ["GetDefaultObject"] = "UObject*", ["GetSafeNormal"] = "FVector", ["GetUnsafeNormal"] = "FVector", ["Vector"] = "FVector",
            ["Rotation"] = "FRotator", ["Quaternion"] = "FQuat", ["GetLocation"] = "FVector", ["GetRotation"] = "FQuat",
            ["GetScale3D"] = "FVector", ["ToString"] = "FString",
        };

        private static BuiltinMember M(string name, string signature, string returns = null) => new BuiltinMember(name, signature, returns);

        /// <summary>Built-in API of a UE template (TArray, TMap, TSharedPtr...), or null.</summary>
        public static IReadOnlyList<BuiltinMember> ApiOf(string templateName)
        {
            if (templateName == null) return null;
            if (Apis.TryGetValue(templateName, out var m)) return m;
            return templateName switch
            {
                "TInlineComponentArray" or "TArrayView" or "TConstArrayView" or "TStaticArray" or "TIndirectArray" => Apis["TArray"],
                "TMultiMap" or "TSortedMap" => Apis["TMap"],
                "TStrongObjectPtr" or "TLazyObjectPtr" => Apis["TWeakObjectPtr"],
                "TNonNullPtr" => Apis["TObjectPtr"],
                _ => null,
            };
        }

        /// <summary>API of a delegate type, chosen by its declaring macro family.</summary>
        public static IReadOnlyList<BuiltinMember> DelegateApi(bool dynamic, bool multicast) =>
            dynamic ? (multicast ? DynamicMulticast : DynamicSingle) : (multicast ? NativeMulticast : NativeSingle);

        /// <summary>Whether a token text is a C++ keyword (or UE fundamental type listed with them).</summary>
        public static bool IsKeyword(string s) => KeywordSet.Contains(s);

        private static readonly HashSet<string> KeywordSet = new HashSet<string>(Keywords, StringComparer.Ordinal);

        /// <summary>Substitutes {T}/{K}/{V}/{Params}/{Ret} placeholders.</summary>
        public static string Instantiate(string text, string t, string k, string v, string parameters = null, string ret = null)
        {
            if (string.IsNullOrEmpty(text)) return text;
            return text.Replace("{T}", t ?? "T").Replace("{K}", k ?? "K").Replace("{V}", v ?? "V")
                .Replace("{Params}", parameters ?? string.Empty).Replace("{Ret}", string.IsNullOrEmpty(ret) ? "void" : ret);
        }

        public static SymbolKind KindOf(BuiltinMember m) => m.IsField ? SymbolKind.Field : SymbolKind.Method;
    }
}
