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
    /// Effective settings = defaults &lt; user (option pages, VS settings store) &lt; solution (.uetovs/settings.json, read-only).
    /// Recomputed when an option page is applied or a solution opens; <see cref="Changed"/> lets services apply live.
    /// </summary>
    internal sealed class SettingsService
    {
        private static readonly Logger L = Log.For("Settings");
        private readonly GeneralOptionsPage _general;
        private readonly LoggingOptionsPage _logging;
        private SettingsMergeResult _current;
        private string _solutionDir;

        public SettingsService(GeneralOptionsPage general, LoggingOptionsPage logging)
        {
            _general = general;
            _logging = logging;
            _current = SettingsMerger.Merge(UserSettings(), null);
            _general.Applied += (s, e) => Recompute("options");
            _logging.Applied += (s, e) => Recompute("options");
        }

        public GeneralOptionsPage General => _general;

        public HostSettings Effective => _current.Effective;

        public SettingsMergeResult Current => _current;

        public event EventHandler<SettingsChangedEventArgs> Changed;

        public HostSettings UserSettings()
        {
            var s = new HostSettings();
            _general.Fill(s);
            _logging.Fill(s);
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
