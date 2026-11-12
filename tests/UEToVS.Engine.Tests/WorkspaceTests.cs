using System;
using System.IO;
using System.Linq;
using UEToVS.Contracts;
using UEToVS.Engine.Unreal;
using Xunit;

namespace UEToVS.Engine.Tests
{
    public sealed class WorkspaceTests : IDisposable
    {
        private readonly FakeUnrealTree _t = new FakeUnrealTree();

        public void Dispose() => _t.Dispose();

        private static EngineLocator NoSystemLookups() => new EngineLocator { UseRegistry = false, LauncherInstalledPath = null };

        private Workspace Discover(Action<WorkspaceOptions> configure = null)
        {
            var o = new WorkspaceOptions { SolutionDir = _t.ProjectRoot };
            configure?.Invoke(o);
            return WorkspaceDiscovery.Discover(o, NoSystemLookups());
        }

        [Fact]
        public void FindsProjectEngineVersionAndBuildId()
        {
            var ws = Discover();
            Assert.Equal(_t.ProjectFile, ws.ProjectFile);
            Assert.Equal("MyGame", ws.ProjectName);
            Assert.Equal(_t.EngineRoot, ws.EngineRoot);
            Assert.Equal("association-path", ws.EngineRootMethod);
            Assert.Equal("5.4.2", ws.EngineVersion.ToString());
            Assert.Equal(123, ws.EngineVersion.Changelist);
            Assert.StartsWith("5.4.2-cl123-", ws.EngineBuildId);
            Assert.Matches("^[A-Za-z0-9._-]+$", ws.EngineBuildId);
            Assert.Equal(ws.EngineBuildId, Discover().EngineBuildId); // stable
            Assert.Empty(ws.Warnings);
        }

        [Fact]
        public void FindsProjectAboveSolutionDir()
        {
            string sub = Path.Combine(_t.ProjectRoot, "Source", "MyGame");
            var ws = WorkspaceDiscovery.Locate(new WorkspaceOptions { SolutionDir = sub }, NoSystemLookups());
            Assert.Equal(_t.ProjectFile, ws.ProjectFile);
        }

        [Fact]
        public void BinaryBuildIdIsUsedWhenPresent()
        {
            _t.Write("UE/Engine/Binaries/Linux/UnrealEditor.version", "{\"BuildId\":\"abc-123 x\"}");
            var ws = Discover();
            Assert.StartsWith("5.4.2-abc-123_x-", ws.EngineBuildId);
        }

        [Fact]
        public void EngineOverrideAcceptsEngineSubfolder()
        {
            var ws = Discover(o => o.EngineRootOverride = Path.Combine(_t.EngineRoot, "Engine"));
            Assert.Equal(_t.EngineRoot, ws.EngineRoot);
            Assert.Equal("override", ws.EngineRootMethod);
        }

        [Fact]
        public void LauncherInstallIsFoundByVersionAssociation()
        {
            _t.Write("Game/MyGame.uproject", "{\"EngineAssociation\":\"5.4\"}");
            string dat = _t.Write("LauncherInstalled.dat", "{\"InstallationList\":[{\"InstallLocation\":\"" + _t.EngineRoot.Replace("\\", "\\\\") + "\",\"AppName\":\"UE_5.4\"}]}");
            var locator = new EngineLocator { UseRegistry = false, LauncherInstalledPath = dat };
            var ws = WorkspaceDiscovery.Locate(new WorkspaceOptions { SolutionDir = _t.ProjectRoot }, locator);
            Assert.Equal(_t.EngineRoot, ws.EngineRoot);
            Assert.Equal("launcher", ws.EngineRootMethod);
        }

        [Fact]
        public void MissingEngineIsAWarningNotAFailure()
        {
            _t.Write("Game/MyGame.uproject", "{\"EngineAssociation\":\"{00000000-0000-0000-0000-000000000000}\"}");
            var ws = Discover();
            Assert.Null(ws.EngineRoot);
            Assert.Contains(ws.Warnings, w => w.Contains("Engine root not found"));
            Assert.Contains(ws.SourceRoots, r => r.Origin == ItemOrigin.Project);
            Assert.DoesNotContain(ws.SourceRoots, r => r.Origin == ItemOrigin.Engine);
        }

        [Fact]
        public void ResolvesPluginEnabledStates()
        {
            var ws = Discover();
            PluginInfo P(string n) => ws.Plugins.Single(p => p.Name == n);
            Assert.True(P("Niagara").Enabled);
            Assert.Equal("uproject", P("Niagara").EnabledReason);
            Assert.False(P("Disabled").Enabled);
            Assert.True(P("Referenced").Enabled);
            Assert.True(P("MyTools").Enabled);
            Assert.Equal(ItemOrigin.ProjectPlugin, P("MyTools").Origin);
            Assert.True(P("DepOfProjPlugin").Enabled);
            Assert.Equal("dependency:MyTools", P("DepOfProjPlugin").EnabledReason);

            Assert.Contains(ws.SourceRoots, r => r.Plugin == "Niagara");
            Assert.DoesNotContain(ws.SourceRoots, r => r.Plugin == "Disabled");
            Assert.Contains(ws.ContentRoots, r => r.ContentMount == "/Game" && r.Origin == ItemOrigin.Project);
            Assert.Contains(ws.ContentRoots, r => r.ContentMount == "/MyTools" && r.Origin == ItemOrigin.ProjectPlugin);
            Assert.Contains(ws.ContentRoots, r => r.ContentMount == "/Engine" && r.Origin == ItemOrigin.Engine);
            Assert.Contains(ws.ContentRoots, r => r.ContentMount == "/Niagara" && r.Origin == ItemOrigin.EnginePlugin);
        }

        [Fact]
        public void UprojectCanDisableADefaultPlugin()
        {
            _t.Write("Game/MyGame.uproject", "{\"EngineAssociation\":\"../UE\",\"Plugins\":[{\"Name\":\"Niagara\",\"Enabled\":false}]}");
            var ws = Discover();
            var n = ws.Plugins.Single(p => p.Name == "Niagara");
            Assert.False(n.Enabled);
            Assert.Equal("uproject-disabled", n.EnabledReason);
        }

        [Fact]
        public void ProjectOnlyOptionSkipsEngine()
        {
            var ws = Discover(o => o.IncludeEngine = false);
            Assert.All(ws.SourceRoots, r => Assert.True(r.IsProject));
            Assert.All(ws.ContentRoots, r => Assert.True(r.IsProject));
        }

        [Fact]
        public void ListsSourceFilesWithFiltersModulesAndOrigins()
        {
            var ws = Discover();
            var listing = FileLister.List(ws, ws.AllListingRoots(), ws.DescriptorFiles);
            var byName = listing.Files.ToLookup(f => f.Name);

            // included kinds
            foreach (var n in new[] { "MyCharacter.h", "MyCharacter.cpp", "MyGame.Build.cs", "MyGame.Target.cs", "DefaultEngine.ini", "MyGame.uproject",
                                      "UnrealString.h", "Common.ush", "BaseEngine.ini", "zlib.h", "inflate.c", "Niagara.uplugin", "MyTools.uplugin" })
                Assert.True(byName.Contains(n), n + " should be listed");

            // excluded: other extensions, Intermediate/Binaries/Saved/.git, disabled plugins
            foreach (var n in new[] { "notes.txt", "MyCharacter.generated.h", "x.h", "y.ini", "z.h", "DisabledThing.h", "Disabled.uplugin" })
                Assert.False(byName.Contains(n), n + " should not be listed");

            var ch = byName["MyCharacter.h"].Single();
            Assert.Equal("MyGame", ch.Module);
            Assert.Equal(ItemOrigin.Project, ch.Origin);
            Assert.Equal(SourceFileKind.Header, ch.Kind);
            Assert.Equal(Path.Combine("Source", "MyGame", "Public", "MyCharacter.h"), ch.RelativePath);

            Assert.Equal(ItemOrigin.ThirdParty, byName["zlib.h"].Single().Origin);
            Assert.Equal("zlib", byName["zlib.h"].Single().Module);
            Assert.Equal(ItemOrigin.EnginePlugin, byName["NiagaraSystem.h"].Single().Origin);
            Assert.Equal("Niagara", byName["NiagaraSystem.h"].Single().Plugin);
            Assert.Equal(ItemOrigin.Engine, byName["UnrealString.h"].Single().Origin);
            Assert.Equal("Core", byName["UnrealString.h"].Single().Module);
            Assert.Equal(ItemOrigin.ProjectPlugin, byName["lib.h"].Single().Origin); // project-local ThirdParty stays the user's
            Assert.Null(byName["MyGame.Target.cs"].Single().Module);

            Assert.Contains(listing.BuildCsFiles, b => b.Module == "MyToolsEditor" && b.Origin == ItemOrigin.ProjectPlugin && b.Plugin == "MyTools");
        }

        [Fact]
        public void BuildsModuleCatalog()
        {
            var ws = Discover();
            var listing = FileLister.List(ws, ws.AllListingRoots(), ws.DescriptorFiles);
            var catalog = ModuleCatalog.Build(ws, listing.BuildCsFiles);

            var game = catalog.Get("MyGame");
            Assert.Equal("Runtime", game.Type);
            Assert.Contains("Core", game.PublicDependencies);
            Assert.Contains("Niagara", game.PublicDependencies);
            Assert.Contains("Slate", game.PrivateDependencies);
            Assert.Contains(Path.Combine(_t.ProjectRoot, "Source", "MyGame", "Public"), game.PublicIncludeDirs);
            Assert.Contains(Path.Combine(_t.ProjectRoot, "Source", "MyGame", "Private"), game.PrivateIncludeDirs);

            Assert.Equal("Editor", catalog.Get("NiagaraEditor").Type);   // from the .uplugin
            Assert.Equal("Editor", catalog.Get("MyToolsEditor").Type);
            Assert.Equal("Runtime", catalog.Get("Core").Type);          // inferred from Engine/Source/Runtime
            Assert.Equal("External", catalog.Get("zlib").Type);
            Assert.Equal(ItemOrigin.EnginePlugin, catalog.Get("Niagara").Origin);

            Assert.Equal("MyGame", catalog.FindForFile(Path.Combine(_t.ProjectRoot, "Source", "MyGame", "Private", "New", "Thing.cpp")).Name);
            Assert.Null(catalog.FindForFile(Path.Combine(_t.ProjectRoot, "Config", "DefaultGame.ini")));
        }

        [Fact]
        public void ListsAssetsWithObjectPaths()
        {
            var ws = Discover();
            var assets = AssetLister.List(ws.ContentRoots);
            var map = assets.Single(a => a.Name == "L_Main");
            Assert.Equal("/Game/Maps/L_Main.L_Main", map.ObjectPath);
            Assert.Equal("World", map.ClassName);
            Assert.Equal("/Game/Characters/BP_Hero.BP_Hero", assets.Single(a => a.Name == "BP_Hero").ObjectPath);
            Assert.Equal("/MyTools/Icons/T_Tool.T_Tool", assets.Single(a => a.Name == "T_Tool").ObjectPath);
            Assert.Equal(ItemOrigin.ProjectPlugin, assets.Single(a => a.Name == "T_Tool").Origin);
            Assert.Equal("/Engine/EngineMaterials/DefaultMaterial.DefaultMaterial", assets.Single(a => a.Name == "DefaultMaterial").ObjectPath);
            Assert.Equal("/Niagara/DefaultSystem.DefaultSystem", assets.Single(a => a.Name == "DefaultSystem").ObjectPath);
            Assert.DoesNotContain(assets, a => a.Name == "KCUWB"); // __ExternalActors__
        }

        [Fact]
        public void ClassifiesOrigins()
        {
            var c = new OriginClassifier(_t.ProjectRoot, _t.EngineRoot);
            Assert.Equal(ItemOrigin.Project, c.Classify(_t.P("Game/Source/MyGame/A.h")));
            Assert.Equal(ItemOrigin.ProjectPlugin, c.Classify(_t.P("Game/Plugins/Tools/MyTools/Source/A.h")));
            Assert.Equal(ItemOrigin.Engine, c.Classify(_t.P("UE/Engine/Source/Runtime/Core/Public/A.h")));
            Assert.Equal(ItemOrigin.EnginePlugin, c.Classify(_t.P("UE/Engine/Plugins/FX/Niagara/Source/A.h")));
            Assert.Equal(ItemOrigin.ThirdParty, c.Classify(_t.P("UE/Engine/Source/ThirdParty/zlib/zlib.h")));
            Assert.Equal(ItemOrigin.ThirdParty, c.Classify(_t.P("UE/Engine/Plugins/FX/Niagara/Source/ThirdParty/x/y.h")));
            Assert.Equal(ItemOrigin.Engine, c.Classify(_t.P("UE/Engine/Source/Runtime/ThirdPartyHelpers.h"))); // file name, not a folder
            Assert.Null(c.Classify(_t.P("Elsewhere/a.h")));

            // A project inside a source engine tree is still the project.
            var nested = new OriginClassifier(_t.P("UE/MyNested"), _t.EngineRoot);
            Assert.Equal(ItemOrigin.Project, nested.Classify(_t.P("UE/MyNested/Source/A.h")));
        }

        [Fact]
        public void SkippedDirectoryCheckIsRelativeToTheRoot()
        {
            string root = Path.Combine(Path.GetTempPath(), ".hidden", "Game");
            Assert.False(FileLister.IsInSkippedDirectory(root, Path.Combine(root, "Source", "A.h")));
            Assert.True(FileLister.IsInSkippedDirectory(root, Path.Combine(root, "Intermediate", "A.h")));
            Assert.True(FileLister.IsInSkippedDirectory(root, Path.Combine(root, ".vs", "UEToVS", "Index", "x.bin")));
        }

        [Fact]
        public void DescriptorParserIsTolerant()
        {
            var d = UnrealDescriptor.Parse("x", "{ // comment\n \"Modules\": [ {\"Name\":\"A\",\"Type\":\"Editor\"}, ], \"EnabledByDefault\": false, }");
            Assert.Null(d.Error);
            Assert.Equal("Editor", Assert.Single(d.Modules).Type);
            Assert.False(d.EnabledByDefault);
            Assert.NotNull(UnrealDescriptor.Parse("x", "{ not json").Error);
        }
    }
}
