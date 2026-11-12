using System;
using System.Collections.Generic;

namespace UEToVS.Core.Unreal
{
    /// <summary>What a string literal holds, judged from the call it is an argument of (design §7.6 string context completion).</summary>
    public enum StringLiteralKind : byte
    {
        None = 0,

        /// <summary>RequestGameplayTag("…"), UE_DEFINE_GAMEPLAY_TAG(X, "…"), meta=(Categories="…").</summary>
        GameplayTag = 1,

        /// <summary>FObjectFinder&lt;T&gt;("…"), FSoftObjectPath("…"), LoadObject&lt;T&gt;(nullptr, "…"), or a literal starting with "/Mount/".</summary>
        AssetPath = 2,

        /// <summary>BindUFunction(Obj, "…"), AddUFunction, CreateUFunction, FindFunction("…").</summary>
        UFunctionName = 3,

        /// <summary>GConfig-&gt;GetString("Section", …): first argument.</summary>
        IniSection = 4,

        /// <summary>GConfig-&gt;GetString(…, "Key", …): second argument.</summary>
        IniKey = 5,
    }

    /// <summary>Result of <see cref="StringLiteralContext.Analyze"/>. Columns are character indices in the analysed line.</summary>
    public sealed class StringLiteralSite
    {
        /// <summary>The caret is inside a string literal (whatever its kind).</summary>
        public bool InString { get; set; }

        public StringLiteralKind Kind { get; set; }

        /// <summary>Column right after the opening quote.</summary>
        public int ContentStart { get; set; }

        /// <summary>Column of the closing quote (or the line end when the literal is not closed yet).</summary>
        public int ContentEnd { get; set; }

        /// <summary>Literal text from the opening quote to the caret.</summary>
        public string Typed { get; set; } = string.Empty;

        /// <summary>The part of the literal a completion item replaces (tag segment, path segment, function name, ini section / key).</summary>
        public int FragmentStart { get; set; }

        public int FragmentLength { get; set; }

        /// <summary>Call the literal is an argument of ("RequestGameplayTag", "FObjectFinder", "BindUFunction", "GetString"...).</summary>
        public string Callee { get; set; }

        /// <summary>Column of the callee name (start).</summary>
        public int CalleeColumn { get; set; } = -1;

        public int ArgumentIndex { get; set; }

        /// <summary>Template argument of the callee (FObjectFinder&lt;UStaticMesh&gt; → "UStaticMesh").</summary>
        public string TemplateArgument { get; set; }

        /// <summary>Asset path wanted as a class (FClassFinder / LoadClass / TSoftClassPtr): blueprint items get the "_C" suffix.</summary>
        public bool WantsClass { get; set; }

        /// <summary>Column of the '.' / '-&gt;' before the callee (member call), else -1.</summary>
        public int ReceiverOperatorColumn { get; set; } = -1;

        /// <summary>Text of a simple receiver (identifier before '.' / '-&gt;', e.g. "GConfig").</summary>
        public string Receiver { get; set; }

        /// <summary>BindUFunction / AddUFunction: column range of the first argument (the target object), else -1.</summary>
        public int TargetStart { get; set; } = -1;

        public int TargetEnd { get; set; } = -1;

        /// <summary>Ini key: literal content of the section argument.</summary>
        public string IniSection { get; set; }

        /// <summary>Ini: the config file variable passed later in the call (GGameIni, GEngineIni...), if on the line.</summary>
        public string IniFile { get; set; }

        public override string ToString() => InString ? Kind + " '" + Typed + "' callee=" + Callee + "#" + ArgumentIndex : "code";
    }

    /// <summary>
    /// Line-local analysis of a caret inside a string literal: which UE API the literal is an argument of, and which part a
    /// completion item replaces. Shared by the host (whether to open a completion session, the applicable span) and the
    /// engine (what to list), so both agree on the span. Pure; tolerant of unfinished code ("TEXT(\"Abil" with no closing quote).
    /// </summary>
    public static class StringLiteralContext
    {
        /// <summary>Wrappers around a literal that do not change what it means: TEXT("…"), FName("…"), FString(TEXT("…")).</summary>
        private static readonly HashSet<string> Wrappers = new HashSet<string>(StringComparer.Ordinal)
        {
            "TEXT", "FName", "FString", "TEXTVIEW", "FStringView", "FText", "FromString", "INVTEXT", "ANSI_TO_TCHAR", "UTF8_TO_TCHAR",
        };

        private static readonly Dictionary<string, int> TagCalls = new Dictionary<string, int>(StringComparer.Ordinal)
        {
            ["RequestGameplayTag"] = 0,
            ["AddNativeGameplayTag"] = 0,
            ["FindGameplayTagNode"] = 0,
            ["UE_DEFINE_GAMEPLAY_TAG"] = 1,
            ["UE_DEFINE_GAMEPLAY_TAG_COMMENT"] = 1,
            ["UE_DEFINE_GAMEPLAY_TAG_STATIC"] = 1,
        };

        private static readonly Dictionary<string, int> AssetCalls = new Dictionary<string, int>(StringComparer.Ordinal)
        {
            ["FObjectFinder"] = 0,
            ["FObjectFinderOptional"] = 0,
            ["FClassFinder"] = 0,
            ["FSoftObjectPath"] = 0,
            ["FSoftClassPath"] = 0,
            ["FStringAssetReference"] = 0,
            ["TSoftObjectPtr"] = 0,
            ["TSoftClassPtr"] = 0,
            ["LoadObject"] = 1,
            ["LoadClass"] = 1,
            ["FindObject"] = 1,
            ["StaticLoadObject"] = 2,
            ["StaticLoadClass"] = 2,
        };

        private static readonly HashSet<string> ClassCalls = new HashSet<string>(StringComparer.Ordinal)
        {
            "FClassFinder", "FSoftClassPath", "TSoftClassPtr", "LoadClass", "StaticLoadClass",
        };

        private static readonly Dictionary<string, int> UFunctionCalls = new Dictionary<string, int>(StringComparer.Ordinal)
        {
            ["BindUFunction"] = 1,
            ["AddUFunction"] = 1,
            ["CreateUFunction"] = 1,
            ["FindFunction"] = 0,
            ["FindFunctionChecked"] = 0,
        };

        private static bool IsIdent(char c) => c == '_' || char.IsLetterOrDigit(c);

        /// <summary>Analyses <paramref name="line"/> with the caret at column <paramref name="caret"/>.</summary>
        public static StringLiteralSite Analyze(string line, int caret)
        {
            var site = new StringLiteralSite();
            if (string.IsNullOrEmpty(line)) return site;
            caret = Math.Max(0, Math.Min(caret, line.Length));
            int open = OpeningQuote(line, caret);
            if (open < 0) return site;
            site.InString = true;
            site.ContentStart = open + 1;
            int close = ClosingQuote(line, open + 1);
            site.ContentEnd = close < 0 ? line.Length : close;
            site.Typed = line.Substring(open + 1, caret - open - 1);

            // the argument the literal stands for: strip L / u8 prefixes and TEXT( / FName( wrappers
            int argStart = open;
            while (argStart > 0 && IsIdent(line[argStart - 1])) argStart--; // L"…", u8"…", TEXT is separate
            while (true)
            {
                int p = SkipSpaceBack(line, argStart - 1);
                if (p < 0 || line[p] != '(') break;
                int nameEnd = SkipSpaceBack(line, p - 1);
                int nameStart = nameEnd;
                while (nameStart >= 0 && IsIdent(line[nameStart])) nameStart--;
                nameStart++;
                if (nameStart > nameEnd) break;
                string name = line.Substring(nameStart, nameEnd - nameStart + 1);
                if (!Wrappers.Contains(name)) break;
                argStart = nameStart;
                // FText::FromString(…)
                int q = SkipSpaceBack(line, nameStart - 1);
                if (q >= 1 && line[q] == ':' && line[q - 1] == ':')
                {
                    int qs = q - 2;
                    while (qs >= 0 && IsIdent(line[qs])) qs--;
                    argStart = qs + 1;
                }
            }

            int before = SkipSpaceBack(line, argStart - 1);
            if (before >= 0 && line[before] == '=')
            {
                // meta = (Categories = "…")
                int ne = SkipSpaceBack(line, before - 1);
                int ns = ne;
                while (ns >= 0 && IsIdent(line[ns])) ns--;
                string key = ne > ns ? line.Substring(ns + 1, ne - ns) : string.Empty;
                if (string.Equals(key, "Categories", StringComparison.OrdinalIgnoreCase) || string.Equals(key, "GameplayTagFilter", StringComparison.OrdinalIgnoreCase))
                {
                    site.Kind = StringLiteralKind.GameplayTag;
                    site.Callee = key;
                    site.CalleeColumn = ns + 1;
                }
            }
            else if (before >= 0 && (line[before] == '(' || line[before] == ','))
            {
                Call(line, before, site);
            }

            if (site.Kind == StringLiteralKind.None && site.Typed.Length >= 1 && site.Typed[0] == '/' &&
                !site.Typed.StartsWith("/Script/", StringComparison.OrdinalIgnoreCase) && site.Typed.IndexOf('/', 1) > 1)
            {
                // "/Game/…" anywhere: an asset path (the engine checks the mount point)
                site.Kind = StringLiteralKind.AssetPath;
            }
            Fragment(line, caret, site);
            return site;
        }

        /// <summary>The call around the argument whose preceding delimiter is at <paramref name="delimiter"/>.</summary>
        private static void Call(string line, int delimiter, StringLiteralSite site)
        {
            int argIndex = 0;
            int depth = 0;
            int firstArgEnd = -1;
            int open = -1;
            for (int i = delimiter; i >= 0; i--)
            {
                char c = line[i];
                if (c == '"' || c == '\'')
                {
                    // skip a literal backwards
                    int j = i - 1;
                    while (j >= 0 && !(line[j] == c && (j == 0 || line[j - 1] != '\\'))) j--;
                    if (j < 0) return;
                    i = j;
                    continue;
                }
                if (c == ')' || c == ']' || c == '}') depth++;
                else if (c == '(' || c == '[' || c == '{')
                {
                    if (depth == 0)
                    {
                        if (c != '(') return;
                        open = i;
                        break;
                    }
                    depth--;
                }
                else if (c == ',' && depth == 0)
                {
                    argIndex++;
                    firstArgEnd = i; // the last one seen scanning back is the first argument's end
                }
                else if (c == ';' && depth == 0) return;
            }
            if (open < 0) return;
            site.ArgumentIndex = argIndex;
            if (argIndex > 0)
            {
                site.TargetStart = SkipSpaceForward(line, open + 1);
                site.TargetEnd = SkipSpaceBack(line, firstArgEnd - 1) + 1;
            }

            // callee: Name( / Name<T>( / Type<T> Var( / Type Var(
            int p = SkipSpaceBack(line, open - 1);
            if (p < 0) return;
            string template = null;
            string name;
            int nameStart;
            if (line[p] == '>')
            {
                int lt = MatchAngleBack(line, p);
                if (lt < 0) return;
                template = line.Substring(lt + 1, p - lt - 1).Trim();
                int ne = SkipSpaceBack(line, lt - 1);
                nameStart = IdentStart(line, ne);
                if (nameStart < 0) return;
                name = line.Substring(nameStart, ne - nameStart + 1);
            }
            else
            {
                if (!IsIdent(line[p])) return;
                nameStart = IdentStart(line, p);
                name = line.Substring(nameStart, p - nameStart + 1);
                // a declaration: "FObjectFinder<UStaticMesh> Finder(" / "FSoftObjectPath Path("
                int q = SkipSpaceBack(line, nameStart - 1);
                if (q >= 0 && line[q] == '>')
                {
                    int lt = MatchAngleBack(line, q);
                    if (lt >= 0)
                    {
                        int ne = SkipSpaceBack(line, lt - 1);
                        int ns = IdentStart(line, ne);
                        if (ns >= 0)
                        {
                            template = line.Substring(lt + 1, q - lt - 1).Trim();
                            name = line.Substring(ns, ne - ns + 1);
                            nameStart = ns;
                        }
                    }
                }
                else if (q >= 0 && IsIdent(line[q]))
                {
                    int ns = IdentStart(line, q);
                    string type = line.Substring(ns, q - ns + 1);
                    if (AssetCalls.ContainsKey(type))
                    {
                        name = type;
                        nameStart = ns;
                    }
                }
            }
            site.Callee = name;
            site.CalleeColumn = nameStart;
            site.TemplateArgument = string.IsNullOrEmpty(template) ? null : template;

            // receiver: "GConfig->GetString(" / "Delegate.BindUFunction("
            int r = SkipSpaceBack(line, nameStart - 1);
            if (r >= 1 && line[r] == '>' && line[r - 1] == '-') site.ReceiverOperatorColumn = r - 1;
            else if (r >= 0 && line[r] == '.') site.ReceiverOperatorColumn = r;
            if (site.ReceiverOperatorColumn >= 0)
            {
                int re = SkipSpaceBack(line, site.ReceiverOperatorColumn - 1);
                int rs = IdentStart(line, re);
                if (rs >= 0) site.Receiver = line.Substring(rs, re - rs + 1);
            }

            if (TagCalls.TryGetValue(name, out int tagArg) && tagArg == argIndex) site.Kind = StringLiteralKind.GameplayTag;
            else if (AssetCalls.TryGetValue(name, out int assetArg) && assetArg == argIndex)
            {
                site.Kind = StringLiteralKind.AssetPath;
                site.WantsClass = ClassCalls.Contains(name);
            }
            else if (UFunctionCalls.TryGetValue(name, out int fnArg) && fnArg == argIndex) site.Kind = StringLiteralKind.UFunctionName;
            else if (site.Receiver == "GConfig" && (name.StartsWith("Get", StringComparison.Ordinal) || name.StartsWith("Set", StringComparison.Ordinal) ||
                                                    name.StartsWith("Remove", StringComparison.Ordinal) || name == "EmptySection"))
            {
                if (argIndex == 0) site.Kind = StringLiteralKind.IniSection;
                else if (argIndex == 1 && !name.StartsWith("GetSection", StringComparison.Ordinal) && name != "EmptySection")
                {
                    site.Kind = StringLiteralKind.IniKey;
                    site.IniSection = FirstLiteral(line, open + 1);
                }
                if (site.Kind != StringLiteralKind.None) site.IniFile = IniFileAfter(line, open);
            }
        }

        /// <summary>Replace range: the tag segment / path segment / identifier, or the whole literal for ini sections and keys.</summary>
        private static void Fragment(string line, int caret, StringLiteralSite site)
        {
            int s = caret, e = caret;
            switch (site.Kind)
            {
                case StringLiteralKind.IniSection:
                case StringLiteralKind.IniKey:
                    s = site.ContentStart;
                    e = site.ContentEnd;
                    break;
                case StringLiteralKind.AssetPath:
                    while (s > site.ContentStart && !IsPathStop(line[s - 1])) s--;
                    while (e < site.ContentEnd && !IsPathStop(line[e])) e++;
                    break;
                default:
                    while (s > site.ContentStart && IsIdent(line[s - 1])) s--;
                    while (e < site.ContentEnd && IsIdent(line[e])) e++;
                    break;
            }
            site.FragmentStart = s;
            site.FragmentLength = Math.Max(0, e - s);
        }

        private static bool IsPathStop(char c) => c == '/' || c == '.' || c == '"' || c == '\'' || c == ' ' || c == '\\';

        /// <summary>Opening quote of the string literal the caret is in (single-line lexical scan), or -1.</summary>
        private static int OpeningQuote(string line, int caret)
        {
            int open = -1;
            bool inString = false, inChar = false;
            for (int i = 0; i < caret; i++)
            {
                char c = line[i];
                if (inString || inChar)
                {
                    if (c == '\\') { i++; continue; }
                    if (inString && c == '"') { inString = false; open = -1; }
                    else if (inChar && c == '\'') inChar = false;
                    continue;
                }
                if (c == '/' && i + 1 < line.Length && line[i + 1] == '/') return -1;
                if (c == '/' && i + 1 < line.Length && line[i + 1] == '*')
                {
                    int end = line.IndexOf("*/", i + 2, StringComparison.Ordinal);
                    if (end < 0 || end + 2 > caret) return -1;
                    i = end + 1;
                    continue;
                }
                if (c == '"')
                {
                    // raw strings R"(…)" are not analysed
                    if (i > 0 && line[i - 1] == 'R' && (i < 2 || !IsIdent(line[i - 2]) || line[i - 2] == '8' || line[i - 2] == 'L' || line[i - 2] == 'u' || line[i - 2] == 'U')) return -1;
                    inString = true;
                    open = i;
                }
                else if (c == '\'' && !(i > 0 && char.IsLetterOrDigit(line[i - 1]) && i + 1 < line.Length && char.IsDigit(line[i + 1]))) inChar = true;
            }
            return inString ? open : -1;
        }

        private static int ClosingQuote(string line, int from)
        {
            for (int i = from; i < line.Length; i++)
            {
                if (line[i] == '\\') { i++; continue; }
                if (line[i] == '"') return i;
            }
            return -1;
        }

        private static int SkipSpaceBack(string line, int i)
        {
            while (i >= 0 && char.IsWhiteSpace(line[i])) i--;
            return i;
        }

        private static int SkipSpaceForward(string line, int i)
        {
            while (i < line.Length && char.IsWhiteSpace(line[i])) i++;
            return i;
        }

        private static int IdentStart(string line, int end)
        {
            if (end < 0 || end >= line.Length || !IsIdent(line[end])) return -1;
            int s = end;
            while (s > 0 && IsIdent(line[s - 1])) s--;
            return s;
        }

        private static int MatchAngleBack(string line, int gt)
        {
            int depth = 0;
            for (int i = gt; i >= 0; i--)
            {
                if (line[i] == '>') depth++;
                else if (line[i] == '<' && --depth == 0) return i;
                else if (line[i] == ';' || line[i] == '(' || line[i] == ')') return -1;
            }
            return -1;
        }

        /// <summary>Content of the first string literal at or after <paramref name="from"/> (the section argument).</summary>
        private static string FirstLiteral(string line, int from)
        {
            int q = line.IndexOf('"', from);
            if (q < 0) return null;
            int e = ClosingQuote(line, q + 1);
            return e < 0 ? null : line.Substring(q + 1, e - q - 1);
        }

        /// <summary>A G*Ini identifier (GGameIni, GEngineIni...) after the call's '(' on the line.</summary>
        private static string IniFileAfter(string line, int open)
        {
            for (int i = open + 1; i < line.Length; i++)
            {
                if (line[i] != 'G' || (i > 0 && IsIdent(line[i - 1]))) continue;
                int e = i;
                while (e < line.Length && IsIdent(line[e])) e++;
                string w = line.Substring(i, e - i);
                if (w.Length > 4 && w.EndsWith("Ini", StringComparison.Ordinal)) return w;
                i = e;
            }
            return null;
        }

        /// <summary>
        /// The config file name a G*Ini variable stands for (GGameIni → "Game", GEngineIni → "Engine"), or null.
        /// Matches DefaultGame.ini / Game.ini / platform variants on the file name.
        /// </summary>
        public static string IniBaseName(string iniVariable)
        {
            if (string.IsNullOrEmpty(iniVariable) || iniVariable.Length < 5 || iniVariable[0] != 'G' || !iniVariable.EndsWith("Ini", StringComparison.Ordinal)) return null;
            return iniVariable.Substring(1, iniVariable.Length - 4);
        }
    }
}
