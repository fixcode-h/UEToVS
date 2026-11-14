using System;
using System.IO;
using System.Linq;
using UEToVS.Host.Logic.Horde;
using UEToVS.Host.Logic.Settings;
using Xunit;

namespace UEToVS.Host.Logic.Tests
{
    public class HordeTests : IDisposable
    {
        private readonly string _dir = Path.Combine(Path.GetTempPath(), "uetovs-horde-" + Guid.NewGuid().ToString("N"));

        public HordeTests() => Directory.CreateDirectory(_dir);

        public void Dispose()
        {
            try { Directory.Delete(_dir, true); } catch (Exception) { }
        }

        private const string Existing =
            "<?xml version=\"1.0\" encoding=\"utf-8\" ?>\r\n" +
            "<Configuration xmlns=\"https://www.unrealengine.com/BuildConfiguration\">\r\n" +
            "    <!-- my own settings -->\r\n" +
            "    <BuildConfiguration>\r\n" +
            "        <bUseUnityBuild>false</bUseUnityBuild>\r\n" +
            "        <MaxParallelActions>24</MaxParallelActions>\r\n" +
            "    </BuildConfiguration>\r\n" +
            "</Configuration>\r\n";

        private static HostSettings Enabled(Action<HostSettings> edit = null)
        {
            var s = new HostSettings { HordeEnabled = true, HordeServer = "http://horde:13340", HordePool = "Win-UE5" };
            edit?.Invoke(s);
            return s;
        }

        [Fact]
        public void LocalCoresDefaultToAQuarterOfTheLogicalCores()
        {
            Assert.Equal(8, HordeSettingsSchema.DefaultLocalCores(32));
            Assert.Equal(1, HordeSettingsSchema.DefaultLocalCores(2));
            Assert.Equal(8, HordeSettingsSchema.EffectiveLocalCores(0, 32));
            Assert.Equal(12, HordeSettingsSchema.EffectiveLocalCores(12, 32));
            Assert.Equal(32, HordeSettingsSchema.EffectiveLocalCores(64, 32));
        }

        [Fact]
        public void DefaultsBlockOthersAndLeaveTheFeatureOff()
        {
            var s = new HostSettings();
            Assert.False(s.HordeEnabled);
            Assert.Equal(HordeAgentPolicy.Block, s.HordeAgentPolicy);
            Assert.Equal(@"C:\Horde", s.HordeInstallPath);
            Assert.Equal(HordeConfigTarget.Engine, s.HordeTarget);
        }

        [Fact]
        public void TargetFilesAreTheOnesUbtReads()
        {
            string engine = Path.Combine(_dir, "UE"), project = Path.Combine(_dir, "Game"), appData = Path.Combine(_dir, "AppData");
            string e = HordeLocations.TargetFile(HordeConfigTarget.Engine, engine, project, appData);
            Assert.Equal(Path.Combine(engine, "Engine", "Saved", "UnrealBuildTool", "BuildConfiguration.xml"), e);
            Assert.Equal(Path.Combine(appData, "Unreal Engine", "UnrealBuildTool", "BuildConfiguration.xml"), HordeLocations.TargetFile(HordeConfigTarget.User, engine, project, appData));
            Assert.Equal(Path.Combine(project, "Saved", "UnrealBuildTool", "BuildConfiguration.xml"), HordeLocations.TargetFile(HordeConfigTarget.Project, engine, project, appData));
            Assert.Null(HordeLocations.TargetFile(HordeConfigTarget.Engine, null, project, appData));
            Assert.True(HordeLocations.IsEngineSavedFile(@"G:\P\Engine\Engine\Saved\UnrealBuildTool\BuildConfiguration.xml"));
            Assert.False(HordeLocations.IsEngineSavedFile(@"G:\P\Project\Saved\UnrealBuildTool\BuildConfiguration.xml"));
            Assert.True(HordeLocations.CanWrite(e));
        }

        [Fact]
        public void OldTargetValuesStillParse()
        {
            var s = new HostSettings();
            HordeSettingsSchema.TrySet(s, HordeSettingsSchema.TargetKey, "Project");
            Assert.Equal(HordeConfigTarget.Project, s.HordeTarget);
            HordeSettingsSchema.TrySet(s, HordeSettingsSchema.TargetKey, "bogus");
            Assert.Equal(HordeConfigTarget.Engine, s.HordeTarget);
        }

        [Fact]
        public void PlanWritesOnlyWhatIsSetAndEveryManagedKeyIsListed()
        {
            var plan = HordeConfigPlan.Build(Enabled(), 32);
            Assert.Equal(HordeConfigPlan.ManagedKeys.Count, plan.Entries.Count);
            string V(string id) => plan.Entries.Single(e => e.Id == id).Value;
            Assert.Equal("true", V("BuildConfiguration/bAllowUBAExecutor"));
            Assert.Equal("8", V("BuildConfiguration/MaxParallelActions"));
            Assert.Equal("http://horde:13340", V("Horde/Server"));
            Assert.Equal("Win-UE5", V("Horde/Pool"));
            Assert.Null(V("Horde/MaxCores"));
            Assert.Equal("true", V("UnrealBuildAccelerator/bPrintSummary"));
            Assert.Null(V("UnrealBuildAccelerator/bLinkRemote"));

            var off = HordeConfigPlan.Build(new HostSettings(), 32);
            Assert.All(off.Entries, e => Assert.Null(e.Value));
            Assert.Equal(0, off.LocalCores);
        }

        [Fact]
        public void KeysTheEngineDoesNotKnowAreSkipped()
        {
            var keys = new UbtConfigKeys();
            keys.AddSource(@"
namespace UnrealBuildTool {
  class BuildConfiguration {
    [XmlConfigFile]
    public bool bAllowUBAExecutor = false;
    [XmlConfigFile]
    [CommandLine(""-MaxParallelActions"")]
    public int MaxParallelActions = 0;
  }
  class UnrealBuildAcceleratorHordeConfig {
    [XmlConfigFile(Category = ""Horde"")]
    public string? Server { get; set; }
    [XmlConfigFile(Category = ""Horde"", Name = ""Pool"")]
    public string? WindowsPool { get; set; }
  }
}");
            Assert.Equal(4, keys.Count);
            Assert.True(keys.Supports("BuildConfiguration", "MaxParallelActions"));
            Assert.True(keys.Supports("Horde", "Pool"));
            Assert.False(keys.Supports("Horde", "MaxCores"));
            Assert.True(keys.Supports("UnrealBuildAccelerator", "bLinkRemote")); // section never seen: allowed

            var plan = HordeConfigPlan.Build(Enabled(s => s.HordeMaxCores = 200), 16, keys);
            Assert.Contains("Horde/MaxCores", plan.Skipped);
            Assert.Null(plan.Entries.Single(e => e.Id == "Horde/MaxCores").Value);
        }

        [Fact]
        public void WriterKeepsOtherContentAndRestoresEarlierValues()
        {
            string path = Path.Combine(_dir, "BuildConfiguration.xml");
            File.WriteAllText(path, Existing);
            var manifest = new HordeBackupManifest();

            var r = BuildConfigurationWriter.Apply(path, HordeConfigPlan.Build(Enabled(), 32), manifest);
            Assert.True(r.FileChanged);
            string text = File.ReadAllText(path);
            Assert.Contains("<!-- my own settings -->", text);
            Assert.Contains("<bUseUnityBuild>false</bUseUnityBuild>", text);
            Assert.Contains("        <MaxParallelActions>8</MaxParallelActions>\r\n", text);
            Assert.Contains("    <Horde>\r\n        <Server>http://horde:13340</Server>\r\n        <Pool>Win-UE5</Pool>\r\n    </Horde>", text);
            Assert.StartsWith("<?xml version=\"1.0\" encoding=\"utf-8\" ?>\r\n<Configuration", text);
            var xml = BuildConfigurationXml.Load(path);
            Assert.Equal("true", xml.Get("BuildConfiguration", "bAllowUBAExecutor"));
            Assert.Equal("24", manifest.Files[path]["BuildConfiguration/MaxParallelActions"]);
            Assert.Equal(HordeBackupManifest.AbsentMarker, manifest.Files[path]["Horde/Server"]);

            // Changing a value again keeps the very first "before" value.
            BuildConfigurationWriter.Apply(path, HordeConfigPlan.Build(Enabled(s => s.HordeLocalCores = 4), 32), manifest);
            Assert.Equal("4", BuildConfigurationXml.Load(path).Get("BuildConfiguration", "MaxParallelActions"));
            Assert.Equal("24", manifest.Files[path]["BuildConfiguration/MaxParallelActions"]);

            // Turning the feature off puts everything back; the file is the original again.
            var off = BuildConfigurationWriter.Apply(path, HordeConfigPlan.Build(new HostSettings(), 32), manifest);
            Assert.Contains("BuildConfiguration/MaxParallelActions", off.Restored);
            Assert.Equal(Existing, File.ReadAllText(path));
            Assert.True(manifest.IsEmpty);
        }

        [Fact]
        public void WriterCreatesAMissingFileAndRestoreAllRemovesItsKeys()
        {
            string path = Path.Combine(_dir, "Saved", "UnrealBuildTool", "BuildConfiguration.xml");
            var manifest = new HordeBackupManifest();
            Assert.False(BuildConfigurationWriter.Apply(path, HordeConfigPlan.Build(new HostSettings(), 8), manifest).FileChanged);
            Assert.False(File.Exists(path));

            BuildConfigurationWriter.Apply(path, HordeConfigPlan.Build(Enabled(), 8), manifest);
            var xml = BuildConfigurationXml.Load(path);
            Assert.Equal("2", xml.Get("BuildConfiguration", "MaxParallelActions"));
            Assert.Equal("Win-UE5", xml.Get("Horde", "Pool"));

            string manifestPath = Path.Combine(_dir, "manifest.json");
            manifest.Save(manifestPath);
            var loaded = HordeBackupManifest.Load(manifestPath);
            var results = BuildConfigurationWriter.RestoreAll(loaded);
            Assert.Single(results);
            var after = BuildConfigurationXml.Load(path);
            Assert.Null(after.Get("BuildConfiguration", "MaxParallelActions"));
            Assert.Null(after.Get("Horde", "Server"));
            Assert.DoesNotContain("<Horde>", File.ReadAllText(path));
        }

        [Fact]
        public void EmptySectionsGetTheFileIndentation()
        {
            var xml = BuildConfigurationXml.Parse("<Configuration xmlns=\"https://www.unrealengine.com/BuildConfiguration\">\n\t<BuildConfiguration>\n\t</BuildConfiguration>\n</Configuration>\n");
            xml.Set("BuildConfiguration", "bAllowUBAExecutor", "true");
            Assert.Equal("<Configuration xmlns=\"https://www.unrealengine.com/BuildConfiguration\">\n\t<BuildConfiguration>\n\t\t<bAllowUBAExecutor>true</bAllowUBAExecutor>\n\t</BuildConfiguration>\n</Configuration>\n", xml.ToString());
        }

        [Fact]
        public void EffectiveValuesComeFromTheLastFileThatSetsThem()
        {
            string engine = Path.Combine(_dir, "Engine"), project = Path.Combine(_dir, "Project"), appData = Path.Combine(_dir, "AppData");
            var files = HordeLocations.BuildConfigurationFiles(engine, project, appData, null);
            Assert.Equal(new[] { HordeLocations.Level.Engine, HordeLocations.Level.UserAppData, HordeLocations.Level.Project }, files.Select(f => f.FileLevel));
            void Write(string path, string server)
            {
                var x = BuildConfigurationXml.Load(path);
                x.Set("Horde", "Server", server);
                x.Save(path);
            }
            Write(files[0].Path, "http://engine");
            Write(files[1].Path, "http://user");
            var eff = EffectiveBuildConfiguration.Read(files, HordeConfigPlan.ManagedKeys);
            Assert.Equal("http://user", eff["Horde/Server"].Value);
            Assert.Equal(files[1].Path, eff["Horde/Server"].SourceFile);
        }

        [Fact]
        public void AgentJsonComputePortIsChangedAndRestored()
        {
            string json = "{\n  \"Horde\": {\n    \"Server\": \"http://horde:13340\",\n    \"ComputePort\": 7000\n  }\n}";
            Assert.Equal("7000", AgentJsonConfig.ReadComputePort(json));
            string blocked = AgentJsonConfig.WithComputePort(json, "0");
            Assert.Equal("0", AgentJsonConfig.ReadComputePort(blocked));
            Assert.Contains("http://horde:13340", blocked);
            Assert.Null(AgentJsonConfig.ReadComputePort(AgentJsonConfig.WithComputePort(blocked, null)));

            Assert.Equal("0", AgentJsonConfig.ReadComputePort(AgentJsonConfig.WithComputePort(null, "0")));
            Assert.Null(AgentJsonConfig.ReadComputePort("{}"));
        }

        [Fact]
        public void AgentPolicyBlocksOnceAndAllowPutsTheEarlierPortBack()
        {
            var manifest = new HordeBackupManifest();
            Assert.False(AgentPolicyPlanner.Plan(HordeAgentPolicy.Block, null, false, manifest).Needed); // no agent on this machine

            var block = AgentPolicyPlanner.Plan(HordeAgentPolicy.Block, "7000", true, manifest);
            Assert.True(block.Needed);
            Assert.Equal("0", block.NewPort);
            AgentPolicyPlanner.Commit(block, "7000", manifest);
            Assert.Equal("7000", manifest.AgentComputePort);
            Assert.False(AgentPolicyPlanner.Plan(HordeAgentPolicy.Block, "0", true, manifest).Needed);

            var allow = AgentPolicyPlanner.Plan(HordeAgentPolicy.Allow, "0", true, manifest);
            Assert.True(allow.Needed);
            Assert.Equal("7000", allow.NewPort);
            AgentPolicyPlanner.Commit(allow, "0", manifest);
            Assert.Null(manifest.AgentComputePort);

            // Blocked by hand before UEToVS: "allow" has no earlier value and leaves it alone.
            Assert.False(AgentPolicyPlanner.Plan(HordeAgentPolicy.Allow, "0", true, manifest).Needed);
            Assert.False(AgentPolicyPlanner.Plan(HordeAgentPolicy.Keep, "7000", true, manifest).Needed);

            // Not set before: allow removes the key again.
            var b2 = AgentPolicyPlanner.Plan(HordeAgentPolicy.Block, null, true, manifest);
            AgentPolicyPlanner.Commit(b2, null, manifest);
            Assert.Equal(HordeBackupManifest.AbsentMarker, manifest.AgentComputePort);
            var a2 = AgentPolicyPlanner.Plan(HordeAgentPolicy.Allow, "0", true, manifest);
            Assert.True(a2.Needed);
            Assert.Null(a2.NewPort);
        }

        [Fact]
        public void EngineVersionIsReadFromBuildVersion()
        {
            Assert.Equal(new Version(5, 4, 2), EngineVersionReader.Parse("{ \"MajorVersion\": 5, \"MinorVersion\": 4, \"PatchVersion\": 2, \"Changelist\": 0 }"));
            Assert.Null(EngineVersionReader.Parse("{}"));
            Assert.True(new Version(5, 3, 2) < HordeConfigPlan.MinimumEngine);
        }

        [Fact]
        public void SettingsChangesThatTouchTheFilesAreDetected()
        {
            var a = new HostSettings();
            var b = a.Clone();
            b.HordeAgentPolicy = HordeAgentPolicy.Allow;
            Assert.False(HordeSettingsSchema.BuildConfigChanged(a, b));
            b.HordeLocalCores = 6;
            Assert.True(HordeSettingsSchema.BuildConfigChanged(a, b));
        }

        [Fact]
        public void StatusTextNamesTheStateInBothLanguages()
        {
            var snap = new HordeStatusSnapshot { EngineVersion = new Version(5, 5), Enabled = true, LocalCores = 4, LogicalCores = 16, AgentJsonExists = true, AgentComputePort = "0" };
            snap.AgentServices.Add(("HordeAgent", "Running"));
            string text = HordeText.Format(snap);
            Assert.Contains("5.5", text);
            Assert.Contains("computePort=0", text);
        }
    
        [Fact]
        public void LocalCoresStillApplyWhenUbaIsTurnedOnElsewhere()
        {
            var off = new HostSettings { HordeEnabled = false, HordeLocalCores = 6, HordeServer = "http://horde:13340" };
            var plan = HordeConfigPlan.Build(off, 32, null, ubaElsewhere: true);
            Assert.Equal(6, plan.LocalCores);
            Assert.Equal(new[] { "BuildConfiguration/MaxParallelActions=6" }, plan.Entries.Where(e => e.Value != null).Select(e => e.ToString()));

            var auto = HordeConfigPlan.Build(new HostSettings { HordeEnabled = false }, 32, null, ubaElsewhere: true);
            Assert.Equal(8, auto.LocalCores);

            var none = HordeConfigPlan.Build(off, 32, null, ubaElsewhere: false);
            Assert.Equal(0, none.LocalCores);
            Assert.All(none.Entries, e => Assert.Null(e.Value));
        }

        [Fact]
        public void UbaTurnedOnByAnotherFileIsFound()
        {
            string engineFile = Path.Combine(_dir, "engine.xml");
            string userFile = Path.Combine(_dir, "user.xml");
            string docsFile = Path.Combine(_dir, "docs.xml");
            const string head = "<?xml version='1.0' encoding='utf-8'?>\n<Configuration xmlns=\"https://www.unrealengine.com/BuildConfiguration\">\n";
            File.WriteAllText(engineFile, head + "</Configuration>\n");
            File.WriteAllText(userFile, head + "  <BuildConfiguration>\n    <bAllowUBAExecutor>true</bAllowUBAExecutor>\n  </BuildConfiguration>\n  <!-- hand-made -->\n</Configuration>\n");
            var files = new[]
            {
                new HordeLocations.ConfigFile(HordeLocations.Level.Engine, engineFile),
                new HordeLocations.ConfigFile(HordeLocations.Level.UserAppData, userFile),
            };

            var found = EffectiveBuildConfiguration.UbaTurnedOnElsewhere(files, engineFile);
            Assert.NotNull(found);
            Assert.Equal(userFile, found.SourceFile);

            // The file UEToVS writes does not count.
            Assert.Null(EffectiveBuildConfiguration.UbaTurnedOnElsewhere(files, userFile));

            // A later file that turns it off wins.
            File.WriteAllText(docsFile, head + "  <BuildConfiguration>\n    <bAllowUBAExecutor>false</bAllowUBAExecutor>\n  </BuildConfiguration>\n</Configuration>\n");
            var withDocs = files.Append(new HordeLocations.ConfigFile(HordeLocations.Level.Documents, docsFile));
            Assert.Null(EffectiveBuildConfiguration.UbaTurnedOnElsewhere(withDocs, engineFile));
        }

        [Fact]
        public void StatusTextNamesUbaTurnedOnElsewhere()
        {
            var snap = new HordeStatusSnapshot
            {
                Enabled = false, LocalCores = 8, LogicalCores = 32,
                UbaElsewhere = new EffectiveValue { Value = "true", SourceFile = @"C:\Users\me\AppData\Roaming\Unreal Engine\UnrealBuildTool\BuildConfiguration.xml" },
                MaxParallelActions = new EffectiveValue { Value = "8", SourceFile = @"G:\UE\Engine\Saved\UnrealBuildTool\BuildConfiguration.xml" },
            };
            string text = HordeText.Format(snap);
            // The text follows the UI language (Chinese "8 / 32", English "8 of 32"); CI runs in English.
            Assert.True(text.Contains("8 / 32") || text.Contains("8 of 32"), text);
            Assert.Contains("Roaming", text);
            Assert.Contains("MaxParallelActions", text.Replace("UBT 本机并发", "MaxParallelActions").Replace("UBT local parallel actions", "MaxParallelActions"));
        }
    }
}
