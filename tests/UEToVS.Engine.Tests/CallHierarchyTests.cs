using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using UEToVS.Contracts;
using UEToVS.Engine.Indexing;
using UEToVS.Engine.Navigation;
using UEToVS.Engine.Unreal;
using Xunit;
using Xunit.Abstractions;

namespace UEToVS.Engine.Tests
{
    /// <summary>A few source files written to a temp folder and indexed in memory (call hierarchy, quick info tests).</summary>
    public sealed class MiniWorkspace : IDisposable
    {
        public MiniWorkspace(IDictionary<string, string> files)
        {
            Root = Path.Combine(Path.GetTempPath(), "uetovs-mini", Guid.NewGuid().ToString("N"));
            Backend = new SegmentedIndexBackend();
            Backend.TryLoad(Path.Combine(Root, ".index"), "mini");
            var parser = new SyntaxFileParser();
            foreach (var kv in files)
            {
                string path = PathOf(kv.Key);
                Directory.CreateDirectory(Path.GetDirectoryName(path));
                File.WriteAllText(path, kv.Value);
                if (!path.EndsWith(".h", StringComparison.Ordinal) && !path.EndsWith(".cpp", StringComparison.Ordinal)) continue;
                bool engine = kv.Key.StartsWith("Engine/", StringComparison.Ordinal);
                var listed = new ListedFile
                {
                    Path = path,
                    Name = Path.GetFileName(path),
                    Module = engine ? "Engine" : "MyGame",
                    Origin = engine ? ItemOrigin.Engine : ItemOrigin.Project,
                    Kind = path.EndsWith(".h", StringComparison.Ordinal) ? SourceFileKind.Header : SourceFileKind.Source,
                };
                Files.Upsert(listed);
                var ctx = new FileContext { Path = path, Module = listed.Module, Origin = listed.Origin, Kind = listed.Kind };
                Backend.UpdateParsed(path, parser.Parse(ctx, File.ReadAllBytes(path), CancellationToken.None));
            }
            Navigation = new NavigationService(Backend, Files);
        }

        public string Root { get; }
        public SegmentedIndexBackend Backend { get; }
        public FileTable Files { get; } = new FileTable();
        public NavigationService Navigation { get; }

        public string PathOf(string relative) => Path.Combine(Root, relative.Replace('/', Path.DirectorySeparatorChar));

        /// <summary>Caret inside the n-th occurrence of <paramref name="text"/> (+<paramref name="into"/> characters).</summary>
        public CaretRequest Caret(string relative, string text, int nth = 0, int into = 1)
        {
            var lines = File.ReadAllLines(PathOf(relative));
            int seen = 0;
            for (int l = 0; l < lines.Length; l++)
            {
                int i = -1;
                while ((i = lines[l].IndexOf(text, i + 1, StringComparison.Ordinal)) >= 0)
                    if (seen++ == nth) return new CaretRequest { FilePath = PathOf(relative), Line = l, Column = i + into };
            }
            throw new InvalidOperationException($"'{text}' #{nth} not found in {relative}");
        }

        public void Dispose()
        {
            Backend.Dispose();
            try { Directory.Delete(Root, true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }
    }

    public sealed class CallHierarchyFixture : IDisposable
    {
        public const string Header = "Game/MyActor.h";
        public const string Source = "Game/MyActor.cpp";
        public const string Other = "Game/Other.cpp";

        public CallHierarchyFixture()
        {
            W = new MiniWorkspace(new Dictionary<string, string>
            {
                [Header] = @"#pragma once
#include ""MyActor.generated.h""
UCLASS()
class MYGAME_API AMyActor : public AActor
{
	GENERATED_BODY()
public:
	UFUNCTION(BlueprintNativeEvent)
	void OnHit(float Damage);

	UFUNCTION()
	void HandleClicked();

	UFUNCTION()
	void HandleTimer();

	void Fire();
	void Reload(int Count);
	virtual void BeginPlay() override;
	FSimpleDelegate OnDone;
	FTimerHandle Timer;
	UButton* Button;
};
",
                [Source] = @"#include ""MyActor.h""

void AMyActor::OnHit_Implementation(float Damage)
{
	Reload(1);
}

void AMyActor::Fire()
{
	OnHit(10.f);
	Reload(2);
	Helper();
	UE_LOG(LogTemp, Log, TEXT(""x""));
}

void AMyActor::Reload(int Count)
{
}

void AMyActor::BeginPlay()
{
	Fire();
	Button->OnClicked.AddDynamic(this, &AMyActor::HandleClicked);
	GetWorldTimerManager().SetTimer(Timer, this, &AMyActor::HandleTimer, 1.f, true);
	OnDone.BindUFunction(this, ""HandleClicked"");
}

void AMyActor::HandleClicked()
{
	Fire();
}

void AMyActor::HandleTimer()
{
}
",
                [Other] = @"#include ""MyActor.h""

void UseActor(AMyActor* A)
{
	A->Fire();
	A->OnHit(5.f);
}
",
            });
        }

        public MiniWorkspace W { get; }

        public void Dispose() => W.Dispose();
    }

    public sealed class CallHierarchyTests : IClassFixture<CallHierarchyFixture>
    {
        private readonly MiniWorkspace _w;
        private readonly ITestOutputHelper _out;

        public CallHierarchyTests(CallHierarchyFixture f, ITestOutputHelper output)
        {
            _w = f.W;
            _out = output;
        }

        private CallHierarchyResponse Get(CaretRequest caret, CallHierarchyDirection direction, SymbolInfo function = null)
        {
            var r = _w.Navigation.GetCallHierarchy(new CallHierarchyRequest { Caret = caret, Function = function, Direction = direction }, CancellationToken.None);
            _out.WriteLine($"{direction} of {r.Root?.Container}::{r.Root?.Name} [{string.Join(",", r.Names)}] reason={r.Reason}");
            foreach (var i in r.Items)
                _out.WriteLine($"  {Name(i)} expand={i.CanExpand} conf={i.Confidence} sites={string.Join("; ", i.Sites.Select(s => s.Kind + "@" + (s.Location.Line + 1) + " " + s.BindMethod))}");
            return r;
        }

        private static string Name(CallHierarchyItem i) => i.Function == null ? i.DisplayName : (i.Function.Container == null ? "" : i.Function.Container + "::") + i.Function.Name;

        [Fact]
        public void Callers_AreGroupedByEnclosingFunction()
        {
            var r = Get(_w.Caret(CallHierarchyFixture.Header, "void Fire", into: 6), CallHierarchyDirection.Callers);
            Assert.Equal("ok", r.Reason);
            Assert.Equal("Fire", r.Root.Name);
            var names = r.Items.Select(Name).ToList();
            Assert.Contains("AMyActor::BeginPlay", names);
            Assert.Contains("AMyActor::HandleClicked", names);
            Assert.Contains("UseActor", names);
            Assert.DoesNotContain("AMyActor::Fire", names); // the definition itself is not a caller
            Assert.All(r.Items, i => Assert.All(i.Sites, s => Assert.Equal(CallSiteKind.Call, s.Kind)));
            Assert.All(r.Items, i => Assert.True(i.CanExpand));
            var use = r.Items.Single(i => Name(i) == "UseActor").Sites.Single();
            Assert.Equal("Fire", use.LineText.Substring(use.LineTextColumn, use.Length));
        }

        [Fact]
        public void ImplementationAndDeclarationAreOneFunction()
        {
            // caret on the _Implementation definition: reported as the UFUNCTION OnHit, callers of OnHit(...) listed
            var r = Get(_w.Caret(CallHierarchyFixture.Source, "OnHit_Implementation", into: 2), CallHierarchyDirection.Callers);
            Assert.Equal("OnHit", r.Root.Name);
            Assert.Contains("OnHit_Implementation", r.Names);
            var names = r.Items.Select(Name).ToList();
            Assert.Contains("AMyActor::Fire", names);
            Assert.Contains("UseActor", names);

            // callees of the declaration = the body of OnHit_Implementation
            var callees = Get(_w.Caret(CallHierarchyFixture.Header, "void OnHit", into: 6), CallHierarchyDirection.Callees);
            Assert.Equal("ok", callees.Reason);
            Assert.Contains("AMyActor::Reload", callees.Items.Select(Name));
        }

        [Fact]
        public void DelegateBindingsCountAsCallers()
        {
            var r = Get(_w.Caret(CallHierarchyFixture.Header, "void HandleClicked", into: 6), CallHierarchyDirection.Callers);
            var begin = r.Items.Single(i => Name(i) == "AMyActor::BeginPlay");
            Assert.Contains(begin.Sites, s => s.Kind == CallSiteKind.DelegateBinding && s.BindMethod == "AddDynamic");
            Assert.Contains(begin.Sites, s => s.Kind == CallSiteKind.DelegateBinding && s.BindMethod == "BindUFunction");
            Assert.All(begin.Sites, s => Assert.Equal(CallSiteKind.DelegateBinding, s.Kind));

            var timer = Get(_w.Caret(CallHierarchyFixture.Header, "void HandleTimer", into: 6), CallHierarchyDirection.Callers);
            var site = timer.Items.Single().Sites.Single();
            Assert.Equal(CallSiteKind.DelegateBinding, site.Kind);
            Assert.Equal("SetTimer", site.BindMethod);
        }

        [Fact]
        public void Callees_InSourceOrder_WithBindingsAndUnresolvedCalls()
        {
            var r = Get(_w.Caret(CallHierarchyFixture.Source, "AMyActor::Fire", into: 11), CallHierarchyDirection.Callees);
            var names = r.Items.Select(Name).ToList();
            Assert.Equal(new[] { "AMyActor::OnHit", "AMyActor::Reload", "Helper" }, names);
            Assert.Equal(ResolutionConfidence.NameOnly, r.Items[2].Confidence);
            Assert.False(r.Items[2].CanExpand);
            Assert.DoesNotContain("UE_LOG", names); // macros are not callees

            var begin = Get(_w.Caret(CallHierarchyFixture.Source, "AMyActor::BeginPlay", into: 11), CallHierarchyDirection.Callees);
            var clicked = begin.Items.Single(i => Name(i) == "AMyActor::HandleClicked");
            Assert.Equal(CallSiteKind.DelegateBinding, clicked.Sites.Single().Kind);
            Assert.Equal("AddDynamic", clicked.Sites.Single().BindMethod);
            Assert.Contains("AMyActor::Fire", begin.Items.Select(Name));
        }

        [Fact]
        public void ExpandingANodeUsesItsFunction()
        {
            var first = Get(_w.Caret(CallHierarchyFixture.Header, "void Reload", into: 6), CallHierarchyDirection.Callers);
            var fire = first.Items.Single(i => Name(i) == "AMyActor::Fire");
            var second = Get(null, CallHierarchyDirection.Callers, fire.Function);
            Assert.Equal("Fire", second.Root.Name);
            Assert.Contains("UseActor", second.Items.Select(Name));
        }

        [Fact]
        public void CaretInsideABodyMeansTheEnclosingFunction()
        {
            var r = Get(_w.Caret(CallHierarchyFixture.Source, "10.f", into: 1), CallHierarchyDirection.Callees);
            Assert.Equal("ok", r.Reason);
            Assert.Equal("Fire", r.Root.Name);
        }

        [Fact]
        public void NoFunctionIsReported()
        {
            var r = Get(_w.Caret(CallHierarchyFixture.Header, "FTimerHandle Timer", into: 15), CallHierarchyDirection.Callers);
            Assert.Equal("no-function", r.Reason);
            Assert.Empty(r.Items);
        }

        [Fact]
        public void LooksLikeMacro()
        {
            Assert.True(NavigationService.LooksLikeMacro("UE_LOG"));
            Assert.True(NavigationService.LooksLikeMacro("TEXT"));
            Assert.False(NavigationService.LooksLikeMacro("Helper"));
            Assert.False(NavigationService.LooksLikeMacro("X"));
        }
    }
}
