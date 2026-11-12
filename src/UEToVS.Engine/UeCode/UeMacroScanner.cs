using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using UEToVS.Contracts;

namespace UEToVS.Engine.UeCode
{
    /// <summary>An automation test declaration found in a source file (0-based line / column of the macro name).</summary>
    public sealed class ScannedTest
    {
        public string Name { get; init; }
        public string ClassName { get; init; }
        public string Macro { get; init; }
        public AutomationTestKind Kind { get; init; }
        public int Line { get; init; }
        public int Column { get; init; }
    }

    /// <summary>A log macro found in a source file (usages: position of the macro; declarations: position of the category).</summary>
    public sealed class ScannedLogSite
    {
        public LogSiteKind Kind { get; init; }
        public string Category { get; init; }
        public string Macro { get; init; }
        public string Verbosity { get; init; }
        public string Format { get; init; }
        public int Line { get; init; }
        public int Column { get; init; }
    }

    public sealed class UeMacroScanResult
    {
        public static readonly UeMacroScanResult Empty = new UeMacroScanResult();

        public List<ScannedTest> Tests { get; } = new List<ScannedTest>();
        public List<ScannedLogSite> LogSites { get; } = new List<ScannedLogSite>();
        public bool IsEmpty => Tests.Count == 0 && LogSites.Count == 0;
    }

    /// <summary>
    /// Text-level scanner for the UE macros the editor link features need, without the full parser:
    /// automation tests (<c>IMPLEMENT_SIMPLE_AUTOMATION_TEST</c>, <c>IMPLEMENT_COMPLEX_AUTOMATION_TEST</c>, the CUSTOM and
    /// NETWORKED variants, <c>BEGIN_DEFINE_SPEC</c>, <c>DEFINE_SPEC</c>; the name is the string argument) and log macros
    /// (<c>UE_LOG / UE_CLOG / UE_LOGFMT</c> with their format literal decoded, <c>DECLARE_LOG_CATEGORY_EXTERN / _CLASS</c>,
    /// <c>DEFINE_LOG_CATEGORY / _STATIC / _CLASS</c>). Comments, string and character literals are skipped; the macro
    /// definitions themselves (<c>#define UE_LOG(...)</c>) are not reported. Pure and allocation-light: a file without any
    /// of the macro names is rejected by one substring search per family.
    /// </summary>
    public static class UeMacroScanner
    {
        private enum MacroKind
        {
            Test,
            LogUsage,
            LogDecl,
        }

        private sealed class MacroSpec
        {
            public MacroKind Kind;
            public AutomationTestKind TestKind;
            public LogSiteKind SiteKind;
            /// <summary>Tests: index of the class / name arguments. Logs: index of category / verbosity / format.</summary>
            public int A, B, C;
        }

        private static readonly Dictionary<string, MacroSpec> Macros = new Dictionary<string, MacroSpec>(StringComparer.Ordinal)
        {
            ["IMPLEMENT_SIMPLE_AUTOMATION_TEST"] = new MacroSpec { Kind = MacroKind.Test, TestKind = AutomationTestKind.Simple, A = 0, B = 1 },
            ["IMPLEMENT_COMPLEX_AUTOMATION_TEST"] = new MacroSpec { Kind = MacroKind.Test, TestKind = AutomationTestKind.Complex, A = 0, B = 1 },
            ["IMPLEMENT_CUSTOM_SIMPLE_AUTOMATION_TEST"] = new MacroSpec { Kind = MacroKind.Test, TestKind = AutomationTestKind.Simple, A = 0, B = 2 },
            ["IMPLEMENT_CUSTOM_COMPLEX_AUTOMATION_TEST"] = new MacroSpec { Kind = MacroKind.Test, TestKind = AutomationTestKind.Complex, A = 0, B = 2 },
            ["IMPLEMENT_NETWORKED_AUTOMATION_TEST"] = new MacroSpec { Kind = MacroKind.Test, TestKind = AutomationTestKind.Simple, A = 0, B = 1 },
            ["BEGIN_DEFINE_SPEC"] = new MacroSpec { Kind = MacroKind.Test, TestKind = AutomationTestKind.Spec, A = 0, B = 1 },
            ["DEFINE_SPEC"] = new MacroSpec { Kind = MacroKind.Test, TestKind = AutomationTestKind.Spec, A = 0, B = 1 },
            ["UE_LOG"] = new MacroSpec { Kind = MacroKind.LogUsage, SiteKind = LogSiteKind.Usage, A = 0, B = 1, C = 2 },
            ["UE_CLOG"] = new MacroSpec { Kind = MacroKind.LogUsage, SiteKind = LogSiteKind.Usage, A = 1, B = 2, C = 3 },
            ["UE_LOGFMT"] = new MacroSpec { Kind = MacroKind.LogUsage, SiteKind = LogSiteKind.Usage, A = 0, B = 1, C = 2 },
            ["DECLARE_LOG_CATEGORY_EXTERN"] = new MacroSpec { Kind = MacroKind.LogDecl, SiteKind = LogSiteKind.Declaration, A = 0 },
            ["DECLARE_LOG_CATEGORY_CLASS"] = new MacroSpec { Kind = MacroKind.LogDecl, SiteKind = LogSiteKind.Declaration, A = 0 },
            ["DEFINE_LOG_CATEGORY"] = new MacroSpec { Kind = MacroKind.LogDecl, SiteKind = LogSiteKind.Definition, A = 0 },
            ["DEFINE_LOG_CATEGORY_STATIC"] = new MacroSpec { Kind = MacroKind.LogDecl, SiteKind = LogSiteKind.StaticDefinition, A = 0 },
            ["DEFINE_LOG_CATEGORY_CLASS"] = new MacroSpec { Kind = MacroKind.LogDecl, SiteKind = LogSiteKind.Definition, A = 1 },
        };

        private static readonly string[] Needles = { "AUTOMATION_TEST", "DEFINE_SPEC", "UE_LOG", "UE_CLOG", "LOG_CATEGORY" };

        /// <summary>Cheap pre-check: does the text mention any of the macro families at all?</summary>
        public static bool MightContain(string text)
        {
            if (string.IsNullOrEmpty(text)) return false;
            foreach (var n in Needles)
                if (text.IndexOf(n, StringComparison.Ordinal) >= 0) return true;
            return false;
        }

        public static UeMacroScanResult Scan(string text)
        {
            if (!MightContain(text)) return UeMacroScanResult.Empty;
            var result = new UeMacroScanResult();
            int n = text.Length;
            int i = 0, line = 0, lineStart = 0;
            bool atLineStart = true, afterDefine = false;
            while (i < n)
            {
                char c = text[i];
                if (c == '\n')
                {
                    line++;
                    lineStart = ++i;
                    atLineStart = true;
                    continue;
                }
                if (c == '/' && i + 1 < n && text[i + 1] == '/')
                {
                    while (i < n && text[i] != '\n') i++;
                    continue;
                }
                if (c == '/' && i + 1 < n && text[i + 1] == '*')
                {
                    i += 2;
                    while (i < n && !(text[i] == '*' && i + 1 < n && text[i + 1] == '/'))
                    {
                        if (text[i] == '\n')
                        {
                            line++;
                            lineStart = i + 1;
                        }
                        i++;
                    }
                    i = Math.Min(n, i + 2);
                    continue;
                }
                if (c == '"' || c == '\'')
                {
                    i = SkipQuoted(text, i, ref line, ref lineStart);
                    atLineStart = false;
                    continue;
                }
                if (c == '#' && atLineStart)
                {
                    int j = i + 1;
                    while (j < n && (text[j] == ' ' || text[j] == '\t')) j++;
                    atLineStart = false;
                    if (string.CompareOrdinal(text, j, "define", 0, 6) == 0 && j + 6 < n && !IsIdentChar(text[j + 6]))
                    {
                        afterDefine = true;
                        i = j + 6;
                    }
                    else i++;
                    continue;
                }
                if (IsIdentStart(c))
                {
                    int start = i;
                    while (i < n && IsIdentChar(text[i])) i++;
                    atLineStart = false;
                    if (afterDefine)
                    {
                        // The name right after #define is a macro definition, not a use.
                        afterDefine = false;
                        continue;
                    }
                    int len = i - start;
                    if (len < 6 || len > 40 || !(text[start] == 'U' || text[start] == 'I' || text[start] == 'B' || text[start] == 'D')) continue;
                    string name = text.Substring(start, len);
                    if (!Macros.TryGetValue(name, out var spec)) continue;
                    int k = i;
                    while (k < n && (text[k] == ' ' || text[k] == '\t' || text[k] == '\r' || text[k] == '\n')) k++;
                    if (k >= n || text[k] != '(') continue;
                    var args = SplitArguments(text, k, out _);
                    if (args == null) continue;
                    Report(result, spec, name, args, text, line, start - lineStart, start);
                    // Continue after the name (the arguments may contain nested macros, e.g. UE_LOG inside a lambda).
                    continue;
                }
                if (c != ' ' && c != '\t' && c != '\r') atLineStart = false;
                i++;
            }
            return result;
        }

        private static void Report(UeMacroScanResult result, MacroSpec spec, string name, List<(int Start, int End)> args, string text, int line, int column, int macroStart)
        {
            string Arg(int index) => index < args.Count ? text.Substring(args[index].Start, args[index].End - args[index].Start).Trim() : null;

            switch (spec.Kind)
            {
                case MacroKind.Test:
                {
                    string cls = Arg(spec.A);
                    var nameArg = ArgRange(spec.B);
                    string testName = nameArg.HasValue ? DecodeLiteral(text, nameArg.Value.Start, nameArg.Value.End) : null;
                    if (string.IsNullOrWhiteSpace(testName) || !IsIdentifier(cls)) return;
                    result.Tests.Add(new ScannedTest { Name = testName.Trim(), ClassName = cls, Macro = name, Kind = spec.TestKind, Line = line, Column = column });
                    return;
                }
                case MacroKind.LogUsage:
                {
                    string cat = Arg(spec.A);
                    if (!IsIdentifier(cat)) return;
                    var fmt = ArgRange(spec.C);
                    result.LogSites.Add(new ScannedLogSite
                    {
                        Kind = LogSiteKind.Usage, Category = cat, Macro = name, Verbosity = Arg(spec.B),
                        Format = fmt.HasValue ? DecodeLiteral(text, fmt.Value.Start, fmt.Value.End) : null, Line = line, Column = column,
                    });
                    return;
                }
                default:
                {
                    var a = ArgRange(spec.A);
                    if (!a.HasValue) return;
                    string cat = Arg(spec.A);
                    if (!IsIdentifier(cat)) return;
                    // Position of the category name itself (declarations are jump targets).
                    int p = a.Value.Start;
                    while (p < a.Value.End && char.IsWhiteSpace(text[p])) p++;
                    int l = line, ls = macroStart - column;
                    for (int q = macroStart; q < p; q++)
                        if (text[q] == '\n')
                        {
                            l++;
                            ls = q + 1;
                        }
                    result.LogSites.Add(new ScannedLogSite { Kind = spec.SiteKind, Category = cat, Macro = name, Line = l, Column = p - ls });
                    return;
                }
            }

            (int Start, int End)? ArgRange(int i) => i < args.Count ? args[i] : ((int, int)?)null;
        }

        /// <summary>
        /// Top-level arguments of the call whose '(' is at <paramref name="open"/> (ranges into the text, exclusive end).
        /// Null when the parentheses are not balanced before the end of the file (or the call is unreasonably long).
        /// </summary>
        public static List<(int Start, int End)> SplitArguments(string text, int open, out int end)
        {
            end = open;
            var args = new List<(int, int)>();
            int depth = 0, argStart = open + 1, n = text.Length, dummyLine = 0, dummyStart = 0;
            int limit = Math.Min(n, open + 64 * 1024);
            for (int i = open; i < limit;)
            {
                char c = text[i];
                if (c == '"' || c == '\'')
                {
                    i = SkipQuoted(text, i, ref dummyLine, ref dummyStart);
                    continue;
                }
                if (c == '/' && i + 1 < n && text[i + 1] == '/')
                {
                    while (i < n && text[i] != '\n') i++;
                    continue;
                }
                if (c == '/' && i + 1 < n && text[i + 1] == '*')
                {
                    int close = text.IndexOf("*/", i + 2, StringComparison.Ordinal);
                    i = close < 0 ? n : close + 2;
                    continue;
                }
                if (c == '(' || c == '[' || c == '{') depth++;
                else if (c == ')' || c == ']' || c == '}')
                {
                    depth--;
                    if (depth == 0)
                    {
                        if (c != ')') return null;
                        args.Add((argStart, i));
                        end = i + 1;
                        if (args.Count == 1 && text.Substring(args[0].Item1, args[0].Item2 - args[0].Item1).Trim().Length == 0) args.Clear();
                        return args;
                    }
                }
                else if (c == ',' && depth == 1)
                {
                    args.Add((argStart, i));
                    argStart = i + 1;
                }
                else if (c == ';' && depth <= 1) return null; // ran past the statement: not a call
                i++;
            }
            return null;
        }

        /// <summary>
        /// Decodes an argument that is only string literals (optionally wrapped: <c>TEXT("a")</c>, <c>TEXT("a") TEXT("b")</c>,
        /// <c>L"a" "b"</c>, <c>UTF8TEXT("a")</c>) into its text, with escapes resolved. Null when the argument is anything else.
        /// </summary>
        public static string DecodeLiteral(string text, int start, int end)
        {
            var sb = new StringBuilder();
            int i = start;
            bool any = false;
            int wrapDepth = 0;
            while (i < end)
            {
                char c = text[i];
                if (char.IsWhiteSpace(c))
                {
                    i++;
                    continue;
                }
                if (c == '/' && i + 1 < end && (text[i + 1] == '/' || text[i + 1] == '*'))
                {
                    if (text[i + 1] == '/')
                        while (i < end && text[i] != '\n') i++;
                    else
                    {
                        int close = text.IndexOf("*/", i + 2, StringComparison.Ordinal);
                        i = close < 0 || close >= end ? end : close + 2;
                    }
                    continue;
                }
                if (c == ')' && wrapDepth > 0)
                {
                    wrapDepth--;
                    i++;
                    continue;
                }
                if (IsIdentStart(c))
                {
                    int s = i;
                    while (i < end && IsIdentChar(text[i])) i++;
                    string id = text.Substring(s, i - s);
                    if (i < end && text[i] == '"' && (id == "L" || id == "u" || id == "U" || id == "u8")) continue; // prefix
                    if (id == "TEXT" || id == "UTF8TEXT" || id == "TEXTVIEW" || id == "ANSITEXTVIEW" || id == "UTF8TEXTVIEW" || id == "WIDETEXT")
                    {
                        while (i < end && char.IsWhiteSpace(text[i])) i++;
                        if (i < end && text[i] == '(')
                        {
                            wrapDepth++;
                            i++;
                            continue;
                        }
                    }
                    return null;
                }
                if (c == '"')
                {
                    if (!ReadString(text, ref i, end, sb)) return null;
                    any = true;
                    continue;
                }
                return null;
            }
            return any && wrapDepth == 0 ? sb.ToString() : null;
        }

        private static bool ReadString(string text, ref int i, int end, StringBuilder sb)
        {
            i++; // opening quote
            while (i < end)
            {
                char c = text[i];
                if (c == '"')
                {
                    i++;
                    return true;
                }
                if (c == '\n') return false;
                if (c != '\\')
                {
                    sb.Append(c);
                    i++;
                    continue;
                }
                if (i + 1 >= end) return false;
                char e = text[i + 1];
                i += 2;
                switch (e)
                {
                    case 'n': sb.Append('\n'); break;
                    case 't': sb.Append('\t'); break;
                    case 'r': sb.Append('\r'); break;
                    case '0' when i >= end || text[i] < '0' || text[i] > '7': sb.Append('\0'); break;
                    case 'a': sb.Append('\a'); break;
                    case 'b': sb.Append('\b'); break;
                    case 'f': sb.Append('\f'); break;
                    case 'v': sb.Append('\v'); break;
                    case '\\': sb.Append('\\'); break;
                    case '"': sb.Append('"'); break;
                    case '\'': sb.Append('\''); break;
                    case '?': sb.Append('?'); break;
                    case '\r':
                        if (i < end && text[i] == '\n') i++;
                        break; // line continuation
                    case '\n': break;
                    case 'x':
                    {
                        int s = i;
                        while (i < end && i - s < 4 && Uri.IsHexDigit(text[i])) i++;
                        if (i == s) return false;
                        sb.Append((char)int.Parse(text.Substring(s, i - s), NumberStyles.HexNumber, CultureInfo.InvariantCulture));
                        break;
                    }
                    case 'u':
                    case 'U':
                    {
                        int digits = e == 'u' ? 4 : 8;
                        if (i + digits > end) return false;
                        if (!int.TryParse(text.Substring(i, digits), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out int cp)) return false;
                        i += digits;
                        try
                        {
                            sb.Append(char.ConvertFromUtf32(cp));
                        }
                        catch (ArgumentOutOfRangeException)
                        {
                            return false;
                        }
                        break;
                    }
                    default:
                        if (e >= '0' && e <= '7')
                        {
                            int v = e - '0', d = 1;
                            while (i < end && d < 3 && text[i] >= '0' && text[i] <= '7')
                            {
                                v = v * 8 + (text[i] - '0');
                                i++;
                                d++;
                            }
                            sb.Append((char)v);
                        }
                        else sb.Append(e);
                        break;
                }
            }
            return false;
        }

        /// <summary>Skips a "..." or '...' literal (raw strings R"d(...)d" too); returns the index after it.</summary>
        private static int SkipQuoted(string text, int i, ref int line, ref int lineStart)
        {
            int n = text.Length;
            char q = text[i];
            if (q == '"' && i > 0 && text[i - 1] == 'R' && (i < 2 || !IsIdentChar(text[i - 2]) || text[i - 2] == 'L' || text[i - 2] == 'u' || text[i - 2] == 'U' || text[i - 2] == '8'))
            {
                int paren = text.IndexOf('(', i + 1);
                if (paren > i && paren - i <= 17)
                {
                    string close = ")" + text.Substring(i + 1, paren - i - 1) + "\"";
                    int endRaw = text.IndexOf(close, paren + 1, StringComparison.Ordinal);
                    int stop = endRaw < 0 ? n : endRaw + close.Length;
                    for (int k = i; k < stop; k++)
                        if (text[k] == '\n')
                        {
                            line++;
                            lineStart = k + 1;
                        }
                    return stop;
                }
            }
            i++;
            while (i < n)
            {
                char c = text[i];
                if (c == '\\')
                {
                    if (i + 1 < n && text[i + 1] == '\n')
                    {
                        line++;
                        lineStart = i + 2;
                    }
                    i += 2;
                    continue;
                }
                if (c == q) return i + 1;
                if (c == '\n') return i; // unterminated: stop at the line end
                i++;
            }
            return n;
        }

        private static bool IsIdentStart(char c) => c == '_' || (c >= 'A' && c <= 'Z') || (c >= 'a' && c <= 'z');

        private static bool IsIdentChar(char c) => IsIdentStart(c) || (c >= '0' && c <= '9');

        private static bool IsIdentifier(string s)
        {
            if (string.IsNullOrEmpty(s) || !IsIdentStart(s[0])) return false;
            foreach (char c in s) if (!IsIdentChar(c)) return false;
            return true;
        }
    }
}
