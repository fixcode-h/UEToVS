using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;

namespace UEToVS.Engine.Navigation
{
    /// <summary>One place where a function is bound to a delegate, input action or timer.</summary>
    public sealed class DelegateBinding
    {
        public string File { get; set; }

        /// <summary>0-based.</summary>
        public int Line { get; set; }

        /// <summary>0-based column of the bind call.</summary>
        public int Column { get; set; }

        /// <summary>AddDynamic, BindUObject, BindUFunction, BindAction, SetTimer...</summary>
        public string Method { get; set; }

        /// <summary>dynamic (UFUNCTION by member pointer), native (raw/UObject/SP...), ufunction_name (by name string), input, timer.</summary>
        public string Kind { get; set; }

        /// <summary>The delegate expression in front of the call (OnClicked, Button->OnClicked...), when on the same line.</summary>
        public string Delegate { get; set; }

        /// <summary>How the function is named there: &amp;UFoo::Bar, "Bar", GET_FUNCTION_NAME_CHECKED(UFoo, Bar).</summary>
        public string Target { get; set; }

        /// <summary>Class named in the member pointer / GET_FUNCTION_NAME_CHECKED, or null (name string).</summary>
        public string TargetClass { get; set; }

        public string Text { get; set; }
    }

    /// <summary>
    /// Recognises delegate/input/timer bindings on source lines (shared by the call hierarchy and the MCP tool
    /// find_delegate_bindings). Pure text work over the lines that the index already
    /// points to (identifier occurrences of the function, or files that call BindUFunction & co.); no parsing of files.
    /// </summary>
    public static class DelegateBindings
    {
        private static readonly Dictionary<string, string> MethodKinds = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["AddDynamic"] = "dynamic", ["AddUniqueDynamic"] = "dynamic", ["BindDynamic"] = "dynamic", ["RemoveDynamic"] = "dynamic",
            ["AddUObject"] = "native", ["BindUObject"] = "native", ["CreateUObject"] = "native",
            ["AddRaw"] = "native", ["BindRaw"] = "native", ["CreateRaw"] = "native",
            ["AddSP"] = "native", ["BindSP"] = "native", ["CreateSP"] = "native",
            ["AddThreadSafeSP"] = "native", ["BindThreadSafeSP"] = "native", ["CreateThreadSafeSP"] = "native",
            ["AddStatic"] = "native", ["BindStatic"] = "native", ["CreateStatic"] = "native",
            ["AddWeakLambda"] = "native", ["BindWeakLambda"] = "native",
            ["AddUFunction"] = "ufunction_name", ["BindUFunction"] = "ufunction_name", ["CreateUFunction"] = "ufunction_name",
            ["BindAction"] = "input", ["BindAxis"] = "input", ["BindKey"] = "input", ["BindTouch"] = "input", ["BindVectorAxis"] = "input",
            ["SetTimer"] = "timer", ["SetTimerForNextTick"] = "timer",
        };

        /// <summary>Identifiers whose occurrences mark a file as binding functions by name (for the string-name scan).</summary>
        public static readonly string[] ByNameMethods = { "BindUFunction", "AddUFunction", "CreateUFunction" };

        private static readonly Regex Call = new Regex(@"(?<delegate>[\w\]\)]+(?:\s*(?:\.|->|::)\s*[\w\]\)]+)*)?\s*(?:\.|->)\s*(?<method>\w+)\s*\(|\b(?<method>\w+)\s*\(",
            RegexOptions.Compiled | RegexOptions.CultureInvariant);

        /// <summary>Methods that bind (keys of the kind table).</summary>
        public static bool IsBindMethod(string name) => name != null && MethodKinds.ContainsKey(name);

        /// <summary>
        /// The binding on <paramref name="line"/> (with up to two <paramref name="previousLines"/> for calls split over lines)
        /// that refers to <paramref name="function"/> by member pointer, GET_FUNCTION_NAME_CHECKED or name string; null when the
        /// line only mentions the function some other way (a call, a declaration...).
        /// </summary>
        public static DelegateBinding Match(string line, string function, IReadOnlyList<string> previousLines = null)
        {
            if (string.IsNullOrEmpty(line) || string.IsNullOrEmpty(function)) return null;
            string fn = Regex.Escape(function);
            var target = Regex.Match(line, @"&\s*(?:(?<cls>[\w:]+)\s*::\s*)" + fn + @"\b");
            string targetClass = null, targetText = null;
            if (target.Success)
            {
                targetClass = LastSegment(target.Groups["cls"].Value);
                targetText = Regex.Replace(target.Value, @"\s+", "");
            }
            else
            {
                var checkedName = Regex.Match(line, @"GET_FUNCTION_NAME_CHECKED\s*\(\s*(?<cls>[\w:]+)\s*,\s*" + fn + @"\s*\)");
                if (checkedName.Success)
                {
                    targetClass = LastSegment(checkedName.Groups["cls"].Value);
                    targetText = Regex.Replace(checkedName.Value, @"\s+", " ");
                }
                else
                {
                    var str = Regex.Match(line, @"""" + fn + @"""");
                    if (!str.Success) return null;
                    targetText = "\"" + function + "\"";
                }
            }

            // the bind call: on this line before the target, else on one of the previous lines (multi-line call)
            var found = LastBindCall(line);
            string text = line;
            if (found == null && previousLines != null)
            {
                for (int i = previousLines.Count - 1; i >= 0 && found == null; i--)
                {
                    found = LastBindCall(previousLines[i]);
                    if (found != null) text = previousLines[i].TrimEnd() + " " + line.Trim();
                }
            }
            if (found == null) return null;
            var (method, del, col) = found.Value;
            string kind = MethodKinds[method];
            if (kind == "ufunction_name" && targetClass == null && targetText.StartsWith("&", StringComparison.Ordinal)) kind = "native";
            return new DelegateBinding
            {
                Method = method,
                Kind = kind,
                Delegate = del,
                Target = targetText,
                TargetClass = targetClass,
                Column = col,
                Text = Trim(text),
            };
        }

        private static (string Method, string Delegate, int Column)? LastBindCall(string line)
        {
            (string, string, int)? best = null;
            foreach (Match m in Call.Matches(line))
            {
                string method = m.Groups["method"].Value;
                if (!IsBindMethod(method)) continue;
                string del = m.Groups["delegate"].Success ? Regex.Replace(m.Groups["delegate"].Value, @"\s+", "") : null;
                if (string.IsNullOrEmpty(del)) del = null;
                best = (method, del, m.Groups["method"].Index);
            }
            return best;
        }

        private static string LastSegment(string qualified)
        {
            if (string.IsNullOrEmpty(qualified)) return null;
            int i = qualified.LastIndexOf("::", StringComparison.Ordinal);
            return i >= 0 ? qualified.Substring(i + 2) : qualified;
        }

        private static string Trim(string line)
        {
            line = (line ?? string.Empty).Replace('\t', ' ').Trim();
            return line.Length > 240 ? line.Substring(0, 239) + "…" : line;
        }
    }
}
