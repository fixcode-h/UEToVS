using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using UEToVS.Host.Logic.IntelliSense;
using Xunit;

namespace UEToVS.Host.Logic.Tests
{
    /// <summary>In-memory DTE stand-in: case-insensitive names, optional write failures.</summary>
    internal sealed class FakeStore : ICppSettingsStore
    {
        public readonly Dictionary<string, object> Values = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase);
        public readonly HashSet<string> FailWrites = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        public readonly List<string> Writes = new List<string>();

        public FakeStore With(string name, object value)
        {
            Values[name] = value;
            return this;
        }

        /// <summary>Property names of a VS 2022-like page.</summary>
        public static FakeStore Vs2022() => new FakeStore()
            .With("DisableIntelliSense", false)
            .With("DisableAutoQuickInfo", false)
            .With("DisableErrorReporting", false)
            .With("DisableSquiggles", false)
            .With("DisableSemanticColorization", false)
            .With("DisableExternalDependenciesFolders", false)
            .With("DisableDatabase", false)
            .With("EnumerateCommentTasks", true);

        public IReadOnlyList<string> GetPropertyNames(string page) => Values.Keys.ToList();

        public bool TryRead(string page, string name, out object value, out string error)
        {
            error = null;
            if (page == CppIntelliSenseSettings.SpecificPage && Values.TryGetValue(name, out value)) return true;
            value = null;
            error = "not found";
            return false;
        }

        public bool TryWrite(string page, string name, object value, out string error)
        {
            error = null;
            if (FailWrites.Contains(name))
            {
                error = "COMException: denied";
                return false;
            }
            if (!Values.ContainsKey(name))
            {
                error = "not found";
                return false;
            }
            Values[name] = value;
            Writes.Add(name + "=" + value);
            return true;
        }
    }

    public class IntelliSenseTakeoverTests
    {
        private static TakeoverReport Apply(FakeStore store, IntelliSenseBackup backup, bool disableDb = true, List<IntelliSenseBackup> persisted = null) =>
            IntelliSenseTakeover.Apply(store, backup, new TakeoverOptions { DisableDatabase = disableDb },
                b => persisted?.Add(Clone(b)));

        private static IntelliSenseBackup Clone(IntelliSenseBackup b) =>
            Newtonsoft.Json.JsonConvert.DeserializeObject<IntelliSenseBackup>(Newtonsoft.Json.JsonConvert.SerializeObject(b));

        [Fact]
        public void TakeoverBacksUpThenDisablesEverything()
        {
            var store = FakeStore.Vs2022();
            var backup = new IntelliSenseBackup();
            var persisted = new List<IntelliSenseBackup>();
            var r = Apply(store, backup, persisted: persisted);

            Assert.Single(persisted); // persisted once, before writes
            Assert.Equal(7, persisted[0].Entries.Count);
            Assert.All(persisted[0].Entries, e => Assert.False(e.Original));
            Assert.True((bool)store.Values["DisableIntelliSense"]);
            Assert.True((bool)store.Values["DisableDatabase"]);
            Assert.True(r.BackupWritten);
            Assert.Equal(7, r.Count(SettingOutcomeStatus.Changed));
            Assert.Equal(5, r.Count(SettingOutcomeStatus.Missing)); // memberList, includeAutoComplete, databaseUpdates, automaticUpdates, symbolIndex not on this "VS"
            Assert.True(r.FullyTakenOver);
            Assert.Equal(TakeoverState.TakenOver, IntelliSenseTakeover.GetState(store, new TakeoverOptions()));
            var missing = r.Outcomes.Single(o => o.Key == CppIntelliSenseSettings.KeyMemberList);
            Assert.Equal(3, missing.MissingCandidates.Count);
        }

        [Fact]
        public void Vs18SemanticIndexIsSwitchedOffWithTheDatabaseAndRestored()
        {
            var store = FakeStore.Vs2022().With("EnableSymbolIndex", true);
            var backup = new IntelliSenseBackup();
            var r = Apply(store, backup);
            Assert.False((bool)store.Values["EnableSymbolIndex"]);
            Assert.Equal(SettingOutcomeStatus.Changed, r.Outcomes.Single(o => o.Key == CppIntelliSenseSettings.KeySymbolIndex).Status);

            // Without "disable database" the semantic index is left alone.
            var other = FakeStore.Vs2022().With("EnableSymbolIndex", true);
            Apply(other, new IntelliSenseBackup(), disableDb: false);
            Assert.True((bool)other.Values["EnableSymbolIndex"]);

            IntelliSenseTakeover.Restore(store, backup);
            Assert.True((bool)store.Values["EnableSymbolIndex"]);
        }

        [Fact]
        public void BrowsingDatabaseUpdatesAreSwitchedOffWithTheDatabaseAndRestored()
        {
            var store = FakeStore.Vs2022().With("DisableDatabaseUpdates", false).With("DisableAutomaticUpdates", false);
            var backup = new IntelliSenseBackup();
            var r = Apply(store, backup);
            Assert.True((bool)store.Values["DisableDatabaseUpdates"]);
            Assert.True((bool)store.Values["DisableAutomaticUpdates"]);
            Assert.Equal(9, r.Count(SettingOutcomeStatus.Changed));
            IntelliSenseTakeover.Restore(store, backup);
            Assert.False((bool)store.Values["DisableDatabaseUpdates"]);
            Assert.False((bool)store.Values["DisableAutomaticUpdates"]);
        }

        [Fact]
        public void ReapplyIsIdempotentAndKeepsTheFirstOriginal()
        {
            var store = FakeStore.Vs2022().With("DisableSquiggles", true); // user had squiggles off already
            var backup = new IntelliSenseBackup();
            Apply(store, backup);
            Assert.Null(backup.Find(CppIntelliSenseSettings.SpecificPage, "DisableSquiggles")); // not ours: never restored
            int entries = backup.Entries.Count;

            var persisted = new List<IntelliSenseBackup>();
            var r = Apply(store, backup, persisted: persisted);
            Assert.Empty(persisted); // nothing new to back up
            Assert.Equal(0, r.Count(SettingOutcomeStatus.Changed));
            Assert.Equal(entries, backup.Entries.Count);
            Assert.All(backup.Entries, e => Assert.False(e.Original));
        }

        [Fact]
        public void RestoreWritesExactlyTheBackedUpValues()
        {
            var store = FakeStore.Vs2022().With("DisableSquiggles", true);
            var backup = new IntelliSenseBackup();
            Apply(store, backup);
            store.Writes.Clear();

            var r = IntelliSenseTakeover.Restore(store, backup);
            Assert.True(backup.IsEmpty);
            Assert.False((bool)store.Values["DisableIntelliSense"]);
            Assert.True((bool)store.Values["DisableSquiggles"]); // user's own value untouched
            Assert.DoesNotContain(store.Writes, w => w.StartsWith("DisableSquiggles"));
            Assert.Equal(6, r.Count(SettingOutcomeStatus.Changed));
            // The user's own "squiggles off" stays, so the state is Partial, not NotTakenOver.
            Assert.Equal(TakeoverState.Partial, IntelliSenseTakeover.GetState(store, new TakeoverOptions()));
        }

        [Fact]
        public void FailedRestoreKeepsTheEntryForRetry()
        {
            var store = FakeStore.Vs2022();
            var backup = new IntelliSenseBackup();
            Apply(store, backup);
            store.FailWrites.Add("DisableDatabase");
            var r = IntelliSenseTakeover.Restore(store, backup);
            Assert.Equal(1, r.Count(SettingOutcomeStatus.Failed));
            Assert.Single(backup.Entries);
            Assert.Equal("DisableDatabase", backup.Entries[0].Property);
        }

        [Fact]
        public void BackupFailureMeansNothingIsWritten()
        {
            var store = FakeStore.Vs2022();
            var r = IntelliSenseTakeover.Apply(store, new IntelliSenseBackup(), new TakeoverOptions(), b => throw new IOException("disk full"));
            Assert.NotNull(r.BackupError);
            Assert.Empty(store.Writes);
            Assert.All(r.Outcomes.Where(o => o.Status != SettingOutcomeStatus.Missing), o => Assert.Equal(SettingOutcomeStatus.Failed, o.Status));
        }

        [Fact]
        public void DisableDatabaseOptionGatesAndRevertsWhenTurnedOff()
        {
            var store = FakeStore.Vs2022();
            var backup = new IntelliSenseBackup();
            Apply(store, backup, disableDb: true);
            Assert.True((bool)store.Values["DisableDatabase"]);

            var r = Apply(store, backup, disableDb: false);
            Assert.False((bool)store.Values["DisableDatabase"]); // back to the user's original
            Assert.Equal(SettingOutcomeStatus.Reverted, r.Outcomes.Single(o => o.Key == CppIntelliSenseSettings.KeyDatabase).Status);
            Assert.Null(backup.FindByKey(CppIntelliSenseSettings.KeyDatabase));

            var fresh = FakeStore.Vs2022();
            var r2 = Apply(fresh, new IntelliSenseBackup(), disableDb: false);
            Assert.Equal(SettingOutcomeStatus.Skipped, r2.Outcomes.Single(o => o.Key == CppIntelliSenseSettings.KeyDatabase).Status);
            Assert.False((bool)fresh.Values["DisableDatabase"]);
        }

        [Fact]
        public void InvertedCandidatesAndCaseInsensitiveNames()
        {
            // A hypothetical VS that exposes "AutoQuickInfo" (true = on) and lower-case names.
            var store = new FakeStore().With("disableintellisense", false).With("AutoQuickInfo", true).With("EnableSemanticColorization", true);
            var backup = new IntelliSenseBackup();
            var r = Apply(store, backup);
            Assert.True((bool)store.Values["disableintellisense"]);
            Assert.False((bool)store.Values["AutoQuickInfo"]);
            Assert.False((bool)store.Values["EnableSemanticColorization"]);
            Assert.True(backup.Find(CppIntelliSenseSettings.SpecificPage, "AutoQuickInfo").Original);

            IntelliSenseTakeover.Restore(store, backup);
            Assert.True((bool)store.Values["AutoQuickInfo"]);
            Assert.False((bool)store.Values["disableintellisense"]);
        }

        [Fact]
        public void NonBooleanValueIsLeftAlone()
        {
            var store = new FakeStore().With("DisableIntelliSense", 1);
            var r = Apply(store, new IntelliSenseBackup());
            Assert.Equal(SettingOutcomeStatus.NotBoolean, r.Outcomes.Single(o => o.Key == CppIntelliSenseSettings.KeyIntelliSense).Status);
            Assert.Empty(store.Writes);
        }

        [Fact]
        public void UnknownVsReportsUnknownState()
        {
            var store = new FakeStore().With("SomethingElse", true);
            Assert.Equal(TakeoverState.Unknown, IntelliSenseTakeover.GetState(store, new TakeoverOptions()));
            var partial = FakeStore.Vs2022().With("DisableIntelliSense", true);
            Assert.Equal(TakeoverState.Partial, IntelliSenseTakeover.GetState(partial, new TakeoverOptions()));
        }

        [Fact]
        public void UnknownInterestingPropertiesAreReported()
        {
            var names = new[] { "DisableIntelliSense", "DisableFancyNewThing", "EnumerateCommentTasks", "IntelliSenseCacheSize" };
            var unknown = IntelliSenseTakeover.UnknownInterestingProperties(names);
            Assert.Equal(new[] { "DisableFancyNewThing", "IntelliSenseCacheSize" }, unknown);
        }

        [Fact]
        public void BackupFileRoundTripAndDeleteWhenEmpty()
        {
            using (var t = new TempDir())
            {
                string path = Path.Combine(t.Path, "sub", IntelliSenseBackupFile.FileName);
                Assert.Null(IntelliSenseBackupFile.Load(path));
                var b = new IntelliSenseBackup { VsVersion = "17.0" };
                b.Entries.Add(new IntelliSenseBackupEntry { Key = "intellisense", Page = "C/C++ Specific", Property = "DisableIntelliSense", Original = false, Applied = true });
                IntelliSenseBackupFile.Save(path, b);
                IntelliSenseBackupFile.Save(path, b); // replace path
                var loaded = IntelliSenseBackupFile.Load(path);
                Assert.Equal("DisableIntelliSense", loaded.Entries.Single().Property);
                Assert.Equal("17.0", loaded.VsVersion);

                loaded.Entries.Clear();
                IntelliSenseBackupFile.SaveOrDelete(path, loaded);
                Assert.False(File.Exists(path));

                File.WriteAllText(path, "{ not json");
                Assert.Throws<InvalidDataException>(() => IntelliSenseBackupFile.Load(path));
            }
        }

        [Fact]
        public void TableIsWellFormed()
        {
            var keys = CppIntelliSenseSettings.All.Select(d => d.Key).ToList();
            Assert.Equal(keys.Count, keys.Distinct().Count());
            Assert.All(CppIntelliSenseSettings.All, d => Assert.NotEmpty(d.Candidates));
            Assert.Contains(CppIntelliSenseSettings.All, d => d.Key == CppIntelliSenseSettings.KeyIntelliSense && d.Candidates[0].Name == "DisableIntelliSense");
        }

        [Fact]
        public void BackgroundCodeAnalysisIsDisabledWithBackupAndRestored()
        {
            var store = new FakeStore().With("DisableBackgroundCodeAnalysis", false).With("DisableIntelliSense", false);
            var backup = new IntelliSenseBackup();
            var persisted = new List<IntelliSenseBackup>();
            var r = IntelliSenseTakeover.Apply(store, backup, null, b => persisted.Add(Clone(b)), CppIntelliSenseSettings.BackgroundCodeAnalysis);

            Assert.Single(persisted);
            Assert.Equal("DisableBackgroundCodeAnalysis", persisted[0].Entries.Single().Property);
            Assert.False(persisted[0].Entries.Single().Original);
            Assert.True((bool)store.Values["DisableBackgroundCodeAnalysis"]);
            Assert.False((bool)store.Values["DisableIntelliSense"]); // IntelliSense takeover table not touched
            Assert.Equal(1, r.Count(SettingOutcomeStatus.Changed));

            var restored = IntelliSenseTakeover.Restore(store, backup);
            Assert.Equal(1, restored.Count(SettingOutcomeStatus.Changed));
            Assert.False((bool)store.Values["DisableBackgroundCodeAnalysis"]);
            Assert.True(backup.IsEmpty);
        }

        [Fact]
        public void BackgroundCodeAnalysisAlreadyOffIsNotBackedUp()
        {
            var store = new FakeStore().With("DisableBackgroundCodeAnalysis", true);
            var backup = new IntelliSenseBackup();
            var r = IntelliSenseTakeover.Apply(store, backup, null, b => throw new InvalidOperationException("must not persist"),
                CppIntelliSenseSettings.BackgroundCodeAnalysis);
            Assert.Equal(1, r.Count(SettingOutcomeStatus.AlreadyAtTarget));
            Assert.True(backup.IsEmpty);
            Assert.Empty(store.Writes);
        }

        [Fact]
        public void BackgroundCodeAnalysisMissingOnOlderVs()
        {
            var store = FakeStore.Vs2022();
            var r = IntelliSenseTakeover.Apply(store, new IntelliSenseBackup(), null, null, CppIntelliSenseSettings.BackgroundCodeAnalysis);
            Assert.Equal(1, r.Count(SettingOutcomeStatus.Missing));
            Assert.Empty(store.Writes);
        }

        [Fact]
        public void VisualAssistFoundInExtensionFolder()
        {
            using (var t = new TempDir())
            {
                var root = t.Sub("Extensions");
                t.File(Path.Combine("Extensions", "abc123", "other.dll"));
                Assert.Null(VisualAssistDetection.FindInExtensionDirectories(new[] { root, null, Path.Combine(t.Path, "missing") }));
                var va = t.File(Path.Combine("Extensions", "x7y8", "VA_X64.dll"));
                Assert.Equal(va, VisualAssistDetection.FindInExtensionDirectories(new[] { root }));
            }
        }
    }
}
