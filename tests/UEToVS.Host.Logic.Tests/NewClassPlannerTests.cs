using System;
using System.IO;
using System.Linq;
using UEToVS.Host.Logic.Unreal;
using Xunit;

namespace UEToVS.Host.Logic.Tests
{
    public class NewClassPlannerTests
    {
        [Fact]
        public void CommonParentsArePinnedWithTheirKinds()
        {
            var names = NewClassPlanner.CommonParents.Where(p => p.Name != null).Select(p => p.Name).ToList();
            Assert.Equal("AActor", names[0]);
            foreach (var n in new[] { "UObject", "UActorComponent", "ACharacter", "UUserWidget" }) Assert.Contains(n, names);
            Assert.Equal(NewClassKind.Actor, NewClassPlanner.FindCommon("ACharacter").Kind);
            Assert.Equal("Blueprint/UserWidget.h", NewClassPlanner.FindCommon("UUserWidget").Include);
            Assert.Contains(NewClassPlanner.CommonParents, p => p.Name == null && p.Kind == NewClassKind.Struct);
            Assert.Contains(NewClassPlanner.CommonParents, p => p.Name == null && p.Kind == NewClassKind.Plain);
        }

        [Theory]
        [InlineData("AMyBaseActor", true, false, NewClassKind.Actor)]
        [InlineData("UMyHealthComponent", true, false, NewClassKind.ActorComponent)]
        [InlineData("UMySettings", true, false, NewClassKind.Object)]
        [InlineData("FMyRow", false, true, NewClassKind.Struct)]
        [InlineData("FPlainThing", false, false, NewClassKind.Plain)]
        [InlineData("UUserWidget", true, false, NewClassKind.Object)]
        public void KindFromParent(string name, bool uclass, bool ustruct, NewClassKind kind) =>
            Assert.Equal(kind, NewClassPlanner.KindForParent(name, uclass, ustruct));

        [Theory]
        [InlineData("MyActor", NewClassKind.Actor, "AMyActor", "MyActor")]
        [InlineData("AMyActor", NewClassKind.Actor, "AMyActor", "MyActor")]
        [InlineData("myActor", NewClassKind.Actor, "AMyActor", "MyActor")]
        [InlineData("Health", NewClassKind.ActorComponent, "UHealth", "Health")]
        [InlineData("UHealth", NewClassKind.Object, "UHealth", "Health")]
        [InlineData("Row", NewClassKind.Struct, "FRow", "Row")]
        [InlineData("Apple", NewClassKind.Actor, "AApple", "Apple")]
        public void PrefixIsAppliedOnce(string typed, NewClassKind kind, string full, string fileBase)
        {
            Assert.Equal(full, NewClassPlanner.ApplyPrefix(typed, kind));
            Assert.Equal(fileBase, NewClassPlanner.FileBaseName(full, kind));
        }

        [Theory]
        [InlineData("", "empty")]
        [InlineData("  ", "empty")]
        [InlineData("1Bad", "invalid")]
        [InlineData("My Actor", "invalid")]
        [InlineData("My-Actor", "invalid")]
        [InlineData("Größe", "invalid")]
        [InlineData("Actor", "same-as-parent")]
        [InlineData("MyActor", null)]
        public void NameValidation(string typed, string problem) =>
            Assert.Equal(problem, NewClassPlanner.ValidateName(typed, NewClassKind.Actor, "AActor"));

        [Fact]
        public void TooLongNamesAreRejected() =>
            Assert.Equal("too-long", NewClassPlanner.ValidateName(new string('x', 80), NewClassKind.Object, null));

        [Fact]
        public void ApiMacroIsUpperCaseModule()
        {
            Assert.Equal("MYGAME_API", NewClassPlanner.ApiMacro("MyGame"));
            Assert.Null(NewClassPlanner.ApiMacro(""));
        }

        [Fact]
        public void ActorTemplate()
        {
            var spec = new NewClassSpec
            {
                ClassName = "AMyActor", Kind = NewClassKind.Actor, ParentName = "AActor", ParentInclude = "GameFramework/Actor.h", ApiMacro = "MYGAME_API",
                HeaderInclude = "Gameplay/MyActor.h", Copyright = "Copyright Me", NewLine = "\n",
            };
            string h = NewClassPlanner.Header(spec);
            Assert.Equal(
                "// Copyright Me\n\n#pragma once\n\n#include \"CoreMinimal.h\"\n#include \"GameFramework/Actor.h\"\n#include \"MyActor.generated.h\"\n\n" +
                "UCLASS()\nclass MYGAME_API AMyActor : public AActor\n{\n\tGENERATED_BODY()\n\npublic:\n\tAMyActor();\n\nprotected:\n" +
                "\tvirtual void BeginPlay() override;\n\npublic:\n\tvirtual void Tick(float DeltaTime) override;\n};\n", h);
            string cpp = NewClassPlanner.Source(spec);
            Assert.Equal(
                "// Copyright Me\n\n#include \"Gameplay/MyActor.h\"\n\nAMyActor::AMyActor()\n{\n\tPrimaryActorTick.bCanEverTick = true;\n}\n\n" +
                "void AMyActor::BeginPlay()\n{\n\tSuper::BeginPlay();\n}\n\nvoid AMyActor::Tick(float DeltaTime)\n{\n\tSuper::Tick(DeltaTime);\n}\n", cpp);
        }

        [Fact]
        public void ActorWithoutTickHasOnlyTheConstructor()
        {
            var spec = new NewClassSpec { ClassName = "AMyActor", Kind = NewClassKind.Actor, ParentName = "AActor", WithBeginPlayAndTick = false, NewLine = "\n" };
            Assert.DoesNotContain("BeginPlay", NewClassPlanner.Header(spec));
            string cpp = NewClassPlanner.Source(spec);
            Assert.Contains("bCanEverTick = false;", cpp);
            Assert.Contains("#include \"MyActor.h\"", cpp);
            Assert.DoesNotContain("Tick(float", cpp);
        }

        [Fact]
        public void ComponentObjectStructAndPlainTemplates()
        {
            var comp = new NewClassSpec { ClassName = "UHealthComponent", Kind = NewClassKind.ActorComponent, ParentName = "UActorComponent", ApiMacro = "MYGAME_API", NewLine = "\n" };
            Assert.Contains("UCLASS(ClassGroup=(Custom), meta=(BlueprintSpawnableComponent))", NewClassPlanner.Header(comp));
            Assert.Contains("TickComponent(float DeltaTime, ELevelTick TickType, FActorComponentTickFunction* ThisTickFunction) override;", NewClassPlanner.Header(comp));
            Assert.Contains("PrimaryComponentTick.bCanEverTick = true;", NewClassPlanner.Source(comp));
            Assert.Contains("Super::TickComponent(DeltaTime, TickType, ThisTickFunction);", NewClassPlanner.Source(comp));

            var widget = new NewClassSpec { ClassName = "UMyWidget", Kind = NewClassKind.Object, ParentName = "UUserWidget", ParentInclude = "Blueprint/UserWidget.h", NewLine = "\n" };
            string wh = NewClassPlanner.Header(widget);
            Assert.Contains("UMyWidget(const FObjectInitializer& ObjectInitializer);", wh);
            Assert.Contains("class UMyWidget : public UUserWidget", wh); // no API macro given
            Assert.Contains("#include \"MyWidget.generated.h\"", wh);
            Assert.Contains("UMyWidget::UMyWidget(const FObjectInitializer& ObjectInitializer)\n\t: Super(ObjectInitializer)", NewClassPlanner.Source(widget));
            Assert.DoesNotContain("BeginPlay", wh);

            var row = new NewClassSpec { ClassName = "FMyRow", Kind = NewClassKind.Struct, ParentName = "FTableRowBase", ParentInclude = "Engine/DataTable.h", ApiMacro = "MYGAME_API", NewLine = "\n" };
            string rh = NewClassPlanner.Header(row);
            Assert.Contains("USTRUCT(BlueprintType)\nstruct MYGAME_API FMyRow : public FTableRowBase\n{\n\tGENERATED_BODY()", rh);
            Assert.Contains("#include \"MyRow.generated.h\"", rh);
            Assert.Contains("FMyRow::FMyRow()", NewClassPlanner.Source(row));

            var plain = new NewClassSpec { ClassName = "FHelper", Kind = NewClassKind.Plain, ApiMacro = "MYGAME_API", NewLine = "\n" };
            string ph = NewClassPlanner.Header(plain);
            Assert.DoesNotContain("generated.h", ph);
            Assert.DoesNotContain("UCLASS", ph);
            Assert.Contains("class MYGAME_API FHelper\n{", ph);
            Assert.Contains("\t~FHelper();", ph);
            Assert.Contains("FHelper::~FHelper()", NewClassPlanner.Source(plain));
        }

        [Fact]
        public void TemplatesUseCrLfByDefault()
        {
            var spec = new NewClassSpec { ClassName = "UThing", Kind = NewClassKind.Object, ParentName = "UObject" };
            Assert.Contains("\r\n", NewClassPlanner.Header(spec));
            Assert.DoesNotContain("\r\r", NewClassPlanner.Header(spec));
        }

        [Theory]
        [InlineData("[/Script/EngineSettings.GeneralProjectSettings]\nProjectID=1\nCopyrightNotice=Copyright Epic Fan. All Rights Reserved.\n", "Copyright Epic Fan. All Rights Reserved.")]
        [InlineData("[/Script/EngineSettings.GeneralProjectSettings]\r\nCopyrightNotice=\"(c) Me\"\r\n", "(c) Me")]
        [InlineData("CopyrightNotice=\n", null)]
        [InlineData("", null)]
        public void CopyrightFromDefaultGameIni(string ini, string expected) => Assert.Equal(expected, NewClassPlanner.CopyrightFromIni(ini));

        [Theory]
        [InlineData("", "")]
        [InlineData("  ", "")]
        [InlineData("A/B", "A|B")]
        [InlineData("\\A\\\\B\\", "A|B")]
        [InlineData("../x", null)]
        [InlineData("a:b", null)]
        [InlineData("x/./y", null)]
        public void SubdirectoryNormalization(string input, string expected) =>
            Assert.Equal(expected?.Replace('|', Path.DirectorySeparatorChar), NewClassPlanner.NormalizeSubdirectory(input));

        [Fact]
        public void ModulesAndTargetsOnDisk()
        {
            using var t = new TempTree();
            t.Write("Source/MyGame/MyGame.Build.cs", "");
            t.Dir("Source/MyGame/Public");
            t.Dir("Source/MyGame/Private");
            t.Write("Source/MyGame.Target.cs", "");
            t.Write("Source/Flat/Flat.Build.cs", "");
            t.Write("Source/Flat/Flat.cpp", "");
            t.Write("Plugins/Group/MyPlugin/MyPlugin.uplugin", "{}");
            t.Write("Plugins/Group/MyPlugin/Source/MyPluginRuntime/MyPluginRuntime.Build.cs", "");
            t.Dir("Plugins/Group/MyPlugin/Source/MyPluginRuntime/Private");
            t.Write("Plugins/Group/MyPlugin/Intermediate/Fake/Fake.Build.cs", "");
            t.Write("Source/MyGame/Intermediate/Nope/Nope.Build.cs", "");

            var modules = NewClassPlanner.ScanModules(t.Root);
            Assert.Equal(new[] { "Flat", "MyGame", "MyPluginRuntime" }, modules.Select(m => m.Name).ToArray());
            var game = modules[1];
            Assert.True(game.CanSplit);
            Assert.Null(game.Plugin);
            Assert.Equal("MYGAME_API", game.ApiMacro);
            var plugin = modules[2];
            Assert.Equal("MyPlugin", plugin.Plugin);
            Assert.False(plugin.CanSplit);
            Assert.Equal("MyPluginRuntime  (MyPlugin)", plugin.DisplayName);

            var pub = NewClassPlanner.Target(game, true, "Gameplay/Actors", "MyActor");
            Assert.Equal(t.P("Source/MyGame/Public/Gameplay/Actors/MyActor.h"), pub.HeaderPath);
            Assert.Equal(t.P("Source/MyGame/Private/Gameplay/Actors/MyActor.cpp"), pub.SourcePath);
            Assert.Equal("Gameplay/Actors/MyActor.h", pub.HeaderInclude);

            var priv = NewClassPlanner.Target(game, false, "", "MyActor");
            Assert.Equal(t.P("Source/MyGame/Private/MyActor.h"), priv.HeaderPath);
            Assert.Equal(t.P("Source/MyGame/Private/MyActor.cpp"), priv.SourcePath);
            Assert.Equal("MyActor.h", priv.HeaderInclude);

            var flat = NewClassPlanner.Target(modules[0], true, "Sub", "Thing");
            Assert.Equal(t.P("Source/Flat/Sub/Thing.h"), flat.HeaderPath);
            Assert.Equal(t.P("Source/Flat/Sub/Thing.cpp"), flat.SourcePath);
            Assert.Equal("Thing.h", flat.HeaderInclude);

            var pluginTarget = NewClassPlanner.Target(plugin, true, null, "Thing");
            Assert.Equal(t.P("Plugins/Group/MyPlugin/Source/MyPluginRuntime/Private/Thing.h"), pluginTarget.HeaderPath);

            var (m1, isPublic1, sub1) = NewClassPlanner.ForFolder(modules, t.P("Source/MyGame/Private/AI/Tasks"));
            Assert.Same(game, m1);
            Assert.False(isPublic1);
            Assert.Equal(Path.Combine("AI", "Tasks"), sub1);
            var (m2, isPublic2, sub2) = NewClassPlanner.ForFolder(modules, t.P("Source/MyGame/Public"));
            Assert.Same(game, m2);
            Assert.True(isPublic2);
            Assert.Equal("", sub2);
            var (m3, _, sub3) = NewClassPlanner.ForFolder(modules, t.P("Source/Flat/Widgets"));
            Assert.Same(modules[0], m3);
            Assert.Equal("Widgets", sub3);
            Assert.Null(NewClassPlanner.ForFolder(modules, t.P("Content")).Module);

            Assert.Equal(t.P("Source/MyGame/Private"), NewClassPlanner.FolderFromDescendant(t.P("Source/MyGame/Private/AI/X.cpp"), 1));
            Assert.Equal(t.P("Source/MyGame/Private/AI"), NewClassPlanner.FolderFromDescendant(t.P("Source/MyGame/Private/AI/X.cpp"), 0));
        }

        private sealed class TempTree : IDisposable
        {
            public TempTree()
            {
                Root = Path.Combine(Path.GetTempPath(), "uetovs-newclass-" + Guid.NewGuid().ToString("N"));
                Directory.CreateDirectory(Root);
            }

            public string Root { get; }

            public string P(string rel) => Path.Combine(Root, rel.Replace('/', Path.DirectorySeparatorChar));

            public void Write(string rel, string text)
            {
                Directory.CreateDirectory(Path.GetDirectoryName(P(rel)));
                File.WriteAllText(P(rel), text);
            }

            public void Dir(string rel) => Directory.CreateDirectory(P(rel));

            public void Dispose()
            {
                try { Directory.Delete(Root, true); } catch { }
            }
        }
    }
}
