using System;
using System.Collections.Generic;
using System.Text;
using System.Text.RegularExpressions;

namespace UEToVS.Engine.IntelliSense
{
    /// <summary>One parsed parameter of a declaration's parameter text.</summary>
    public readonly struct ParameterText
    {
        public ParameterText(string type, string name, string defaultValue)
        {
            Type = type;
            Name = name;
            DefaultValue = defaultValue;
        }

        public string Type { get; }
        public string Name { get; }
        public string DefaultValue { get; }

        public override string ToString() => Type + " " + Name + (DefaultValue != null ? " = " + DefaultValue : string.Empty);
    }

    /// <summary>
    /// Text-level knowledge of what UHT accepts, shared by the UE squiggle rules (UE0004…UE0010). Only rules that UHT is known to
    /// enforce for every engine version are here; anything else is left alone. Pure.
    /// </summary>
    public static class UhtRules
    {
        /// <summary>UFUNCTION specifiers whose function body UHT generates (the user writes <c>Name_Implementation</c> or nothing).</summary>
        public static readonly string[] GeneratedBodySpecifiers = { "Server", "Client", "NetMulticast", "BlueprintNativeEvent", "BlueprintImplementableEvent" };

        /// <summary>Wrappers UHT does not understand: "Unrecognized type".</summary>
        private static readonly string[] UnsupportedHeads = { "TSharedPtr", "TSharedRef", "TWeakPtr", "TUniquePtr", "TFunction", "TFunctionRef" };

        /// <summary>Numeric types that exist as properties but are not supported by Blueprint (UHT: "Type 'X' is not supported by blueprint").</summary>
        private static readonly HashSet<string> NonBlueprintNumbers = new HashSet<string>(StringComparer.Ordinal) { "int8", "int16", "uint16", "uint32", "uint64" };

        /// <summary>Pointer to a struct (F-prefixed, UHT enforces U/A prefixes for objects) or to a builtin: "Inappropriate '*'".</summary>
        private static readonly Regex ForbiddenPointer = new Regex(
            @"^(?:F[A-Z][A-Za-z0-9_]*|bool|int8|int16|int32|int64|uint8|uint16|uint32|uint64|int|float|double)\s*\*\s*&?$",
            RegexOptions.Compiled | RegexOptions.CultureInvariant);

        /// <summary>Top-level items of a specifier list (<c>Server, Reliable, meta=(A, B), Category="X, Y"</c>): key (before '=') and value.</summary>
        public static List<KeyValuePair<string, string>> Specifiers(string text)
        {
            var list = new List<KeyValuePair<string, string>>();
            if (string.IsNullOrWhiteSpace(text)) return list;
            foreach (var item in SplitTopLevel(text))
            {
                string s = item.Trim();
                if (s.Length == 0) continue;
                int eq = IndexOfTopLevel(s, '=');
                string key = (eq < 0 ? s : s.Substring(0, eq)).Trim();
                string value = eq < 0 ? null : s.Substring(eq + 1).Trim().Trim('"').Trim();
                list.Add(new KeyValuePair<string, string>(key, value));
            }
            return list;
        }

        public static bool HasKey(List<KeyValuePair<string, string>> specs, string key)
        {
            foreach (var kv in specs)
                if (string.Equals(kv.Key, key, StringComparison.OrdinalIgnoreCase)) return true;
            return false;
        }

        public static string Value(List<KeyValuePair<string, string>> specs, string key)
        {
            foreach (var kv in specs)
                if (string.Equals(kv.Key, key, StringComparison.OrdinalIgnoreCase)) return kv.Value;
            return null;
        }

        /// <summary>The specifier that makes UHT generate the function body, or null.</summary>
        public static string GeneratedBodySpecifier(List<KeyValuePair<string, string>> specs)
        {
            foreach (var w in GeneratedBodySpecifiers)
                if (HasKey(specs, w)) return w;
            return null;
        }

        /// <summary>Splits a parameter list (<c>int32 A, TMap&lt;K, V&gt; B = {}, const FVector&amp; C</c>) into type / name / default.</summary>
        public static List<ParameterText> Parameters(string text)
        {
            var list = new List<ParameterText>();
            if (string.IsNullOrWhiteSpace(text)) return list;
            foreach (var raw in SplitTopLevel(text))
            {
                string p = raw.Trim();
                if (p.Length == 0 || p == "void" || p == "...") continue;
                string def = null;
                int eq = IndexOfTopLevel(p, '=');
                if (eq >= 0)
                {
                    def = p.Substring(eq + 1).Trim();
                    p = p.Substring(0, eq).Trim();
                }
                int e = p.Length;
                int b = e;
                while (b > 0 && (char.IsLetterOrDigit(p[b - 1]) || p[b - 1] == '_')) b--;
                string name = b < e ? p.Substring(b) : null;
                string type = name == null ? p : p.Substring(0, b).Trim();
                // "int32" alone (unnamed parameter) or "const FVector&" without name
                if (name != null && (type.Length == 0 || type == "const" || type == "unsigned" || type == "signed" || type == "struct" || type == "class"))
                {
                    type = p;
                    name = null;
                }
                list.Add(new ParameterText(Normalize(type), name, def));
            }
            return list;
        }

        /// <summary>Collapses whitespace and drops <c>const</c>, <c>class</c>, <c>struct</c>, <c>enum</c> keywords.</summary>
        public static string Normalize(string type)
        {
            if (string.IsNullOrEmpty(type)) return string.Empty;
            var sb = new StringBuilder(type.Length);
            int i = 0;
            while (i < type.Length)
            {
                char c = type[i];
                if (char.IsLetter(c) || c == '_')
                {
                    int s = i;
                    while (i < type.Length && (char.IsLetterOrDigit(type[i]) || type[i] == '_')) i++;
                    string w = type.Substring(s, i - s);
                    if (w == "const" || w == "class" || w == "struct" || w == "enum" || w == "volatile") continue;
                    if (sb.Length > 0 && (char.IsLetterOrDigit(sb[sb.Length - 1]) || sb[sb.Length - 1] == '_')) sb.Append(' ');
                    sb.Append(w);
                    continue;
                }
                if (!char.IsWhiteSpace(c)) sb.Append(c);
                i++;
            }
            return sb.ToString();
        }

        /// <summary>A type UHT cannot parse at all for UPROPERTY / UFUNCTION parameters: "pointer", "unsupported", or null when fine / unknown.</summary>
        public static string ReflectedTypeProblem(string type)
        {
            string t = Normalize(type);
            if (t.Length == 0) return null;
            if (t.StartsWith("std::", StringComparison.Ordinal) || t.StartsWith("::std::", StringComparison.Ordinal)) return "unsupported";
            foreach (var h in UnsupportedHeads)
                if (t.StartsWith(h + "<", StringComparison.Ordinal) || t == h) return "unsupported";
            if (ForbiddenPointer.IsMatch(t)) return "pointer";
            return null;
        }

        /// <summary>A parameter type BlueprintCallable / BlueprintPure functions cannot use (int8, int16, uint16, uint32, uint64 by value or reference).</summary>
        public static bool IsNonBlueprintNumber(string type)
        {
            string t = Normalize(type).TrimEnd('&');
            return NonBlueprintNumbers.Contains(t);
        }

        private static IEnumerable<string> SplitTopLevel(string text)
        {
            int depth = 0, start = 0;
            bool quote = false;
            for (int i = 0; i < text.Length; i++)
            {
                char c = text[i];
                if (c == '"') quote = !quote;
                if (quote) continue;
                if (c == '(' || c == '<' || c == '[' || c == '{') depth++;
                else if ((c == ')' || c == '>' || c == ']' || c == '}') && depth > 0) depth--;
                else if (c == ',' && depth == 0)
                {
                    yield return text.Substring(start, i - start);
                    start = i + 1;
                }
            }
            yield return text.Substring(start);
        }

        private static int IndexOfTopLevel(string text, char wanted)
        {
            int depth = 0;
            bool quote = false;
            for (int i = 0; i < text.Length; i++)
            {
                char c = text[i];
                if (c == '"') quote = !quote;
                if (quote) continue;
                if (c == '(' || c == '<' || c == '[' || c == '{') depth++;
                else if ((c == ')' || c == '>' || c == ']' || c == '}') && depth > 0) depth--;
                else if (c == wanted && depth == 0) return i;
            }
            return -1;
        }

        // ------------------------------------------------------------ string literals

        /// <summary>A complete gameplay tag ("A.B.C"): letters, digits, '_' and single dots, no leading / trailing dot.</summary>
        public static bool IsPlainTag(string s)
        {
            if (string.IsNullOrEmpty(s) || s[0] == '.' || s[s.Length - 1] == '.') return false;
            for (int i = 0; i < s.Length; i++)
            {
                char c = s[i];
                if (c == '.')
                {
                    if (s[i - 1] == '.') return false;
                    continue;
                }
                if (!(char.IsLetterOrDigit(c) || c == '_')) return false;
            }
            return true;
        }

        private static readonly Regex AssetPathRegex = new Regex(
            @"^/Game(?:/[A-Za-z0-9_\- ]+)+(?:\.[A-Za-z0-9_\-]+)?(?::[A-Za-z0-9_\-.]+)?$", RegexOptions.Compiled | RegexOptions.CultureInvariant);

        /// <summary>
        /// A complete /Game/ asset reference ("/Game/A/B", "/Game/A/B.B", "/Game/A/B.B_C", "/Game/A/B.B:Sub"); returns the package
        /// path ("/Game/A/B"), or null for anything else (format strings, folders ending in '/', other mounts).
        /// </summary>
        public static string GamePackagePath(string s)
        {
            if (string.IsNullOrEmpty(s) || !AssetPathRegex.IsMatch(s)) return null;
            int cut = s.Length;
            int dot = s.IndexOf('.');
            int colon = s.IndexOf(':');
            if (dot >= 0) cut = Math.Min(cut, dot);
            if (colon >= 0) cut = Math.Min(cut, colon);
            string pkg = s.Substring(0, cut).TrimEnd();
            return pkg.Length > "/Game/".Length ? pkg : null;
        }
    }
}
