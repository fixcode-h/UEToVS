using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using UEToVS.Host.Logic.Fonts;
using UEToVS.Host.Logic.Settings;
using UEToVS.UI.Logic;
using Xunit;

namespace UEToVS.Host.Logic.Tests
{
    public class FontSettingsTests
    {
        private static readonly List<SettingDescriptor> Schema = HostSettingsSchema.Build(16);

        [Fact]
        public void CategoriesUseTheShellInteropGuidsAndUniqueIds()
        {
            // Microsoft.VisualStudio.Shell.Interop.FontsAndColorsCategory (Shell.Interop.8.0).
            Assert.Equal(new Guid("A27B4E24-A735-4d1d-B8E7-9716E1E3D8E0"), VsFontCategories.TextEditor.Guid);
            Assert.Equal(new Guid("9973EFDF-317D-431c-8BC1-5E88CBFD4F7F"), VsFontCategories.OutputWindow.Guid);
            Assert.Equal(new Guid("1F987C00-E7C4-4869-8A17-23FD602268B0"), VsFontCategories.Environment.Guid);
            Assert.Equal(VsFontCategories.All.Count, VsFontCategories.All.Select(c => c.Guid).Distinct().Count());
            Assert.Equal(VsFontCategories.All.Count, VsFontCategories.All.Select(c => c.Id).Distinct(StringComparer.OrdinalIgnoreCase).Count());
        }

        [Fact]
        public void DefaultTargetsUseTheBundledFonts()
        {
            var targets = VsFontPlanner.Targets(new HostSettings()).ToDictionary(t => t.Category.Id);
            Assert.Equal("Maple Mono NL", targets["textEditor"].Face);
            Assert.Equal(10, targets["textEditor"].Size);
            Assert.Same(BundledFontCatalog.MapleMonoNl, targets["textEditor"].Bundled);
            Assert.Equal("Maple Mono NL", targets["output"].Face);
            Assert.Null(targets["output"].Size); // keep VS's size
            Assert.Equal("Maple Mono NL", targets["findResults"].Face);
            Assert.Equal("IBM Plex Sans", targets["environment"].Face);
            Assert.Equal("IBM Plex Sans", targets["tooltip"].Face);
            Assert.False(targets.ContainsKey("printer")); // 不修改 by default
        }

        [Fact]
        public void FollowVsAndKeepProduceNoTarget()
        {
            var s = new HostSettings { CodeFont = "vs", VsFonts = VsFontSettingsText.With("", "environment", FontChoice.Keep, null) };
            var ids = VsFontPlanner.Targets(s).Select(t => t.Category.Id).ToList();
            Assert.DoesNotContain("textEditor", ids); // code font follows VS → VS keeps its own editor font
            Assert.DoesNotContain("environment", ids);
            Assert.Contains("tooltip", ids);
        }

        [Fact]
        public void SystemFontsAreWrittenWithoutInstall()
        {
            var s = new HostSettings { VsFonts = VsFontSettingsText.With("", "textEditor", FontChoice.OfSystem("Consolas"), 12) };
            var t = VsFontPlanner.Targets(s).Single(x => x.Category.Id == "textEditor");
            Assert.Equal("Consolas", t.Face);
            Assert.Equal(12, t.Size);
            Assert.Null(t.Bundled);
        }

        [Fact]
        public void VsFontTextRoundTripsAndOnlyStoresChanges()
        {
            Assert.Equal("", VsFontSettingsText.Format(VsFontSettingsText.Parse("")));
            string text = VsFontSettingsText.With("", "printer", FontChoice.OfSystem("Consolas"), 9);
            text = VsFontSettingsText.With(text, "textEditor", null, 14);
            Assert.Equal("textEditor=code@14;printer=Consolas@9", text);
            var map = VsFontSettingsText.Parse(text + ";bogus=x@1;environment=keep@abc;tooltip=ui@99");
            Assert.Equal(14, map["textEditor"].Size);
            Assert.Equal(FontChoiceKind.Keep, map["environment"].Family.Kind);
            Assert.Equal(0, map["environment"].Size); // bad size → category default
            Assert.Equal(VsFontCategories.MaxSize, map["tooltip"].Size);
            Assert.Equal(0, VsFontSettingsText.NormalizeSize(-3));
            Assert.Equal(VsFontCategories.MinSize, VsFontSettingsText.NormalizeSize(2));
        }

        [Fact]
        public void PlanBacksUpOnceWritesTargetsAndRestoresDroppedCategories()
        {
            var targets = VsFontPlanner.Targets(new HostSettings());
            var first = VsFontPlanner.Plan(true, targets, null);
            Assert.Equal(targets.Count, first.Backup.Count);
            Assert.Equal(targets.Count, first.Write.Count);
            Assert.Equal(new[] { "Maple Mono NL", "IBM Plex Sans" }, first.Install.Select(f => f.Family).ToArray());
            Assert.Empty(first.Restore);

            var backup = new VsFontBackup();
            foreach (var c in first.Backup) backup.AddIfMissing(new VsFontBackupEntry { Category = c.Id, Face = "Consolas", PointSize = 10 });
            backup.AddIfMissing(new VsFontBackupEntry { Category = "printer", Face = "Courier New", PointSize = 10 });
            Assert.False(backup.AddIfMissing(new VsFontBackupEntry { Category = "textEditor", Face = "Other" })); // first original wins
            Assert.Equal("Consolas", backup.Find("TEXTEDITOR").Face);

            var again = VsFontPlanner.Plan(true, targets, backup);
            Assert.Empty(again.Backup);
            Assert.Equal(targets.Count, again.Write.Count);
            Assert.Equal("printer", Assert.Single(again.Restore).Category);

            var off = VsFontPlanner.Plan(false, targets, backup);
            Assert.Empty(off.Write);
            Assert.Empty(off.Install);
            Assert.Equal(backup.Entries.Count, off.Restore.Count);
            Assert.True(VsFontPlanner.Plan(false, targets, null).IsEmpty);
        }

        [Fact]
        public void IsAppliedComparesFaceAndOptionalSize()
        {
            var t = new VsFontTarget { Category = VsFontCategories.TextEditor, Face = "Maple Mono NL", Size = 10 };
            Assert.True(VsFontPlanner.IsApplied(t, "maple mono nl", 10));
            Assert.False(VsFontPlanner.IsApplied(t, "Maple Mono NL", 11));
            t.Size = null;
            Assert.True(VsFontPlanner.IsApplied(t, "Maple Mono NL", 13));
            Assert.False(VsFontPlanner.IsApplied(t, "Consolas", 13));
        }

        [Fact]
        public void BackupFileRoundTripsDeletesWhenEmptyAndRefusesCorruptFiles()
        {
            string dir = Path.Combine(Path.GetTempPath(), "uetovs-fonts-" + Guid.NewGuid().ToString("N"));
            string path = Path.Combine(dir, VsFontBackup.FileName);
            try
            {
                Assert.Null(VsFontBackup.Load(path));
                var b = new VsFontBackup { VsVersion = "18.0" };
                b.AddIfMissing(new VsFontBackupEntry { Category = "textEditor", CategoryGuid = VsFontCategories.TextEditor.Guid, Face = "Cascadia Mono", PointSize = 10, CharSet = 1, WasDefault = true });
                VsFontBackup.SaveOrDelete(path, b);
                VsFontBackup.SaveOrDelete(path, b); // replace path
                var loaded = VsFontBackup.Load(path);
                var e = Assert.Single(loaded.Entries);
                Assert.Equal("Cascadia Mono", e.Face);
                Assert.True(e.WasDefault);
                Assert.Equal(VsFontCategories.TextEditor.Guid, e.CategoryGuid);

                Assert.True(loaded.Remove("textEditor"));
                VsFontBackup.SaveOrDelete(path, loaded);
                Assert.False(File.Exists(path));

                File.WriteAllText(path, "{ not json");
                Assert.Throws<InvalidDataException>(() => VsFontBackup.Load(path));
            }
            finally
            {
                if (Directory.Exists(dir)) Directory.Delete(dir, true);
            }
        }

        [Theory]
        [InlineData("MapleMonoNL-Regular.ttf", "Maple Mono NL (TrueType)")]
        [InlineData("MapleMonoNL-Bold.ttf", "Maple Mono NL Bold (TrueType)")]
        [InlineData("IBMPlexSans-SemiBold.ttf", "IBM Plex Sans Semi Bold (TrueType)")]
        public void RegistryNamesFollowWindowsConvention(string file, string expected)
        {
            var font = file.StartsWith("Maple", StringComparison.Ordinal) ? BundledFontCatalog.MapleMonoNl : BundledFontCatalog.IbmPlexSans;
            Assert.Equal(expected, FontInstallNames.RegistryValueName(font, file));
        }

        [Fact]
        public void EveryCatalogFileIsInTheRepository()
        {
            string dir = AppContext.BaseDirectory;
            while (dir != null && !File.Exists(Path.Combine(dir, "UEToVS.sln")) && !Directory.Exists(Path.Combine(dir, "src", "UEToVS.Host"))) dir = Path.GetDirectoryName(dir);
            Assert.NotNull(dir);
            string fonts = Path.Combine(dir, "src", "UEToVS.Host", "Resources", "Fonts");
            foreach (var b in BundledFontCatalog.All)
            {
                Assert.True(File.Exists(Path.Combine(fonts, b.Folder, "OFL.txt")), b.Folder + "/OFL.txt");
                foreach (var f in b.Files) Assert.True(File.Exists(Path.Combine(fonts, b.Folder, f)), b.Folder + "/" + f);
            }
        }

        [Fact]
        public void AppearancePageHasTheFontRowsAndTheSchemaIsValid()
        {
            Assert.Empty(SettingsModel.Validate(Schema, HostSettingsSchema.Categories));
            var rows = Schema.Where(d => d.Category == HostSettingsSchema.Appearance).ToList();
            Assert.Equal(new[] { "英文字体", "中文字体", "代码字体" }, rows.Take(3).Select(d => d.TitleZh).ToArray());
            Assert.Equal("字母、数字和符号用的字体", rows[0].DescriptionZh);
            Assert.Equal("汉字用的字体", rows[1].DescriptionZh);
            Assert.Equal("代码、命令和路径用的等宽字体", rows[2].DescriptionZh);
            Assert.Equal("IBM Plex Sans（内置）", rows[0].Options.Single(o => o.Value == rows[0].Default).TitleZh);
            var manage = rows.Single(d => d.Key == FontSettingsSchema.ManageVsKey);
            Assert.Equal("true", manage.Default);
            Assert.Equal(1 + 1 + 3 + 2 * VsFontCategories.All.Count, rows.Count);
            Assert.All(rows.Where(d => d.Kind == SettingKind.Choice), d => Assert.True(d.AllowCustomValue));
            Assert.All(rows.Where(d => d.Kind == SettingKind.Int), d => Assert.False(string.IsNullOrEmpty(d.AutoLabelZh)));
        }

        [Fact]
        public void VsFontRowsAreGroupedWithSizeOnTheFamilyRow()
        {
            var rows = Schema.Where(d => d.Key.StartsWith(FontSettingsSchema.VsPrefix, StringComparison.Ordinal) && d.Key != FontSettingsSchema.ManageVsKey && d.Key != FontSettingsSchema.VsStatusKey).ToList();
            Assert.All(rows, d => Assert.Equal(FontSettingsSchema.ManageVsKey, d.EnabledWhenKey));
            var own = SettingsModel.MergeInlineRows(rows, out var inline);
            Assert.Equal(VsFontCategories.All.Count, own.Count);
            Assert.All(own, d => Assert.Equal(SettingKind.Choice, d.Kind));
            Assert.All(own, d => Assert.Equal(SettingKind.Int, Assert.Single(inline[d.Key]).Kind));
            Assert.Equal("字号", inline["fonts.vs.textEditor.family"][0].InlineLabelZh);

            // One card per kind of window: code windows first (contiguous), then interface windows.
            var groups = own.Select(d => d.GroupZh).Distinct().ToArray();
            Assert.Equal(new[] { "VS 字体 · 代码类窗口", "VS 字体 · 界面类窗口" }, groups);
            Assert.Equal("文本编辑器", own[0].TitleZh);
            Assert.Equal(new[] { "环境（对话框和工具窗口）", "语句完成", "编辑器工具提示" },
                own.Where(d => d.GroupZh == groups[1]).Select(d => d.TitleZh).ToArray());
        }

        [Fact]
        public void CodecRoundTripsFontValues()
        {
            var values = HostSettingsSchema.Codec.ToValues(new HostSettings());
            Assert.Equal("bundled:IBM Plex Sans", values[FontSettingsSchema.UiLatinKey]);
            Assert.Equal("code", values["fonts.vs.textEditor.family"]);
            Assert.Equal("10", values["fonts.vs.textEditor.size"]);

            values[FontSettingsSchema.UiCjkKey] = "Microsoft YaHei UI";
            values[FontSettingsSchema.ManageVsKey] = "false";
            values["fonts.vs.environment.family"] = "keep";
            values["fonts.vs.printer.size"] = "11";
            var problems = new List<string>();
            var s = HostSettingsSchema.Codec.FromValues(values, Schema, new HostSettings(), problems);
            Assert.Empty(problems);
            Assert.Equal("Microsoft YaHei UI", s.UiCjkFont);
            Assert.False(s.ManageVsFonts);
            Assert.Equal("environment=keep@0;printer=keep@11", s.VsFonts);
            Assert.Equal(values.OrderBy(k => k.Key), HostSettingsSchema.Codec.ToValues(s).OrderBy(k => k.Key));
            Assert.True(FontSettingsSchema.Changed(new HostSettings(), s));
            Assert.False(FontSettingsSchema.Changed(new HostSettings(), new HostSettings()));
        }

        [Fact]
        public void InstalledFontsFeedTheDropdowns()
        {
            var schema = HostSettingsSchema.Build(4);
            int calls = 0;
            FontSettingsSchema.AttachInstalledFonts(schema, () => { calls++; return new[] { "Segoe UI", "Consolas" }; });
            // 英文 / 中文 / 代码三行是照 Agent 面板的固定列表，不列本机字体；手输的字体名仍显示
            var latin = schema.Single(d => d.Key == FontSettingsSchema.UiLatinKey).ResolveOptions("Gone Font");
            Assert.Equal(new[] { "bundled:Maple Mono NL", "Segoe UI", "bundled:Inter", "bundled:IBM Plex Sans", "Gone Font" }, latin.Select(o => o.Value).ToArray());
            Assert.Equal("系统字体", latin[1].TitleZh);
            var cjk = schema.Single(d => d.Key == FontSettingsSchema.UiCjkKey);
            Assert.Equal(new[] { "bundled:Noto Sans SC", "bundled:Maple Mono CN", "Microsoft YaHei UI" }, cjk.ResolveOptions().Select(o => o.Value).ToArray());
            Assert.Equal("思源黑体 Noto Sans SC（内置）", cjk.ResolveOptions()[0].TitleZh);
            Assert.Equal("系统字体（微软雅黑）", cjk.ResolveOptions()[2].TitleZh);
            Assert.False(string.IsNullOrEmpty(cjk.HintZh));
            var code = schema.Single(d => d.Key == FontSettingsSchema.CodeKey).ResolveOptions();
            Assert.Equal(new[] { "Maple Mono NL（内置）", "JetBrains Mono（内置）" }, code.Select(o => o.TitleZh).ToArray());
            var editor = schema.Single(d => d.Key == "fonts.vs.textEditor.family").ResolveOptions("code");
            Assert.Equal(new[] { "keep", "code", "ui", "bundled:Maple Mono NL" }, editor.Take(4).Select(o => o.Value).ToArray());
            Assert.Equal("不修改（使用 VS 当前设置）", editor[0].TitleZh);
            Assert.Equal(1, calls);
            Assert.Null(schema.Single(d => d.Key == "nav.ctrlClick").OptionsSource);
        }
    }
}
