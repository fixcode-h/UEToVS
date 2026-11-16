using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace UEToVS.Host.Logic.Debugging
{
    /// <summary>Pure helpers of the DTE backend (kept here so they are unit tested without VS).</summary>
    public static class DebugHelpers
    {
        /// <summary>
        /// Picks a solution configuration: "Development Editor", "Development Editor|Win64" or a unique prefix / substring
        /// ("debuggame edi"). Returns the index into <paramref name="configs"/>; throws with the list when none or several match.
        /// </summary>
        public static int FindConfiguration(IReadOnlyList<(string Name, string Platform)> configs, string wanted)
        {
            if (configs == null || configs.Count == 0) throw new DebugControlException("解决方案里没有可用的配置");
            string w = (wanted ?? "").Trim();
            string wName = w, wPlatform = null;
            int bar = w.IndexOf('|');
            if (bar >= 0)
            {
                wName = w.Substring(0, bar).Trim();
                wPlatform = w.Substring(bar + 1).Trim();
            }
            bool PlatformOk((string Name, string Platform) c) => wPlatform == null || string.Equals(c.Platform, wPlatform, StringComparison.OrdinalIgnoreCase);

            var exact = Indexes(configs, c => PlatformOk(c) && string.Equals(c.Name, wName, StringComparison.OrdinalIgnoreCase));
            if (exact.Count == 0) exact = Indexes(configs, c => PlatformOk(c) && Normalize(c.Name).Contains(Normalize(wName)));
            // Same name on several platforms (Win64 + others): prefer Win64, the UE desktop default.
            if (exact.Count > 1 && wPlatform == null)
            {
                var win64 = exact.Where(i => string.Equals(configs[i].Platform, "Win64", StringComparison.OrdinalIgnoreCase)).ToList();
                if (win64.Count == 1 && exact.All(i => string.Equals(configs[i].Name, configs[win64[0]].Name, StringComparison.OrdinalIgnoreCase)))
                    return win64[0];
            }
            if (exact.Count == 1) return exact[0];
            string all = string.Join("、", configs.Select(c => c.Name + "|" + c.Platform).Distinct());
            throw new DebugControlException(exact.Count == 0
                ? $"没有叫 {wanted} 的配置，可用：{all}"
                : $"{wanted} 对上了多个配置，写全名（如 Development Editor|Win64）：{string.Join("、", exact.Select(i => configs[i].Name + "|" + configs[i].Platform))}");
        }

        private static List<int> Indexes<T>(IReadOnlyList<T> items, Func<T, bool> pred)
        {
            var r = new List<int>();
            for (int i = 0; i < items.Count; i++) if (pred(items[i])) r.Add(i);
            return r;
        }

        private static string Normalize(string s) => new string((s ?? "").Where(ch => !char.IsWhiteSpace(ch) && ch != '_').ToArray()).ToLowerInvariant();

        /// <summary>
        /// A breakpoint file: absolute as is; relative tried against each base folder (solution, .uproject folder). Throws
        /// when no candidate exists, because VS would otherwise accept the breakpoint and never bind it.
        /// </summary>
        public static string ResolveSourceFile(string path, IEnumerable<string> baseDirs, Func<string, bool> exists)
        {
            if (string.IsNullOrWhiteSpace(path)) throw new DebugControlException("缺少参数 file");
            string p = path.Trim().Trim('"').Replace('/', Path.DirectorySeparatorChar);
            if (Path.IsPathRooted(p))
            {
                if (exists(p)) return Path.GetFullPath(p);
                throw new DebugControlException($"找不到文件 {path}");
            }
            foreach (var b in baseDirs ?? Enumerable.Empty<string>())
            {
                if (string.IsNullOrEmpty(b)) continue;
                string full = Path.GetFullPath(Path.Combine(b, p));
                if (exists(full)) return full;
            }
            throw new DebugControlException($"找不到文件 {path}：写绝对路径，或相对解决方案 / .uproject 所在目录的路径");
        }

        /// <summary>Last <paramref name="maxChars"/> characters, cut at a line start when possible; <paramref name="cut"/> = dropped chars.</summary>
        public static string Tail(string text, int maxChars, out int cut)
        {
            cut = 0;
            if (string.IsNullOrEmpty(text) || text.Length <= maxChars) return text ?? "";
            int start = text.Length - maxChars;
            int nl = text.IndexOf('\n', start);
            if (nl >= 0 && nl < text.Length - 1) start = nl + 1;
            cut = start;
            return text.Substring(start);
        }

        /// <summary>
        /// Output pane paging by line: given the pane's line count and the caller's cursor (the "next" of the last call),
        /// the first line to read. A cursor past the end means the pane was cleared (new debug session): start over.
        /// </summary>
        public static int OutputStartLine(int lineCount, int? cursor, int tailLines, out bool restarted)
        {
            restarted = false;
            if (cursor == null) return Math.Max(1, lineCount - tailLines + 1);
            if (cursor.Value > lineCount + 1)
            {
                restarted = true;
                return 1;
            }
            return Math.Max(1, cursor.Value);
        }

        /// <summary>
        /// One launch's command arguments for the startup project: the map goes right after the .uproject (UE takes the
        /// first token after the project as the map), "-game" and the extra arguments at the end. <paramref name="original"/> is
        /// the project's own "调试 > 命令参数" (UE generates <c>"$(SolutionDir)$(ProjectName).uproject" -skipcompile</c>).
        /// </summary>
        public static string ComposeLaunchArgs(string original, string map, bool game, string extra)
        {
            string s = (original ?? "").Trim();
            if (!string.IsNullOrWhiteSpace(map))
            {
                string m = map.Trim();
                if (m.IndexOf(' ') >= 0 && !m.StartsWith("\"")) m = "\"" + m + "\"";
                int at = s.IndexOf(".uproject", StringComparison.OrdinalIgnoreCase);
                if (at >= 0)
                {
                    int end = at + ".uproject".Length;
                    if (end < s.Length && s[end] == '"') end++;
                    s = s.Substring(0, end) + " " + m + s.Substring(end);
                }
                else s = (m + " " + s).Trim();
            }
            if (game && !HasSwitch(s, "-game")) s = (s + " -game").Trim();
            if (!string.IsNullOrWhiteSpace(extra)) s = (s + " " + extra.Trim()).Trim();
            return s;
        }

        private static bool HasSwitch(string args, string sw) =>
            (" " + args + " ").IndexOf(" " + sw + " ", StringComparison.OrdinalIgnoreCase) >= 0;

        /// <summary>Readable break reason from EnvDTE's dbgEventReason name.</summary>
        public static string ReasonText(string dbgEventReason)
        {
            switch (dbgEventReason)
            {
                case "dbgEventReasonBreakpoint": return "breakpoint";
                case "dbgEventReasonStep": return "step";
                case "dbgEventReasonExceptionThrown": return "exception";
                case "dbgEventReasonExceptionNotHandled": return "unhandledException";
                case "dbgEventReasonUserBreak": return "pause";
                case "dbgEventReasonEndProgram": return "exited";
                case "dbgEventReasonStopDebugging": return "stopped";
                case "dbgEventReasonDetachProgram": return "detached";
                case "dbgEventReasonLaunchProgram": return "launched";
                case "dbgEventReasonAttachProgram": return "attached";
                case "dbgEventReasonGo": return "go";
                case "dbgEventReasonContextSwitch": return "contextSwitch";
                default: return string.IsNullOrEmpty(dbgEventReason) ? null : dbgEventReason.Replace("dbgEventReason", "").ToLowerInvariant();
            }
        }
    }
}
