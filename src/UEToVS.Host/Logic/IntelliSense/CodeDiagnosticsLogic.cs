using System;
using System.Collections.Generic;
using System.Linq;
using UEToVS.Contracts;
using UEToVS.Core.Localization;
using UEToVS.Host.Logic.Unreal;

namespace UEToVS.Host.Logic.IntelliSense
{
    /// <summary>Localized squiggle texts from rule code + arguments (the engine sends English fallbacks only). Pure.</summary>
    public static class CodeDiagnosticText
    {
        public static string Message(CodeDiagnostic d)
        {
            if (d == null) return string.Empty;
            string A(int i) => d.Args != null && i < d.Args.Count ? d.Args[i] : "?";
            switch (d.Code)
            {
                case CodeDiagnosticCodes.UnknownMember:
                    return Loc.F("“{1}” 没有名为 “{0}” 的成员", "'{1}' has no member named '{0}'", A(0), A(1));
                case CodeDiagnosticCodes.MissingModuleDependency:
                    return Loc.F("“{0}” 属于模块 {1}，但 {2}.Build.cs 的依赖里没有它（链接或包含会失败）",
                                 "'{0}' belongs to module {1}, which is not a dependency in {2}.Build.cs (include / link will fail)", A(0), A(1), A(2));
                case CodeDiagnosticCodes.MissingGeneratedBody:
                    return Loc.F("{0} 类型 “{1}” 缺少 GENERATED_BODY()（UHT 会报错）", "{0} type '{1}' is missing GENERATED_BODY() (UHT will fail)", A(0), A(1));
                default:
                    return d.Message ?? d.Code ?? string.Empty;
            }
        }

        /// <summary>Tooltip of the squiggle: "UE0001: message (UEToVS)".</summary>
        public static string ToolTip(CodeDiagnostic d) => d == null ? string.Empty : (d.Code + ": " + Message(d) + " (UEToVS)");
    }

    /// <summary>Request planning for the squiggle tagger: the viewport ± a margin, and whether a stored answer still covers it. Pure.</summary>
    public static class CodeDiagnosticsPlan
    {
        public const int MarginLines = 50;

        public static LineRange Range(int firstVisible, int lastVisible, int lineCount, int margin = MarginLines)
        {
            if (lineCount <= 0) return new LineRange(0, -1);
            int first = Math.Max(0, Math.Min(firstVisible, lastVisible));
            int last = Math.Max(firstVisible, lastVisible);
            return new LineRange(Math.Max(0, first - margin), Math.Min(lineCount - 1, last + margin));
        }

        /// <summary>A scroll needs a new request only when the visible lines leave the range the last answer covered.</summary>
        public static bool NeedsRequest(LineRange covered, long coveredVersion, int firstVisible, int lastVisible, long version) =>
            coveredVersion != version || covered.IsEmpty || !covered.Contains(new LineRange(Math.Max(0, firstVisible), Math.Max(firstVisible, lastVisible)));
    }

    /// <summary>UE0002: missing Build.cs module dependencies (<see cref="IncludeDependencyChecker"/>) as squiggles on the include path. Pure.</summary>
    public static class IncludeDiagnostics
    {
        /// <summary>Cache key of a file's include list: the check only has to run again when the includes change.</summary>
        public static string Key(IReadOnlyList<IncludeRef> includes) =>
            includes == null ? string.Empty : string.Join("\n", includes.Select(i => i.Line + ":" + i.Path));

        /// <summary>
        /// Squiggles for <paramref name="missing"/>, located on the include path text of each line. Dropped (uncertain) when the
        /// owner Build.cs adds raw include paths (<c>PublicIncludePaths</c> / <c>PrivateIncludePaths</c>): the header may be
        /// reachable without the module dependency.
        /// </summary>
        public static List<CodeDiagnostic> From(IReadOnlyList<MissingDependency> missing, Func<int, string> lineText, Func<string, string> buildCsText, out int uncertain)
        {
            uncertain = 0;
            var list = new List<CodeDiagnostic>();
            if (missing == null) return list;
            var rawPaths = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
            foreach (var m in missing)
            {
                if (m == null || string.IsNullOrEmpty(m.Include)) continue;
                string owner = m.OwnerBuildCsPath ?? string.Empty;
                if (!rawPaths.TryGetValue(owner, out bool raw))
                {
                    string text = owner.Length == 0 ? null : buildCsText?.Invoke(owner);
                    raw = text != null && (text.IndexOf("PublicIncludePaths", StringComparison.Ordinal) >= 0 || text.IndexOf("PrivateIncludePaths", StringComparison.Ordinal) >= 0);
                    rawPaths[owner] = raw;
                }
                if (raw)
                {
                    uncertain++;
                    continue;
                }
                string line = lineText?.Invoke(m.Line);
                if (line == null)
                {
                    uncertain++;
                    continue;
                }
                int col = line.IndexOf(m.Include, StringComparison.Ordinal);
                if (col < 0)
                {
                    uncertain++;
                    continue;
                }
                list.Add(new CodeDiagnostic
                {
                    Line = m.Line,
                    Column = col,
                    Length = m.Include.Length,
                    Severity = CodeDiagnosticSeverity.Warning,
                    Code = CodeDiagnosticCodes.MissingModuleDependency,
                    Args = new List<string> { m.Include, m.Module, m.OwnerModule },
                    Message = $"'{m.Include}' belongs to module {m.Module}, which is not a dependency of {m.OwnerModule}",
                });
            }
            return list;
        }
    }
}
