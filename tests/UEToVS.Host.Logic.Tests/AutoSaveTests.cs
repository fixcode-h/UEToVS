using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using UEToVS.Host.Logic.Editing;
using UEToVS.Host.Logic.Settings;
using Xunit;

namespace UEToVS.Host.Logic.Tests
{
    public sealed class AutoSaveTests
    {
        private static readonly DateTime T0 = new DateTime(2026, 11, 15, 8, 0, 0, DateTimeKind.Utc);

        [Fact]
        public void IdleDue_AfterTheIdleTime_OncePerEdit()
        {
            Assert.False(AutoSavePolicy.IdleDue(T0, DateTime.MinValue, T0.AddSeconds(14), 15));
            Assert.True(AutoSavePolicy.IdleDue(T0, DateTime.MinValue, T0.AddSeconds(15), 15));
            // saved after that edit: nothing to do until the next edit
            Assert.False(AutoSavePolicy.IdleDue(T0, T0.AddSeconds(15), T0.AddSeconds(60), 15));
            // 0 = only on deactivation
            Assert.False(AutoSavePolicy.IdleDue(T0, DateTime.MinValue, T0.AddHours(1), 0));
        }

        [Fact]
        public void ShouldSave_OnlyModifiedWritableFilesOnDisk()
        {
            string path = Path.GetFullPath("Foo.cpp");
            Assert.True(AutoSavePolicy.ShouldSave(path, true, true, false));
            Assert.False(AutoSavePolicy.ShouldSave(path, false, true, false));
            Assert.False(AutoSavePolicy.ShouldSave(path, true, false, false)); // untitled / deleted: no Save As
            Assert.False(AutoSavePolicy.ShouldSave(path, true, true, true));   // read-only: no prompt
            Assert.False(AutoSavePolicy.ShouldSave("Source1.cpp", true, true, false));
        }

        [Fact]
        public void Settings_DefaultOn_RoundTrip()
        {
            var schema = HostSettingsSchema.Build(8);
            var d = schema.Single(x => x.Key == "editor.autoSave");
            Assert.Equal("true", d.Default);
            Assert.Equal("15", schema.Single(x => x.Key == "editor.autoSaveIdleSeconds").Default);
            Assert.Equal("true", schema.Single(x => x.Key == "editor.gitTabMarker").Default);
            var defaults = new HostSettings();
            Assert.True(defaults.AutoSaveEnabled);
            Assert.True(defaults.GitTabMarker);

            var s = HostSettingsSchema.Codec.FromValues(new Dictionary<string, string>
            {
                ["editor.autoSave"] = "false", ["editor.autoSaveIdleSeconds"] = "9999", ["editor.gitTabMarker"] = "false",
            }, schema, new HostSettings());
            Assert.False(s.AutoSaveEnabled);
            Assert.Equal(EditorFeatureSettings.MaxAutoSaveIdle, s.AutoSaveIdleSeconds);
            Assert.False(s.GitTabMarker);
            var v = HostSettingsSchema.Codec.ToValues(s);
            Assert.Equal("false", v["editor.autoSave"]);
            Assert.Equal("600", v["editor.autoSaveIdleSeconds"]);
        }

        [Fact]
        public void GitStatus_ChangedPaths()
        {
            string root = Path.GetFullPath("repo");
            string output = " M Source/A.cpp\0A  Source/B.h\0?? Source/New.cpp\0 D Source/Gone.cpp\0R  Source/Renamed.h\0Source/Old.h\0MM Source/C.cpp\0";
            var set = GitStatusParser.Parse(output, root);
            string P(string rel) => Path.GetFullPath(Path.Combine(root, rel.Replace('/', Path.DirectorySeparatorChar)));
            Assert.Contains(P("Source/A.cpp"), set);
            Assert.Contains(P("Source/B.h"), set);
            Assert.Contains(P("Source/New.cpp"), set);
            Assert.Contains(P("Source/Renamed.h"), set);
            Assert.Contains(P("Source/C.cpp"), set);
            Assert.DoesNotContain(P("Source/Gone.cpp"), set);
            Assert.DoesNotContain(P("Source/Old.h"), set);
            Assert.Empty(GitStatusParser.Parse("", root));
        }
    }
}
