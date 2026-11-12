using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using UEToVS.Contracts;

namespace UEToVS.Host.Logic.Refactoring
{
    /// <summary>
    /// Add Forward Declaration (pure, v0.4.5). In a header, a type that is only used through a pointer / reference or a UE
    /// handle template (<c>TObjectPtr</c>, <c>TSubclassOf</c>, <c>TWeakObjectPtr</c>, <c>TSoftObjectPtr</c>, …) needs no
    /// #include: <c>class UFoo;</c> is enough. The declaration goes after the include group (after any forward declarations
    /// already there) and before the first UCLASS / class. Anything else (by-value use, base class, Cast&lt;T&gt;, a member
    /// access in an inline body, nested types) means "not offered".
    /// Reason codes: "not-header", "declared-here", "exists", "by-value", "inline-use", "nested", "no-underlying", "not-type".
    /// </summary>
    public static class ForwardDeclarationPlanner
    {
        /// <summary>Templates whose argument may be an incomplete type in a declaration.</summary>
        public static readonly HashSet<string> HandleTemplates = new HashSet<string>(StringComparer.Ordinal)
        {
            "TObjectPtr", "TSubclassOf", "TWeakObjectPtr", "TSoftObjectPtr", "TSoftClassPtr", "TLazyObjectPtr", "TSharedPtr", "TSharedRef", "TWeakPtr",
            "TScriptInterface", "TWeakInterfacePtr", "TStrongObjectPtr",
        };

        /// <summary>Keyword of the declaration: class / struct / enum class (null when the kind cannot be forward-declared).</summary>
        public static string KeywordFor(SymbolKind kind, bool scopedEnum)
        {
            switch (kind)
            {
                case SymbolKind.Class: return "class";
                case SymbolKind.Struct: return "struct";
                case SymbolKind.Enum: return scopedEnum ? "enum class" : "enum";
                default: return null;
            }
        }

        /// <summary><c>enum class EFoo : uint8 {</c> in the declaring header → ("uint8", scoped); null when the enum has no fixed underlying type.</summary>
        public static string EnumUnderlyingType(string headerText, string enumName, out bool scoped)
        {
            scoped = false;
            var m = Regex.Match(headerText ?? string.Empty, @"\benum\s+(class\s+|struct\s+)?" + Regex.Escape(enumName) + @"\s*:\s*([A-Za-z_][\w:\s]*?)\s*\{");
            if (!m.Success) return null;
            scoped = m.Groups[1].Success && m.Groups[1].Length > 0;
            return ChangeSignaturePlanner.NormalizeSpaces(m.Groups[2].Value);
        }

        /// <summary>The declaration line: <c>class UFoo;</c>, <c>enum class EBar : uint8;</c>, or wrapped in its namespace.</summary>
        public static string Declaration(string keyword, string name, string underlying = null, string ns = null)
        {
            string decl = keyword + " " + name + (string.IsNullOrEmpty(underlying) ? string.Empty : " : " + underlying) + ";";
            return string.IsNullOrEmpty(ns) ? decl : "namespace " + ns + " { " + decl + " }";
        }

        /// <summary>Whether the header already declares (or defines) the type itself.</summary>
        public static bool DeclaresType(string headerText, string name)
        {
            var t = CppTextScan.Tokenize(headerText);
            for (int k = 0; k + 1 < t.Count; k++)
            {
                if (!(t[k].Is("class") || t[k].Is("struct") || t[k].Is("enum") || t[k].Is("union"))) continue;
                if (k > 0 && (t[k - 1].Is("friend") || t[k - 1].Is("<") || t[k - 1].Is(",") || t[k - 1].Is("("))) continue;
                int j = k + 1;
                if (t[j].Is("class") || t[j].Is("struct")) j++;
                // API macro between the keyword and the name
                while (j + 1 < t.Count && t[j].IsIdent && t[j + 1].IsIdent && MoveImplementationPlanner.IsMacroName(t[j].Text)) j++;
                if (j < t.Count && t[j].Is(name) && j + 1 < t.Count && (t[j + 1].Is(";") || t[j + 1].Is("{") || t[j + 1].Is(":") || t[j + 1].Is("final")))
                    return true;
            }
            return false;
        }

        /// <summary>
        /// Every use of <paramref name="name"/> in the header is indirect (pointer, reference, handle template, elaborated
        /// <c>class X*</c>, friend). <paramref name="reason"/> tells the first use that is not.
        /// </summary>
        public static bool OnlyIndirectUses(string headerText, string name, out string reason)
        {
            reason = null;
            var t = CppTextScan.Tokenize(headerText);
            var bodies = InlineBodies(t);
            var pointerNames = new HashSet<string>(StringComparer.Ordinal);
            int uses = 0;
            for (int k = 0; k < t.Count; k++)
            {
                if (!t[k].IsIdent || !t[k].Is(name)) continue;
                if (k > 0 && (t[k - 1].Is(".") || (t[k - 1].Is(">") && k > 1 && t[k - 2].Is("-")))) continue; // a member of something else
                if (k > 0 && t[k - 1].Is("::")) { reason = "nested"; return false; }
                if (k + 1 < t.Count && t[k + 1].Is("::")) { reason = "by-value"; return false; } // X::StaticClass(), X::Value
                if (bodies.Any(b => k > b.Open && k < b.Close)) { reason = "inline-use"; return false; }
                uses++;
                int next = k + 1;
                while (next < t.Count && t[next].Is("const")) next++;
                bool indirect = next < t.Count && (t[next].Is("*") || t[next].Is("&"));
                bool handle = false;
                if (k >= 2 && t[k - 1].Is("<") && t[k - 2].IsIdent && HandleTemplates.Contains(t[k - 2].Text) && next < t.Count && t[next].Is(">")) handle = true;
                if (k >= 3 && t[k - 1].Is("const") && t[k - 2].Is("<") && t[k - 3].IsIdent && HandleTemplates.Contains(t[k - 3].Text) && next < t.Count && t[next].Is(">")) handle = true;
                bool elaborated = k > 0 && (t[k - 1].Is("class") || t[k - 1].Is("struct")) && (indirect || (next < t.Count && t[next].Is(">")));
                bool friendDecl = k >= 2 && t[k - 1].Is("class") && t[k - 2].Is("friend");
                if (!(indirect || handle || elaborated || friendDecl))
                {
                    reason = "by-value";
                    return false;
                }
                // the declared variable / member: dereferencing it in an inline body needs the full type
                int d = next;
                if (handle) d = next + 1;
                while (d < t.Count && (t[d].Is("*") || t[d].Is("&") || t[d].Is("const") || t[d].Is(">"))) d++;
                if (d < t.Count && t[d].IsIdent && d + 1 < t.Count && (t[d + 1].Is(";") || t[d + 1].Is("=") || t[d + 1].Is("{") || t[d + 1].Is(",") || t[d + 1].Is(")") || t[d + 1].Is("[")))
                    pointerNames.Add(t[d].Text);
            }
            if (uses == 0) { reason = "not-type"; return false; }
            foreach (var b in bodies)
                for (int k = b.Open + 1; k < b.Close; k++)
                    if (t[k].IsIdent && pointerNames.Contains(t[k].Text) && UseNeedsCompleteType(t, k)) { reason = "inline-use"; return false; }
            return true;
        }

        /// <summary>
        /// A use of a pointer-typed variable that needs the pointee's definition: member access, dereference, or passing it to
        /// a call (an implicit up-cast to a base needs the full type). Returning / comparing / assigning / testing it does not.
        /// </summary>
        private static bool UseNeedsCompleteType(List<ScanToken> t, int k)
        {
            if (k > 0 && (t[k - 1].Is(".") || t[k - 1].Is("::") || (t[k - 1].Is(">") && k > 1 && t[k - 2].Is("-")))) return false; // Other.Name: not ours
            if (k + 1 < t.Count && (t[k + 1].Is(".") || t[k + 1].Is("[") || (t[k + 1].Is("-") && k + 2 < t.Count && t[k + 2].Is(">")))) return true;
            if (k > 0 && t[k - 1].Is("*")) return true; // "*Name": treated as a dereference
            if (k > 0 && (t[k - 1].Is("(") || t[k - 1].Is(",")))
            {
                // argument of a call (not of if / while / return-style parentheses)
                int open = -1, depth = 0;
                for (int m = k - 1; m >= 0; m--)
                {
                    if (t[m].Is(")")) depth++;
                    else if (t[m].Is("(")) { if (depth == 0) { open = m; break; } depth--; }
                    else if (t[m].Is(";") || t[m].Is("{") || t[m].Is("}")) return false;
                }
                if (open > 0 && (t[open - 1].IsIdent || t[open - 1].Is(">")) && !CodeScan.ControlKeywords.Contains(t[open - 1].Text)) return true;
            }
            return false;
        }

        /// <summary>Function bodies (in-class or inline) in a header: token ranges of their braces.</summary>
        private static List<(int Open, int Close)> InlineBodies(List<ScanToken> t)
        {
            var list = new List<(int, int)>();
            for (int k = 0; k < t.Count; k++)
            {
                if (!t[k].Is("{")) continue;
                var kind = CodeScan.Classify(t, k, out _);
                if (kind == BodyKind.None) continue;
                int close = CppTextScan.MatchClose(t, k);
                list.Add((k, close));
                k = close;
            }
            return list;
        }

        private static readonly Regex ForwardLine = new Regex(@"^\s*(class|struct|enum\s+class|enum\s+struct|enum)\s+\w+(\s*:\s*[\w:]+)?\s*;\s*(//.*)?$", RegexOptions.CultureInvariant);
        private static readonly Regex NamespacedForward = new Regex(@"^\s*namespace\s+[\w:]+\s*\{\s*(class|struct|enum\s+class|enum)\s+\w+(\s*:\s*[\w:]+)?\s*;\s*\}\s*$", RegexOptions.CultureInvariant);
        private static readonly Regex IncludeLine = new Regex(@"^\s*#\s*include\b", RegexOptions.CultureInvariant);
        private static readonly Regex PragmaOnce = new Regex(@"^\s*#\s*pragma\s+once\b", RegexOptions.CultureInvariant);

        /// <summary>Whether <c>class X;</c> (any keyword, optionally in a namespace block) is already in the text.</summary>
        public static bool HasForwardDeclaration(string headerText, string name)
        {
            var rx = new Regex(@"\b(class|struct|enum(\s+class|\s+struct)?)\s+" + Regex.Escape(name) + @"\s*(:\s*[\w:\s]+)?;");
            foreach (var line in CppTextScan.Lines(headerText))
            {
                string l = StripLineComment(line);
                if (rx.IsMatch(l) && !l.TrimStart().StartsWith("friend", StringComparison.Ordinal)) return true;
            }
            return false;
        }

        private static string StripLineComment(string l)
        {
            int i = l.IndexOf("//", StringComparison.Ordinal);
            return i < 0 ? l : l.Substring(0, i);
        }

        /// <summary>
        /// Where the declaration goes: after the last forward declaration before the first type / UCLASS, else after the last
        /// #include (blank lines around it), else after #pragma once, else at the top.
        /// </summary>
        public static InsertPoint Plan(string headerText, string declaration)
        {
            string nl = CppTextScan.NewlineOf(headerText);
            var lines = CppTextScan.Lines(headerText);
            var t = CppTextScan.Tokenize(headerText);
            // first line of real code (a token that is not part of a forward declaration line)
            int anchor = lines.Length;
            foreach (var tok in t)
            {
                string l = lines[tok.Line];
                if (ForwardLine.IsMatch(l) || NamespacedForward.IsMatch(l)) continue;
                anchor = tok.Line;
                break;
            }
            // UCLASS()/USTRUCT() etc. lines just before the class head are part of it: tokens catch them already
            int lastForward = -1, lastInclude = -1, pragma = -1;
            for (int i = 0; i < Math.Min(anchor, lines.Length); i++)
            {
                if (ForwardLine.IsMatch(lines[i]) || NamespacedForward.IsMatch(lines[i])) lastForward = i;
                else if (IncludeLine.IsMatch(lines[i])) lastInclude = i;
                else if (PragmaOnce.IsMatch(lines[i])) pragma = i;
            }
            if (lastForward >= 0 && lastForward >= lastInclude)
                return new InsertPoint { Line = lastForward + 1, Text = declaration + nl, Reason = "after-forward" };
            int after = lastInclude >= 0 ? lastInclude : pragma;
            if (after < 0) return new InsertPoint { Line = 0, Text = declaration + nl + nl, Reason = "top" };
            bool blankAfter = after + 1 < lines.Length && lines[after + 1].Trim().Length == 0;
            // "#include …\n\nclass UFoo;\n\nUCLASS()": one blank line before and after
            if (blankAfter)
            {
                bool blankAfterThat = after + 2 < lines.Length && lines[after + 2].Trim().Length == 0;
                return new InsertPoint { Line = after + 2, Text = declaration + nl + (blankAfterThat ? string.Empty : nl), Reason = lastInclude >= 0 ? "after-includes" : "after-pragma" };
            }
            return new InsertPoint { Line = after + 1, Text = nl + declaration + nl, Reason = lastInclude >= 0 ? "after-includes" : "after-pragma" };
        }
    }
}
