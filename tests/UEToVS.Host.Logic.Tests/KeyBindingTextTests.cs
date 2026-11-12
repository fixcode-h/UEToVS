using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Xml.Linq;
using UEToVS.Host.Logic.Commands;
using Xunit;

namespace UEToVS.Host.Logic.Tests
{
    /// <summary>Reading / writing DTE bindings in localized VS (the "快捷键 all (无)" bug) and the first-run default bindings.</summary>
    public class BindingScopesTests
    {
        private static readonly Dictionary<string, string[]> ChineseVs = new Dictionary<string, string[]>
        {
            ["File.SaveAll"] = new[] { "全局::Ctrl+Shift+S" },
            ["Edit.LineDelete"] = new[] { "文本编辑器::Ctrl+Shift+L" },
        };

        private static IEnumerable<string> Lookup(Dictionary<string, string[]> vs, string cmd) => vs.TryGetValue(cmd, out var b) ? b : null;

        [Fact]
        public void DetectsLocalizedScopesFromWellKnownBindings()
        {
            var s = BindingScopes.Detect(c => Lookup(ChineseVs, c), "en-US");
            Assert.Equal("全局", s.LocalGlobal);
            Assert.Equal("文本编辑器", s.LocalTextEditor);
            Assert.Equal("probe", s.Source);
        }

        [Fact]
        public void DetectFallsBackToCultureThenEnglish()
        {
            var zh = BindingScopes.Detect(c => null, "zh-CN");
            Assert.Equal("全局", zh.LocalGlobal);
            Assert.Equal("文本编辑器", zh.LocalTextEditor);
            var tw = BindingScopes.Detect(c => throw new ArgumentException("missing"), "zh-TW");
            Assert.Equal("全域", tw.LocalGlobal);
            var en = BindingScopes.Detect(c => null, "en-US");
            Assert.Equal("Global", en.LocalGlobal);
            Assert.Equal("Text Editor", en.LocalTextEditor);
            // half found: the probe wins, the culture fills the other
            var half = BindingScopes.Detect(c => c == "File.SaveAll" ? new[] { "全局::Ctrl+Shift+S" } : null, "zh-CN");
            Assert.Equal("文本编辑器", half.LocalTextEditor);
        }

        [Theory]
        [InlineData("全局::Alt+G", "Global::Alt+G")]
        [InlineData("文本编辑器::alt+shift+g", "Text Editor::Shift+Alt+G")]
        [InlineData("Global::Alt+Left Arrow", "Global::Alt+Left Arrow")]
        [InlineData("全局::Alt+向左键", "Global::Alt+Left Arrow")]
        [InlineData("文本编辑器::Ctrl+K, Ctrl+I", "Text Editor::Ctrl+K, Ctrl+I")]
        [InlineData("Text-Editor::Strg+Umschalt+L", "Text Editor::Ctrl+Shift+L")]
        [InlineData("解决方案资源管理器::Ctrl+Alt+L", "解决方案资源管理器::Ctrl+Alt+L")]
        [InlineData("  全局 :: Ctrl+, ", "Global::Ctrl+,")]
        public void CanonicalizesRawDteBindings(string raw, string expected)
        {
            var s = new BindingScopes("全局", "文本编辑器");
            Assert.Equal(expected, s.Canonical(raw));
        }

        [Theory]
        [InlineData(null)]
        [InlineData("")]
        [InlineData("Alt+G")]
        [InlineData("::Alt+G")]
        [InlineData("全局::")]
        [InlineData("全局::Ctrl+A+B")]
        public void RejectsBadBindings(string raw) => Assert.Null(new BindingScopes("全局", "文本编辑器").Canonical(raw));

        [Fact]
        public void LocalizesForWriting()
        {
            var s = new BindingScopes("全局", "文本编辑器");
            Assert.Equal("全局::Alt+Left Arrow", s.Local("Global::alt+left"));
            Assert.Equal("文本编辑器::Alt+G", s.Local("Text Editor::Alt+G"));
            // already localized (a backup) / other scopes: kept
            Assert.Equal("全局::Alt+G", s.Local("全局::Alt+G"));
            Assert.Equal("类视图::Ctrl+Q", s.Local("类视图::Ctrl+Q"));
            // unparseable keys are written back untouched
            Assert.Equal("全局::Weird+Key+Combo", s.Local("全局::Weird+Key+Combo"));
            Assert.Equal("Global::Alt+G", BindingScopes.English.Local("全局::Alt+G"));
        }

        [Fact]
        public void CanonicalAllKeepsMultipleBindingsAndDropsDuplicates()
        {
            var s = new BindingScopes("全局", "文本编辑器");
            var all = s.CanonicalAll(new[] { "全局::Alt+G", "Global::alt+g", "文本编辑器::F12", "garbage", null });
            Assert.Equal(new[] { "Global::Alt+G", "Text Editor::F12" }, all);
        }

        [Fact]
        public void FromDteHandlesArraysStringsAndNull()
        {
            Assert.Empty(BindingScopes.FromDte(null));
            Assert.Equal(new[] { "全局::Alt+G" }, BindingScopes.FromDte("全局::Alt+G"));
            Assert.Equal(new[] { "a::B", "c::D" }, BindingScopes.FromDte(new object[] { "a::B", null, "", "c::D" }));
            Assert.Empty(BindingScopes.FromDte(42));
        }
    }

    public class DefaultKeymapPlannerTests
    {
        private static Dictionary<string, string[]> AllUnbound() =>
            CommandCatalog.All.ToDictionary(c => c.Name, c => Array.Empty<string>(), StringComparer.OrdinalIgnoreCase);

        [Fact]
        public void AppliesEveryDefaultWhenNothingIsBound()
        {
            var d = DefaultKeymapPlanner.Plan(CommandCatalog.All, AllUnbound(), Array.Empty<KeyValuePair<string, string[]>>(), null);
            Assert.All(d, x => Assert.Equal(DefaultBindingOutcome.Apply, x.Outcome));
            Assert.Equal(CommandCatalog.All.Count(c => c.VaKeys != null), d.Count);
            Assert.Contains(d, x => x.Command == "UEToVS.GoToDefinition" && x.Binding == "Text Editor::Alt+G");
            Assert.Contains(d, x => x.Command == "UEToVS.NavigateBack" && x.Binding == "Global::Alt+Left Arrow");
            Assert.Contains(d, x => x.Command == "UEToVS.FindAction" && x.Binding == "Global::Ctrl+Shift+Alt+A");
        }

        [Fact]
        public void KeepsExistingBindingsAndRespectsTakenKeys()
        {
            var ours = AllUnbound();
            ours["UEToVS.GoToDefinition"] = new[] { "Text Editor::F12" };       // user choice
            ours["UEToVS.GoToSymbol"] = new[] { "Global::Alt+M" };              // user moved another UEToVS command onto Alt+M
            ours.Remove("UEToVS.LiveCoding");                                    // not registered yet
            var others = new Dictionary<string, string[]>
            {
                ["Edit.Custom"] = new[] { "Global::Alt+O" },                     // takes ToggleHeaderSource's Alt+O
                ["Edit.Chord"] = new[] { "Text Editor::Shift+Alt+F, Ctrl+X" },   // chord prefix collides with Shift+Alt+F
                ["SolutionExplorer.Only"] = new[] { "Solution Explorer::Alt+Left Arrow" }, // our Global binding would shadow it there
                ["View.Unrelated"] = new[] { "Global::Ctrl+Alt+G" },
            };
            var d = DefaultKeymapPlanner.Plan(CommandCatalog.All, ours, others, null).ToDictionary(x => x.Command);
            Assert.Equal(DefaultBindingOutcome.AlreadyBound, d["UEToVS.GoToDefinition"].Outcome);
            Assert.Empty(d["UEToVS.GoToDefinition"].Overrides);                   // the user's own key: nothing to fight for
            Assert.Equal(DefaultBindingOutcome.CommandMissing, d["UEToVS.LiveCoding"].Outcome);
            // UEToVS wins over VS commands: applied, the VS binding is removed
            Assert.Equal(DefaultBindingOutcome.Apply, d["UEToVS.ToggleHeaderSource"].Outcome);
            Assert.Equal(new[] { new KeyValuePair<string, string>("Edit.Custom", "Global::Alt+O") }, d["UEToVS.ToggleHeaderSource"].Overrides);
            Assert.Equal(DefaultBindingOutcome.Apply, d["UEToVS.FindReferences"].Outcome);
            Assert.Equal("Edit.Chord", Assert.Single(d["UEToVS.FindReferences"].Overrides).Key);
            Assert.Equal(DefaultBindingOutcome.Apply, d["UEToVS.NavigateBack"].Outcome); // Global overlaps every scope
            Assert.Equal("SolutionExplorer.Only", Assert.Single(d["UEToVS.NavigateBack"].Overrides).Key);
            // but never over a key the user gave another UEToVS command
            Assert.Equal(DefaultBindingOutcome.KeyTaken, d["UEToVS.ListMembersInFile"].Outcome);
            Assert.Equal("UEToVS.GoToSymbol", d["UEToVS.ListMembersInFile"].TakenBy);
            Assert.Equal(DefaultBindingOutcome.Apply, d["UEToVS.GoToImplementation"].Outcome);
            Assert.Empty(d["UEToVS.GoToImplementation"].Overrides);
            Assert.Equal(DefaultBindingOutcome.Apply, d["UEToVS.NavigateForward"].Outcome);
            // decisions with overrides are remembered only after they were applied
            Assert.DoesNotContain(DefaultKeymapPlanner.ToRemember(d.Values), k => k.StartsWith("UEToVS.ToggleHeaderSource=", StringComparison.Ordinal));
        }

        [Fact]
        public void DefaultAlreadyBoundButShadowedByVsIsReclaimed()
        {
            var ours = AllUnbound();
            ours["UEToVS.GoToFile"] = new[] { "Global::Shift+Alt+O" };          // VSCT default reached VS…
            var others = new Dictionary<string, string[]> { ["Edit.OpenFile"] = new[] { "Text Editor::Shift+Alt+O", "Global::Ctrl+O" } }; // …but VS shadows it in the editor
            var d = DefaultKeymapPlanner.Plan(CommandCatalog.All, ours, others, null).Single(x => x.Command == "UEToVS.GoToFile");
            Assert.Equal(DefaultBindingOutcome.AlreadyBound, d.Outcome);
            Assert.True(d.NeedsConflictScan);
            Assert.Equal(new[] { new KeyValuePair<string, string>("Edit.OpenFile", "Text Editor::Shift+Alt+O") }, d.Overrides);
        }

        [Fact]
        public void RemovalsListOnlyNonUEToVSCommands()
        {
            var mine = new Dictionary<string, string[]> { ["UEToVS.SearchEverywhere"] = new[] { "Global::Ctrl+T" } };
            var all = new Dictionary<string, string[]>
            {
                ["Edit.GoToAll"] = new[] { "Global::Ctrl+T", "Global::Ctrl+," },
                ["UEToVS.GoToType"] = new[] { "Global::Ctrl+T" },
                ["Edit.Unrelated"] = new[] { "Global::Ctrl+Shift+T" },
            };
            var r = DefaultKeymapPlanner.Removals(mine, all);
            Assert.Equal(new[] { "Edit.GoToAll" }, r.Keys.ToArray());
            Assert.Equal(new[] { "Global::Ctrl+T" }, r["Edit.GoToAll"].ToArray());
        }

        [Fact]
        public void HandledDefaultsAreNotRevisitedAndOthersNullSkipsConflictCheck()
        {
            var handled = DefaultKeymapPlanner.ParseHandled("UEToVS.GoToDefinition=Text Editor::Alt+G; junk ;");
            var d = DefaultKeymapPlanner.Plan(CommandCatalog.All, AllUnbound(), null, handled).ToDictionary(x => x.Command);
            Assert.Equal(DefaultBindingOutcome.AlreadyHandled, d["UEToVS.GoToDefinition"].Outcome);
            Assert.Equal(DefaultBindingOutcome.Apply, d["UEToVS.GoToImplementation"].Outcome);
        }

        [Fact]
        public void RememberedKeysRoundTrip()
        {
            var ours = AllUnbound();
            ours["UEToVS.GoToDefinition"] = new[] { "Text Editor::Alt+G" };
            var d = DefaultKeymapPlanner.Plan(CommandCatalog.All, ours, Array.Empty<KeyValuePair<string, string[]>>(), null);
            var remember = DefaultKeymapPlanner.ToRemember(d).ToList();
            Assert.Equal(new[] { "UEToVS.GoToDefinition=Text Editor::Alt+G" }, remember); // Apply is remembered only after it succeeded
            var text = DefaultKeymapPlanner.FormatHandled(remember.Concat(new[] { "UEToVS.FindAction=Global::Ctrl+Shift+Alt+A", "" }));
            var back = DefaultKeymapPlanner.ParseHandled(text);
            Assert.Equal(2, back.Count);
            Assert.Contains("UEToVS.FindAction=Global::Ctrl+Shift+Alt+A", back);
        }
    }

    /// <summary>The VSCT KeyBindings must be something VS 2022 / 2026 accepts.</summary>
    public class VsctKeyBindingValidityTests
    {
        private static readonly XNamespace Ns = "http://schemas.microsoft.com/VisualStudio/2005-10-18/CommandTable";

        [Fact]
        public void KeyBindingsUseValidEditorsModifiersAndKeys()
        {
            var doc = XDocument.Load(Path.Combine(AppContext.BaseDirectory, "UEToVSHostPackage.vsct"));
            var buttons = new HashSet<string>(doc.Descendants(Ns + "Button").Select(b => (string)b.Attribute("id")));
            var symbols = doc.Descendants(Ns + "GuidSymbol").ToDictionary(g => (string)g.Attribute("name"), g => (string)g.Attribute("value"));
            Assert.Equal("{8B382828-6202-11d1-8870-0000F87579D2}", symbols["guidTextEditorFactory"], ignoreCase: true);
            var validMods = new HashSet<string> { "Alt", "Control", "Shift", "Windows" };
            var seen = new HashSet<string>();
            var bindings = doc.Descendants(Ns + "KeyBinding").ToList();
            Assert.NotEmpty(bindings);
            foreach (var kb in bindings)
            {
                string id = (string)kb.Attribute("id");
                Assert.Equal("guidUEToVSHostCmdSet", (string)kb.Attribute("guid"));
                Assert.Contains(id, buttons);
                string editor = (string)kb.Attribute("editor");
                Assert.True(editor == "guidVSStd97" || editor == "guidTextEditorFactory", id + ": editor " + editor);
                string key = (string)kb.Attribute("key1");
                Assert.True(key.Length == 1 && char.IsLetterOrDigit(key[0]) && !char.IsLower(key[0]) || key.StartsWith("VK_", StringComparison.Ordinal), id + ": key1 " + key);
                var mods = ((string)kb.Attribute("mod1") ?? "").Split(' ', StringSplitOptions.RemoveEmptyEntries);
                Assert.NotEmpty(mods);
                Assert.All(mods, m => Assert.Contains(m, validMods));
                Assert.Equal(mods.Length, mods.Distinct().Count());
                string key2 = (string)kb.Attribute("key2");
                if (key2 != null)
                {
                    Assert.True(key2.Length == 1 && char.IsLetterOrDigit(key2[0]) && !char.IsLower(key2[0]) || key2.StartsWith("VK_", StringComparison.Ordinal), id + ": key2 " + key2);
                    Assert.All(((string)kb.Attribute("mod2") ?? "").Split(' ', StringSplitOptions.RemoveEmptyEntries), m => Assert.Contains(m, validMods));
                }
                Assert.True(seen.Add(editor + "|" + key + "|" + string.Join(" ", mods.OrderBy(m => m)) + "|" + key2 + "|" + (string)kb.Attribute("mod2")), id + ": duplicate key binding");
            }
        }
    }
}
