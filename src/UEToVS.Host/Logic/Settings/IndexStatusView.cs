using System;
using System.Linq;
using UEToVS.Host.Logic.Localization;

namespace UEToVS.Host.Logic.Settings
{
    /// <summary>How the index status card is drawn (mapped to UEToVS.UI Severity in the window).</summary>
    public enum IndexStatusTone
    {
        /// <summary>Nothing running (no solution, not started): plain text.</summary>
        Neutral = 0,
        /// <summary>Indexing: neutral card with a progress bar.</summary>
        Progress = 1,
        /// <summary>Index ready.</summary>
        Success = 2,
        /// <summary>Ready but the engine reported a problem, or the engine is restarting.</summary>
        Warning = 3,
        /// <summary>Index failed / engine unavailable: error card with the reason and "查看日志" / "重建".</summary>
        Error = 4,
    }

    /// <summary>Everything the settings window knows about indexing (all plain values; built from EngineClient / EngineStatus).</summary>
    public sealed class IndexStatusInput
    {
        /// <summary>False when no UE solution is open (no engine client).</summary>
        public bool HasEngine { get; set; }
        /// <summary>EngineState name ("Connected", "Unavailable", …).</summary>
        public string EngineState { get; set; }
        /// <summary>Last engine status received, or null.</summary>
        public bool HasStatus { get; set; }
        /// <summary>IndexPhase as int (0 NotStarted … 6 Ready, 7 Failed).</summary>
        public int Phase { get; set; }
        public int FilesParsed { get; set; }
        public int FilesToParse { get; set; }
        public int SymbolCount { get; set; }
        public int AssetCount { get; set; }
        public string LastError { get; set; }
        /// <summary>Engine-side failure text when the engine itself is not usable (EngineClient.LastError), or null.</summary>
        public string EngineError { get; set; }
        public int CppThreads { get; set; }
        public int BlueprintThreads { get; set; }
        public int Cores { get; set; }
    }

    /// <summary>What the index status card shows.</summary>
    public sealed class IndexStatusViewModel
    {
        public IndexStatusTone Tone { get; set; }
        /// <summary>Headline, e.g. "索引失败", "正在解析 42%", "就绪".</summary>
        public string Title { get; set; }
        /// <summary>Counts line ("1,234 / 5,678 文件 · 0 符号 · 8,811 资产"), or null.</summary>
        public string Counts { get; set; }
        /// <summary>Failure / warning reason in readable form, or null.</summary>
        public string Reason { get; set; }
        /// <summary>Effective thread counts line.</summary>
        public string Threads { get; set; }
        /// <summary>Show a progress bar.</summary>
        public bool ShowProgress { get; set; }
        /// <summary>0..1, or -1 for indeterminate.</summary>
        public double Fraction { get; set; } = -1;
        public bool ShowViewLog { get; set; }
        public bool ShowRebuild { get; set; }

        /// <summary>Single-line text (accessibility name, logs, tests).</summary>
        public override string ToString() =>
            string.Join(" · ", new[] { Title, Counts, Reason }.Where(s => !string.IsNullOrEmpty(s)));
    }

    /// <summary>Pure mapping engine status → index status card (tone, headline, reason, progress, actions).</summary>
    public static class IndexStatusView
    {
        public const int PhaseNotStarted = 0;
        public const int PhaseParsing = 4;
        public const int PhaseReady = 6;
        public const int PhaseFailed = 7;
        private const int MaxReasonLength = 400;

        public static IndexStatusViewModel From(IndexStatusInput i)
        {
            i = i ?? new IndexStatusInput();
            var vm = new IndexStatusViewModel { Threads = SettingsText.IndexThreadsEffective(i.CppThreads, i.BlueprintThreads, i.Cores) };
            if (!i.HasEngine)
            {
                vm.Tone = IndexStatusTone.Neutral;
                vm.Title = SettingsText.IndexStatusNone;
                return vm;
            }

            if (!i.HasStatus)
            {
                switch (i.EngineState)
                {
                    case "Unavailable":
                    case "Incompatible":
                    case "NotInstalled":
                        vm.Tone = IndexStatusTone.Error;
                        vm.Title = SettingsText.IndexEngineUnavailable(Strings.EngineStateName(i.EngineState));
                        vm.Reason = CleanReason(i.EngineError) ?? SettingsText.IndexNoReason;
                        vm.ShowViewLog = true;
                        return vm;
                    case "Starting":
                    case "Restarting":
                        vm.Tone = IndexStatusTone.Progress;
                        vm.Title = Strings.EngineStateName(i.EngineState);
                        vm.ShowProgress = true;
                        vm.Fraction = -1;
                        return vm;
                    default:
                        vm.Tone = IndexStatusTone.Neutral;
                        vm.Title = Strings.EngineStateName(i.EngineState);
                        return vm;
                }
            }

            vm.Counts = SettingsText.IndexCounts(i.FilesParsed, i.FilesToParse, i.SymbolCount, i.AssetCount);
            string reason = CleanReason(i.LastError);
            switch (i.Phase)
            {
                case PhaseFailed:
                    vm.Tone = IndexStatusTone.Error;
                    vm.Title = SettingsText.IndexFailedTitle;
                    vm.Reason = reason ?? SettingsText.IndexNoReason;
                    vm.ShowViewLog = true;
                    vm.ShowRebuild = true;
                    return vm;
                case PhaseReady:
                    vm.Tone = reason == null ? IndexStatusTone.Success : IndexStatusTone.Warning;
                    vm.Title = Strings.PhaseName(PhaseReady);
                    vm.Reason = reason;
                    vm.ShowViewLog = reason != null;
                    return vm;
                case PhaseNotStarted:
                    vm.Tone = IndexStatusTone.Neutral;
                    vm.Title = Strings.PhaseName(PhaseNotStarted);
                    return vm;
                default:
                    vm.Tone = IndexStatusTone.Progress;
                    vm.ShowProgress = true;
                    vm.Fraction = Fraction(i.Phase, i.FilesParsed, i.FilesToParse);
                    vm.Title = vm.Fraction >= 0
                        ? Strings.PhaseName(i.Phase) + " " + Math.Floor(vm.Fraction * 100).ToString("0", System.Globalization.CultureInfo.InvariantCulture) + "%"
                        : Strings.PhaseName(i.Phase);
                    vm.Reason = reason;
                    return vm;
            }
        }

        /// <summary>Parsing progress (0..1) while parsing with a known total; -1 (indeterminate) otherwise.</summary>
        public static double Fraction(int phase, int parsed, int toParse)
        {
            if (phase != PhaseParsing || toParse <= 0) return -1;
            return Math.Max(0, Math.Min(1, parsed / (double)toParse));
        }

        /// <summary>
        /// Engine error text → something a user can read: drops the "index.failed:&lt;phase&gt;: " event prefix the engine
        /// writes, collapses whitespace and caps the length. Null/blank → null.
        /// </summary>
        public static string CleanReason(string error)
        {
            if (string.IsNullOrWhiteSpace(error)) return null;
            string s = error.Trim();
            const string prefix = "index.failed:";
            if (s.StartsWith(prefix, StringComparison.Ordinal))
            {
                int sep = s.IndexOf(": ", prefix.Length, StringComparison.Ordinal);
                s = sep >= 0 ? s.Substring(sep + 2) : s.Substring(prefix.Length);
            }
            s = System.Text.RegularExpressions.Regex.Replace(s, @"\s+", " ").Trim();
            if (s.Length == 0) return null;
            if (s.Length > MaxReasonLength) s = s.Substring(0, MaxReasonLength - 1) + "…";
            return s;
        }
    }
}
