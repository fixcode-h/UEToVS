using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Xml.Linq;
using UEToVS.Host.Logic.Commands;
using UEToVS.Host.Logic.Settings;
using UEToVS.UI.Logic;
using Xunit;

namespace UEToVS.Host.Logic.Tests
{
    public class HostSettingsSchemaTests
    {
        private static readonly List<SettingDescriptor> Schema = HostSettingsSchema.Build(16);

        [Fact]
        public void SchemaIsValid() => Assert.Empty(SettingsModel.Validate(Schema, HostSettingsSchema.Categories));

        [Fact]
        public void DescriptorDefaultsMatchHostSettingsDefaults()
        {
            var values = HostSettingsSchema.Codec.ToValues(new HostSettings());
            foreach (var d in Schema.Where(x => x.HasValue))
            {
                Assert.True(values.ContainsKey(d.Key), "codec misses " + d.Key);
                Assert.Equal(SettingsModel.Normalize(d, d.Default, out _), SettingsModel.Normalize(d, values[d.Key], out _));
            }
            // and every codec key is described (nothing persisted that the window can't show)
            foreach (var k in values.Keys) Assert.Contains(Schema, d => d.Key == k);
        }

        [Fact]
        public void CodecRoundTripsEveryValue()
        {
            var s = new HostSettings
            {
                LogLevel = UEToVS.Core.Diagnostics.LogLevel.Debug, EnginePathOverride = @"C:\e\UEToVS.Engine.exe", EngineRequestTimeoutSeconds = 12,
                IntelliSenseTakeover = true, IntelliSenseTakeoverMode = IntelliSenseTakeoverMode.UnrealSolutionOnly, DisableDatabase = false,
                DisableBackgroundCodeAnalysis = true, LogRetentionDays = 3, LogMaxTotalMB = 50, CppIndexThreads = 6, BlueprintIndexThreads = 2,
                IndexDuringBuild = IndexDuringBuild.Pause, IndexEngineSource = false, HighlightReferences = false, ShowNavigationBar = false,
                CtrlClickGoToDefinition = false, ShowToolbar = false, HiddenToolbarButtons = "back,performance", DoubleShiftSearchEverywhere = false,
                PerformanceSampleSeconds = 5,
            };
            var back = HostSettingsSchema.Codec.FromValues(HostSettingsSchema.Codec.ToValues(s), Schema, new HostSettings());
            Assert.Equal(HostSettingsSchema.Codec.ToValues(s), HostSettingsSchema.Codec.ToValues(back));
            Assert.Equal("back,performance", back.HiddenToolbarButtons);
        }

        [Fact]
        public void BadValuesFallBackAndAreReported()
        {
            var problems = new List<string>();
            var s = HostSettingsSchema.Codec.FromValues(new Dictionary<string, string>
            {
                ["indexing.cppThreads"] = "999", ["log.level"] = "Verbose", ["unknown.key"] = "x",
            }, Schema, new HostSettings(), problems);
            Assert.Equal(16, s.CppIndexThreads); // clamped to the core count
            Assert.Equal(UEToVS.Core.Diagnostics.LogLevel.Info, s.LogLevel);
            Assert.Equal(2, problems.Count);
        }

        [Theory]
        [InlineData(1, 1)]
        [InlineData(4, 1)]
        [InlineData(8, 2)]
        [InlineData(32, 8)]
        public void DefaultThreadsAreAQuarterOfTheCores(int cores, int expected) =>
            Assert.Equal(expected, HostSettingsSchema.DefaultIndexThreads(cores));

        [Fact]
        public void IndexingSettingsMapping()
        {
            var i = HostSettingsSchema.Codec.ToIndexing(new HostSettings { CppIndexThreads = 3, IndexDuringBuild = IndexDuringBuild.FullSpeed, IndexEngineSource = false });
            Assert.Equal(3, i.CppThreads);
            Assert.Equal(0, i.AssetThreads);
            Assert.Equal(2, i.DuringBuild);
            Assert.False(i.IncludeEngine);
        }

        [Fact]
        public void SettingsButtonCannotBeHidden()
        {
            var hidden = ToolbarButtons.ParseHidden("settings, back");
            Assert.True(ToolbarButtons.IsVisible("settings", true, hidden));
            Assert.False(ToolbarButtons.IsVisible("back", true, hidden));
            Assert.False(ToolbarButtons.IsVisible("forward", false, hidden));
        }
    }

    public class ShortcutTests
    {
        [Theory]
        [InlineData("alt+g", "Alt+G")]
        [InlineData("Shift+Alt+G", "Shift+Alt+G")]
        [InlineData("Alt+Shift+G", "Shift+Alt+G")]
        [InlineData("Alt+Left", "Alt+Left Arrow")]
        [InlineData("Ctrl+Alt+←", "Ctrl+Alt+Left Arrow")]
        [InlineData("Ctrl+K,Ctrl+O", "Ctrl+K, Ctrl+O")]
        [InlineData("Ctrl+,", "Ctrl+,")]
        [InlineData("Ctrl+-", "Ctrl+-")]
        [InlineData("Ctrl++", "Ctrl++")]
        [InlineData("f12", "F12")]
        public void Normalizes(string input, string expected) => Assert.Equal(expected, Shortcut.Normalize(input));

        [Theory]
        [InlineData("")]
        [InlineData("Ctrl+")]
        [InlineData("Ctrl+A+B")]
        [InlineData("A, B, C")]
        public void RejectsInvalid(string input) => Assert.Null(Shortcut.Normalize(input));

        [Fact]
        public void ChordPrefixOverlaps()
        {
            Assert.True(Shortcut.Overlaps("Ctrl+K", "Ctrl+K, Ctrl+O"));
            Assert.False(Shortcut.Overlaps("Ctrl+K, Ctrl+C", "Ctrl+K, Ctrl+O"));
            Assert.True(Shortcut.ScopesOverlap("Global", "Text Editor"));
            Assert.False(Shortcut.ScopesOverlap("Text Editor", "Solution Explorer"));
        }

        [Fact]
        public void BindingParse()
        {
            Assert.True(Shortcut.TryParseBinding("Text Editor::alt+o", out var scope, out var keys));
            Assert.Equal("Text Editor", scope);
            Assert.Equal("Alt+O", keys);
            Assert.False(Shortcut.TryParseBinding("Alt+O", out _, out _));
        }
    }

    public class KeymapPlannerTests
    {
        [Fact]
        public void FindsConflictsWithOtherCommandsAndBetweenOurs()
        {
            var ours = new Dictionary<string, string[]>
            {
                ["UEToVS.GoToDefinition"] = new[] { "Text Editor::Alt+G" },
                ["UEToVS.FileMembers"] = new[] { "Global::Alt+G" },
            };
            var all = new Dictionary<string, string[]>
            {
                ["Git.Menu"] = new[] { "Global::Alt+G, Alt+C" },
                ["Edit.Unrelated"] = new[] { "Text Editor::Ctrl+G" },
                ["UEToVS.GoToDefinition"] = new[] { "Text Editor::Alt+G" },
            };
            var c = KeymapPlanner.FindConflicts(ours, all);
            Assert.Contains(c, x => x.OtherCommand == "Git.Menu");
            Assert.Contains(c, x => x.Command == "UEToVS.GoToDefinition" && x.OtherCommand == "UEToVS.FileMembers");
            Assert.DoesNotContain(c, x => x.OtherCommand == "Edit.Unrelated");
        }

        [Fact]
        public void PresetsHaveNoInternalConflicts()
        {
            foreach (KeymapPresetId p in Enum.GetValues(typeof(KeymapPresetId)))
            {
                var ours = KeymapPlanner.PresetBindings(p);
                Assert.Empty(KeymapPlanner.FindConflicts(ours, Array.Empty<KeyValuePair<string, string[]>>()));
                foreach (var b in ours.Values.SelectMany(v => v)) Assert.True(Shortcut.TryParseBinding(b, out _, out _), p + ": " + b);
            }
        }

        [Fact]
        public void PlanOnlyListsChangedCommands()
        {
            var target = KeymapPlanner.PresetBindings(KeymapPresetId.VisualAssist);
            var current = target.ToDictionary(kv => kv.Key, kv => kv.Value.Select(b => b.ToLowerInvariant()).ToArray());
            Assert.Empty(KeymapPlanner.Plan(current, target));
            current["UEToVS.GoToDefinition"] = new[] { "Text Editor::F12" };
            var plan = KeymapPlanner.Plan(current, target);
            Assert.Single(plan);
            Assert.Equal("UEToVS.GoToDefinition", plan[0].Command);
        }

        [Fact]
        public void BackupRoundTrips()
        {
            var b = new KeymapPlanner.Backup { CreatedUtc = DateTime.UtcNow, Reason = "preset:Rider" };
            b.Bindings["UEToVS.GoToDefinition"] = new[] { "Text Editor::Alt+G" };
            var back = KeymapPlanner.ParseBackup(KeymapPlanner.SerializeBackup(b));
            Assert.Equal("Text Editor::Alt+G", back.Bindings["UEToVS.GoToDefinition"][0]);
            Assert.Null(KeymapPlanner.ParseBackup("{not json"));
        }
    }

    public class DoubleShiftDetectorTests
    {
        [Fact]
        public void TwoQuickTapsFire()
        {
            var d = new DoubleShiftDetector();
            Assert.False(d.OnKeyDown(true, false, 0));
            d.OnKeyUp(true, 80);
            Assert.True(d.OnKeyDown(true, false, 250));
        }

        [Fact]
        public void TypingWithShiftDoesNotFire()
        {
            var d = new DoubleShiftDetector();
            d.OnKeyDown(true, false, 0);
            d.OnKeyDown(false, false, 50); // Shift+A
            d.OnKeyUp(false, 90);
            d.OnKeyUp(true, 120);
            Assert.False(d.OnKeyDown(true, false, 200));
        }

        [Fact]
        public void SlowOrHeldShiftDoesNotFire()
        {
            var d = new DoubleShiftDetector();
            d.OnKeyDown(true, false, 0);
            d.OnKeyUp(true, 1000); // held
            Assert.False(d.OnKeyDown(true, false, 1100));
            d.OnKeyUp(true, 1150);
            Assert.False(d.OnKeyDown(true, false, 1800)); // too late
            Assert.False(d.OnKeyDown(true, true, 1810)); // auto-repeat
        }
    }

    /// <summary>The VSCT must agree with <see cref="CommandCatalog"/>: ids, canonical names and VA key bindings.</summary>
    public class VsctTests
    {
        private static readonly XNamespace Ns = "http://schemas.microsoft.com/VisualStudio/2005-10-18/CommandTable";

        private static XDocument Load() => XDocument.Load(Path.Combine(AppContext.BaseDirectory, "UEToVSHostPackage.vsct"));

        private static Dictionary<string, int> Symbols(XDocument doc) =>
            doc.Descendants(Ns + "GuidSymbol").Single(g => (string)g.Attribute("name") == "guidUEToVSHostCmdSet")
               .Elements(Ns + "IDSymbol").ToDictionary(e => (string)e.Attribute("name"), e => Convert.ToInt32((string)e.Attribute("value"), 16));

        [Fact]
        public void EveryCatalogCommandIsDeclaredWithItsCanonicalName()
        {
            var doc = Load();
            var sym = Symbols(doc);
            var buttons = doc.Descendants(Ns + "Button").ToDictionary(b => sym[(string)b.Attribute("id")],
                b => (string)b.Element(Ns + "Strings").Element(Ns + "CanonicalName"));
            foreach (var c in CommandCatalog.All)
            {
                Assert.True(buttons.ContainsKey(c.Id), c.Name + " missing in VSCT");
                Assert.Equal(c.Name, buttons[c.Id]);
            }
            foreach (var t in ToolbarButtons.All) Assert.True(buttons.ContainsKey(t.CommandId), t.Key);
        }

        [Fact]
        public void KeyBindingsMatchVisualAssistPreset()
        {
            var doc = Load();
            var sym = Symbols(doc);
            var declared = new Dictionary<int, string>();
            foreach (var kb in doc.Descendants(Ns + "KeyBinding"))
            {
                string key = (string)kb.Attribute("key1");
                key = key == "VK_LEFT" ? "Left Arrow" : key == "VK_RIGHT" ? "Right Arrow" : key;
                string mods = string.Join("+", ((string)kb.Attribute("mod1") ?? "").Split(' ', StringSplitOptions.RemoveEmptyEntries)
                                                                                 .Select(m => m == "Control" ? "Ctrl" : m));
                declared[sym[(string)kb.Attribute("id")]] = Shortcut.Normalize(mods.Length > 0 ? mods + "+" + key : key);
            }
            foreach (var c in CommandCatalog.All)
            {
                declared.TryGetValue(c.Id, out var keys);
                Assert.Equal(Shortcut.Normalize(c.VaKeys), keys);
            }
        }

        [Fact]
        public void ReservedIdRangesAreRespected()
        {
            foreach (var c in CommandCatalog.All.Where(x => x.Group == "navigation")) Assert.InRange(c.Id, 0x0200, 0x02FF);
            foreach (var t in ToolbarButtons.All) Assert.InRange(t.CommandId, 0x0400, 0x04FF);
            Assert.Equal(CommandCatalog.All.Count, CommandCatalog.All.Select(c => c.Id).Distinct().Count());
        }
    }
}
