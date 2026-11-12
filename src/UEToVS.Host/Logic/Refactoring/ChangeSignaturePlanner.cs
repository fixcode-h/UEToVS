using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using UEToVS.Contracts;

namespace UEToVS.Host.Logic.Refactoring
{
    /// <summary>One parameter of the edited signature (Change Signature dialog row).</summary>
    public sealed class EditableParameter
    {
        /// <summary>Index in the original parameter list; -1 for a new parameter.</summary>
        public int OriginalIndex { get; set; } = -1;

        public string Type { get; set; }
        public string Name { get; set; }

        /// <summary>Default argument (declarations only), or null.</summary>
        public string DefaultValue { get; set; }

        /// <summary>New parameters: the argument written at existing call sites (null → the default value).</summary>
        public string CallValue { get; set; }

        public bool IsNew => OriginalIndex < 0;

        public EditableParameter Clone() => new EditableParameter
        {
            OriginalIndex = OriginalIndex, Type = Type, Name = Name, DefaultValue = DefaultValue, CallValue = CallValue,
        };

        public override string ToString() => (Type + " " + Name).Trim() + (string.IsNullOrEmpty(DefaultValue) ? string.Empty : " = " + DefaultValue);
    }

    /// <summary>What a place the change touches is.</summary>
    public enum SignatureSiteKind
    {
        Declaration,
        Definition,
        Call,

        /// <summary>A use without a call (function pointer, delegate binding): listed only.</summary>
        Reference,
    }

    /// <summary>One place Change Signature updates (preview row).</summary>
    public sealed class SignatureSite
    {
        public string FilePath { get; set; }
        public int Line { get; set; }
        public int Column { get; set; }

        /// <summary>The function name at this place (Name, Name_Implementation, Name_Validate).</summary>
        public string Name { get; set; }

        public SignatureSiteKind Kind { get; set; }
        public bool IsEditable { get; set; }
        public bool Checked { get; set; }
        public string OldLineText { get; set; }

        /// <summary>The line after the change (preview); equals the old line when nothing changes here.</summary>
        public string NewLineText { get; set; }

        /// <summary>Why this place is not changed (shown in the preview), or null.</summary>
        public string Problem { get; set; }

        public List<StructuredEdit> Edits { get; } = new List<StructuredEdit>();

        public override string ToString() => $"{System.IO.Path.GetFileName(FilePath)}:{Line + 1} {Kind} {(Problem ?? "ok")}";
    }

    /// <summary>
    /// Change Signature planning (pure, v0.4.5): parameter list parsing / formatting, validation of the edited list, and the
    /// edits for every declaration, definition (parameter renames inside the body included) and call site found by Find
    /// References. Default arguments are written only in declarations; call sites get reordered arguments, the call value of
    /// new parameters and, where a call relied on a default that no longer applies, the old default spelled out.
    /// Problem codes: "variadic", "unparsable", "count-mismatch", "no-call", "missing-value".
    /// </summary>
    public static class ChangeSignaturePlanner
    {
        // ---------------------------------------------------------------- parameters

        /// <summary>
        /// Parameters of a list as written (<c>const FVector&amp; Dir, int32 Count = 1</c>); null when one cannot be represented
        /// as type + name (variadic, function pointer, array, unnamed parameter in a declaration we must rename).
        /// </summary>
        public static List<EditableParameter> Parse(string parameters)
        {
            var result = new List<EditableParameter>();
            int i = 0;
            foreach (var p in SignatureText.SplitParameters(parameters))
            {
                if (!TrySplit(p, out string type, out string name, out string def)) return null;
                result.Add(new EditableParameter { OriginalIndex = i++, Type = type, Name = name, DefaultValue = def });
            }
            return result;
        }

        /// <summary>Splits one parameter declaration into type, name (may be null when unnamed) and default.</summary>
        public static bool TrySplit(string parameter, out string type, out string name, out string defaultValue)
        {
            type = name = defaultValue = null;
            string p = (parameter ?? string.Empty).Trim();
            if (p.Length == 0 || p == "..." || p.EndsWith("...", StringComparison.Ordinal)) return false;
            int eq = SignatureText.IndexOfTopLevel(p, '=');
            string decl = eq >= 0 ? p.Substring(0, eq).TrimEnd() : p;
            if (eq >= 0) defaultValue = p.Substring(eq + 1).Trim();
            if (decl.IndexOf('[') >= 0 || decl.IndexOf("(*", StringComparison.Ordinal) >= 0 || decl.IndexOf("(&", StringComparison.Ordinal) >= 0) return false;
            name = SignatureText.ParameterName(decl);
            type = name == null ? decl : decl.Substring(0, decl.Length - name.Length).TrimEnd();
            type = NormalizeSpaces(type);
            return type.Length > 0;
        }

        public static string NormalizeSpaces(string s)
        {
            var sb = new StringBuilder();
            bool space = false;
            foreach (char c in (s ?? string.Empty).Trim())
            {
                if (char.IsWhiteSpace(c)) { space = true; continue; }
                if (space && sb.Length > 0) sb.Append(' ');
                space = false;
                sb.Append(c);
            }
            return sb.ToString();
        }

        /// <summary>The parameter list text: <c>Type Name = Default, …</c> (defaults only when <paramref name="withDefaults"/>).</summary>
        public static string Format(IEnumerable<EditableParameter> parameters, bool withDefaults) =>
            string.Join(", ", parameters.Select(p => FormatOne(p.Type, p.Name, withDefaults ? p.DefaultValue : null)));

        private static string FormatOne(string type, string name, string def)
        {
            string s = string.IsNullOrEmpty(name) ? type : JoinTypeName(type, name);
            return string.IsNullOrEmpty(def) ? s : s + " = " + def;
        }

        /// <summary><c>FVector&amp;</c> + <c>Out</c> → <c>FVector&amp; Out</c>.</summary>
        public static string JoinTypeName(string type, string name) => (type ?? string.Empty).TrimEnd() + " " + name;

        /// <summary>Error code for the edited list, or null when it can be applied.</summary>
        public static string Validate(IReadOnlyList<EditableParameter> parameters)
        {
            var names = new HashSet<string>(StringComparer.Ordinal);
            bool sawDefault = false;
            foreach (var p in parameters)
            {
                if (string.IsNullOrWhiteSpace(p.Type)) return "empty-type";
                if (p.Type.IndexOf(';') >= 0 || p.Type.IndexOf('{') >= 0 || p.Type.IndexOf('}') >= 0) return "invalid-type";
                if (!Balanced(p.Type) || !Balanced(p.DefaultValue) || !Balanced(p.CallValue)) return "unbalanced";
                if (string.IsNullOrWhiteSpace(p.Name))
                {
                    if (p.IsNew) return "empty-name";
                }
                else
                {
                    if (!RenamePlanner.IsValidIdentifier(p.Name)) return "invalid-name";
                    if (!names.Add(p.Name)) return "duplicate-name";
                }
                bool hasDefault = !string.IsNullOrWhiteSpace(p.DefaultValue);
                if (hasDefault) sawDefault = true;
                else if (sawDefault) return "default-order";
                if (p.IsNew && string.IsNullOrWhiteSpace(p.CallValue) && !hasDefault) return "missing-call-value";
            }
            return null;
        }

        private static bool Balanced(string s)
        {
            if (string.IsNullOrEmpty(s)) return true;
            int paren = 0, bracket = 0;
            bool inString = false;
            char quote = '\0';
            for (int i = 0; i < s.Length; i++)
            {
                char c = s[i];
                if (inString)
                {
                    if (c == '\\') i++;
                    else if (c == quote) inString = false;
                    continue;
                }
                if (c == '"' || c == '\'') { inString = true; quote = c; }
                else if (c == '(') paren++;
                else if (c == ')') { if (--paren < 0) return false; }
                else if (c == '[') bracket++;
                else if (c == ']') { if (--bracket < 0) return false; }
                else if (c == ';') return false;
            }
            return !inString && paren == 0 && bracket == 0 && SingleItem(s);
        }

        /// <summary>One list item: no top-level comma (template arguments such as <c>TMap&lt;int32, float&gt;</c> are fine).</summary>
        private static bool SingleItem(string s)
        {
            string wrapped = "(" + s + ")";
            var t = CppTextScan.Tokenize(wrapped);
            if (t.Count < 2 || !t[0].Is("(") || CppTextScan.MatchClose(t, 0) != t.Count - 1) return false;
            return Items(wrapped, t, 0, t.Count - 1).Count == 1;
        }

        /// <summary>Whether the edited list equals the original (nothing to do).</summary>
        public static bool IsUnchanged(IReadOnlyList<EditableParameter> original, IReadOnlyList<EditableParameter> edited)
        {
            if (original.Count != edited.Count) return false;
            for (int i = 0; i < edited.Count; i++)
            {
                var e = edited[i];
                var o = original[i];
                if (e.OriginalIndex != i || NormalizeSpaces(e.Type) != NormalizeSpaces(o.Type) || (e.Name ?? string.Empty) != (o.Name ?? string.Empty) ||
                    NormalizeSpaces(e.DefaultValue) != NormalizeSpaces(o.DefaultValue)) return false;
            }
            return true;
        }

        // ---------------------------------------------------------------- text structure

        /// <summary>
        /// The parentheses after a function name at <paramref name="nameOffset"/>: skips white space, comments and template
        /// arguments (<c>Name&lt;T&gt;(</c>). False when the name is not followed by '(' (a pointer to the function, a binding).
        /// </summary>
        public static bool FindParentheses(List<ScanToken> t, int nameToken, out int open, out int close)
        {
            open = close = -1;
            int k = nameToken + 1;
            if (k < t.Count && t[k].Is("<"))
            {
                int depth = 0;
                for (; k < t.Count && k < nameToken + 64; k++)
                {
                    if (t[k].Is("<")) depth++;
                    else if (t[k].Is(">") && --depth == 0) break;
                    else if (t[k].Is(";") || t[k].Is("{") || t[k].Is("}")) return false;
                }
                k++;
            }
            if (k >= t.Count || !t[k].Is("(")) return false;
            open = k;
            close = CppTextScan.MatchClose(t, k);
            return close > open && t[close].Is(")");
        }

        /// <summary>Top-level comma-separated items between two tokens: (start, end) offsets of each item, trimmed. Empty for "()".</summary>
        public static List<(int Start, int End)> Items(string text, List<ScanToken> t, int open, int close)
        {
            var list = new List<(int, int)>();
            int depth = 0, angle = 0, itemStart = CodeScan.End(t[open]);
            int firstTok = -1;
            for (int k = open + 1; k < close; k++)
            {
                var tok = t[k];
                if (tok.Is("(") || tok.Is("[") || tok.Is("{")) depth++;
                else if (tok.Is(")") || tok.Is("]") || tok.Is("}")) depth--;
                else if (depth == 0 && tok.Is("<") && IsTemplateOpen(t, k)) angle++;
                else if (depth == 0 && tok.Is(">") && angle > 0) angle--;
                if (depth == 0 && angle == 0 && tok.Is(","))
                {
                    list.Add(Trim(text, itemStart, tok.Offset));
                    itemStart = CodeScan.End(tok);
                    continue;
                }
                if (firstTok < 0) firstTok = k;
            }
            int end = t[close].Offset;
            if (list.Count > 0 || !CodeScan.IsBlankOrComment(text, itemStart, end) || firstTok >= 0) list.Add(Trim(text, itemStart, end));
            if (list.Count == 1 && list[0].Item1 >= list[0].Item2) list.Clear();
            return list;
        }

        // template argument heuristics for "<": an identifier before it starting with T / known templates, or ::
        private static bool IsTemplateOpen(List<ScanToken> t, int k)
        {
            if (k == 0 || !t[k - 1].IsIdent) return false;
            string n = t[k - 1].Text;
            if (n == "Cast" || n == "CastChecked" || n == "NewObject" || n == "static_cast" || n == "const_cast" || n == "reinterpret_cast" || n == "dynamic_cast" ||
                n == "GetDefault" || n == "GetMutableDefault" || n == "MakeShared" || n == "MakeUnique" || n == "StaticCast" || n == "LoadObject" ||
                n == "FindObject" || n == "CreateDefaultSubobject" || n == "GetSubsystem" || n == "SpawnActor" || n == "std") return true;
            if (n.Length > 1 && n[0] == 'T' && char.IsUpper(n[1])) return true;
            // a matching '>' before any ';' or ')' at this level with only type-ish tokens between
            int depth = 0;
            for (int m = k; m < t.Count && m < k + 32; m++)
            {
                if (t[m].Is("<")) depth++;
                else if (t[m].Is(">")) { if (--depth == 0) return m + 1 < t.Count && (t[m + 1].Is("(") || t[m + 1].Is("::") || t[m + 1].Is(">") || t[m + 1].Is(",") || t[m + 1].Is(")")); }
                else if (!(t[m].IsIdent || t[m].Is("::") || t[m].Is("*") || t[m].Is("&") || t[m].Is(","))) return false;
            }
            return false;
        }

        private static (int, int) Trim(string text, int start, int end)
        {
            while (start < end && char.IsWhiteSpace(text[start])) start++;
            while (end > start && char.IsWhiteSpace(text[end - 1])) end--;
            return (start, end);
        }

        // ---------------------------------------------------------------- planning

        /// <summary>Inputs of <see cref="PlanSite"/> that are the same for every place.</summary>
        public sealed class Change
        {
            /// <summary>The original parameters (from the edited declaration).</summary>
            public IReadOnlyList<EditableParameter> Original { get; set; }

            /// <summary>The edited list (rows of the dialog, in their new order).</summary>
            public IReadOnlyList<EditableParameter> Edited { get; set; }

            /// <summary>The declaration the user edited (its defaults are the new ones); other declarations keep theirs.</summary>
            public string PrimaryDeclarationFile { get; set; }

            public int PrimaryDeclarationLine { get; set; } = -1;
        }

        /// <summary>
        /// Plans one place: the name token at (<paramref name="line"/>, <paramref name="column"/>) in <paramref name="text"/>.
        /// <paramref name="kind"/> from Find References decides declaration / definition; anything else followed by '(' is a call.
        /// </summary>
        public static SignatureSite PlanSite(string filePath, string text, List<ScanToken> tokens, int line, int column, string name, ReferenceKind kind,
                                             bool editable, Change change)
        {
            var starts = TextEditApplier.LineStarts(text);
            var site = new SignatureSite { FilePath = filePath, Line = line, Column = column, Name = name, IsEditable = editable };
            var lines = CppTextScan.Lines(text);
            site.OldLineText = line < lines.Length ? lines[line] : string.Empty;
            site.NewLineText = site.OldLineText;
            int offset = TextEditApplier.OffsetOf(text, starts, line, column);
            int nameTok = offset < 0 ? -1 : CodeScan.IndexAtOrAfter(tokens, offset);
            if (nameTok < 0 || nameTok >= tokens.Count || tokens[nameTok].Offset != offset || !tokens[nameTok].Is(name))
            {
                site.Problem = "unparsable";
                return site;
            }
            if (!FindParentheses(tokens, nameTok, out int open, out int close))
            {
                site.Kind = SignatureSiteKind.Reference;
                site.Problem = "no-call";
                return site;
            }
            bool isDecl = (kind & ReferenceKind.Declaration) != 0, isDef = (kind & ReferenceKind.Definition) != 0;
            if (!isDecl && !isDef && LooksLikeDefinitionHead(tokens, nameTok, close)) isDef = true;
            site.Kind = isDef ? SignatureSiteKind.Definition : isDecl ? SignatureSiteKind.Declaration : SignatureSiteKind.Call;
            var items = Items(text, tokens, open, close);
            string problem = site.Kind == SignatureSiteKind.Call
                ? PlanCall(text, items, open, close, tokens, change, site)
                : PlanDeclaration(text, items, open, close, tokens, change, site, filePath, line);
            if (problem != null)
            {
                site.Problem = problem;
                site.Edits.Clear();
                return site;
            }
            site.Checked = editable;
            site.NewLineText = PreviewLine(text, starts, line, site.Edits);
            return site;
        }

        /// <summary>A qualified name followed by its parameter list and a body / initializer list: <c>AFoo::Bar(int A) const {</c>.</summary>
        private static bool LooksLikeDefinitionHead(List<ScanToken> t, int nameTok, int close)
        {
            if (nameTok < 2 || !t[nameTok - 1].Is("::")) return false;
            if (t[nameTok - 2].Is("Super")) return false;
            for (int k = close + 1; k < t.Count && k < close + 12; k++)
            {
                if (t[k].Is("{")) return true;
                if (t[k].Is(":") ) return true;
                if (!(t[k].Is("const") || t[k].Is("noexcept") || t[k].Is("override") || t[k].Is("final") || t[k].Is("&"))) return false;
            }
            return false;
        }

        private static string PlanCall(string text, List<(int Start, int End)> items, int open, int close, List<ScanToken> t, Change change, SignatureSite site)
        {
            var original = change.Original;
            int required = original.Count(p => string.IsNullOrEmpty(p.DefaultValue));
            if (items.Count < required || items.Count > original.Count) return "count-mismatch";
            // argument i: copy of the call's own text, or a literal
            var pieces = new List<(bool Copy, int Start, int End, string Literal, bool Implicit)>();
            foreach (var p in change.Edited)
            {
                if (p.IsNew)
                {
                    bool useDefault = string.IsNullOrWhiteSpace(p.CallValue);
                    string v = useDefault ? p.DefaultValue : p.CallValue.Trim();
                    if (string.IsNullOrWhiteSpace(v)) return "missing-value";
                    pieces.Add((false, 0, 0, v, useDefault));
                }
                else if (p.OriginalIndex < items.Count) pieces.Add((true, items[p.OriginalIndex].Start, items[p.OriginalIndex].End, null, false));
                else
                {
                    string def = original[p.OriginalIndex].DefaultValue;
                    if (string.IsNullOrWhiteSpace(def)) return "missing-value";
                    // still defaulted to the same value: can stay implicit
                    bool same = NormalizeSpaces(def) == NormalizeSpaces(p.DefaultValue);
                    pieces.Add((false, 0, 0, def, same));
                }
            }
            // trailing arguments that only restate the declaration's default are left out
            int keep = pieces.Count;
            while (keep > 0 && pieces[keep - 1].Implicit && !string.IsNullOrWhiteSpace(change.Edited[keep - 1].DefaultValue)) keep--;
            var (from, to) = ListRange(items, keep > 0, CodeScan.End(t[open]), t[close].Offset);
            var edit = new StructuredEdit(from, to) { Tag = site };
            string sep = Separator(text, items);
            for (int i = 0; i < keep; i++)
            {
                if (i > 0) edit.Literal(sep);
                if (pieces[i].Copy) edit.Copy(pieces[i].Start, pieces[i].End);
                else edit.Literal(pieces[i].Literal);
            }
            // keep the call untouched when nothing changes (e.g. a new trailing parameter with a default)
            if (Render(text, edit) == text.Substring(edit.Start, edit.End - edit.Start)) return null;
            site.Edits.Add(edit);
            return null;
        }

        private static string PlanDeclaration(string text, List<(int Start, int End)> items, int open, int close, List<ScanToken> t, Change change,
                                              SignatureSite site, string filePath, int line)
        {
            var original = change.Original;
            if (items.Count != original.Count) return "count-mismatch";
            var own = new List<(string Type, string Name, string Default)>();
            foreach (var (s, e) in items)
            {
                if (!TrySplit(text.Substring(s, e - s), out string type, out string name, out string def)) return "unparsable";
                own.Add((type, name, def));
            }
            bool isDef = site.Kind == SignatureSiteKind.Definition;
            bool primary = !isDef && string.Equals(filePath, change.PrimaryDeclarationFile, StringComparison.OrdinalIgnoreCase) && line == change.PrimaryDeclarationLine;
            var renames = new Dictionary<string, string>(StringComparer.Ordinal);
            var parts = new List<string>();
            foreach (var p in change.Edited)
            {
                string def = null;
                if (p.IsNew)
                {
                    if (primary) def = p.DefaultValue;
                    parts.Add(FormatOne(p.Type, p.Name, isDef ? null : def));
                    continue;
                }
                var o = original[p.OriginalIndex];
                var mine = own[p.OriginalIndex];
                bool typeChanged = NormalizeSpaces(p.Type) != NormalizeSpaces(o.Type);
                string type = typeChanged ? p.Type : mine.Type;
                string name = mine.Name;
                bool renamed = !string.Equals(p.Name ?? string.Empty, o.Name ?? string.Empty, StringComparison.Ordinal);
                if (renamed)
                {
                    // the place's own name follows when it was the declaration's name (or unnamed in a declaration)
                    if (mine.Name == null && !isDef) name = p.Name;
                    else if (mine.Name != null && mine.Name == o.Name)
                    {
                        name = p.Name;
                        if (isDef && !string.IsNullOrEmpty(p.Name)) renames[mine.Name] = p.Name;
                    }
                }
                if (!isDef) def = primary ? p.DefaultValue : mine.Default;
                parts.Add(FormatOne(type, name, isDef ? null : def));
            }
            if (isDef && renames.Count > 0 && !PlanBodyRenames(text, t, close, renames, site)) return "unparsable";
            var (from, to) = ListRange(items, parts.Count > 0, CodeScan.End(t[open]), t[close].Offset);
            var edit = StructuredEdit.Replace(from, to, JoinParts(text, items, parts));
            edit.Tag = site;
            if (edit.Pieces[0].LiteralText != text.Substring(edit.Start, edit.End - edit.Start)) site.Edits.Add(edit);
            return null;
        }

        /// <summary>
        /// The range an edit replaces: from the first item to the last one, so the whitespace around the list
        /// (e.g. a line break after '(') stays; the whole inside of the parentheses when either side is empty.
        /// </summary>
        private static (int Start, int End) ListRange(List<(int Start, int End)> items, bool hasParts, int inner, int closeOffset)
        {
            if (items.Count == 0 || !hasParts) return (inner, closeOffset);
            return (items[0].Start, items[items.Count - 1].End);
        }

        /// <summary>The separator between items: ", " or the multi-line layout of the original list.</summary>
        private static string Separator(string text, List<(int Start, int End)> items)
        {
            if (items.Count < 2) return ", ";
            string between = text.Substring(items[0].End, items[1].Start - items[0].End);
            int nl = between.IndexOf('\n');
            if (nl < 0) return ", ";
            string newline = between.IndexOf("\r\n", StringComparison.Ordinal) >= 0 ? "\r\n" : "\n";
            return "," + newline + between.Substring(nl + 1);
        }

        private static string JoinParts(string text, List<(int Start, int End)> items, List<string> parts)
        {
            return parts.Count == 0 ? string.Empty : string.Join(Separator(text, items), parts);
        }

        /// <summary>Renames parameter uses in the definition's initializer list and body (members of other objects untouched).</summary>
        private static bool PlanBodyRenames(string text, List<ScanToken> t, int close, Dictionary<string, string> renames, SignatureSite site)
        {
            // body '{' : the first '{' at depth 0 after the parameters that is not a brace initializer of the init list
            int k = close + 1, depth = 0, open = -1;
            bool initList = false;
            for (; k < t.Count; k++)
            {
                var tok = t[k];
                if (tok.Is(";")) return false;
                if (depth == 0 && tok.Is(":")) initList = true;
                if (tok.Is("(")) depth++;
                else if (tok.Is(")")) depth--;
                else if (tok.Is("{"))
                {
                    if (depth == 0 && !(initList && k > 0 && (t[k - 1].IsIdent || t[k - 1].Is(">")))) { open = k; break; }
                    k = CppTextScan.MatchClose(t, k);
                }
            }
            if (open < 0) return false;
            int end = CppTextScan.MatchClose(t, open);
            for (int m = close + 1; m <= end; m++)
            {
                var tok = t[m];
                if (!tok.IsIdent || !renames.TryGetValue(tok.Text, out string to)) continue;
                if (m > 0 && (t[m - 1].Is(".") || t[m - 1].Is("::") || (t[m - 1].Is(">") && m > 1 && t[m - 2].Is("-")))) continue;
                if (m + 1 < t.Count && t[m + 1].Is("::")) continue;
                // init list "Member(Param)": the member name is not the parameter
                if (m < open && m + 1 < t.Count && (t[m + 1].Is("(") || t[m + 1].Is("{")) && (t[m - 1].Is(":") || t[m - 1].Is(","))) continue;
                site.Edits.Add(new StructuredEdit(tok.Offset, CodeScan.End(tok)) { Tag = site }.Literal(to));
            }
            return true;
        }

        private static string Render(string text, StructuredEdit e)
        {
            var sb = new StringBuilder();
            foreach (var p in e.Pieces) sb.Append(p.IsCopy ? text.Substring(p.Start, p.End - p.Start) : p.LiteralText);
            return sb.ToString();
        }

        /// <summary>The site's line after its own edits (for the preview list).</summary>
        public static string PreviewLine(string text, int[] starts, int line, IEnumerable<StructuredEdit> edits)
        {
            if (line >= starts.Length) return string.Empty;
            int ls = starts[line];
            int le = line + 1 < starts.Length ? starts[line + 1] : text.Length;
            var list = edits.ToList();
            if (list.Count == 0) return text.Substring(ls, le - ls).TrimEnd('\r', '\n');
            int from = Math.Min(ls, list.Min(e => e.Start));
            int to = Math.Max(le, list.Max(e => e.End));
            from = CodeScan.LineStart(text, from);
            to = Math.Max(to, CodeScan.LineEnd(text, to));
            string slice = text.Substring(from, to - from);
            var shifted = list.Select(e => Shift(e, -from)).ToList();
            string changed = StructuredEditComposer.Apply(slice, shifted);
            var outLines = changed.Replace("\r\n", "\n").Split('\n').Select(l => l.Trim()).Where(l => l.Length > 0);
            return string.Join(" ", outLines);
        }

        private static StructuredEdit Shift(StructuredEdit e, int delta)
        {
            var s = new StructuredEdit(e.Start + delta, e.End + delta) { Tag = e.Tag };
            foreach (var p in e.Pieces)
            {
                if (p.IsCopy) s.Copy(p.Start + delta, p.End + delta);
                else s.Literal(p.LiteralText);
            }
            return s;
        }

        /// <summary>Text edits of the checked, editable sites of one file (nested edits composed); sites whose edits collide are reported.</summary>
        public static List<TextEdit> Edits(string filePath, string text, IEnumerable<SignatureSite> sites, out List<SignatureSite> collided)
        {
            var all = sites.Where(s => s.Checked && s.IsEditable && s.Problem == null).SelectMany(s => s.Edits).ToList();
            var edits = StructuredEditComposer.Compose(filePath, text, all, out var dropped);
            collided = dropped.Select(d => d.Tag as SignatureSite).Where(s => s != null).Distinct().ToList();
            return edits;
        }
    }
}
