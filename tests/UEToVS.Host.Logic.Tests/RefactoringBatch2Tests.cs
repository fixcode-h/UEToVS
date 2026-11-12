using System;
using System.Collections.Generic;
using System.Linq;
using UEToVS.Contracts;
using UEToVS.Host.Logic.Refactoring;
using Xunit;

namespace UEToVS.Host.Logic.Tests
{
    /// <summary>v0.4.5 refactorings, second batch: Change Signature, Move Implementation, Forward Declaration, Introduce Variable, Extract Method, Encapsulate Field.</summary>
    public class RefactoringBatch2Tests
    {
        private static (int Line, int Column) Pos(string text, string marker, int occurrence = 0)
        {
            int idx = -1;
            for (int i = 0; i <= occurrence; i++) idx = text.IndexOf(marker, idx + 1, StringComparison.Ordinal);
            Assert.True(idx >= 0, marker);
            var starts = TextEditApplier.LineStarts(text);
            int line = StructuredEditComposer.LineOf(starts, idx);
            return (line, idx - starts[line]);
        }

        private static (int Start, int End) Span(string text, string selected, int occurrence = 0)
        {
            int idx = -1;
            for (int i = 0; i <= occurrence; i++) idx = text.IndexOf(selected, idx + 1, StringComparison.Ordinal);
            Assert.True(idx >= 0, selected);
            return (idx, idx + selected.Length);
        }

        // ================================================================ structured edits

        [Fact]
        public void StructuredEditsComposeNestedChanges()
        {
            string text = "Foo(Foo(1, 2), 3);";
            // outer call: swap its two arguments; inner call (inside the first argument) swapped too
            var outer = new StructuredEdit(4, 16).Copy(15, 16).Literal(", ").Copy(4, 13);
            var inner = new StructuredEdit(8, 12).Copy(11, 12).Literal(", ").Copy(8, 9);
            Assert.Equal("Foo(3, Foo(2, 1));", StructuredEditComposer.Apply(text, new[] { inner, outer }));
        }

        [Fact]
        public void StructuredEditsDropPartialOverlapsAndKeepInsertionsAtTheSameOffset()
        {
            string text = "abcdef";
            var a = StructuredEdit.Replace(1, 4, "X");
            var b = StructuredEdit.Replace(3, 5, "Y");
            var composed = StructuredEditComposer.Compose("f", text, new[] { a, b }, out var dropped);
            Assert.Single(composed);
            Assert.Same(b, Assert.Single(dropped));
            var ins = StructuredEdit.Replace(1, 1, "<");
            Assert.Equal("a<Xef", StructuredEditComposer.Apply(text, new[] { a, ins }));
        }

        // ================================================================ Change Signature

        private const string SigHeader = @"#pragma once
UCLASS()
class AMyActor : public AActor
{
	GENERATED_BODY()
public:
	UFUNCTION(BlueprintCallable)
	void Fire(const FVector& Dir, int32 Count = 1);
};
";

        private const string SigCpp = @"#include ""MyActor.h""

void AMyActor::Fire(const FVector& Dir, int32 Count)
{
	for (int32 I = 0; I < Count; ++I) Shoot(Dir);
	Owner->Fire(Dir, Count);
}

void AMyActor::Tick(float DeltaSeconds)
{
	Fire(GetActorForwardVector());
	Fire(FVector(1, 2, 3), Max(1, 2));
	Delegate.AddUObject(this, &AMyActor::Fire);
}
";

        private static ChangeSignaturePlanner.Change Change(Func<List<EditableParameter>, List<EditableParameter>> edit, string header = "/p/MyActor.h", int line = 7)
        {
            var original = ChangeSignaturePlanner.Parse("const FVector& Dir, int32 Count = 1");
            return new ChangeSignaturePlanner.Change
            {
                Original = original, Edited = edit(original.Select(p => p.Clone()).ToList()), PrimaryDeclarationFile = header, PrimaryDeclarationLine = line,
            };
        }

        private static string ApplySites(string text, string file, IEnumerable<(string Marker, int Occ, ReferenceKind Kind)> hits, ChangeSignaturePlanner.Change change,
                                         out List<SignatureSite> sites)
        {
            var tokens = CppTextScan.Tokenize(text);
            sites = new List<SignatureSite>();
            foreach (var (marker, occ, kind) in hits)
            {
                var (l, c) = Pos(text, marker, occ);
                sites.Add(ChangeSignaturePlanner.PlanSite(file, text, tokens, l, c, "Fire", kind, true, change));
            }
            var edits = ChangeSignaturePlanner.Edits(file, text, sites, out var collided);
            Assert.Empty(collided);
            return TextEditApplier.Apply(text, edits, out int skipped);
        }

        [Fact]
        public void ParsesAndValidatesParameterLists()
        {
            var p = ChangeSignaturePlanner.Parse("const TArray<int32, TInlineAllocator<4>>& Items, UPARAM(ref) FHitResult& Hit, int32 Count = Max(1, 2)");
            Assert.Equal(3, p.Count);
            Assert.Equal("const TArray<int32, TInlineAllocator<4>>&", p[0].Type);
            Assert.Equal("Items", p[0].Name);
            Assert.Equal("UPARAM(ref) FHitResult&", p[1].Type);
            Assert.Equal("Max(1, 2)", p[2].DefaultValue);
            Assert.Null(ChangeSignaturePlanner.Parse("int32 A, ..."));
            Assert.Null(ChangeSignaturePlanner.Parse("void (*Callback)(int)"));
            Assert.Empty(ChangeSignaturePlanner.Parse("void"));

            var edited = ChangeSignaturePlanner.Parse("int32 A, int32 B = 1");
            Assert.Null(ChangeSignaturePlanner.Validate(edited));
            var single = ChangeSignaturePlanner.Parse("int32 A");
            single.Add(new EditableParameter { Type = "float", Name = "C" });
            Assert.Equal("missing-call-value", ChangeSignaturePlanner.Validate(single));
            single[1].CallValue = "0.f";
            Assert.Null(ChangeSignaturePlanner.Validate(single));
            edited.Add(new EditableParameter { Type = "float", Name = "C", CallValue = "0.f" });
            Assert.Equal("default-order", ChangeSignaturePlanner.Validate(edited));
            edited[2].DefaultValue = "1.f";
            Assert.Null(ChangeSignaturePlanner.Validate(edited));
            edited[2].Name = "A";
            Assert.Equal("duplicate-name", ChangeSignaturePlanner.Validate(edited));
            edited[2].Name = "class";
            Assert.Equal("invalid-name", ChangeSignaturePlanner.Validate(edited));
            edited[2].Name = "C";
            edited[2].DefaultValue = "Foo(1, 2";
            Assert.Equal("unbalanced", ChangeSignaturePlanner.Validate(edited));
            Assert.True(ChangeSignaturePlanner.IsUnchanged(ChangeSignaturePlanner.Parse("int32 A"), ChangeSignaturePlanner.Parse("int32  A")));
        }

        [Fact]
        public void ReorderingUpdatesDeclarationDefinitionAndCalls()
        {
            // swap: (int32 Count = 1, const FVector& Dir) is invalid (default order), so move Count first and drop its default
            var change = Change(ps =>
            {
                ps[1].DefaultValue = null;
                return new List<EditableParameter> { ps[1], ps[0] };
            }, "/p/MyActor.h", 7);
            string header = ApplySites(SigHeader, "/p/MyActor.h", new[] { ("Fire(", 0, ReferenceKind.Declaration) }, change, out _);
            Assert.Contains("void Fire(int32 Count, const FVector& Dir);", header);

            string cpp = ApplySites(SigCpp, "/p/MyActor.cpp", new[]
            {
                ("Fire(const", 0, ReferenceKind.Definition), ("Fire(Dir, Count)", 0, ReferenceKind.Call), ("Fire(GetActor", 0, ReferenceKind.Call),
                ("Fire(FVector", 0, ReferenceKind.Call),
            }, change, out var sites);
            Assert.Contains("void AMyActor::Fire(int32 Count, const FVector& Dir)", cpp);
            Assert.Contains("Owner->Fire(Count, Dir);", cpp);
            // the call relied on the old default: it is spelled out now
            Assert.Contains("Fire(1, GetActorForwardVector());", cpp);
            Assert.Contains("Fire(Max(1, 2), FVector(1, 2, 3));", cpp);
            Assert.All(sites, s => Assert.Null(s.Problem));
            Assert.StartsWith("void AMyActor::Fire(int32 Count, const FVector& Dir)", sites[0].NewLineText);
        }

        [Fact]
        public void NewParameterGetsCallValueAndDefaultOnlyInTheDeclaration()
        {
            var change = Change(ps =>
            {
                ps.Add(new EditableParameter { Type = "bool", Name = "bLoud", DefaultValue = "false", CallValue = "true" });
                return ps;
            });
            string header = ApplySites(SigHeader, "/p/MyActor.h", new[] { ("Fire(", 0, ReferenceKind.Declaration) }, change, out _);
            Assert.Contains("void Fire(const FVector& Dir, int32 Count = 1, bool bLoud = false);", header);
            string cpp = ApplySites(SigCpp, "/p/MyActor.cpp", new[]
            {
                ("Fire(const", 0, ReferenceKind.Definition), ("Fire(GetActor", 0, ReferenceKind.Call), ("Fire(Dir, Count)", 0, ReferenceKind.Call),
            }, change, out _);
            Assert.Contains("void AMyActor::Fire(const FVector& Dir, int32 Count, bool bLoud)", cpp);
            Assert.Contains("Fire(GetActorForwardVector(), 1, true);", cpp);
            Assert.Contains("Owner->Fire(Dir, Count, true);", cpp);
        }

        [Fact]
        public void NewDefaultedParameterWithoutCallValueLeavesCallsAlone()
        {
            var change = Change(ps =>
            {
                ps.Add(new EditableParameter { Type = "bool", Name = "bLoud", DefaultValue = "false" });
                return ps;
            });
            var tokens = CppTextScan.Tokenize(SigCpp);
            var (l, c) = Pos(SigCpp, "Fire(GetActor");
            var site = ChangeSignaturePlanner.PlanSite("/p/MyActor.cpp", SigCpp, tokens, l, c, "Fire", ReferenceKind.Call, true, change);
            Assert.Null(site.Problem);
            Assert.Empty(site.Edits);
        }

        [Fact]
        public void RenamingAParameterRenamesItsUsesInTheDefinitionBody()
        {
            var change = Change(ps =>
            {
                ps[0].Name = "Direction";
                return ps;
            });
            string cpp = ApplySites(SigCpp, "/p/MyActor.cpp", new[] { ("Fire(const", 0, ReferenceKind.Definition) }, change, out _);
            Assert.Contains("void AMyActor::Fire(const FVector& Direction, int32 Count)", cpp);
            Assert.Contains("Shoot(Direction);", cpp);
            Assert.Contains("Owner->Fire(Direction, Count);", cpp);
        }

        [Fact]
        public void RecursiveCallInsideTheDefinitionComposesWithRenames()
        {
            var change = Change(ps =>
            {
                ps[0].Name = "Direction";
                return new List<EditableParameter> { ps[1], ps[0] };
            });
            change.Edited[0].DefaultValue = null;
            string cpp = ApplySites(SigCpp, "/p/MyActor.cpp", new[] { ("Fire(const", 0, ReferenceKind.Definition), ("Fire(Dir, Count)", 0, ReferenceKind.Call) }, change, out _);
            Assert.Contains("Owner->Fire(Count, Direction);", cpp);
        }

        [Fact]
        public void BindingsAndMismatchedCallsAreListedNotChanged()
        {
            var change = Change(ps => new List<EditableParameter> { ps[0] });
            var tokens = CppTextScan.Tokenize(SigCpp);
            var (l, c) = Pos(SigCpp, "Fire);");
            var site = ChangeSignaturePlanner.PlanSite("/p/MyActor.cpp", SigCpp, tokens, l, c, "Fire", ReferenceKind.Read, true, change);
            Assert.Equal(SignatureSiteKind.Reference, site.Kind);
            Assert.Equal("no-call", site.Problem);
            Assert.False(site.Checked);
            // a call with too many arguments for the original signature (another overload)
            string other = "void F() { Fire(A, B, C); }";
            var t2 = CppTextScan.Tokenize(other);
            var (l2, c2) = Pos(other, "Fire(");
            site = ChangeSignaturePlanner.PlanSite("/p/X.cpp", other, t2, l2, c2, "Fire", ReferenceKind.Call, true, change);
            Assert.Equal("count-mismatch", site.Problem);
        }

        [Fact]
        public void MultiLineParameterListsKeepTheirLayout()
        {
            string decl = "\tvoid Fire(\n\t\tconst FVector& Dir,\n\t\tint32 Count = 1);\n";
            var change = Change(ps => new List<EditableParameter> { ps[1], ps[0] }, "/p/A.h", 0);
            change.Edited[0].DefaultValue = null;
            string result = ApplySites(decl, "/p/A.h", new[] { ("Fire(", 0, ReferenceKind.Declaration) }, change, out _);
            Assert.Equal("\tvoid Fire(\n\t\tint32 Count,\n\t\tconst FVector& Dir);\n", result);
        }

        [Fact]
        public void CallArgumentsWithTemplatesAndComparisonsSplitCorrectly()
        {
            string text = "void F() { Fire(TPair<int32, float>(1, 2.f), A < B); Fire(Cast<AActor>(X), C > D); }";
            var t = CppTextScan.Tokenize(text);
            var (l, c) = Pos(text, "Fire(");
            int nameTok = CodeScan.IndexAtOrAfter(t, c);
            Assert.True(ChangeSignaturePlanner.FindParentheses(t, nameTok, out int open, out int close));
            var items = ChangeSignaturePlanner.Items(text, t, open, close);
            Assert.Equal(new[] { "TPair<int32, float>(1, 2.f)", "A < B" }, items.Select(i => text.Substring(i.Start, i.End - i.Start)));
            (l, c) = Pos(text, "Fire(", 1);
            nameTok = CodeScan.IndexAtOrAfter(t, c);
            ChangeSignaturePlanner.FindParentheses(t, nameTok, out open, out close);
            items = ChangeSignaturePlanner.Items(text, t, open, close);
            Assert.Equal(new[] { "Cast<AActor>(X)", "C > D" }, items.Select(i => text.Substring(i.Start, i.End - i.Start)));
        }

        // ================================================================ Move Implementation

        private const string InlineHeader = @"#pragma once
#include ""MyActor.generated.h""

UCLASS()
class MYGAME_API AMyActor : public AActor
{
	GENERATED_BODY()
public:
	struct FState { int32 X; };

	AMyActor() : Health(100) { bReplicates = true; }

	UFUNCTION(BlueprintCallable)
	virtual float GetHealth(int32 Scale = 1) const override
	{
		// scaled
		return Health * Scale;
	}

	FORCEINLINE FState MakeState() const { return FState{ 1 }; }

	static int32 Count() { return 3; }

	template<typename T> T* Find() const { return nullptr; }

	void Declared();

private:
	float Health = 0.f;
};
";

        private const string InlineCpp = @"#include ""MyActor.h""

void AMyActor::Declared()
{
}
";

        [Fact]
        public void FindsInlineMembersAndRejectsTemplates()
        {
            var members = MoveImplementationPlanner.InlineMembersOf(InlineHeader, "AMyActor");
            Assert.Equal(new[] { "AMyActor", "GetHealth", "MakeState", "Count" }, members.Select(m => m.Name));
            var get = members[1];
            Assert.Equal("float AMyActor::GetHealth(int32 Scale) const", get.DefinitionHead);
            Assert.Equal("AMyActor::FState AMyActor::MakeState() const", members[2].DefinitionHead);
            Assert.Equal("int32 AMyActor::Count()", members[3].DefinitionHead);
            Assert.Equal("AMyActor::AMyActor()", members[0].DefinitionHead);
            int caret = InlineHeader.IndexOf("Find()", StringComparison.Ordinal);
            Assert.Null(MoveImplementationPlanner.InlineMemberAt(InlineHeader, caret, out string why));
            Assert.Equal("template", why);
            caret = InlineHeader.IndexOf("return Health * Scale", StringComparison.Ordinal);
            Assert.Equal("GetHealth", MoveImplementationPlanner.InlineMemberAt(InlineHeader, caret, out why).Name);
        }

        [Fact]
        public void MovesOneInlineBodyToTheSource()
        {
            int caret = InlineHeader.IndexOf("GetHealth", StringComparison.Ordinal);
            var m = MoveImplementationPlanner.InlineMemberAt(InlineHeader, caret, out _);
            var plan = MoveImplementationPlanner.ToSource("/p/MyActor.h", InlineHeader, "/p/MyActor.cpp", InlineCpp, new[] { m });
            Assert.True(plan.Ok, plan.Problem);
            string header = TextEditApplier.Apply(InlineHeader, plan.HeaderEdits, out int s1);
            string cpp = TextEditApplier.Apply(InlineCpp, plan.SourceEdits, out int s2);
            Assert.Equal(0, s1 + s2);
            Assert.Contains("\tvirtual float GetHealth(int32 Scale = 1) const override;\n", header);
            Assert.DoesNotContain("return Health * Scale", header);
            Assert.Contains("float AMyActor::GetHealth(int32 Scale) const\n{\n\t// scaled\n\treturn Health * Scale;\n}\n", cpp);
            Assert.DoesNotContain("virtual", cpp);
            Assert.DoesNotContain("override", cpp);
        }

        [Fact]
        public void MovesConstructorWithInitializerAndDropsForceInline()
        {
            var members = MoveImplementationPlanner.InlineMembersOf(InlineHeader, "AMyActor");
            var plan = MoveImplementationPlanner.ToSource("/p/MyActor.h", InlineHeader, "/p/MyActor.cpp", InlineCpp, new[] { members[0], members[2] });
            Assert.True(plan.Ok, plan.Problem);
            string header = TextEditApplier.Apply(InlineHeader, plan.HeaderEdits, out _);
            string cpp = TextEditApplier.Apply(InlineCpp, plan.SourceEdits, out _);
            Assert.Contains("\tAMyActor();\n", header);
            Assert.Contains("\tFState MakeState() const;\n", header);
            Assert.DoesNotContain("FORCEINLINE", header);
            Assert.Contains("AMyActor::AMyActor()\n\t: Health(100)\n{\n\tbReplicates = true;\n}\n", cpp);
            Assert.Contains("AMyActor::FState AMyActor::MakeState() const\n{\n\treturn FState{ 1 };\n}\n", cpp);
            Assert.Equal(new[] { "AMyActor", "MakeState" }, plan.Moved);
        }

        [Fact]
        public void MoveToSourceRefusesWhenTheSourceAlreadyDefinesIt()
        {
            int caret = InlineHeader.IndexOf("Count()", StringComparison.Ordinal);
            var m = MoveImplementationPlanner.InlineMemberAt(InlineHeader, caret, out _);
            string cpp = InlineCpp + "\nint32 AMyActor::Count()\n{\n\treturn 1;\n}\n";
            Assert.Equal("exists", MoveImplementationPlanner.ToSource("/p/MyActor.h", InlineHeader, "/p/MyActor.cpp", cpp, new[] { m }).Problem);
        }

        [Fact]
        public void MovesADefinitionBackIntoTheHeader()
        {
            int caret = InlineCpp.IndexOf("Declared", StringComparison.Ordinal);
            var def = MoveImplementationPlanner.DefinitionAt(InlineCpp, caret);
            Assert.Equal("Declared", def.Name);
            var plan = MoveImplementationPlanner.ToHeader("/p/MyActor.h", InlineHeader, "/p/MyActor.cpp", InlineCpp, def);
            Assert.True(plan.Ok, plan.Problem);
            string header = TextEditApplier.Apply(InlineHeader, plan.HeaderEdits, out _);
            string cpp = TextEditApplier.Apply(InlineCpp, plan.SourceEdits, out _);
            Assert.Contains("\tvoid Declared()\n\t{\n\t}\n", header);
            Assert.Equal("#include \"MyActor.h\"\n", cpp);
        }

        [Fact]
        public void MoveToHeaderMatchesOverloadsByParameterTypes()
        {
            string h = "class AFoo\n{\npublic:\n\tvoid Set(int32 A);\n\tvoid Set(float A) const;\n\tvirtual void Pure() = 0;\n};\n";
            string cpp = "void AFoo::Set(float Value) const\n{\n\tX = Value;\n}\n\nvoid AFoo::Set(int32 Value)\n{\n}\n";
            var def = MoveImplementationPlanner.DefinitionAt(cpp, 2);
            var plan = MoveImplementationPlanner.ToHeader("/p/A.h", h, "/p/A.cpp", cpp, def);
            Assert.True(plan.Ok, plan.Problem);
            string header = TextEditApplier.Apply(h, plan.HeaderEdits, out _);
            Assert.Contains("\tvoid Set(float A) const\n\t{\n\t\tX = Value;\n\t}\n", header);
            Assert.Contains("\tvoid Set(int32 A);\n", header);
            Assert.Equal("void AFoo::Set(int32 Value)\n{\n}\n", TextEditApplier.Apply(cpp, plan.SourceEdits, out _));
            // _Implementation bodies have no declaration of their own
            string rpc = "void AFoo::Server_Implementation()\n{\n}\n";
            Assert.Equal("no-declaration", MoveImplementationPlanner.ToHeader("/p/A.h", h, "/p/A.cpp", rpc, MoveImplementationPlanner.DefinitionAt(rpc, 1)).Problem);
        }

        // ================================================================ Forward Declaration

        private const string FwdHeader = @"#pragma once

#include ""CoreMinimal.h""
#include ""GameFramework/Actor.h""
#include ""MyActor.generated.h""

UCLASS()
class AMyActor : public AActor
{
	GENERATED_BODY()
public:
	UPROPERTY(EditAnywhere)
	TObjectPtr<UStaticMeshComponent> Mesh;

	UPROPERTY(EditAnywhere)
	TSubclassOf<AProjectile> ProjectileClass;

	void Hit(const FHitResult& Result, UDamageType* Type);

	UHealthComponent* GetHealth() const { return Health; }

	UWeapon* Weapon;
	void Use() { Weapon->Fire(); }

	FVector Location;
	// UFoo Bar; in a comment does not count
};
";

        [Fact]
        public void ForwardDeclarationOnlyForIndirectUses()
        {
            Assert.True(ForwardDeclarationPlanner.OnlyIndirectUses(FwdHeader, "UStaticMeshComponent", out _));
            Assert.True(ForwardDeclarationPlanner.OnlyIndirectUses(FwdHeader, "AProjectile", out _));
            Assert.True(ForwardDeclarationPlanner.OnlyIndirectUses(FwdHeader, "FHitResult", out _));
            Assert.True(ForwardDeclarationPlanner.OnlyIndirectUses(FwdHeader, "UDamageType", out _));
            Assert.True(ForwardDeclarationPlanner.OnlyIndirectUses(FwdHeader, "UHealthComponent", out _), "returning the pointer needs no definition");
            Assert.False(ForwardDeclarationPlanner.OnlyIndirectUses(FwdHeader, "UWeapon", out string why));
            Assert.Equal("inline-use", why);
            Assert.False(ForwardDeclarationPlanner.OnlyIndirectUses(FwdHeader, "FVector", out why));
            Assert.Equal("by-value", why);
            Assert.False(ForwardDeclarationPlanner.OnlyIndirectUses(FwdHeader, "AActor", out why), "base class");
            Assert.False(ForwardDeclarationPlanner.OnlyIndirectUses(FwdHeader, "UFoo", out why));
            Assert.Equal("not-type", why);
            Assert.False(ForwardDeclarationPlanner.OnlyIndirectUses("void F() { auto* C = UFoo::StaticClass(); }\nUFoo* P;", "UFoo", out _));
        }

        [Fact]
        public void ForwardDeclarationGoesAfterTheIncludes()
        {
            var p = ForwardDeclarationPlanner.Plan(FwdHeader, "class UStaticMeshComponent;");
            string result = TextEditApplier.Apply(FwdHeader, new[] { TextEdit.Insert("f", p.Line, p.Text) }, out _);
            Assert.Contains("#include \"MyActor.generated.h\"\n\nclass UStaticMeshComponent;\n\nUCLASS()", result);
            // a second one joins the first
            var p2 = ForwardDeclarationPlanner.Plan(result, "struct FHitResult;");
            result = TextEditApplier.Apply(result, new[] { TextEdit.Insert("f", p2.Line, p2.Text) }, out _);
            Assert.Contains("class UStaticMeshComponent;\nstruct FHitResult;\n\nUCLASS()", result);
            Assert.True(ForwardDeclarationPlanner.HasForwardDeclaration(result, "FHitResult"));
            Assert.False(ForwardDeclarationPlanner.HasForwardDeclaration(result, "AProjectile"));
            // no blank line after the includes
            string tight = "#pragma once\n#include \"A.h\"\nclass UFoo* G;\n";
            var p3 = ForwardDeclarationPlanner.Plan(tight, "class UBar;");
            Assert.Equal("#pragma once\n#include \"A.h\"\n\nclass UBar;\nclass UFoo* G;\n", TextEditApplier.Apply(tight, new[] { TextEdit.Insert("f", p3.Line, p3.Text) }, out _));
        }

        [Fact]
        public void ForwardDeclarationTextAndEnums()
        {
            Assert.Equal("class UFoo;", ForwardDeclarationPlanner.Declaration(ForwardDeclarationPlanner.KeywordFor(SymbolKind.Class, false), "UFoo"));
            Assert.Equal("struct FBar;", ForwardDeclarationPlanner.Declaration(ForwardDeclarationPlanner.KeywordFor(SymbolKind.Struct, false), "FBar"));
            Assert.Equal("namespace Game { class UFoo; }", ForwardDeclarationPlanner.Declaration("class", "UFoo", null, "Game"));
            string enums = "UENUM(BlueprintType)\nenum class EState : uint8\n{\n\tA,\n};\nenum EPlain { X };\n";
            Assert.Equal("uint8", ForwardDeclarationPlanner.EnumUnderlyingType(enums, "EState", out bool scoped));
            Assert.True(scoped);
            Assert.Equal("enum class EState : uint8;", ForwardDeclarationPlanner.Declaration(ForwardDeclarationPlanner.KeywordFor(SymbolKind.Enum, scoped), "EState", "uint8"));
            Assert.Null(ForwardDeclarationPlanner.EnumUnderlyingType(enums, "EPlain", out _));
            Assert.True(ForwardDeclarationPlanner.DeclaresType("class MYGAME_API UFoo : public UObject {", "UFoo"));
            Assert.False(ForwardDeclarationPlanner.DeclaresType("friend class UFoo;", "UFoo"));
        }

        // ================================================================ Introduce Variable

        private const string IvCpp = @"void AMyActor::Tick(float DeltaSeconds)
{
	if (GetOwner()->GetActorLocation().Z > 100.f)
	{
		Move(GetOwner()->GetActorLocation(), 2.5f);
		Log(GetOwner()->GetActorLocation());
	}
	for (int32 I = 0; I < Items.Num(); ++I) Use(Items[I] * 2);
	if (Target && Target->IsAlive()) Kill();
	const float Sum = A + B * C;
	Count = Count + 1;
	switch (Mode) { case 1: Run(Speed * 2); break; }
}
";

        [Fact]
        public void IntroduceVariableHoistsBeforeTheStatement()
        {
            var (s, e) = Span(IvCpp, "GetOwner()->GetActorLocation()", 1);
            var a = IntroduceVariablePlanner.Analyze(IvCpp, s, e);
            Assert.True(a.Ok, a.Problem);
            Assert.True(a.IsChain);
            Assert.Equal(2, a.Occurrences.Count); // the one in the if head is outside this block
            Assert.Equal("FVector", IntroduceVariablePlanner.ChooseType(a, "FVector"));
            string name = IntroduceVariablePlanner.SuggestName(a.Expression, "FVector", a.Identifiers);
            Assert.Equal("ActorLocation", name);
            string result = TextEditApplier.Apply(IvCpp, IntroduceVariablePlanner.Edits("f", IvCpp, a, "FVector", name, true), out _);
            Assert.Contains("\t\tFVector ActorLocation = GetOwner()->GetActorLocation();\n\t\tMove(ActorLocation, 2.5f);\n\t\tLog(ActorLocation);", result);
            Assert.Contains("if (GetOwner()->GetActorLocation().Z > 100.f)", result);
            string single = TextEditApplier.Apply(IvCpp, IntroduceVariablePlanner.Edits("f", IvCpp, a, "FVector", name, false), out _);
            Assert.Contains("Log(GetOwner()->GetActorLocation());", single);
        }

        [Fact]
        public void IntroduceVariableLiteralAndAutoTypes()
        {
            var (s, e) = Span(IvCpp, "2.5f");
            var a = IntroduceVariablePlanner.Analyze(IvCpp, s, e);
            Assert.True(a.Ok, a.Problem);
            Assert.Equal("float", IntroduceVariablePlanner.ChooseType(a, null));
            (s, e) = Span(IvCpp, "A + B * C");
            a = IntroduceVariablePlanner.Analyze(IvCpp, s, e);
            Assert.True(a.Ok, a.Problem);
            Assert.False(a.IsChain);
            Assert.Equal("auto", IntroduceVariablePlanner.ChooseType(a, "float"));
            // in the if head: inserted before the if
            (s, e) = Span(IvCpp, "GetOwner()->GetActorLocation().Z");
            a = IntroduceVariablePlanner.Analyze(IvCpp, s, e);
            Assert.True(a.Ok, a.Problem);
            string result = TextEditApplier.Apply(IvCpp, IntroduceVariablePlanner.Edits("f", IvCpp, a, "double", "Z", false), out _);
            Assert.Contains("\tdouble Z = GetOwner()->GetActorLocation().Z;\n\tif (Z > 100.f)", result);
        }

        [Theory]
        [InlineData("Items[I] * 2", "loop")]
        [InlineData("Target->IsAlive()", "short-circuit")]
        [InlineData("A + B", "partial")]
        [InlineData("Count", "identifier")]
        [InlineData("Speed * 2", "case")]
        [InlineData("GetActorLocation()", "partial")]
        [InlineData("Move(GetOwner()->GetActorLocation(), 2.5f);", "not-expression")]
        public void IntroduceVariableRefusesUnsafeSelections(string selected, string problem)
        {
            var (s, e) = Span(IvCpp, selected);
            Assert.Equal(problem, IntroduceVariablePlanner.Analyze(IvCpp, s, e).Problem);
        }

        [Fact]
        public void IntroduceVariableStopsReplacingAfterAWrite()
        {
            string text = "void F()\n{\n\tUse(A.X + 1);\n\tA.X = 3;\n\tUse(A.X + 1);\n}\n";
            var (s, e) = Span(text, "A.X + 1");
            var a = IntroduceVariablePlanner.Analyze(text, s, e);
            Assert.True(a.Ok, a.Problem);
            Assert.Single(a.Occurrences);
        }

        [Fact]
        public void IntroduceVariableRefusesOutsideFunctionsAndInComments()
        {
            string text = "class A\n{\n\tint32 X = 1 + 2;\n};\nvoid F()\n{\n\t// Use(1 + 2);\n}\n";
            var (s, e) = Span(text, "1 + 2");
            Assert.Equal("not-in-function", IntroduceVariablePlanner.Analyze(text, s, e).Problem);
            (s, e) = Span(text, "1 + 2", 1);
            Assert.Equal("empty", IntroduceVariablePlanner.Analyze(text, s, e).Problem);
        }

        [Fact]
        public void SuggestsNamesFromCallsAndTypes()
        {
            Assert.Equal("Owner", IntroduceVariablePlanner.SuggestName("GetOwner()", "AActor*", null));
            Assert.Equal("Health", IntroduceVariablePlanner.SuggestName("Foo->Health", "float", null));
            Assert.Equal("Health2", IntroduceVariablePlanner.SuggestName("Foo->Health", "float", new HashSet<string> { "Health" }));
            Assert.Equal("bIsAlive", IntroduceVariablePlanner.SuggestName("IsAlive()", "bool", null));
            Assert.Equal("Value", IntroduceVariablePlanner.SuggestName("1 + 2", "auto", null));
        }

        // ================================================================ Extract Method

        private const string EmCpp = @"#include ""MyActor.h""

void AMyActor::Tick(float DeltaSeconds)
{
	Super::Tick(DeltaSeconds);
	const FVector Start = GetActorLocation();
	float Speed = 10.f;
	TArray<AActor*> Hits;
	// begin
	FVector End = Start + GetActorForwardVector() * Speed * DeltaSeconds;
	Hits.Add(Trace(Start, End));
	Speed += 1.f;
	// end
	Report(End, Speed, Hits);
	for (int32 I = 0; I < 3; ++I)
	{
		if (I == 1) break;
		Use(I);
	}
}
";

        private static List<ExtractLocal> EmLocals(string text) => new List<ExtractLocal>
        {
            new ExtractLocal { Name = "DeltaSeconds", Type = "float", Offset = text.IndexOf("DeltaSeconds", StringComparison.Ordinal), IsParameter = true },
            new ExtractLocal { Name = "Start", Type = "const FVector", Offset = text.IndexOf("Start =", StringComparison.Ordinal) },
            new ExtractLocal { Name = "Speed", Type = "float", Offset = text.IndexOf("Speed =", StringComparison.Ordinal) },
            new ExtractLocal { Name = "Hits", Type = "TArray<AActor*>", Offset = text.IndexOf("Hits;", StringComparison.Ordinal) },
            new ExtractLocal { Name = "End", Type = "FVector", Offset = text.IndexOf("End =", StringComparison.Ordinal) },
            new ExtractLocal
            {
                Name = "I", Type = "int32", Offset = text.IndexOf("I = 0", StringComparison.Ordinal),
                ScopeEnd = text.LastIndexOf('}', text.LastIndexOf('}') - 1),
            },
        };

        [Fact]
        public void ExtractMethodRefusesSeveralResults()
        {
            int s = EmCpp.IndexOf("\tFVector End", StringComparison.Ordinal);
            int e = EmCpp.IndexOf("\t// end", StringComparison.Ordinal);
            var a = ExtractMethodPlanner.Analyze(EmCpp, s, e, EmLocals(EmCpp));
            Assert.Equal("several-results", a.Problem);
            Assert.Equal("End, Speed", a.ProblemDetail);
        }

        [Fact]
        public void ExtractMethodBuildsParametersAndResult()
        {
            int s = EmCpp.IndexOf("\tFVector End", StringComparison.Ordinal);
            int e = EmCpp.IndexOf("\tSpeed += 1.f;", StringComparison.Ordinal);
            var a = ExtractMethodPlanner.Analyze(EmCpp, s, e, EmLocals(EmCpp));
            Assert.True(a.Ok, a.Problem + " " + a.ProblemDetail);
            Assert.Equal("End", a.ReturnVariable);
            Assert.True(a.ReturnDeclaredInSelection);
            Assert.Equal("FVector", a.ReturnType);
            Assert.Equal(new[] { "float DeltaSeconds", "const FVector& Start", "float Speed", "TArray<AActor*>& Hits" }, a.Parameters.Select(p => p.Declaration));
            Assert.Equal("AMyActor", a.ClassName);
            Assert.Equal("AMyActor::", a.Qualifier);
            var g = ExtractMethodPlanner.Generate(a, "TraceAhead", false, "\n");
            Assert.Equal("FVector End = TraceAhead(DeltaSeconds, Start, Speed, Hits);", g.Call);
            Assert.Equal("FVector TraceAhead(float DeltaSeconds, const FVector& Start, float Speed, TArray<AActor*>& Hits);", g.Declaration);
            string result = TextEditApplier.Apply(EmCpp, ExtractMethodPlanner.SourceEdits("f", EmCpp, a, g), out _);
            Assert.Contains("\t// begin\n\tFVector End = TraceAhead(DeltaSeconds, Start, Speed, Hits);\n\tSpeed += 1.f;", result);
            Assert.Contains("}\n\nFVector AMyActor::TraceAhead(float DeltaSeconds, const FVector& Start, float Speed, TArray<AActor*>& Hits)\n{\n" +
                            "\tFVector End = Start + GetActorForwardVector() * Speed * DeltaSeconds;\n\tHits.Add(Trace(Start, End));\n\treturn End;\n}\n", result);
        }

        [Fact]
        public void ExtractMethodReturnsAnAssignedOuterVariable()
        {
            int s = EmCpp.IndexOf("\tSpeed += 1.f;", StringComparison.Ordinal);
            int e = EmCpp.IndexOf("\t// end", StringComparison.Ordinal);
            var a = ExtractMethodPlanner.Analyze(EmCpp, s, e, EmLocals(EmCpp));
            Assert.True(a.Ok, a.Problem);
            Assert.Equal("Speed", a.ReturnVariable);
            Assert.False(a.ReturnDeclaredInSelection);
            var g = ExtractMethodPlanner.Generate(a, "Faster", false, "\n");
            Assert.Equal("Speed = Faster(Speed);", g.Call);
            Assert.Contains("float AMyActor::Faster(float Speed)\n{\n\tSpeed += 1.f;\n\treturn Speed;\n}", g.Definition);
        }

        [Fact]
        public void ExtractMethodRefusesJumpsAndPartialStatements()
        {
            var locals = EmLocals(EmCpp);
            int s = EmCpp.IndexOf("\t\tif (I == 1) break;", StringComparison.Ordinal);
            int e = EmCpp.IndexOf("\t\tUse(I);", StringComparison.Ordinal);
            Assert.Equal("jump", ExtractMethodPlanner.Analyze(EmCpp, s, e, locals).Problem);
            // the whole loop is fine: break stays inside it
            s = EmCpp.IndexOf("\tfor (", StringComparison.Ordinal);
            e = EmCpp.LastIndexOf("\t}", StringComparison.Ordinal) + 3;
            var a = ExtractMethodPlanner.Analyze(EmCpp, s, e, locals);
            Assert.True(a.Ok, a.Problem);
            Assert.Empty(a.Parameters);
            // half a statement
            s = EmCpp.IndexOf("Start + GetActorForwardVector()", StringComparison.Ordinal);
            e = EmCpp.IndexOf("Hits.Add", StringComparison.Ordinal);
            Assert.Equal("partial-statement", ExtractMethodPlanner.Analyze(EmCpp, s, e, locals).Problem);
            // return
            string ret = "int32 AFoo::F(int32 A)\n{\n\tif (A > 1) { return 2; }\n\treturn A;\n}\n";
            s = ret.IndexOf("\tif", StringComparison.Ordinal);
            e = ret.IndexOf("\treturn A", StringComparison.Ordinal);
            Assert.Equal("jump", ExtractMethodPlanner.Analyze(ret, s, e, new List<ExtractLocal>()).Problem);
        }

        [Fact]
        public void ExtractMethodRefusesUnknownTypesAndFreeFunctions()
        {
            string text = "void AFoo::F()\n{\n\tauto X = Make();\n\tUse(X);\n}\n";
            var locals = new List<ExtractLocal> { new ExtractLocal { Name = "X", Type = "auto", Offset = text.IndexOf("X =", StringComparison.Ordinal) } };
            int s = text.IndexOf("\tUse", StringComparison.Ordinal);
            int e = text.IndexOf("}", s, StringComparison.Ordinal);
            var a = ExtractMethodPlanner.Analyze(text, s, e, locals);
            Assert.Equal("unknown-type", a.Problem);
            Assert.Equal("X", a.ProblemDetail);
            string free = "void F()\n{\n\tUse(1);\n}\n";
            s = free.IndexOf("\tUse", StringComparison.Ordinal);
            Assert.Equal("not-member", ExtractMethodPlanner.Analyze(free, s, free.IndexOf('}'), new List<ExtractLocal>()).Problem);
        }

        [Fact]
        public void ExtractMethodKeepsConstAndReadsStaticFromTheHeader()
        {
            string text = "int32 AFoo::Get() const\n{\n\tint32 R = 0;\n\tR += Base;\n\treturn R;\n}\n";
            var locals = new List<ExtractLocal> { new ExtractLocal { Name = "R", Type = "int32", Offset = text.IndexOf("R = 0", StringComparison.Ordinal) } };
            int s = text.IndexOf("\tR +=", StringComparison.Ordinal);
            int e = text.IndexOf("\treturn", StringComparison.Ordinal);
            var a = ExtractMethodPlanner.Analyze(text, s, e, locals);
            Assert.True(a.Ok, a.Problem);
            Assert.True(a.IsConst);
            Assert.Equal("int32 AddBase(int32 R) const;", ExtractMethodPlanner.Generate(a, "AddBase", false, "\n").Declaration);
            Assert.Equal("static int32 AddBase(int32 R);", ExtractMethodPlanner.Generate(a, "AddBase", true, "\n").Declaration);
            string header = "class AFoo\n{\npublic:\n\tstatic int32 Get();\n\tvoid Other();\n};\n";
            Assert.True(ExtractMethodPlanner.IsStaticInHeader(header, "AFoo", "Get"));
            Assert.False(ExtractMethodPlanner.IsStaticInHeader(header, "AFoo", "Other"));
            Assert.Null(ExtractMethodPlanner.IsStaticInHeader(header, "AFoo", "Missing"));
        }

        [Fact]
        public void ExtractMethodPassesModifiedVariablesByReference()
        {
            string text = "void AFoo::F()\n{\n\tFString Name;\n\tTArray<int32> Values;\n\tName.Append(TEXT(\"x\"));\n\tValues[0] = 1;\n\tLog(Name, Values);\n}\n";
            var locals = new List<ExtractLocal>
            {
                new ExtractLocal { Name = "Name", Type = "FString", Offset = text.IndexOf("Name;", StringComparison.Ordinal) },
                new ExtractLocal { Name = "Values", Type = "TArray<int32>", Offset = text.IndexOf("Values;", StringComparison.Ordinal) },
            };
            int s = text.IndexOf("\tName.Append", StringComparison.Ordinal);
            int e = text.IndexOf("\tLog", StringComparison.Ordinal);
            var a = ExtractMethodPlanner.Analyze(text, s, e, locals);
            Assert.True(a.Ok, a.Problem);
            Assert.Equal(new[] { "FString& Name", "TArray<int32>& Values" }, a.Parameters.Select(p => p.Declaration));
            Assert.Null(a.ReturnVariable);
        }

        [Fact]
        public void CheapTypesArePassedByValue()
        {
            Assert.True(ExtractMethodPlanner.IsCheap("int32"));
            Assert.True(ExtractMethodPlanner.IsCheap("const AActor*"));
            Assert.True(ExtractMethodPlanner.IsCheap("TObjectPtr<UFoo>"));
            Assert.True(ExtractMethodPlanner.IsCheap("EMovementMode"));
            Assert.True(ExtractMethodPlanner.IsCheap("unsigned int"));
            Assert.False(ExtractMethodPlanner.IsCheap("FVector"));
            Assert.False(ExtractMethodPlanner.IsCheap("TArray<int32>"));
            Assert.False(ExtractMethodPlanner.IsCheap("FString"));
        }

        // ================================================================ Encapsulate Field

        private const string FieldHeader = @"#pragma once
UCLASS()
class AMyActor : public AActor
{
	GENERATED_BODY()
public:
	void Existing();

protected:
	UPROPERTY(EditAnywhere, BlueprintReadOnly, meta = (ClampMin = ""0""))
	float Health = 100.f;

	UPROPERTY()
	uint8 bIsDead : 1;

	UPROPERTY(EditAnywhere)
	TObjectPtr<UStaticMeshComponent> Mesh;

	FVector Location;
	bool bVisible = true;
	static int32 Counter;
	int32 A, B;
	float Values[4];
	float GetHealth() const;
};
";

        private static FieldDeclaration FieldOn(string marker, out string problem) =>
            EncapsulateFieldPlanner.FieldAt(FieldHeader, FieldHeader.IndexOf(marker, StringComparison.Ordinal), out problem);

        [Fact]
        public void FindsFieldsAndRejectsOthers()
        {
            var f = FieldOn("Health = 100", out _);
            Assert.Equal("Health", f.Name);
            Assert.Equal("float", f.Type);
            Assert.Equal("AMyActor", f.ClassName);
            Assert.Contains("BlueprintReadOnly", f.Specifiers);
            // caret on the UPROPERTY line works too
            Assert.Equal("Health", FieldOn("EditAnywhere, BlueprintReadOnly", out _).Name);
            f = FieldOn("bIsDead", out _);
            Assert.True(f.IsBitfield);
            Assert.True(f.IsBool);
            Assert.Null(FieldOn("Counter", out string why));
            Assert.Equal("static", why);
            Assert.Null(FieldOn("A, B", out why));
            Assert.Equal("several", why);
            Assert.Null(FieldOn("Values[4]", out why));
            Assert.Equal("array", why);
            Assert.Null(FieldOn("GetHealth() const", out _));
        }

        [Fact]
        public void AccessorNamesFollowUeConventions()
        {
            Assert.Equal(("IsDead", "SetIsDead"), EncapsulateFieldPlanner.Names(FieldOn("bIsDead", out _)));
            Assert.Equal(("IsVisible", "SetVisible"), EncapsulateFieldPlanner.Names(FieldOn("bVisible", out _)));
            Assert.Equal(("GetHealth", "SetHealth"), EncapsulateFieldPlanner.Names(FieldOn("Health = 100", out _)));
            Assert.Equal("UStaticMeshComponent*", EncapsulateFieldPlanner.ValueType(FieldOn("Mesh;", out _), out bool byRef));
            Assert.False(byRef);
            Assert.Equal("FVector", EncapsulateFieldPlanner.ValueType(FieldOn("FVector Location", out _), out byRef));
            Assert.True(byRef);
        }

        [Fact]
        public void GeneratesAccessorsInThePublicSection()
        {
            var f = FieldOn("FVector Location", out _);
            var plan = EncapsulateFieldPlanner.Plan(f, new EncapsulateOptions(), n => EncapsulateFieldPlanner.ClassDeclares(FieldHeader, f, n));
            Assert.True(plan.Ok, plan.Problem);
            string result = TextEditApplier.Apply(FieldHeader, EncapsulateFieldPlanner.Edits("f", FieldHeader, f, plan, out var point), out _);
            Assert.Contains("\tvoid Existing();\n\tconst FVector& GetLocation() const { return Location; }\n\tvoid SetLocation(const FVector& InLocation) { Location = InLocation; }\n", result);
            // an existing getter is not generated again
            var health = FieldOn("Health = 100", out _);
            plan = EncapsulateFieldPlanner.Plan(health, new EncapsulateOptions(), n => EncapsulateFieldPlanner.ClassDeclares(FieldHeader, health, n));
            Assert.Equal(new[] { "SetHealth" }, plan.Generated);
            plan = EncapsulateFieldPlanner.Plan(health, new EncapsulateOptions { Setter = false }, n => EncapsulateFieldPlanner.ClassDeclares(FieldHeader, health, n));
            Assert.Equal("exists", plan.Problem);
        }

        [Fact]
        public void BlueprintAccessorsUpdateTheUProperty()
        {
            var f = FieldOn("Health = 100", out _);
            var plan = EncapsulateFieldPlanner.Plan(f, new EncapsulateOptions { BlueprintGetter = true, BlueprintSetter = true }, n => false);
            Assert.Equal("EditAnywhere, BlueprintGetter=GetHealth, BlueprintSetter=SetHealth, meta = (ClampMin = \"0\")", plan.NewSpecifiers);
            Assert.Equal(new[]
            {
                "UFUNCTION(BlueprintGetter)", "float GetHealth() const { return Health; }", "UFUNCTION(BlueprintSetter)", "void SetHealth(float InHealth) { Health = InHealth; }",
            }, plan.Lines);
            string result = TextEditApplier.Apply(FieldHeader, EncapsulateFieldPlanner.Edits("f", FieldHeader, f, plan, out _), out int skipped);
            Assert.Equal(0, skipped);
            Assert.Contains("UPROPERTY(EditAnywhere, BlueprintGetter=GetHealth, BlueprintSetter=SetHealth, meta = (ClampMin = \"0\"))", result);
            // bool bitfield
            var dead = FieldOn("bIsDead", out _);
            plan = EncapsulateFieldPlanner.Plan(dead, new EncapsulateOptions(), n => false);
            Assert.Equal(new[] { "bool IsDead() const { return bIsDead; }", "void SetIsDead(bool bInIsDead) { bIsDead = bInIsDead; }" }, plan.Lines);
            Assert.Null(plan.NewSpecifiers);
        }

        // ================================================================ code scan

        [Fact]
        public void EnclosingBodyFindsFunctionsLambdasAndCtorBodies()
        {
            string text = "AFoo::AFoo() : Super(), X{1}\n{\n\tauto L = [this](int A) { return A; };\n\tif (X) { Y(); }\n}\nstruct S { int A = 1; };";
            var t = CppTextScan.Tokenize(text);
            var body = CodeScan.EnclosingBody(t, text.IndexOf("Y()", StringComparison.Ordinal));
            Assert.Equal(BodyKind.Function, body.Kind);
            Assert.Equal(1, t[body.OpenToken].Line);
            body = CodeScan.EnclosingBody(t, text.IndexOf("return A", StringComparison.Ordinal));
            Assert.Equal(BodyKind.Lambda, body.Kind);
            Assert.Null(CodeScan.EnclosingBody(t, text.IndexOf("A = 1", StringComparison.Ordinal)));
        }
    }
}
