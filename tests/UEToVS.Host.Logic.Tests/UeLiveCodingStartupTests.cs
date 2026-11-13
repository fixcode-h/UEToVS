using UEToVS.Host.Logic.Link;
using UEToVS.Host.Logic.Settings;
using Xunit;

namespace UEToVS.Host.Logic.Tests
{
    public class UeLiveCodingStartupTests
    {
        private const string Path = @"C:\P\Saved\Config\WindowsEditor\EditorPerProjectUserSettings.ini";

        [Fact]
        public void IniPathIsTheUe5PerProjectUserSettings()
        {
            Assert.Equal(System.IO.Path.Combine(@"C:\P", "Saved", "Config", "WindowsEditor", "EditorPerProjectUserSettings.ini"), UeLiveCodingStartup.IniPath(@"C:\P"));
            Assert.Null(UeLiveCodingStartup.IniPath(null));
        }

        [Fact]
        public void SetsManualInAnExistingSectionAndRestoresTheOriginal()
        {
            const string ini = "[/Script/UnrealEd.EditorPerProjectUserSettings]\r\nbA=True\r\n\r\n[/Script/LiveCoding.LiveCodingSettings]\r\nbEnabled=True\r\nStartup=AutomaticButHidden\r\n\r\n[Other]\r\nX=1\r\n";
            var backup = new LiveCodingBackup();
            string manual = UeLiveCodingStartup.Apply(ini, true, Path, backup, out var change);
            Assert.Equal(LiveCodingStartupChange.SetManual, change);
            Assert.Equal("Manual", UeLiveCodingStartup.CurrentValue(manual));
            Assert.Equal(ini.Replace("Startup=AutomaticButHidden", "Startup=Manual"), manual);
            Assert.Equal("AutomaticButHidden", backup.Originals[Path]);

            // Applying again keeps the first original.
            Assert.Equal(manual, UeLiveCodingStartup.Apply(manual, true, Path, backup, out change));
            Assert.Equal(LiveCodingStartupChange.None, change);
            Assert.Equal("AutomaticButHidden", backup.Originals[Path]);

            string restored = UeLiveCodingStartup.Apply(manual, false, Path, backup, out change);
            Assert.Equal(LiveCodingStartupChange.Restored, change);
            Assert.Equal(ini, restored);
            Assert.True(backup.IsEmpty);
        }

        [Fact]
        public void AddsTheSectionWhenMissingAndRemovesItsKeyOnRestore()
        {
            const string ini = "[/Script/UnrealEd.EditorPerProjectUserSettings]\r\nbA=True\r\n";
            var backup = new LiveCodingBackup();
            string manual = UeLiveCodingStartup.Apply(ini, true, Path, backup, out _);
            Assert.Contains("[/Script/LiveCoding.LiveCodingSettings]\r\nStartup=Manual", manual);
            Assert.StartsWith(ini, manual);
            Assert.Equal(UeLiveCodingStartup.Absent, backup.Originals[Path]);
            string restored = UeLiveCodingStartup.Apply(manual, false, Path, backup, out var change);
            Assert.Equal(LiveCodingStartupChange.Restored, change);
            Assert.Null(UeLiveCodingStartup.CurrentValue(restored));
            Assert.Contains("bA=True", restored);
        }

        [Fact]
        public void WorksWithoutAFileAndInsertsIntoAnExistingSection()
        {
            var backup = new LiveCodingBackup();
            string created = UeLiveCodingStartup.Apply(null, true, Path, backup, out _);
            Assert.Equal("Manual", UeLiveCodingStartup.CurrentValue(created));

            const string ini = "[/Script/LiveCoding.LiveCodingSettings]\nbEnabled=True\n\n[Next]\nY=2\n";
            string manual = UeLiveCodingStartup.Apply(ini, true, "other", backup, out _);
            Assert.Equal("[/Script/LiveCoding.LiveCodingSettings]\nbEnabled=True\nStartup=Manual\n\n[Next]\nY=2\n", manual);
        }

        [Fact]
        public void LeavesTheUsersLaterChoiceAlone()
        {
            var backup = new LiveCodingBackup();
            backup.Originals[Path] = "Automatic";
            const string ini = "[/Script/LiveCoding.LiveCodingSettings]\r\nStartup=AutomaticButHidden\r\n";
            Assert.Equal(ini, UeLiveCodingStartup.Apply(ini, false, Path, backup, out var change));
            Assert.Equal(LiveCodingStartupChange.None, change);
            Assert.True(backup.IsEmpty);
            // Nothing backed up: turning the switch off changes nothing.
            Assert.Equal(ini, UeLiveCodingStartup.Apply(ini, false, Path, backup, out change));
            Assert.Equal(LiveCodingStartupChange.None, change);
        }

        [Fact]
        public void BackupRoundTripsAndRejectsGarbage()
        {
            var b = new LiveCodingBackup();
            b.Originals[Path] = UeLiveCodingStartup.Absent;
            var back = LiveCodingBackup.Parse(b.ToJson());
            Assert.Equal(UeLiveCodingStartup.Absent, back.Originals[Path.ToUpperInvariant()]);
            Assert.True(LiveCodingBackup.Parse("").IsEmpty);
            Assert.Null(LiveCodingBackup.Parse("{not json"));
        }

        [Fact]
        public void SwitchIsOnByDefault()
        {
            Assert.True(new HostSettings().UeLiveCodingManualStartup);
            Assert.Equal("true", HostSettingsSchema.Codec.ToValues(new HostSettings())["link.ueLiveCodingManual"]);
        }
    }
}
