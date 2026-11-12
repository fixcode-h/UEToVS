using System.Linq;
using UEToVS.Contracts;
using Xunit;
using static UEToVS.Engine.Syntax.Tests.TestUtil;

namespace UEToVS.Engine.Syntax.Tests
{
    public class OccurrenceTests
    {
        private static Occurrence Single(FileSummary s, string name) => Assert.Single(Occ(s, name));

        [Fact]
        public void ReadsWritesAndCalls()
        {
            var s = Parse("void F() { Health = MaxHealth; Count += Step; ++Ticks; Frames--; DoThing(Arg); }");
            Assert.Equal(OccurrenceRole.Write, Single(s, "Health").Role);
            Assert.Equal(OccurrenceRole.Read, Single(s, "MaxHealth").Role);
            Assert.Equal(OccurrenceRole.Write, Single(s, "Count").Role);
            Assert.Equal(OccurrenceRole.Read, Single(s, "Step").Role);
            Assert.Equal(OccurrenceRole.Write, Single(s, "Ticks").Role);
            Assert.Equal(OccurrenceRole.Write, Single(s, "Frames").Role);
            Assert.Equal(OccurrenceRole.Call, Single(s, "DoThing").Role);
            Assert.Equal(OccurrenceRole.Read, Single(s, "Arg").Role);
        }

        [Fact]
        public void MemberAccessQualifiers()
        {
            var s = Parse("void F() { Comp->Activate(); Data.Value = 3; Super::BeginPlay(); UGameplayStatics::GetPlayerPawn(this, 0)->Jump(); }");
            var activate = Single(s, "Activate");
            Assert.Equal(OccurrenceRole.Call | OccurrenceRole.MemberAccess, activate.Role);
            Assert.Equal(MemberQualifier.Arrow, activate.Qualifier);
            var value = Single(s, "Value");
            Assert.Equal(OccurrenceRole.Write | OccurrenceRole.MemberAccess, value.Role);
            Assert.Equal(MemberQualifier.Dot, value.Qualifier);
            Assert.Equal(OccurrenceRole.Read, Single(s, "Data").Role);
            Assert.Equal(OccurrenceRole.TypeUse, Single(s, "Super").Role);
            var bp = Single(s, "BeginPlay");
            Assert.Equal(MemberQualifier.Scope, bp.Qualifier);
            Assert.True((bp.Role & OccurrenceRole.Call) != 0);
            Assert.Equal(OccurrenceRole.TypeUse, Single(s, "UGameplayStatics").Role);
            Assert.Equal(MemberQualifier.Arrow, Single(s, "Jump").Qualifier);
        }

        [Fact]
        public void TypeUsesInLocalsTemplatesAndNew()
        {
            var s = Parse("void F() { FVector Loc = GetLoc(); AActor* Actor = nullptr; auto* C = Cast<ACharacter>(Actor); TArray<FName> Names; UThing* T = NewObject<UThing>(this); auto P = new FPlain(); }");
            Assert.Equal(OccurrenceRole.TypeUse, Single(s, "FVector").Role);
            Assert.Equal(OccurrenceRole.Write, Single(s, "Loc").Role);
            Assert.Equal(OccurrenceRole.TypeUse, Single(s, "AActor").Role);
            Assert.Equal(OccurrenceRole.Call, Single(s, "Cast").Role);
            Assert.Equal(OccurrenceRole.TypeUse, Single(s, "ACharacter").Role);
            Assert.Equal(OccurrenceRole.TypeUse, Single(s, "TArray").Role);
            Assert.Equal(OccurrenceRole.TypeUse, Single(s, "FName").Role);
            Assert.All(Occ(s, "UThing"), o => Assert.Equal(OccurrenceRole.TypeUse, o.Role));
            Assert.Equal(OccurrenceRole.Call, Single(s, "NewObject").Role);
            Assert.Equal(OccurrenceRole.TypeUse, Single(s, "FPlain").Role);
        }

        [Fact]
        public void LessThanIsNotATemplate()
        {
            var s = Parse("void F() { if (A < B && C > D) { Run(); } for (int I = 0; I < Num; ++I) {} }");
            Assert.Equal(OccurrenceRole.Read, Single(s, "A").Role);
            Assert.Equal(OccurrenceRole.Read, Single(s, "B").Role);
            Assert.Equal(OccurrenceRole.Read, Single(s, "Num").Role);
        }

        [Fact]
        public void AddressOf()
        {
            var s = Parse("void F() { Bind(&UMyComp::OnHit, &Local); int X = A & B; }");
            Assert.True((Single(s, "Local").Role & OccurrenceRole.AddressOf) != 0);
            Assert.True((Single(s, "UMyComp").Role & OccurrenceRole.AddressOf) != 0);
            Assert.True((Single(s, "B").Role & OccurrenceRole.AddressOf) == 0);
        }

        [Fact]
        public void LambdasInsideBodiesAreScanned()
        {
            var s = Parse("void F() { auto L = [this, &Total](int32 X) { if (X > 0) { Total += X; Notify(); } }; L(1); }\nint After;");
            Assert.Equal(OccurrenceRole.Write, Occ(s, "Total").Last().Role);
            Assert.Single(Occ(s, "Notify"));
            One(s, "After");
            Assert.Equal(s.Declarations.ToList().IndexOf(One(s, "F")), Single(s, "Notify").Function);
        }

        [Fact]
        public void OccurrencesHaveOffsetsAndInternedNames()
        {
            const string code = "void F() { Foo(); Foo(); }";
            var s = Parse(code);
            var foos = Occ(s, "Foo");
            Assert.Equal(2, foos.Length);
            Assert.Equal(code.IndexOf("Foo"), foos[0].Offset);
            Assert.Same(foos[0].Name, foos[1].Name);
        }

        [Fact]
        public void OccurrencesCanBeDisabled()
        {
            var s = Parse("void F() { Foo(); } int G;", new ParseOptions { CollectOccurrences = false });
            Assert.Empty(s.Occurrences);
            Assert.False(One(s, "F").Body.IsNone);
            One(s, "G");
        }

        [Fact]
        public void NoDeclarationsFromInsideBodies()
        {
            var s = Parse("void F() { int Local = 0; struct FLocal { int Q; }; auto Lambda = []() {}; for (int I = 0; I < 3; ++I) {} }");
            Assert.Equal(new[] { "F" }, s.Declarations.Select(d => d.Name).ToArray());
        }

        [Fact]
        public void SharedNameTableAcrossFiles()
        {
            var names = new NameTable();
            var opt = new ParseOptions { Names = names };
            var a = Parse("void F() { Shared(); }", opt);
            var b = Parse("void G() { Shared(); }", opt);
            Assert.Same(a.Occurrences[0].Name, b.Occurrences[0].Name);
        }
    }
}
