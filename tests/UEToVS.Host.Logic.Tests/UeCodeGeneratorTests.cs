using UEToVS.Host.Logic.Generate;
using Xunit;

namespace UEToVS.Host.Logic.Tests
{
    public sealed class UeCodeGeneratorTests
    {
        // Normalized: a Windows checkout (core.autocrlf) turns this verbatim string into CRLF.
        private static readonly string Header = (@"#pragma once
#include ""Weapon.generated.h""

// class ANotThis { };
UCLASS()
class MYGAME_API AWeapon : public AActor
{
	GENERATED_BODY()

public:
	UFUNCTION(Server, Reliable, WithValidation)
	void ServerFire(int32 Count, float Scale = 1.f);

	void Reload(int32 Amount = 3);

	UPROPERTY(ReplicatedUsing = OnRep_Ammo)
	int32 Ammo;
	UPROPERTY(Replicated, Category = ""Net"")
	TArray<FVector> Hits;
	UPROPERTY(EditAnywhere, Category = Replicated)
	int32 NotReplicated;
	UPROPERTY(Replicated)
	uint8 bFlag : 1;
};
").Replace("\r\n", "\n");

        private static int Caret(string marker) => Header.IndexOf(marker, System.StringComparison.Ordinal);

        [Fact]
        public void FindClass_InnermostBody()
        {
            var c = UeCodeGenerator.FindClass(Header, Caret("int32 Ammo"));
            Assert.Equal("AWeapon", c.Name);
            Assert.Equal("\t", c.MemberIndent);
            Assert.True(c.AfterGeneratedBody > c.BodyOpen);
            Assert.Null(UeCodeGenerator.FindClass(Header, 5));
            Assert.Equal(new[] { "Ammo", "Hits", "bFlag" }, UeCodeGenerator.ReplicatedProperties(Header, c));
        }

        [Fact]
        public void Constructor_DeclarationAndDefinition()
        {
            var r = UeCodeGenerator.Constructor(Header, Caret("int32 Ammo"), "#include \"Weapon.h\"\n", "\n");
            Assert.True(r.Ok);
            string h = GenerateResult.Apply(Header, r.Header);
            Assert.Contains("GENERATED_BODY()\n\npublic:\n\tAWeapon();\n", h);
            string s = GenerateResult.Apply("#include \"Weapon.h\"\n", r.Source);
            Assert.Equal("#include \"Weapon.h\"\n\nAWeapon::AWeapon()\n{\n\tPrimaryActorTick.bCanEverTick = false;\n}\n", s);
            Assert.Equal("exists", UeCodeGenerator.Constructor(h, Caret("int32 Ammo") + 20, null, "\n").Problem);
            Assert.Equal("no-class", UeCodeGenerator.Constructor(Header, 0, null, "\n").Problem);
        }

        [Fact]
        public void Replication_NewDefinition()
        {
            string src = "#include \"Weapon.h\"\n#include \"Engine/World.h\"\n\nvoid AWeapon::Foo() {}\n";
            var r = UeCodeGenerator.Replication(Header, Caret("int32 Ammo"), src, "\n");
            Assert.True(r.Ok, r.Problem);
            string h = GenerateResult.Apply(Header, r.Header);
            Assert.Contains("\tvirtual void GetLifetimeReplicatedProps(TArray<FLifetimeProperty>& OutLifetimeProps) const override;\n", h);
            string s = GenerateResult.Apply(src, r.Source);
            Assert.Contains("#include \"Engine/World.h\"\n#include \"Net/UnrealNetwork.h\"\n", s);
            Assert.Contains("void AWeapon::GetLifetimeReplicatedProps(TArray<FLifetimeProperty>& OutLifetimeProps) const\n{\n\tSuper::GetLifetimeReplicatedProps(OutLifetimeProps);\n\n\tDOREPLIFETIME(AWeapon, Ammo);\n\tDOREPLIFETIME(AWeapon, Hits);\n\tDOREPLIFETIME(AWeapon, bFlag);\n}\n", s);
        }

        [Fact]
        public void Replication_MergesIntoExistingDefinition()
        {
            string src = "#include \"Weapon.h\"\n#include \"Net/UnrealNetwork.h\"\n\nvoid AWeapon::GetLifetimeReplicatedProps(TArray<FLifetimeProperty>& OutLifetimeProps) const\n{\n\tSuper::GetLifetimeReplicatedProps(OutLifetimeProps);\n\tDOREPLIFETIME_CONDITION(AWeapon, Ammo, COND_OwnerOnly);\n}\n";
            string header = Header.Replace("\tvoid Reload(", "\tvirtual void GetLifetimeReplicatedProps(TArray<FLifetimeProperty>& OutLifetimeProps) const override;\n\tvoid Reload(");
            var r = UeCodeGenerator.Replication(header, header.IndexOf("int32 Ammo"), src, "\n");
            Assert.True(r.Ok, r.Problem);
            Assert.Empty(r.Header);
            string s = GenerateResult.Apply(src, r.Source);
            Assert.Contains("COND_OwnerOnly);\n\tDOREPLIFETIME(AWeapon, Hits);\n\tDOREPLIFETIME(AWeapon, bFlag);\n}\n", s);
            Assert.Equal(new[] { "Hits", "bFlag" }, r.Generated);
            Assert.Equal("exists", UeCodeGenerator.Replication(header, header.IndexOf("int32 Ammo"), s, "\n").Problem);
        }

        [Fact]
        public void Rpc_OnRpcDeclaration_ImplementationAndValidate()
        {
            var r = UeCodeGenerator.Rpc(Header, Caret("ServerFire(int32") + 3, "#include \"Weapon.h\"\n", "\n");
            Assert.True(r.Ok, r.Problem);
            Assert.Empty(r.Header);
            string s = GenerateResult.Apply("#include \"Weapon.h\"\n", r.Source);
            Assert.Contains("void AWeapon::ServerFire_Implementation(int32 Count, float Scale)\n{\n}\n\nbool AWeapon::ServerFire_Validate(int32 Count, float Scale)\n{\n\treturn true;\n}\n", s);
            Assert.Equal("exists", UeCodeGenerator.Rpc(Header, Caret("ServerFire(int32") + 3, s, "\n").Problem);
        }

        [Fact]
        public void Rpc_OnPlainFunction_CreatesServerRpc()
        {
            var r = UeCodeGenerator.Rpc(Header, Caret("Reload(int32"), "", "\n");
            Assert.True(r.Ok, r.Problem);
            string h = GenerateResult.Apply(Header, r.Header);
            Assert.Contains("\tvoid Reload(int32 Amount = 3);\n\n\tUFUNCTION(Server, Reliable, WithValidation)\n\tvoid ServerReload(int32 Amount = 3);\n", h);
            string s = GenerateResult.Apply("", r.Source);
            Assert.Contains("void AWeapon::ServerReload_Implementation(int32 Amount)\n{\n\tReload(Amount);\n}", s);
            Assert.Equal("no-function", UeCodeGenerator.Rpc(Header, Caret("int32 Ammo"), "", "\n").Problem);
        }

        [Fact]
        public void Helpers()
        {
            Assert.Equal("TMap<int32, FString> M, int32 B", UeCodeGenerator.StripDefaults("TMap<int32, FString> M = {}, int32 B = (1 + 2)"));
            Assert.Equal("A, B", UeCodeGenerator.ArgumentNames("int32 A, const FVector& B"));
            Assert.True(UeCodeGenerator.HasSpecifier("Replicated, Category=\"X\"", "Replicated"));
            Assert.False(UeCodeGenerator.HasSpecifier("Category=Replicated", "Replicated"));
            Assert.Null(UeCodeGenerator.EnsureInclude("#include <Net/UnrealNetwork.h>\n", "Net/UnrealNetwork.h", "\n"));
            Assert.Equal(0, UeCodeGenerator.EnsureInclude("void F();\n", "A.h", "\n").Offset);
        }
    }
}
