using System;
using System.IO;
using System.Linq;
using UEToVS.Contracts;
using Xunit;
using static UEToVS.Engine.Syntax.Tests.TestUtil;

namespace UEToVS.Engine.Syntax.Tests
{
    /// <summary>The legacy prototype's sample files, with the expectations of its scanner tests carried over.</summary>
    public class SampleTests
    {
        private static FileSummary ParseSample(string name) =>
            CppParser.ParseFile(Path.Combine(AppContext.BaseDirectory, "Samples", name));

        [Fact]
        public void HeaderDeclarations()
        {
            var s = ParseSample("MyCharacter.h");

            var cls = One(s, "AMyCharacter", SymbolKind.Class);
            Assert.Equal(UeFlags.UClass | UeFlags.GeneratedBody, cls.Ue);
            Assert.Equal("Blueprintable", cls.Specifiers);
            Assert.Equal(28, cls.Line);
            Assert.Equal(new[] { "ACharacter" }, cls.Bases);

            var st = One(s, "FWeaponData", SymbolKind.Struct);
            Assert.Equal(UeFlags.UStruct | UeFlags.GeneratedBody, st.Ue);

            var e = One(s, "EWeaponState", SymbolKind.Enum);
            Assert.Equal(UeFlags.UEnum, e.Ue);
            Assert.Equal("EWeaponState", One(s, "Reloading", SymbolKind.EnumValue).Container);
            var idle = One(s, "Idle", SymbolKind.EnumValue);
            Assert.Equal(UeFlags.UMeta, idle.Ue);
            Assert.Equal("DisplayName = \"Idle\"", idle.Specifiers);
            One(s, "Firing", SymbolKind.EnumValue);

            var del = One(s, "FOnHealthChanged", SymbolKind.Delegate);
            Assert.True((del.Ue & UeFlags.UDelegate) != 0);

            var fire = One(s, "Server_Fire", SymbolKind.Method);
            Assert.Equal("AMyCharacter", fire.Container);
            Assert.Equal(UeFlags.UFunction, fire.Ue);
            Assert.Contains("WithValidation", fire.Specifiers);
            Assert.False(fire.IsDefinition);
            Assert.Equal("void Server_Fire(const FVector& Target)", fire.Signature);

            var tick = One(s, "Tick", SymbolKind.Method);
            Assert.Equal(DeclModifiers.Virtual | DeclModifiers.Override, tick.Modifiers);
            One(s, "OnHit", SymbolKind.Method);
            Assert.True(One(s, "FindComp", SymbolKind.Method).IsDefinition);
            One(s, "AMyCharacter", SymbolKind.Constructor);

            var damage = One(s, "Damage", SymbolKind.Field);
            Assert.Equal("FWeaponData", damage.Container);
            Assert.Equal(UeFlags.UProperty, damage.Ue);
            var ammo = One(s, "Ammo", SymbolKind.Field);
            Assert.Equal(UeFlags.UProperty, ammo.Ue);
            Assert.Equal(AccessLevel.Protected, ammo.Access);
            Assert.Contains("meta = (AllowPrivateAccess = \"true\")", ammo.Specifiers);
            Assert.Equal(UeFlags.None, One(s, "bIsAiming", SymbolKind.Field).Ue);
            Assert.Equal(AccessLevel.Private, One(s, "Callbacks", SymbolKind.Field).Access);
            One(s, "OnHealthChanged", SymbolKind.Field);
            One(s, "Sockets", SymbolKind.Field);

            Assert.DoesNotContain(s.Declarations, x => x.Name == "GENERATED_BODY" || x.Name == "public" || x.Name == "MYGAME_API");
            Assert.Equal(new[] { "CoreMinimal.h", "GameFramework/Character.h", "MyCharacter.generated.h" }, s.Includes.Select(i => i.Path).ToArray());
            Assert.Empty(s.Diagnostics);
        }

        [Fact]
        public void SourceDefinitions()
        {
            var s = ParseSample("MyCharacter.cpp");
            var impl = One(s, "Server_Fire_Implementation");
            Assert.Equal(SymbolKind.Method, impl.Kind);
            Assert.Equal("AMyCharacter", impl.Container);
            Assert.True(impl.IsDefinition);
            One(s, "Server_Fire_Validate");
            One(s, "OnHit_Implementation");
            Assert.Equal("AMyCharacter", One(s, "Tick").Container);
            One(s, "LogMyCharacter", SymbolKind.Variable);
            One(s, "ComputeSpread", SymbolKind.Function);
            One(s, "HelperCount", SymbolKind.Variable);
            var ctor = One(s, "AMyCharacter", SymbolKind.Constructor);
            Assert.True(ctor.IsDefinition);
            Assert.DoesNotContain(s.Declarations, x => x.Name == "Lambda" || x.Name == "I" || x.Name == "UE_LOG");
            Assert.Empty(s.Diagnostics);

            // body occurrences
            Assert.Contains(s.Occurrences, o => o.Name == "Ammo" && o.Role == OccurrenceRole.Write); // initializer list and +=
            Assert.Contains(s.Occurrences, o => o.Name == "bCanEverTick" && o.Qualifier == MemberQualifier.Dot && (o.Role & OccurrenceRole.Write) != 0);
            Assert.Contains(s.Occurrences, o => o.Name == "Tick" && o.Qualifier == MemberQualifier.Scope && (o.Role & OccurrenceRole.Call) != 0);
            Assert.Contains(s.Occurrences, o => o.Name == "LogMyCharacter" && o.Role == OccurrenceRole.Read);
            Assert.All(s.Occurrences, o => Assert.True(o.Function >= 0));
        }

        [Fact]
        public void TrickyComponentHeader()
        {
            var s = ParseSample("TrickyComponent.h");
            Assert.Empty(s.Diagnostics);
            Assert.Equal("ETrickyMode::Type", One(s, "Fast", SymbolKind.EnumValue).Container);
            Assert.True((One(s, "AsPtr", SymbolKind.Function).Modifiers & DeclModifiers.Template) != 0);
            One(s, "operator<", SymbolKind.Method);
            One(s, "operator>", SymbolKind.Method);
            Assert.DoesNotContain(s.Declarations, d => d.Name == "GetTypeHash" || d.Name == "FTrickyDetails" || d.Name == "UWorld" || d.Name == "FHitResult");

            var cls = One(s, "UTrickyComponent", SymbolKind.Class);
            Assert.Equal("Abstract, Config = Game, meta = (BlueprintSpawnableComponent)", cls.Specifiers);
            Assert.Equal(new[] { "UActorComponent", "FTickableGameObject" }, cls.Bases);
            Assert.Equal("UActorComponent", Assert.Single(s.Declarations, d => d.IsImplicit && d.Name == "Super").Type);

            Assert.Equal("const FObjectInitializer& ObjectInitializer = FObjectInitializer::Get()", One(s, "UTrickyComponent", SymbolKind.Constructor).Parameters);
            Assert.Equal(UeFlags.UFunction, One(s, "OldThing").Ue);
            Assert.Equal("float DeltaTime, enum ELevelTick TickType, FActorComponentTickFunction* ThisTickFunction", One(s, "TickComponent").Parameters);
            Assert.True(One(s, "GetStatId").IsDefinition);
            Assert.True((One(s, "IsTickable").Modifiers & DeclModifiers.Pure) != 0);
            Assert.Equal("const TCHAR* Default = TEXT(\"none\")", One(s, "GetLabel").Parameters);
            var find = One(s, "Find", SymbolKind.Method);
            Assert.True((find.Modifiers & DeclModifiers.Static) != 0);
            Assert.Equal("UTrickyComponent*", find.Type);
            Assert.Equal("UTrickyComponent", One(s, "FOnTricky", SymbolKind.Delegate).Container);
            Assert.Equal("FOnTricky&", One(s, "OnTricky").Type);

            Assert.True(One(s, "PostEditChangeChainProperty").IsConditional);
            var icon = One(s, "EditorIcon", SymbolKind.Field);
            Assert.True(icon.IsConditional);
            Assert.Equal(UeFlags.UProperty, icon.Ue);

            Assert.Contains("EditCondition = \"bEnabled && Mode != ETrickyMode::None\"", One(s, "Lookup").Specifiers);
            Assert.Equal("TArray<TPair<FName, TSoftObjectPtr<UObject>>>", One(s, "Lookup").Type);
            Assert.Equal(UeFlags.UProperty, One(s, "bEnabled").Ue);
            Assert.Equal("TFunction<bool(const FHitResult&, float)>", One(s, "Filter").Type);
            One(s, "Handles", SymbolKind.Field);
            Assert.Equal(AccessLevel.Private, One(s, "Mode", SymbolKind.Field).Access);
            Assert.Equal("ETrickyMode::Type", One(s, "Mode", SymbolKind.Field).Type);
            Assert.Equal(UeFlags.UFunction, One(s, "OnRep_Value").Ue);
            Assert.Null(One(s, "operator==", SymbolKind.Function).Container);
            Assert.True(s.Includes[1].IsMacro);
        }
    }
}
