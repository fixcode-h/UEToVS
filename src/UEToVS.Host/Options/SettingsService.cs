using System;
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json;
using UEToVS.Core.Diagnostics;
using UEToVS.Host.Logic.Diagnostics;
using UEToVS.Host.Logic.Settings;

namespace UEToVS.Host.Options
{
    internal sealed class SettingsChangedEventArgs : EventArgs
    {
        public SettingsChangedEventArgs(HostSettings oldSettings, HostSettings newSettings)
        {
            Old = oldSettings;
            New = newSettings;
        }

        public HostSettings Old { get; }
        public HostSettings New { get; }
    }

    /// <summary>
    /// Effective settings = defaults &lt; user (UEToVS settings window, <see cref="SettingsStore"/>) &lt; solution
    /// (.uetovs/settings.json, read-only). Recomputed when the settings window applies or a solution opens;
    /// <see cref="Changed"/> lets services apply live. The old Tools &gt; Options pages are only read once, for migration.
    /// </summary>
    internal sealed class SettingsService
    {
        private static readonly Logger L = Log.For("Settings");
        private readonly GeneralOptionsPage _general;
        private readonly SettingsStore _store;
        private SettingsMergeResult _current;
        private string _solutionDir;

        public SettingsService(GeneralOptionsPage general, LoggingOptionsPage logging)
        {
            _general = general;
            Schema = HostSettingsSchema.Build(Environment.ProcessorCount);
            _store = new SettingsStore(Schema, () =>
            {
                var legacy = new HostSettings();
                general.Fill(legacy);
                logging.Fill(legacy);
                return legacy;
            });
            _current = SettingsMerger.Merge(UserSettings(), null);
        }

        public GeneralOptionsPage General => _general;

        /// <summary>Descriptors of every setting in the settings window (built for this machine's core count).</summary>
        public IReadOnlyList<UEToVS.UI.Logic.SettingDescriptor> Schema { get; }

        /// <summary>Stored user values (descriptor key → invariant string), defaults filled in.</summary>
        public Dictionary<string, string> UserValues()
        {
            var d = UEToVS.UI.Logic.SettingsModel.Defaults(Schema);
            foreach (var kv in _store.Values) d[kv.Key] = kv.Value;
            return d;
        }

        /// <summary>Settings window "应用/确定": persists the values and recomputes (raises <see cref="Changed"/>).</summary>
        public void ApplyUserValues(IReadOnlyDictionary<string, string> values, string reason)
        {
            var changed = _store.Write(values);
            L.Info("settings.window.applied", null, ("reason", reason), ("changed", changed));
            if (changed.Count > 0) Recompute(reason);
        }

        public HostSettings Effective => _current.Effective;

        public SettingsMergeResult Current => _current;

        public event EventHandler<SettingsChangedEventArgs> Changed;

        public HostSettings UserSettings()
        {
            var problems = new List<string>();
            var s = HostSettingsSchema.Codec.FromValues(_store.Values, Schema, new HostSettings(), problems);
            foreach (var p in problems) L.Warn("settings.store.invalid", p);
            return s;
        }

        /// <summary>Loads the solution layer (null = no solution). Small file read; fine on any thread.</summary>
        public void SetSolution(string solutionDir)
        {
            _solutionDir = solutionDir;
            Recompute("solution");
        }

        public void Recompute(string reason)
        {
            var old = _current?.Effective;
            var next = string.IsNullOrEmpty(_solutionDir)
                ? SettingsMerger.Merge(UserSettings(), null)
                : SettingsMerger.MergeFromSolution(UserSettings(), _solutionDir);
            _current = next;
            foreach (var w in next.Warnings) L.Warn("settings.solution.warning", w, ("file", next.SolutionFile));
            if (next.SolutionFile != null)
                L.Info("settings.solution.loaded", "Solution settings applied", ("file", next.SolutionFile),
                       ("overrides", next.SolutionOverrides.Keys.ToList()), ("foreignKeys", next.ForeignKeys.Count));
            L.Info("settings.effective", null, ("reason", reason), ("settings", Describe(next.Effective)));
            if (old != null) Changed?.Invoke(this, new SettingsChangedEventArgs(old, next.Effective));
        }

        /// <summary>Flat dictionary for the session header and the diagnostics package (secrets masked).</summary>
        public static Dictionary<string, object> Describe(HostSettings s)
        {
            var d = new Dictionary<string, object>
            {
                ["logLevel"] = s.LogLevel.ToString(),
                ["enginePathOverride"] = string.IsNullOrEmpty(s.EnginePathOverride) ? "" : s.EnginePathOverride,
                ["unrealEngineRootOverride"] = s.UnrealEngineRootOverride ?? "",
                ["engineRequestTimeoutSeconds"] = s.EngineRequestTimeoutSeconds,
                ["intelliSenseTakeover"] = s.IntelliSenseTakeover,
                ["intelliSenseTakeoverMode"] = s.IntelliSenseTakeoverMode.ToString(),
                ["disableDatabase"] = s.DisableDatabase,
                ["disableBackgroundCodeAnalysis"] = s.DisableBackgroundCodeAnalysis,
                ["logRetentionDays"] = s.LogRetentionDays,
                ["logMaxTotalMB"] = s.LogMaxTotalMB,
                ["cppIndexThreads"] = s.CppIndexThreads,
                ["blueprintIndexThreads"] = s.BlueprintIndexThreads,
                ["indexDuringBuild"] = s.IndexDuringBuild.ToString(),
                ["indexEngineSource"] = s.IndexEngineSource,
                ["engineIndexPath"] = s.EngineIndexPath ?? "",
                ["shareEngineIndex"] = s.ShareEngineIndex,
                ["showToolbar"] = s.ShowToolbar,
                ["hiddenToolbarButtons"] = s.HiddenToolbarButtons,
                ["performanceSampleSeconds"] = s.PerformanceSampleSeconds,
                ["ctrlClickNavigation"] = s.CtrlClickNavigation,
                ["referenceHighlighting"] = s.ReferenceHighlighting,
                ["referenceHighlightReadColor"] = s.ReferenceHighlightReadColor ?? "",
                ["referenceHighlightWriteColor"] = s.ReferenceHighlightWriteColor ?? "",
                ["navigationBar"] = s.NavigationBar,
                ["doubleShiftEnabled"] = s.DoubleShiftEnabled,
                ["doubleShiftIntervalMs"] = s.DoubleShiftIntervalMs,
                ["doubleShiftLeftOnly"] = s.DoubleShiftLeftOnly,
                ["searchMaxResultsPerCategory"] = s.SearchMaxResultsPerCategory,
                ["mouseButtonsNavigate"] = s.MouseButtonsNavigate,
                ["navigationLineThreshold"] = s.NavigationLineThreshold,
                ["navigationHistoryCapacity"] = s.NavigationHistoryCapacity,
                ["checkIncludesOnSave"] = s.CheckIncludesOnSave,
                ["editorLinkPort"] = s.EditorLinkPort,
                ["enableMcpServer"] = s.EnableMcpServer,
            };
            return Sanitizer.MaskSecrets(d);
        }

        public string ToSanitizedJson()
        {
            var payload = new
            {
                effective = Describe(Effective),
                user = Describe(UserSettings()),
                solutionFile = _current.SolutionFile,
                solutionOverrides = _current.SolutionOverrides,
                warnings = _current.Warnings,
            };
            string json = JsonConvert.SerializeObject(payload, Formatting.Indented);
            return Sanitizer.Sanitize(json, Environment.UserName, Environment.GetFolderPath(Environment.SpecialFolder.UserProfile));
        }
    }
}
