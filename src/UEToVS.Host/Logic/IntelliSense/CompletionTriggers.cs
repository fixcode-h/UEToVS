using System;
using System.Collections.Generic;
using UEToVS.Contracts;
using UEToVS.Host.Logic.Settings;

namespace UEToVS.Host.Logic.IntelliSense
{
    /// <summary>Lexical situation of the caret on its line (single-line scan; block comments opened on earlier lines are the host's job).</summary>
    public enum LineContext
    {
        Code = 0,
        LineComment = 1,
        BlockComment = 2,
        String = 3,
        CharLiteral = 4,

        /// <summary>Inside the path of #include "…" / &lt;…&gt;.</summary>
        IncludePath = 5,
    }

    /// <summary>Outcome of <see cref="CompletionTriggers.Evaluate"/>.</summary>
    public readonly struct TriggerResult
    {
        public TriggerResult(bool start, CompletionContextKind hint, char triggerChar, int wordStart, int wordLength)
        {
            Start = start;
            Hint = hint;
            TriggerChar = triggerChar;
            WordStart = wordStart;
            WordLength = wordLength;
        }

        /// <summary>Open a completion session.</summary>
        public bool Start { get; }

        /// <summary>Locally detected context (the engine decides the real one; used for logs and the UE-only filter).</summary>
        public CompletionContextKind Hint { get; }

        /// <summary>Character sent to the engine as CompletionRequest.TriggerChar ('\0' = typing / explicit).</summary>
        public char TriggerChar { get; }

        /// <summary>Column / length of the text the session replaces (identifier or include path fragment around the caret).</summary>
        public int WordStart { get; }

        public int WordLength { get; }

        public static TriggerResult None => default(TriggerResult);

        public override string ToString() => Start ? Hint + " '" + (TriggerChar == '\0' ? "" : TriggerChar.ToString()) + "' [" + WordStart + "," + WordLength + "]" : "none";
    }

    /// <summary>
    /// When to open UEToVS completion (§7.6): identifier typing, '.', '-&gt;', '::', #include "/&lt;, inside UCLASS(…) /
    /// UPROPERTY(…) / meta=(…) specifier lists, and Ctrl+Space. Pure: works on the caret line's text; the column is the
    /// caret position after the typed character. Never triggers in comments or ordinary strings.
    /// </summary>
    public static class CompletionTriggers
    {
        /// <summary>Reflection macros whose argument list holds specifiers.</summary>
        public static readonly HashSet<string> SpecifierMacros = new HashSet<string>(StringComparer.Ordinal)
        {
            "UCLASS", "UPROPERTY", "UFUNCTION", "USTRUCT", "UENUM", "UINTERFACE", "UMETA", "UPARAM", "UDELEGATE",
        };

        public static bool IsIdentifierChar(char c) => c == '_' || char.IsLetterOrDigit(c);

        public static bool IsIdentifierStart(char c) => c == '_' || char.IsLetter(c);

        /// <param name="line">Text of the caret line.</param>
        /// <param name="caret">Caret column (after the typed character for typing triggers).</param>
        /// <param name="typed">Typed character, or '\0' for an explicit invoke / deletion.</param>
        /// <param name="explicitInvoke">Ctrl+Space / Edit.ListMembers.</param>
        /// <param name="onIdentifier">Setting: open the list when an identifier starts (VA style).</param>
        public static TriggerResult Evaluate(string line, int caret, char typed, bool explicitInvoke, bool onIdentifier = true)
        {
            if (line == null) return TriggerResult.None;
            caret = Math.Max(0, Math.Min(caret, line.Length));
            var ctx = ContextAt(line, caret);
            if (ctx == LineContext.IncludePath)
            {
                var (ps, pl) = IncludeFragment(line, caret);
                bool includeStart = explicitInvoke || typed == '"' || typed == '<' || typed == '/' || typed == '\\' ||
                                    (typed != '\0' && onIdentifier && IsIdentifierChar(typed) && pl == 1);
                return includeStart ? new TriggerResult(true, CompletionContextKind.Include, typed == '\0' ? '\0' : typed, ps, pl) : TriggerResult.None;
            }
            if (ctx != LineContext.Code) return TriggerResult.None;

            var (ws, wl) = WordAt(line, caret);
            bool inSpecifier = IsInSpecifierList(line, caret, out _);

            if (explicitInvoke)
                return new TriggerResult(true, inSpecifier ? CompletionContextKind.Specifier : HintBefore(line, ws), '\0', ws, wl);

            switch (typed)
            {
                case '.':
                    // a.b / a->b member access; not "1." / "..." / ".5".
                    if (caret >= 2 && IsMemberOperand(line, caret - 2) && !IsNumberBefore(line, caret - 1))
                        return new TriggerResult(true, CompletionContextKind.Member, '.', caret, 0);
                    return TriggerResult.None;
                case '>':
                    if (caret >= 3 && line[caret - 2] == '-' && IsMemberOperand(line, caret - 3))
                        return new TriggerResult(true, CompletionContextKind.Member, '>', caret, 0);
                    return TriggerResult.None;
                case ':':
                    if (caret >= 2 && line[caret - 2] == ':' && (caret < 3 || line[caret - 3] != ':'))
                    {
                        // "::Global", "Foo::", "TArray<int>::", but not a label/case "x ::" after ')' etc.
                        char before = caret >= 3 ? line[caret - 3] : ' ';
                        if (caret < 3 || IsIdentifierChar(before) || before == '>' || char.IsWhiteSpace(before) || before == '(' || before == ',')
                            return new TriggerResult(true, CompletionContextKind.Scope, ':', caret, 0);
                    }
                    return TriggerResult.None;
                case '(':
                case ',':
                    if (inSpecifier) return new TriggerResult(true, CompletionContextKind.Specifier, typed, caret, 0);
                    return TriggerResult.None;
                case ' ':
                    // "UPROPERTY(EditAnywhere, |": a space right after the separator, not inside "Category = …".
                    if (inSpecifier && caret >= 2 && (line[caret - 2] == ',' || line[caret - 2] == '('))
                        return new TriggerResult(true, CompletionContextKind.Specifier, ',', caret, 0);
                    return TriggerResult.None;
                case '\0':
                    return TriggerResult.None;
            }

            if (IsIdentifierChar(typed) && onIdentifier)
            {
                // Only when an identifier starts (the session filters the rest); never inside a number.
                if (wl != 1 || ws != caret - 1 || !IsIdentifierStart(typed)) return TriggerResult.None;
                var hint = inSpecifier ? CompletionContextKind.Specifier : HintBefore(line, ws);
                return new TriggerResult(true, hint, '\0', ws, wl);
            }
            return TriggerResult.None;
        }

        /// <summary>Context implied by what precedes the word (".", "->", "::", "override").</summary>
        public static CompletionContextKind HintBefore(string line, int wordStart)
        {
            int i = wordStart - 1;
            while (i >= 0 && line[i] == ' ') i--;
            if (i >= 0 && line[i] == '.') return CompletionContextKind.Member;
            if (i >= 1 && line[i] == '>' && line[i - 1] == '-') return CompletionContextKind.Member;
            if (i >= 1 && line[i] == ':' && line[i - 1] == ':') return CompletionContextKind.Scope;
            if (EndsWithWord(line, i + 1, "virtual") || EndsWithWord(line, i + 1, "override")) return CompletionContextKind.Override;
            return CompletionContextKind.General;
        }

        private static bool EndsWithWord(string line, int end, string word)
        {
            int start = end - word.Length;
            if (start < 0 || string.CompareOrdinal(line, start, word, 0, word.Length) != 0) return false;
            return start == 0 || !IsIdentifierChar(line[start - 1]);
        }

        private static bool IsMemberOperand(string line, int i) =>
            i >= 0 && i < line.Length && (IsIdentifierChar(line[i]) || line[i] == ')' || line[i] == ']' || line[i] == '>');

        /// <summary>The '.' at <paramref name="dot"/> is part of a numeric literal (1. / 0x1.).</summary>
        private static bool IsNumberBefore(string line, int dot)
        {
            int i = dot - 1;
            while (i >= 0 && IsIdentifierChar(line[i])) i--;
            int start = i + 1;
            return start < dot && char.IsDigit(line[start]);
        }

        /// <summary>Identifier around the caret (start column, length), for the applicable span.</summary>
        public static (int Start, int Length) WordAt(string line, int caret)
        {
            if (line == null) return (0, 0);
            caret = Math.Max(0, Math.Min(caret, line.Length));
            int s = caret, e = caret;
            while (s > 0 && IsIdentifierChar(line[s - 1])) s--;
            while (e < line.Length && IsIdentifierChar(line[e])) e++;
            return (s, e - s);
        }

        /// <summary>Path fragment after the last '/' (or the opening quote) of an #include, up to the caret's word end.</summary>
        public static (int Start, int Length) IncludeFragment(string line, int caret)
        {
            int s = caret;
            while (s > 0 && line[s - 1] != '"' && line[s - 1] != '<' && line[s - 1] != '/' && line[s - 1] != '\\') s--;
            int e = caret;
            while (e < line.Length && line[e] != '"' && line[e] != '>' && line[e] != '/' && line[e] != '\\') e++;
            return (s, e - s);
        }

        /// <summary>Lexical context at <paramref name="caret"/>: comment, string, char literal, include path or code.</summary>
        public static LineContext ContextAt(string line, int caret)
        {
            if (line == null) return LineContext.Code;
            caret = Math.Max(0, Math.Min(caret, line.Length));
            if (IsIncludePath(line, caret)) return LineContext.IncludePath;
            var state = LineContext.Code;
            for (int i = 0; i < caret; i++)
            {
                char c = line[i];
                char n = i + 1 < line.Length ? line[i + 1] : '\0';
                switch (state)
                {
                    case LineContext.Code:
                        if (c == '/' && n == '/' && i + 1 < caret) return LineContext.LineComment;
                        if (c == '/' && n == '*' && i + 1 < caret)
                        {
                            state = LineContext.BlockComment;
                            i++;
                        }
                        else if (c == '"')
                        {
                            state = LineContext.String;
                        }
                        else if (c == '\'' && !(i > 0 && char.IsLetterOrDigit(line[i - 1]) && char.IsDigit(n)))
                        {
                            state = LineContext.CharLiteral; // 1'000 digit separators excluded
                        }
                        break;
                    case LineContext.BlockComment:
                        if (c == '*' && n == '/' && i + 1 < caret)
                        {
                            state = LineContext.Code;
                            i++;
                        }
                        break;
                    case LineContext.String:
                    case LineContext.CharLiteral:
                        if (c == '\\') i++;
                        else if ((c == '"' && state == LineContext.String) || (c == '\'' && state == LineContext.CharLiteral)) state = LineContext.Code;
                        break;
                }
            }
            return state;
        }

        /// <summary>#include "… or #include &lt;… with the caret after the opening delimiter and before the closing one.</summary>
        public static bool IsIncludePath(string line, int caret)
        {
            int i = 0;
            while (i < line.Length && char.IsWhiteSpace(line[i])) i++;
            if (i >= line.Length || line[i] != '#') return false;
            i++;
            while (i < line.Length && char.IsWhiteSpace(line[i])) i++;
            int kw = i;
            while (i < line.Length && char.IsLetter(line[i])) i++;
            string word = line.Substring(kw, i - kw);
            if (word != "include" && word != "import" && word != "include_next") return false;
            while (i < line.Length && char.IsWhiteSpace(line[i])) i++;
            if (i >= line.Length || (line[i] != '"' && line[i] != '<')) return false;
            char close = line[i] == '"' ? '"' : '>';
            int open = i;
            if (caret <= open) return false;
            int end = line.IndexOf(close, open + 1);
            return end < 0 || caret <= end;
        }

        /// <summary>
        /// Caret inside the argument list of UCLASS( … ) / UPROPERTY( … ) / … (any nesting, e.g. meta=( … )).
        /// <paramref name="macro"/> = the reflection macro.
        /// </summary>
        public static bool IsInSpecifierList(string line, int caret, out string macro)
        {
            macro = null;
            int depth = 0;
            for (int i = Math.Min(caret, line.Length) - 1; i >= 0; i--)
            {
                char c = line[i];
                if (c == ')') depth++;
                else if (c == '(')
                {
                    if (depth > 0)
                    {
                        depth--;
                        continue;
                    }
                    int e = i;
                    while (e > 0 && line[e - 1] == ' ') e--;
                    int s = e;
                    while (s > 0 && IsIdentifierChar(line[s - 1])) s--;
                    string name = line.Substring(s, e - s);
                    if (SpecifierMacros.Contains(name))
                    {
                        macro = name;
                        return true;
                    }
                    if (name.Length == 0 && s > 0 && line[s - 1] == '=') continue; // meta=( … ): keep looking for the outer macro
                    if (name == "meta" || name == "Meta") continue;
                    return false;
                }
                else if (c == ';' || c == '{' || c == '}') return false;
            }
            return false;
        }
    }

    /// <summary>
    /// The "UE-only" policy (§7.5): before the takeover VS's own C++ completion is still active, so UEToVS only adds what VS
    /// lacks — reflection specifiers, #include paths, overrides and UE-reflected members — instead of listing every symbol
    /// twice. After the takeover (or with <see cref="CompletionScope.All"/>) UEToVS provides everything. Pure.
    /// </summary>
    public static class CompletionPolicy
    {
        public static bool ProvidesAll(CompletionScope scope, bool takenOver) =>
            scope == CompletionScope.All || (scope == CompletionScope.Auto && takenOver);

        /// <summary>Should a session start for a locally detected context?</summary>
        public static bool Participates(CompletionContextKind hint, bool providesAll)
        {
            if (providesAll) return true;
            switch (hint)
            {
                case CompletionContextKind.Specifier:
                case CompletionContextKind.Include:
                case CompletionContextKind.Override:
                case CompletionContextKind.Member:
                case CompletionContextKind.Scope:
                    return true;
                default:
                    return false;
            }
        }

        /// <summary>Keep an engine item in the list (the engine's context decides).</summary>
        public static bool Keep(CompletionItem item, CompletionContextKind context, bool providesAll)
        {
            if (item == null || string.IsNullOrEmpty(item.Label)) return false;
            if (providesAll) return true;
            switch (context)
            {
                case CompletionContextKind.Specifier:
                case CompletionContextKind.Include:
                case CompletionContextKind.Override:
                    return true;
                case CompletionContextKind.Member:
                case CompletionContextKind.Scope:
                    return item.Ue != UeFlags.None;
                default:
                    return false;
            }
        }
    }
}
