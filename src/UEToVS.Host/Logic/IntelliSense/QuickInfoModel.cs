using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using UEToVS.Contracts;
using UEToVS.Host.Logic.Localization;
using UEToVS.Host.Logic.Navigation;

namespace UEToVS.Host.Logic.IntelliSense
{
    /// <summary>Lexical / semantic class of a declaration token (mapped to VS classification names by the host).</summary>
    public enum DeclTokenClass
    {
        Text = 0,
        Whitespace,
        Punctuation,
        Keyword,
        Number,
        String,
        Comment,

        /// <summary>A semantic kind (see <see cref="DeclToken.Kind"/>).</summary>
        Semantic,
    }

    public readonly struct DeclToken
    {
        public DeclToken(string text, DeclTokenClass cls, SemanticKind kind = SemanticKind.None)
        {
            Text = text;
            Class = cls;
            Kind = kind;
        }

        public string Text { get; }
        public DeclTokenClass Class { get; }
        public SemanticKind Kind { get; }

        public override string ToString() => Class == DeclTokenClass.Semantic ? Kind + ":" + Text : Class + ":" + Text;
    }

    /// <summary>
    /// Colours a declaration / signature string the way the editor colours code (§7.7 "Quick Info 复用同一套分类"): C++
    /// keywords, literals, punctuation, and identifiers classified by context — the hovered symbol by its kind, "X::" as
    /// type / namespace, "name(" as function (ALL_CAPS → macro, UE reflection macros separately), UE-prefixed names
    /// (AActor, UObject, FVector, TArray, EFoo, IFoo) and other identifiers followed by a name as types, the name before
    /// ',' / ')' / '=' inside a parameter list as parameter. Heuristic and pure (no engine call).
    /// </summary>
    public static class DeclarationTokenizer
    {
        public static readonly HashSet<string> Keywords = new HashSet<string>(StringComparer.Ordinal)
        {
            "alignas", "alignof", "auto", "bool", "break", "case", "catch", "char", "char8_t", "char16_t", "char32_t", "class", "const",
            "consteval", "constexpr", "constinit", "const_cast", "continue", "decltype", "default", "delete", "do", "double",
            "dynamic_cast", "else", "enum", "explicit", "export", "extern", "false", "final", "float", "for", "friend", "goto", "if",
            "inline", "int", "long", "mutable", "namespace", "new", "noexcept", "nullptr", "operator", "override", "private",
            "protected", "public", "register", "reinterpret_cast", "return", "short", "signed", "sizeof", "static", "static_assert",
            "static_cast", "struct", "switch", "template", "this", "thread_local", "throw", "true", "try", "typedef", "typeid",
            "typename", "union", "unsigned", "using", "virtual", "void", "volatile", "wchar_t", "while", "#define", "define",
            "int8", "int16", "int32", "int64", "uint8", "uint16", "uint32", "uint64", "TCHAR", "ANSICHAR", "WIDECHAR", "SIZE_T",
            "FORCEINLINE", "FORCENOINLINE",
        };

        /// <summary>UE reflection / boilerplate macros (coloured as UE macros).</summary>
        public static readonly HashSet<string> UeMacros = new HashSet<string>(StringComparer.Ordinal)
        {
            "UCLASS", "USTRUCT", "UENUM", "UINTERFACE", "UPROPERTY", "UFUNCTION", "UMETA", "UPARAM", "UDELEGATE",
            "GENERATED_BODY", "GENERATED_UCLASS_BODY", "GENERATED_USTRUCT_BODY", "GENERATED_IINTERFACE_BODY", "UE_LOG", "TEXT",
            "check", "checkf", "ensure", "ensureMsgf", "verify", "DECLARE_DELEGATE", "DECLARE_MULTICAST_DELEGATE",
            "DECLARE_DYNAMIC_DELEGATE", "DECLARE_DYNAMIC_MULTICAST_DELEGATE", "DECLARE_LOG_CATEGORY_EXTERN", "DEFINE_LOG_CATEGORY",
        };

        public static bool IsUePrefixedType(string id)
        {
            if (id == null || id.Length < 2) return false;
            char p = id[0], n = id[1];
            return (p == 'A' || p == 'U' || p == 'F' || p == 'T' || p == 'E' || p == 'I' || p == 'S') && char.IsUpper(n) &&
                   id.Skip(1).Any(char.IsLower);
        }

        private static bool IsAllCaps(string id) => id.Length > 1 && id.All(c => char.IsUpper(c) || char.IsDigit(c) || c == '_') && id.Any(char.IsLetter);

        /// <param name="decl">Declaration text.</param>
        /// <param name="symbol">The hovered symbol: its name gets its own kind (method, field, …).</param>
        public static List<DeclToken> Tokenize(string decl, SymbolInfo symbol = null)
        {
            var raw = Lex(decl ?? string.Empty);
            var result = new List<DeclToken>(raw.Count);
            int parenDepth = 0;
            for (int i = 0; i < raw.Count; i++)
            {
                var (text, cls) = raw[i];
                if (cls == DeclTokenClass.Punctuation)
                {
                    if (text == "(") parenDepth++;
                    else if (text == ")" && parenDepth > 0) parenDepth--;
                }
                if (cls != DeclTokenClass.Text)
                {
                    result.Add(new DeclToken(text, cls));
                    continue;
                }
                result.Add(Classify(raw, i, text, symbol, parenDepth));
            }
            return result;
        }

        private static DeclToken Classify(List<(string Text, DeclTokenClass Class)> raw, int i, string id, SymbolInfo symbol, int parenDepth)
        {
            if (Keywords.Contains(id)) return new DeclToken(id, DeclTokenClass.Keyword);
            string next = NextSignificant(raw, i);
            string prev = PrevSignificant(raw, i);
            if (symbol != null && id == symbol.Name && next != "::")
                return new DeclToken(id, DeclTokenClass.Semantic, KindOf(symbol));
            if (UeMacros.Contains(id)) return new DeclToken(id, DeclTokenClass.Semantic, SemanticKind.UeMacro);
            if (next == "::") return new DeclToken(id, DeclTokenClass.Semantic, IsUePrefixedType(id) || char.IsUpper(id[0]) ? SemanticKind.Class : SemanticKind.Namespace);
            if (next == "(") return new DeclToken(id, DeclTokenClass.Semantic, IsAllCaps(id) ? SemanticKind.Macro : prev == "::" ? SemanticKind.Method : SemanticKind.Function);
            if (IsUePrefixedType(id)) return new DeclToken(id, DeclTokenClass.Semantic, SemanticKind.Class);
            if (parenDepth > 0 && (next == "," || next == ")" || next == "=" || next == "[")) return new DeclToken(id, DeclTokenClass.Semantic, SemanticKind.Parameter);
            if (next != null && next.Length > 0 && (char.IsLetter(next[0]) || next[0] == '_' || next == "*" || next == "&" || next == "<"))
                return new DeclToken(id, DeclTokenClass.Semantic, SemanticKind.Class);
            if (IsAllCaps(id)) return new DeclToken(id, DeclTokenClass.Semantic, SemanticKind.Macro);
            return new DeclToken(id, DeclTokenClass.Text);
        }

        public static SemanticKind KindOf(SymbolInfo s)
        {
            if (s == null) return SemanticKind.None;
            bool reflected = (s.Ue & (UeFlags.UClass | UeFlags.UStruct | UeFlags.UEnum | UeFlags.UInterface)) != 0;
            switch (s.Kind)
            {
                case SymbolKind.Namespace: return SemanticKind.Namespace;
                case SymbolKind.Class: return reflected ? SemanticKind.UeType : SemanticKind.Class;
                case SymbolKind.Struct:
                case SymbolKind.Union: return reflected ? SemanticKind.UeType : SemanticKind.Struct;
                case SymbolKind.Enum: return reflected ? SemanticKind.UeType : SemanticKind.Enum;
                case SymbolKind.EnumValue: return SemanticKind.EnumValue;
                case SymbolKind.Typedef:
                case SymbolKind.Concept: return SemanticKind.Typedef;
                case SymbolKind.Function: return SemanticKind.Function;
                case SymbolKind.Method:
                case SymbolKind.Constructor:
                case SymbolKind.Destructor: return SemanticKind.Method;
                case SymbolKind.Field: return SemanticKind.Field;
                case SymbolKind.Variable: return SemanticKind.LocalVariable;
                case SymbolKind.Macro: return DeclarationTokenizer.UeMacros.Contains(s.Name ?? string.Empty) ? SemanticKind.UeMacro : SemanticKind.Macro;
                case SymbolKind.Delegate: return SemanticKind.Delegate;
                default: return SemanticKind.None;
            }
        }

        private static string NextSignificant(List<(string Text, DeclTokenClass Class)> raw, int i)
        {
            for (int j = i + 1; j < raw.Count; j++)
                if (raw[j].Class != DeclTokenClass.Whitespace) return raw[j].Text;
            return null;
        }

        private static string PrevSignificant(List<(string Text, DeclTokenClass Class)> raw, int i)
        {
            for (int j = i - 1; j >= 0; j--)
                if (raw[j].Class != DeclTokenClass.Whitespace) return raw[j].Text;
            return null;
        }

        /// <summary>Raw lexing: identifiers (Text), whitespace, numbers, strings / chars, comments, punctuation ("::" and "->" as one).</summary>
        public static List<(string Text, DeclTokenClass Class)> Lex(string s)
        {
            var list = new List<(string, DeclTokenClass)>();
            int i = 0;
            while (i < s.Length)
            {
                char c = s[i];
                int start = i;
                if (char.IsWhiteSpace(c))
                {
                    while (i < s.Length && char.IsWhiteSpace(s[i])) i++;
                    list.Add((s.Substring(start, i - start), DeclTokenClass.Whitespace));
                }
                else if (c == '_' || char.IsLetter(c) || (c == '#' && i + 1 < s.Length && char.IsLetter(s[i + 1])))
                {
                    i++;
                    while (i < s.Length && (s[i] == '_' || char.IsLetterOrDigit(s[i]))) i++;
                    string word = s.Substring(start, i - start);
                    list.Add((word, word[0] == '#' ? DeclTokenClass.Keyword : DeclTokenClass.Text));
                }
                else if (char.IsDigit(c))
                {
                    while (i < s.Length && (char.IsLetterOrDigit(s[i]) || s[i] == '.' || s[i] == '\'')) i++;
                    list.Add((s.Substring(start, i - start), DeclTokenClass.Number));
                }
                else if (c == '"' || c == '\'')
                {
                    i++;
                    while (i < s.Length && s[i] != c)
                    {
                        if (s[i] == '\\') i++;
                        i++;
                    }
                    i = Math.Min(s.Length, i + 1);
                    list.Add((s.Substring(start, i - start), DeclTokenClass.String));
                }
                else if (c == '/' && i + 1 < s.Length && s[i + 1] == '/')
                {
                    int nl = s.IndexOf('\n', i);
                    i = nl < 0 ? s.Length : nl;
                    list.Add((s.Substring(start, i - start), DeclTokenClass.Comment));
                }
                else if (c == '/' && i + 1 < s.Length && s[i + 1] == '*')
                {
                    int end = s.IndexOf("*/", i + 2, StringComparison.Ordinal);
                    i = end < 0 ? s.Length : end + 2;
                    list.Add((s.Substring(start, i - start), DeclTokenClass.Comment));
                }
                else
                {
                    if ((c == ':' || c == '-') && i + 1 < s.Length && (s[i + 1] == ':' || (c == '-' && s[i + 1] == '>'))) i += 2;
                    else i++;
                    list.Add((s.Substring(start, i - start), DeclTokenClass.Punctuation));
                }
            }
            return list;
        }
    }

    /// <summary>Quick Info sections ready for the presenter (§7.6). Pure.</summary>
    public sealed class QuickInfoModel
    {
        public const int MaxDocumentationChars = 4000;
        public const int MaxMacroLines = 400;
        public const int MaxMacroChars = 16 * 1024;
        public const int CollapsedMacroLines = 1;

        public List<DeclToken> Declaration { get; } = new List<DeclToken>();

        /// <summary>"+3 个重载" or null.</summary>
        public string Overloads { get; set; }

        /// <summary>UE badges (UCLASS / UFUNCTION …) for the declaration line.</summary>
        public List<string> Badges { get; } = new List<string>();

        public string Documentation { get; set; }

        /// <summary>Reflection summary line, e.g. UFUNCTION(BlueprintCallable, Category="Combat").</summary>
        public string Reflection { get; set; }

        /// <summary>Human readable reflection tags (蓝图可调用, 可编辑 …).</summary>
        public List<string> ReflectionTags { get; } = new List<string>();

        /// <summary>Macro expansion (capped), or null.</summary>
        public string MacroExpansion { get; set; }

        public int MacroExpansionLines { get; set; }

        public bool MacroExpansionTruncated { get; set; }

        /// <summary>Readable expansion steps (one level, fully expanded, constants computed), see <see cref="MacroExpansionFormatter"/>.</summary>
        public List<MacroStep> MacroSteps { get; } = new List<MacroStep>();

        /// <summary>"Module · File.h:120" or null.</summary>
        public string Location { get; set; }

        /// <summary>Low-confidence note or null.</summary>
        public string ConfidenceNote { get; set; }

        // ---- v0.4.5 navigation gaps: GENERATED_BODY() → the UHT output of that line (from the .generated.h)
        /// <summary>The generated code (capped), or null.</summary>
        public string GeneratedCode { get; set; }

        /// <summary>File name of the .generated.h it came from.</summary>
        public string GeneratedCodeFile { get; set; }

        public int GeneratedCodeLines { get; set; }

        /// <summary>"Build once" / "stale" hint when there is no generated code to show, or null.</summary>
        public string GeneratedNote { get; set; }

        public const int MaxGeneratedLines = 400;

        /// <summary>v0.4.5: "已弃用（5.1）：Use X instead" when the symbol is deprecated, or null.</summary>
        public string Deprecation { get; set; }

        public bool IsEmpty => Declaration.Count == 0 && Documentation == null && MacroExpansion == null && Reflection == null &&
                               GeneratedCode == null && GeneratedNote == null && Deprecation == null;

        public static QuickInfoModel Build(QuickInfoResponse r)
        {
            var m = new QuickInfoModel();
            if (r == null) return m;
            string decl = r.Declaration;
            if (string.IsNullOrWhiteSpace(decl)) decl = r.Symbol?.Signature ?? (r.Symbol == null ? null : SymbolPresentation.QualifiedName(r.Symbol));
            if (!string.IsNullOrWhiteSpace(decl)) m.Declaration.AddRange(DeclarationTokenizer.Tokenize(decl.Trim(), r.Symbol));
            if (r.OverloadCount > 0) m.Overloads = IntelliSenseStrings.Overloads(r.OverloadCount);
            if (r.Symbol != null)
            {
                var badge = SymbolPresentation.UeBadge(r.Symbol.Ue);
                if (badge != null) m.Badges.Add(badge.Value.Text);
                m.Location = LocationText(r.Symbol);
            }
            m.Documentation = Trim(CleanDocumentation(r.Documentation), MaxDocumentationChars);
            if (r.IsDeprecated) m.Deprecation = UEToVS.Host.Logic.Localization.EditorFeatureStrings.DeprecatedQuickInfo(r.DeprecatedVersion, r.DeprecationMessage);
            m.Reflection = string.IsNullOrWhiteSpace(r.Reflection) ? null : r.Reflection.Trim();
            m.ReflectionTags.AddRange(ReflectionTagsOf(r.Reflection ?? r.Symbol?.Specifiers));
            if (!string.IsNullOrWhiteSpace(r.MacroExpansion) || !string.IsNullOrWhiteSpace(r.MacroExpansionOneLevel))
            {
                string one = r.MacroExpansionOneLevel, full = r.MacroExpansionFull;
                if (one == null)
                {
                    // older engines: "one level\n\nfull"
                    string raw = r.MacroExpansion.Replace("\r\n", "\n");
                    int split = raw.IndexOf("\n\n", StringComparison.Ordinal);
                    one = split < 0 ? raw : raw.Substring(0, split);
                    full = split < 0 ? raw : raw.Substring(split + 2);
                }
                int budget = MaxMacroChars, lineBudget = MaxMacroLines, total = 0;
                bool truncated = false;
                foreach (var step in MacroExpansionFormatter.Steps(Cap(one), Cap(full)))
                {
                    if (budget <= 0 || lineBudget <= 0) { truncated = true; break; }
                    string text = step.Text.Replace("\r\n", "\n").TrimEnd();
                    if (text.Length > budget) { text = text.Substring(0, budget); truncated = true; }
                    var lines = text.Split('\n');
                    if (lines.Length > lineBudget) { text = string.Join("\n", lines.Take(lineBudget)); truncated = true; }
                    int n = Math.Min(lines.Length, lineBudget);
                    budget -= text.Length;
                    lineBudget -= n;
                    total += n;
                    m.MacroSteps.Add(new MacroStep(step.Kind, text));
                }
                m.MacroExpansion = string.Join("\n\n", m.MacroSteps.Select(x => x.Text));
                m.MacroExpansionLines = total;
                m.MacroExpansionTruncated = truncated;
            }
            ApplyGeneratedCode(m, r);
            if (r.Confidence == ResolutionConfidence.NameOnly) m.ConfidenceNote = IntelliSenseStrings.HeuristicNote;
            return m;
        }

        private static void ApplyGeneratedCode(QuickInfoModel m, QuickInfoResponse r)
        {
            switch (r.GeneratedCodeState)
            {
                case GeneratedCodeState.Available when !string.IsNullOrWhiteSpace(r.GeneratedCode):
                    var lines = r.GeneratedCode.Replace("\r\n", "\n").TrimEnd().Split('\n');
                    m.GeneratedCodeLines = Math.Min(lines.Length, MaxGeneratedLines);
                    m.GeneratedCode = string.Join("\n", lines.Take(MaxGeneratedLines));
                    string path = r.GeneratedCodeFile ?? string.Empty;
                    m.GeneratedCodeFile = path.Substring(Math.Max(path.LastIndexOf('\\'), path.LastIndexOf('/')) + 1);
                    break;
                case GeneratedCodeState.NotBuilt:
                    m.GeneratedNote = NavGapStrings.GeneratedCodeNotBuilt;
                    break;
                case GeneratedCodeState.Stale:
                case GeneratedCodeState.Available:
                    m.GeneratedNote = NavGapStrings.GeneratedCodeStale;
                    break;
            }
        }

        private static string Cap(string s) => s == null || s.Length <= MaxMacroChars * 2 ? s : s.Substring(0, MaxMacroChars * 2);

        public static string LocationText(SymbolInfo s)
        {
            if (s?.Location?.FilePath == null) return s?.Module;
            string path = s.Location.FilePath;
            string file = path.Substring(Math.Max(path.LastIndexOf('\\'), path.LastIndexOf('/')) + 1) + ":" + (s.Location.Line + 1);
            return string.IsNullOrEmpty(s.Module) ? file : s.Module + " · " + file;
        }

        /// <summary>Strips leading "*" / "///" / "//" comment markers and blank edges (the engine normally sends clean text).</summary>
        public static string CleanDocumentation(string doc)
        {
            if (string.IsNullOrWhiteSpace(doc)) return null;
            var sb = new StringBuilder();
            foreach (var rawLine in doc.Replace("\r\n", "\n").Split('\n'))
            {
                string l = rawLine.Trim();
                if (l.StartsWith("/**")) l = l.Substring(3);
                else if (l.StartsWith("///")) l = l.Substring(3);
                else if (l.StartsWith("//")) l = l.Substring(2);
                else if (l.StartsWith("/*")) l = l.Substring(2);
                if (l.EndsWith("*/")) l = l.Substring(0, l.Length - 2);
                l = l.Trim();
                if (l.StartsWith("*")) l = l.Substring(1).Trim();
                sb.Append(l).Append('\n');
            }
            string text = sb.ToString().Trim('\n', ' ');
            return text.Length == 0 ? null : text;
        }

        private static string Trim(string s, int max) => s == null || s.Length <= max ? s : s.Substring(0, max) + "…";

        /// <summary>Specifier list (from "UFUNCTION(BlueprintCallable, Category=…)" or bare "BlueprintCallable, …") → readable tags.</summary>
        public static List<string> ReflectionTagsOf(string reflection)
        {
            var tags = new List<string>();
            if (string.IsNullOrWhiteSpace(reflection)) return tags;
            string s = reflection;
            int open = s.IndexOf('(');
            int close = s.LastIndexOf(')');
            if (open >= 0 && close > open) s = s.Substring(open + 1, close - open - 1);
            foreach (var spec in SplitTopLevel(s))
            {
                string name = spec;
                int eq = name.IndexOf('=');
                if (eq >= 0) name = name.Substring(0, eq);
                string tag = IntelliSenseStrings.SpecifierTag(name.Trim());
                if (tag != null && !tags.Contains(tag)) tags.Add(tag);
            }
            return tags;
        }

        private static IEnumerable<string> SplitTopLevel(string s)
        {
            int depth = 0, start = 0;
            bool quote = false;
            for (int i = 0; i < s.Length; i++)
            {
                char c = s[i];
                if (c == '"') quote = !quote;
                if (quote) continue;
                if (c == '(') depth++;
                else if (c == ')') depth--;
                else if (c == ',' && depth == 0)
                {
                    yield return s.Substring(start, i - start).Trim();
                    start = i + 1;
                }
            }
            if (start < s.Length) yield return s.Substring(start).Trim();
        }
    }
}
