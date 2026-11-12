using System;
using UEToVS.Contracts;
using UEToVS.Core.Localization;

namespace UEToVS.Host.Logic.Localization
{
    /// <summary>
    /// The engine returns stable codes instead of user-facing text (it does not know the UI language); the host
    /// localizes them here. Codes: active work names ("index.parse.project", "index.Parsing"), LastError
    /// ("index.failed:&lt;IndexPhase&gt;: &lt;technical detail&gt;"), phase stages ("project"/"engine").
    /// Unknown codes are shown as-is (never hidden).
    /// </summary>
    public static class EngineCodes
    {
        public static string Stage(string stage)
        {
            switch (stage)
            {
                case "project": return Loc.T("项目", "project");
                case "engine": return Loc.T("引擎源码", "engine source");
                default: return stage;
            }
        }

        /// <summary>"index.parse.project" → "解析项目文件"; "index.Parsing" → phase name.</summary>
        public static string WorkName(string code)
        {
            if (string.IsNullOrEmpty(code)) return code;
            const string parse = "index.parse.";
            if (code.StartsWith(parse, StringComparison.Ordinal))
                return Loc.F("解析{0}文件", "Parsing {0} files", Stage(code.Substring(parse.Length)));
            const string index = "index.";
            if (code.StartsWith(index, StringComparison.Ordinal) &&
                Enum.TryParse(code.Substring(index.Length), false, out IndexPhase phase) && Enum.IsDefined(typeof(IndexPhase), phase))
                return Strings.PhaseName((int)phase);
            return code;
        }

        /// <summary>
        /// "index.failed:Parsing: IOException: ..." → "索引失败（正在解析）：IOException: ...". Null/empty → null.
        /// </summary>
        public static string Error(string lastError)
        {
            if (string.IsNullOrEmpty(lastError)) return null;
            const string failed = "index.failed:";
            if (!lastError.StartsWith(failed, StringComparison.Ordinal)) return lastError;
            string rest = lastError.Substring(failed.Length);
            int colon = rest.IndexOf(':');
            string phaseText = colon < 0 ? rest : rest.Substring(0, colon);
            string detail = colon < 0 ? string.Empty : rest.Substring(colon + 1).Trim();
            string phase = Enum.TryParse(phaseText.Trim(), false, out IndexPhase p) && Enum.IsDefined(typeof(IndexPhase), p)
                ? Strings.PhaseName((int)p)
                : phaseText;
            return detail.Length == 0
                ? Loc.F("索引失败（{0}）", "Indexing failed ({0})", phase)
                : Loc.F("索引失败（{0}）：{1}", "Indexing failed ({0}): {1}", phase, detail);
        }

        /// <summary>Phase detail "project files=1234 cacheHits=10" → stage localized, key=value pairs kept.</summary>
        public static string Detail(string detail)
        {
            if (string.IsNullOrEmpty(detail)) return detail;
            var parts = detail.Split(' ');
            for (int i = 0; i < parts.Length; i++)
                if (parts[i].IndexOf('=') < 0) parts[i] = Stage(parts[i]);
            return string.Join(" ", parts);
        }
    }
}
