using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using UEToVS.Contracts;
using UEToVS.Engine.Indexing;
using UEToVS.Engine.IntelliSense;
using UEToVS.Engine.Unreal;
using Xunit;
using Xunit.Abstractions;

namespace UEToVS.Engine.Tests
{
    /// <summary>A small UE-like tree for the v0.4.5 editor features: inlay hints, deprecated APIs, naming conventions.</summary>
    public sealed class EditorFeatureFixture : IDisposable
    {
        public static readonly Dictionary<string, string> Sources = new Dictionary<string, string>
        {
            ["Engine/CoreUObject.h"] = @"#pragma once
class COREUOBJECT_API UObject
{
public:
	UObject();
};
class COREUOBJECT_API UInterface : public UObject
{
};
",
            ["Engine/Actor.h"] = @"#pragma once
#include ""CoreUObject.h""
struct FVector { float X; float Y; float Z; };
UCLASS()
class ENGINE_API AActor : public UObject
{
	GENERATED_BODY()
public:
	void SetActorLocation(const FVector& NewLocation, bool bSweep = false, int32 Flags = 0);
	void SetLifeSpan(float InLifespan);
	void Overloaded(int32 Count);
	void Overloaded(float Scale);
	void Pair(int32 A, int32 B);
	void Pair(float A, float Other);
	AActor* GetOwner() const;
	FVector GetActorLocation() const;

	UE_DEPRECATED(5.1, ""Use NewThing instead."")
	void OldThing(int32 Value);
	void NewThing(int32 Value);

	UE_DEPRECATED(5.2, ""This is gone; there is no replacement."")
	void Gone();

	[[deprecated(""Use Modern() instead"")]]
	void Legacy();
	void Modern();

	UE_DEPRECATED_FORGAME(5.3, ""Please use FreshValue instead"")
	int32 StaleValue;
	int32 FreshValue;
};

void FreeFunction(int32 Count, const TCHAR* Name);
",
            ["Game/MyActor.h"] = @"#pragma once
#include ""Actor.h""
#include ""MyActor.generated.h""

UCLASS()
class MYGAME_API AMyActor : public AActor
{
	GENERATED_BODY()
public:
	void Run();
	bool bReady;
	bool Visible;
};
",
        };

        public EditorFeatureFixture()
        {
            Root = Path.Combine(Path.GetTempPath(), "uetovs-editorfeatures", Guid.NewGuid().ToString("N"));
            Backend = new SegmentedIndexBackend();
            Backend.TryLoad(Path.Combine(Root, ".index"), "editor-features");
            var parser = new SyntaxFileParser();
            foreach (var kv in Sources)
            {
                string path = PathOf(kv.Key);
                Directory.CreateDirectory(Path.GetDirectoryName(path));
                File.WriteAllText(path, kv.Value);
                bool engine = kv.Key.StartsWith("Engine/", StringComparison.Ordinal);
                var listed = new ListedFile { Path = path, Name = Path.GetFileName(path), Module = engine ? "Engine" : "MyGame", Origin = engine ? ItemOrigin.Engine : ItemOrigin.Project, Kind = SourceFileKind.Header };
                Files.Upsert(listed);
                var ctx = new FileContext { Path = path, Module = listed.Module, Origin = listed.Origin, Kind = listed.Kind };
                Backend.UpdateParsed(path, parser.Parse(ctx, File.ReadAllBytes(path), CancellationToken.None));
            }
            Service = new IntelliSenseService(Backend, Files, () => true);
        }

        public string Root { get; }
        public SegmentedIndexBackend Backend { get; }
        public FileTable Files { get; } = new FileTable();
        public IntelliSenseService Service { get; }

        public string PathOf(string relative) => Path.Combine(Root, relative.Replace('/', Path.DirectorySeparatorChar));

        /// <summary>A project .cpp that is listed in the file table (so it counts as project code).</summary>
        public string ProjectCpp(string name)
        {
            string path = PathOf("Game/" + name);
            if (!Files.TryGet(path, out _))
                Files.Upsert(new ListedFile { Path = path, Name = name, Module = "MyGame", Origin = ItemOrigin.Project, Kind = SourceFileKind.Source });
            return path;
        }

        public void Dispose()
        {
            Backend.Dispose();
            try { Directory.Delete(Root, true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }
    }

    public sealed class InlayHintsTests : IClassFixture<EditorFeatureFixture>
    {
        private static long _version = 9000;
        private readonly EditorFeatureFixture _f;
        private readonly ITestOutputHelper _out;

        public InlayHintsTests(EditorFeatureFixture f, ITestOutputHelper output)
        {
            _f = f;
            _out = output;
        }

        private InlayHintsResponse Run(string text, bool names = true, bool autos = false, int start = 0, int end = 100000)
        {
            var req = new InlayHintsRequest
            {
                Document = new CaretRequest { FilePath = _f.ProjectCpp("MyActor.cpp"), Text = text, TextVersion = Interlocked.Increment(ref _version) },
                StartLine = start,
                EndLine = end,
                ParameterNames = names,
                AutoTypes = autos,
            };
            var resp = _f.Service.GetInlayHints(req, CancellationToken.None);
            foreach (var h in resp.Items) _out.WriteLine($"{h.Kind} {h.Line + 1}:{h.Column + 1} {h.Text} ({h.Detail})");
            return resp;
        }

        private static (int Line, int Column) At(string text, string marker)
        {
            int i = text.IndexOf(marker, StringComparison.Ordinal);
            Assert.True(i >= 0, marker);
            int line = text.Substring(0, i).Count(c => c == '\n');
            int col = i - (text.LastIndexOf('\n', Math.Max(0, i - 1)) + 1);
            if (i == 0) col = 0;
            return (line, col);
        }

        [Fact]
        public void ParameterNames_OnlyForLiterals()
        {
            string text = "#include \"MyActor.h\"\nvoid AMyActor::Run()\n{\n\tFVector Loc;\n\tSetActorLocation(Loc, true, 3);\n\tSetLifeSpan(-2.5f);\n\tFreeFunction(Count, TEXT(\"Name\"));\n\tSetLifeSpan(Speed);\n}\n";
            var r = Run(text);
            var hints = r.Items.Select(h => (h.Line, h.Column, h.Text)).ToList();
            var (l1, c1) = At(text, "true, 3");
            Assert.Contains((l1, c1, "bSweep"), hints);
            Assert.Contains((l1, c1 + 6, "Flags"), hints);
            var (l2, c2) = At(text, "-2.5f");
            Assert.Contains((l2, c2, "InLifespan"), hints);
            var (l3, c3) = At(text, "TEXT(\"Name\")");
            Assert.Contains((l3, c3, "Name"), hints);
            // non-literal arguments never get a hint
            Assert.DoesNotContain(hints, h => h.Text == "NewLocation" || h.Text == "Count");
            Assert.Equal(4, hints.Count);
            Assert.All(r.Items, h => Assert.Equal(InlayHintKind.ParameterName, h.Kind));
            Assert.True(r.Calls >= 3);
        }

        [Fact]
        public void ParameterNames_OverloadsMustAgree()
        {
            string text = "void AMyActor::Run()\n{\n\tOverloaded(3);\n\tPair(1, 2);\n\tGetOwner()->SetLifeSpan(1.0f);\n}\n";
            var hints = Run(text).Items.Select(h => h.Text).ToList();
            Assert.DoesNotContain("Count", hints);
            Assert.DoesNotContain("Scale", hints);
            Assert.Contains("A", hints);          // both overloads call the first parameter A
            Assert.DoesNotContain("B", hints);    // B vs Other
            Assert.Contains("InLifespan", hints); // member call through a chain
        }

        [Fact]
        public void ParameterNames_SkipsMacrosAndDeclarations()
        {
            string text = "void AMyActor::Run()\n{\n\tUE_LOG(LogTemp, Log, TEXT(\"x\"));\n\tcheck(true);\n}\nvoid Other(int32 A = 3);\n";
            Assert.Empty(Run(text).Items);
        }

        [Fact]
        public void ParameterNames_OnlyRequestedLines()
        {
            string text = "void AMyActor::Run()\n{\n\tSetLifeSpan(1.0f);\n\tSetLifeSpan(2.0f);\n\tSetLifeSpan(3.0f);\n}\n";
            var r = Run(text, start: 3, end: 3);
            Assert.Single(r.Items);
            Assert.Equal(3, r.Items[0].Line);
        }

        [Fact]
        public void AutoTypes_AfterTheName_WhenNotWrittenInTheInitializer()
        {
            string text = "void AMyActor::Run()\n{\n\tauto Loc = GetActorLocation();\n\tauto* Owner = GetOwner();\n\tauto Same = FVector();\n\tint32 Plain = 3;\n}\n";
            var r = Run(text, names: false, autos: true);
            var hints = r.Items.Select(h => (h.Line, h.Column, h.Text)).ToList();
            var (l1, c1) = At(text, "Loc =");
            Assert.Contains((l1, c1 + 3, "FVector"), hints);
            var (l2, c2) = At(text, "Owner =");
            Assert.Contains(hints, h => h.Line == l2 && h.Column == c2 + 5 && h.Text.StartsWith("AActor", StringComparison.Ordinal));
            Assert.DoesNotContain(hints, h => h.Line == At(text, "Same =").Line);
            Assert.All(r.Items, h => Assert.Equal(InlayHintKind.Type, h.Kind));
            Assert.Equal(2, hints.Count);
        }

        [Fact]
        public void Off_ProducesNothing()
        {
            string text = "void AMyActor::Run()\n{\n\tSetLifeSpan(1.0f);\n\tauto Loc = GetActorLocation();\n}\n";
            Assert.Empty(Run(text, names: false, autos: false).Items);
        }
    }
}
