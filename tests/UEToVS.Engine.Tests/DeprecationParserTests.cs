using System;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using UEToVS.Contracts;
using UEToVS.Engine.Deprecation;
using UEToVS.Engine.Indexing;
using UEToVS.Engine.Syntax;
using UEToVS.Engine.Unreal;
using Xunit;

namespace UEToVS.Engine.Tests
{
    /// <summary>v0.4.5 deprecation markers: the standalone parser and its path through the indexer into the segment.</summary>
    public sealed class DeprecationParserTests
    {
        private static byte[] U(string s) => Encoding.UTF8.GetBytes(s);

        [Fact]
        public void Scan_FindsAllMarkerFormsAndSkipsCommentsAndDefines()
        {
            const string src = @"
#define UE_DEPRECATED(Version, Message) [[deprecated(Message "" Please update your code."")]]
// UE_DEPRECATED(4.0, ""in a comment"")
/* [[deprecated(""block comment"")]] */
UE_DEPRECATED(5.1, ""Use GetActorNameOrLabel() instead."")
FString GetActorLabelOld() const;
UE_DEPRECATED_FORGAME(5.0, ""Game code should use NewThing."")
void OldGameThing();
[[deprecated(""use Bar"")]] void Foo();
[[nodiscard, deprecated]] int Baz();
DEPRECATED(4.20, ""legacy"") void Legacy();
";
            var marks = DeprecationParser.Scan(U(src));
            Assert.Equal(5, marks.Count);
            Assert.Equal(DeprecationMacro.UeDeprecated, marks[0].Kind);
            Assert.Equal("5.1", marks[0].Version);
            Assert.Equal("Use GetActorNameOrLabel() instead.", marks[0].Message);
            Assert.Equal(DeprecationMacro.UeDeprecatedForGame, marks[1].Kind);
            Assert.Equal("5.0", marks[1].Version);
            Assert.Equal(DeprecationMacro.CxxAttribute, marks[2].Kind);
            Assert.Equal("use Bar", marks[2].Message);
            Assert.True(string.IsNullOrEmpty(marks[2].Version));
            Assert.Equal(DeprecationMacro.CxxAttribute, marks[3].Kind);
            Assert.True(string.IsNullOrEmpty(marks[3].Message));
            Assert.Equal(DeprecationMacro.Legacy, marks[4].Kind);
            Assert.Equal("4.20", marks[4].Version);
        }

        [Fact]
        public void Scan_ConcatenatedMessageAndNoMarkerFastPath()
        {
            var marks = DeprecationParser.Scan(U("UE_DEPRECATED(5.3, \"Use \" \"NewApi\" \" instead.\") void Old();"));
            Assert.Single(marks);
            Assert.Equal("Use NewApi instead.", marks[0].Message);
            Assert.False(DeprecationParser.MayContain(U("void Fine(); int32 X;")));
            Assert.Empty(DeprecationParser.Scan(U("void Fine();")));
        }

        [Fact]
        public void Attach_BindsMarkerToFollowingDeclarationOnly()
        {
            const string src = @"
UCLASS()
class AThing : public AActor
{
	GENERATED_BODY()
public:
	UE_DEPRECATED(5.2, ""Use NewDo instead."")
	UFUNCTION(BlueprintCallable)
	void OldDo(int32 A);

	void NewDo(int32 A);

	UE_DEPRECATED(5.0, ""Use bNewFlag."")
	bool bOldFlag_DEPRECATED;
	bool bNewFlag;
};

class UE_DEPRECATED(5.1, ""Use UNewThing."") UOldThing
{
};

UE_DEPRECATED(5.3, ""dangling"");
void NotMarked();
";
            byte[] utf8 = U(src);
            var summary = CppParser.Parse(utf8, new ParseOptions());
            var decls = summary.Declarations;
            var attached = DeprecationParser.Attach(utf8, decls);
            Assert.NotNull(attached);
            string VersionOf(string name)
            {
                for (int i = 0; i < decls.Count; i++)
                    if (decls[i].Name == name && !decls[i].IsImplicit) return attached[i]?.Version;
                throw new InvalidOperationException(name + " not parsed");
            }
            Assert.Equal("5.2", VersionOf("OldDo"));
            Assert.Null(VersionOf("NewDo"));
            Assert.Equal("5.0", VersionOf("bOldFlag_DEPRECATED"));
            Assert.Null(VersionOf("bNewFlag"));
            Assert.Equal("5.1", VersionOf("UOldThing"));
            Assert.Null(VersionOf("NotMarked")); // a ';' after the marker ends it
            Assert.Null(VersionOf("AThing"));
            Assert.Null(DeprecationParser.Attach(U("void A(); void B();"), CppParser.Parse(U("void A(); void B();"), new ParseOptions()).Declarations));
        }

        [Theory]
        [InlineData("Use GetActorNameOrLabel() instead.", "GetActorLabel", "GetActorNameOrLabel")]
        [InlineData("Please call UGameplayStatics::SpawnSound2D instead", "PlaySound", "UGameplayStatics::SpawnSound2D")]
        [InlineData("Replaced by FNewThing.", "FOldThing", "FNewThing")]
        [InlineData("Renamed to SetTargetArmLength", "SetArmLength", "SetTargetArmLength")]
        [InlineData("Deprecated in favor of GetOwningPlayer().", "GetOwner", "GetOwningPlayer")]
        [InlineData("Use the new API instead.", "Old", null)]
        [InlineData("This function is no longer used.", "Old", null)]
        [InlineData("Use OldName instead.", "OldName", null)]
        [InlineData(null, "Old", null)]
        public void SuggestReplacement_OnlyIdentifierLikeTargets(string message, string name, string expected)
        {
            Assert.Equal(expected, DeprecationParser.SuggestReplacement(message, name));
        }

        [Fact]
        public void CompareVersions_IsNumeric()
        {
            Assert.True(DeprecationParser.CompareVersions("5.10", "5.9") > 0);
            Assert.True(DeprecationParser.CompareVersions("4.27", "5.0") < 0);
            Assert.Equal(0, DeprecationParser.CompareVersions("5.1", "5.1"));
            Assert.True(DeprecationParser.CompareVersions("", "5.0") < 0);
            Assert.Equal("Bar", DeprecationParser.LastSegment("Foo::Bar"));
        }

        [Fact]
        public void Indexer_StoresDeprecationInSegment()
        {
            string root = Path.Combine(Path.GetTempPath(), "uetovs-depr", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(root);
            try
            {
                string path = Path.Combine(root, "Old.h");
                File.WriteAllText(path, "struct FOld {\n\tUE_DEPRECATED(5.2, \"Use NewDo instead.\")\n\tvoid OldDo();\n\tvoid NewDo();\n};\n");
                var backend = new SegmentedIndexBackend();
                backend.TryLoad(Path.Combine(root, ".index"), "depr");
                var ctx = new FileContext { Path = path, Module = "Game", Origin = ItemOrigin.Project, Kind = SourceFileKind.Header };
                backend.UpdateParsed(path, new SyntaxFileParser().Parse(ctx, File.ReadAllBytes(path), CancellationToken.None));
                var list = ((IDeprecationIndex)backend).ListDeprecated(CancellationToken.None);
                var d = Assert.Single(list);
                Assert.Equal("OldDo", d.Symbol.Name);
                Assert.Equal("5.2", d.Version);
                Assert.Equal("Use NewDo instead.", d.Message);
                backend.Dispose();
            }
            finally
            {
                try { Directory.Delete(root, true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
            }
        }
    }
}
