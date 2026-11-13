using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

namespace UEToVS.Host.Logic.Generate
{
    /// <summary>The class / struct whose body contains the caret (text offsets).</summary>
    public sealed class ClassAtCaret
    {
        public string Name { get; set; }
        public string Keyword { get; set; }
        public int BodyOpen { get; set; }
        public int BodyClose { get; set; }

        /// <summary>Offset right after the GENERATED_BODY() / GENERATED_UCLASS_BODY() line break, or -1.</summary>
        public int AfterGeneratedBody { get; set; } = -1;

        /// <summary>Leading white space of members ("\t" by default).</summary>
        public string MemberIndent { get; set; } = "\t";

        public override string ToString() => Keyword + " " + Name;
    }

    /// <summary>An insertion into a file.</summary>
    public sealed class TextInsertion
    {
        public TextInsertion(int offset, string text)
        {
            Offset = offset;
            Text = text;
        }

        public int Offset { get; }
        public string Text { get; }
        public override string ToString() => "@" + Offset + ": " + Text;
    }

    /// <summary>What a generator wants to change. <see cref="Problem"/> is set (and nothing changes) when it cannot run.</summary>
    public sealed class GenerateResult
    {
        public string Problem { get; set; }
        public string ClassName { get; set; }
        public List<TextInsertion> Header { get; } = new List<TextInsertion>();
        public List<TextInsertion> Source { get; } = new List<TextInsertion>();

        /// <summary>Names generated (properties registered, functions defined) for the log / status bar.</summary>
        public List<string> Generated { get; } = new List<string>();

        public bool Ok => Problem == null && (Header.Count > 0 || Source.Count > 0);

        public static GenerateResult Fail(string problem) => new GenerateResult { Problem = problem };

        /// <summary>Applies insertions to a text (descending offsets): for tests and for files that are not open.</summary>
        public static string Apply(string text, IEnumerable<TextInsertion> inserts)
        {
            var sb = new StringBuilder(text ?? string.Empty);
            foreach (var i in inserts.OrderByDescending(x => x.Offset)) sb.Insert(Math.Min(sb.Length, Math.Max(0, i.Offset)), i.Text);
            return sb.ToString();
        }
    }

    /// <summary>
    /// Generate menu (Rider style) on header text: constructor, GetLifetimeReplicatedProps from the class's Replicated
    /// properties, and the RPC trio (declaration + _Implementation + _Validate). Text-level (no engine), works on unsaved
    /// buffers; the source file only receives appended definitions and a missing #include. Pure.
    /// Problem codes: "no-class", "exists", "no-replicated", "no-function", "not-header".
    /// </summary>
    public static class UeCodeGenerator
    {
        private static readonly Regex ClassHead = new Regex(@"\b(class|struct)\s+(?:[A-Z][A-Z0-9_]*_API\s+)?(\w+)\s*(?:final\s*)?(?::[^{;]*)?\{\s*$",
                                                            RegexOptions.Compiled | RegexOptions.CultureInvariant);

        private static readonly Regex GeneratedBody = new Regex(@"\bGENERATED_(?:UCLASS_|USTRUCT_|UINTERFACE_|IINTERFACE_)?BODY(?:_LEGACY)?\s*\(\s*\)[^\n]*\n?",
                                                                RegexOptions.Compiled | RegexOptions.CultureInvariant);

        /// <summary>Text with comments and string / char literals blanked (same offsets), so scanning never matches inside them.</summary>
        public static string Blank(string text)
        {
            if (string.IsNullOrEmpty(text)) return text ?? string.Empty;
            var c = text.ToCharArray();
            int i = 0;
            while (i < c.Length)
            {
                if (c[i] == '/' && i + 1 < c.Length && c[i + 1] == '/')
                {
                    while (i < c.Length && c[i] != '\n') c[i++] = ' ';
                }
                else if (c[i] == '/' && i + 1 < c.Length && c[i + 1] == '*')
                {
                    c[i++] = ' ';
                    c[i++] = ' ';
                    while (i < c.Length && !(c[i] == '*' && i + 1 < c.Length && c[i + 1] == '/'))
                    {
                        if (c[i] != '\n') c[i] = ' ';
                        i++;
                    }
                    if (i < c.Length) { c[i++] = ' '; }
                    if (i < c.Length) { c[i++] = ' '; }
                }
                else if (c[i] == '"' || c[i] == '\'')
                {
                    char q = c[i++];
                    while (i < c.Length && c[i] != q && c[i] != '\n')
                    {
                        if (c[i] == '\\' && i + 1 < c.Length) c[i++] = ' ';
                        c[i++] = ' ';
                    }
                    i++;
                }
                else i++;
            }
            return new string(c);
        }

        /// <summary>The innermost class / struct body around <paramref name="caret"/>.</summary>
        public static ClassAtCaret FindClass(string text, int caret)
        {
            string b = Blank(text);
            caret = Math.Max(0, Math.Min(caret, b.Length));
            // walk outwards over '{' that enclose the caret
            int depth = 0;
            for (int i = caret - 1; i >= 0; i--)
            {
                char ch = b[i];
                if (ch == '}') depth++;
                else if (ch == '{')
                {
                    if (depth > 0) { depth--; continue; }
                    int lineStart = HeadStart(b, i);
                    var m = ClassHead.Match(b.Substring(lineStart, i - lineStart + 1));
                    if (!m.Success) continue;
                    int close = MatchBrace(b, i);
                    if (close < 0 || close < caret) return null;
                    var c = new ClassAtCaret { Keyword = m.Groups[1].Value, Name = m.Groups[2].Value, BodyOpen = i, BodyClose = close };
                    var g = GeneratedBody.Match(b, i, close - i);
                    if (g.Success)
                    {
                        c.AfterGeneratedBody = g.Index + g.Length;
                        int ls = b.LastIndexOf('\n', g.Index) + 1;
                        c.MemberIndent = text.Substring(ls, g.Index - ls);
                        if (c.MemberIndent.Trim().Length != 0) c.MemberIndent = "\t";
                    }
                    return c;
                }
            }
            return null;
        }

        /// <summary>Start of the class head: back over at most a few lines (multi-line base lists), stopping at ';', '}' or '{'.</summary>
        private static int HeadStart(string b, int brace)
        {
            int i = brace - 1;
            int lines = 0;
            while (i >= 0)
            {
                char ch = b[i];
                if (ch == ';' || ch == '}' || ch == '{' || ch == ')') break;
                if (ch == '\n' && ++lines > 4) break;
                if (ch == '#') { int e = b.IndexOf('\n', i); return e < 0 ? brace : e + 1; }
                i--;
            }
            return i + 1;
        }

        internal static int MatchBrace(string b, int open)
        {
            int depth = 0;
            for (int i = open; i < b.Length; i++)
            {
                if (b[i] == '{') depth++;
                else if (b[i] == '}' && --depth == 0) return i;
            }
            return -1;
        }

        /// <summary>Whether the class body declares <paramref name="name"/>(…) at member level.</summary>
        public static bool DeclaresFunction(string text, ClassAtCaret c, string name)
        {
            string b = Blank(text);
            var re = new Regex(@"(?<![\w~:])" + Regex.Escape(name) + @"\s*\(");
            int depth = 0;
            for (int i = c.BodyOpen; i <= c.BodyClose && i < b.Length; i++)
            {
                if (b[i] == '{') depth++;
                else if (b[i] == '}') depth--;
                else if (depth == 1 && b[i] == name[0] && re.IsMatch(b.Substring(i, Math.Min(b.Length - i, name.Length + 64))) && (i == 0 || !(char.IsLetterOrDigit(b[i - 1]) || b[i - 1] == '_' || b[i - 1] == '~')))
                    return true;
            }
            return false;
        }

        /// <summary>Names of the class's UPROPERTY(… Replicated / ReplicatedUsing …) members, in order.</summary>
        public static List<string> ReplicatedProperties(string text, ClassAtCaret c)
        {
            var list = new List<string>();
            string b = Blank(text);
            int depth = 0;
            for (int i = c.BodyOpen; i < c.BodyClose; i++)
            {
                if (b[i] == '{') { depth++; continue; }
                if (b[i] == '}') { depth--; continue; }
                if (depth != 1 || b[i] != 'U' || string.CompareOrdinal(b, i, "UPROPERTY", 0, 9) != 0 || i > 0 && (char.IsLetterOrDigit(b[i - 1]) || b[i - 1] == '_')) continue;
                int open = b.IndexOf('(', i + 9);
                if (open < 0) break;
                int close = MatchParen(b, open);
                if (close < 0) break;
                string specs = text.Substring(open + 1, close - open - 1);
                int semi = b.IndexOf(';', close);
                if (semi < 0 || semi > c.BodyClose) break;
                i = semi;
                if (!HasSpecifier(specs, "Replicated") && !HasSpecifier(specs, "ReplicatedUsing")) continue;
                string decl = b.Substring(close + 1, semi - close - 1);
                int eq = decl.IndexOf('=');
                if (eq >= 0) decl = decl.Substring(0, eq);
                int brace = decl.IndexOf('{');
                if (brace >= 0) decl = decl.Substring(0, brace);
                int colon = decl.LastIndexOf(':');
                if (colon > 0 && decl[colon - 1] != ':') decl = decl.Substring(0, colon); // bitfield
                var m = Regex.Match(decl, @"(\w+)\s*(?:\[[^\]]*\])?\s*$");
                if (m.Success) list.Add(m.Groups[1].Value);
            }
            return list;
        }

        internal static int MatchParen(string b, int open)
        {
            int depth = 0;
            for (int i = open; i < b.Length; i++)
            {
                if (b[i] == '(') depth++;
                else if (b[i] == ')' && --depth == 0) return i;
            }
            return -1;
        }

        /// <summary>Top-level specifier key match (Category=Replicated is not "Replicated").</summary>
        public static bool HasSpecifier(string specs, string key)
        {
            int depth = 0, start = 0;
            bool quote = false;
            for (int i = 0; i <= specs.Length; i++)
            {
                char ch = i < specs.Length ? specs[i] : ',';
                if (ch == '"') quote = !quote;
                if (quote) continue;
                if (ch == '(') depth++;
                else if (ch == ')') depth--;
                else if (ch == ',' && depth == 0)
                {
                    string item = specs.Substring(start, i - start);
                    int eq = item.IndexOf('=');
                    if (string.Equals((eq < 0 ? item : item.Substring(0, eq)).Trim(), key, StringComparison.OrdinalIgnoreCase)) return true;
                    start = i + 1;
                }
            }
            return false;
        }

        /// <summary>Where member declarations go: after GENERATED_BODY() (with a "public:" line), else after '{'.</summary>
        private static TextInsertion MemberInsertion(string text, ClassAtCaret c, string nl, IEnumerable<string> lines)
        {
            var sb = new StringBuilder();
            int at;
            if (c.AfterGeneratedBody >= 0)
            {
                at = c.AfterGeneratedBody;
                if (at > 0 && text[at - 1] != '\n') sb.Append(nl);
            }
            else
            {
                at = c.BodyOpen + 1;
                sb.Append(nl);
            }
            string outer = c.MemberIndent.Length > 0 ? c.MemberIndent.Substring(0, c.MemberIndent.Length - 1) : string.Empty;
            sb.Append(nl).Append(outer).Append("public:").Append(nl);
            foreach (var l in lines) sb.Append(c.MemberIndent).Append(l).Append(nl);
            if (c.AfterGeneratedBody < 0) sb.Length -= nl.Length;
            return new TextInsertion(at, sb.ToString());
        }

        /// <summary>Appends definitions at the end of the source text (one blank line before).</summary>
        internal static TextInsertion AppendToSource(string source, string nl, string block)
        {
            source = source ?? string.Empty;
            var sb = new StringBuilder();
            if (source.Length > 0 && !source.EndsWith("\n", StringComparison.Ordinal)) sb.Append(nl);
            if (source.Trim().Length > 0) sb.Append(nl);
            sb.Append(block).Append(nl);
            return new TextInsertion(source.Length, sb.ToString());
        }

        /// <summary>#include "<paramref name="header"/>" after the last #include of the source (or at the top), when not there yet.</summary>
        public static TextInsertion EnsureInclude(string source, string header, string nl)
        {
            source = source ?? string.Empty;
            if (Regex.IsMatch(source, @"#\s*include\s*[""<]" + Regex.Escape(header) + @"["">]")) return null;
            int at = 0;
            foreach (Match m in Regex.Matches(source, @"^[ \t]*#\s*include[^\n]*\n?", RegexOptions.Multiline)) at = m.Index + m.Length;
            string pre = at > 0 && source[at - 1] != '\n' ? nl : string.Empty;
            return new TextInsertion(at, pre + "#include \"" + header + "\"" + nl);
        }

        // ================================================================== generators

        public static GenerateResult Constructor(string header, int caret, string source, string nl)
        {
            var c = FindClass(header, caret);
            if (c == null) return GenerateResult.Fail("no-class");
            if (DeclaresFunction(header, c, c.Name)) return GenerateResult.Fail("exists");
            var r = new GenerateResult { ClassName = c.Name };
            r.Header.Add(MemberInsertion(header, c, nl, new[] { c.Name + "();" }));
            if (source != null)
            {
                string body = c.Name + "::" + c.Name + "()" + nl + "{" + nl;
                if (c.Name.StartsWith("A", StringComparison.Ordinal) && c.Keyword == "class") body += "\tPrimaryActorTick.bCanEverTick = false;" + nl;
                body += "}";
                r.Source.Add(AppendToSource(source, nl, body));
            }
            r.Generated.Add(c.Name + "()");
            return r;
        }

        public static GenerateResult Replication(string header, int caret, string source, string nl)
        {
            var c = FindClass(header, caret);
            if (c == null) return GenerateResult.Fail("no-class");
            var props = ReplicatedProperties(header, c);
            if (props.Count == 0) return GenerateResult.Fail("no-replicated");
            var r = new GenerateResult { ClassName = c.Name };
            const string fn = "GetLifetimeReplicatedProps";
            if (!DeclaresFunction(header, c, fn))
                r.Header.Add(MemberInsertion(header, c, nl, new[] { "virtual void " + fn + "(TArray<FLifetimeProperty>& OutLifetimeProps) const override;" }));
            if (source != null)
            {
                string blank = Blank(source);
                var def = new Regex(@"\b" + Regex.Escape(c.Name) + @"\s*::\s*" + fn + @"\s*\([^)]*\)\s*const\s*\{").Match(blank);
                if (def.Success)
                {
                    int close = MatchBrace(blank, def.Index + def.Length - 1);
                    if (close < 0) return GenerateResult.Fail("no-class");
                    string body = source.Substring(def.Index, close - def.Index);
                    var missing = props.Where(p => !Regex.IsMatch(body, @"\b(?:DOREPLIFETIME\w*|DISABLE_REPLICATED_\w*)\s*\(\s*\w+\s*,\s*" + Regex.Escape(p) + @"\b")).ToList();
                    if (missing.Count == 0 && r.Header.Count == 0) return GenerateResult.Fail("exists");
                    if (missing.Count > 0)
                    {
                        int lineStart = source.LastIndexOf('\n', Math.Max(0, close - 1)) + 1;
                        var sb = new StringBuilder();
                        if (source.Substring(lineStart, close - lineStart).Trim().Length > 0) sb.Append(nl);
                        foreach (var p in missing) sb.Append('\t').Append("DOREPLIFETIME(").Append(c.Name).Append(", ").Append(p).Append(");").Append(nl);
                        r.Source.Add(new TextInsertion(lineStart, sb.ToString()));
                        r.Generated.AddRange(missing);
                    }
                }
                else
                {
                    var sb = new StringBuilder();
                    sb.Append("void ").Append(c.Name).Append("::").Append(fn).Append("(TArray<FLifetimeProperty>& OutLifetimeProps) const").Append(nl).Append('{').Append(nl);
                    sb.Append("\tSuper::").Append(fn).Append("(OutLifetimeProps);").Append(nl).Append(nl);
                    foreach (var p in props) sb.Append("\tDOREPLIFETIME(").Append(c.Name).Append(", ").Append(p).Append(");").Append(nl);
                    sb.Append('}');
                    r.Source.Add(AppendToSource(source, nl, sb.ToString()));
                    r.Generated.AddRange(props);
                }
                var inc = EnsureInclude(source, "Net/UnrealNetwork.h", nl);
                if (inc != null && r.Source.Count > 0) r.Source.Add(inc);
            }
            if (!r.Ok) return GenerateResult.Fail("exists");
            return r;
        }

        /// <summary>The member function declaration around <paramref name="caret"/>: UFUNCTION specifiers, return type, name, parameters, end offset.</summary>
        public sealed class FunctionDecl
        {
            public string Specifiers { get; set; }
            public string ReturnType { get; set; }
            public string Name { get; set; }
            public string Parameters { get; set; }
            public bool IsConst { get; set; }
            public int End { get; set; }
            public int Start { get; set; }
            public string Indent { get; set; }
        }

        private static readonly Regex FunctionRe = new Regex(
            @"^\s*(?:UFUNCTION\s*\((?<spec>(?:[^()]|\((?:[^()]|\([^()]*\))*\))*)\)\s*)?(?:(?:virtual|static|FORCEINLINE|inline)\s+)*(?<ret>[\w:<>,\s\*&]+?)\s*\b(?<name>\w+)\s*\((?<params>(?:[^()]|\([^()]*\))*)\)\s*(?<const>const)?\s*(?:override|final|\s)*;\s*$",
            RegexOptions.Compiled | RegexOptions.CultureInvariant | RegexOptions.Singleline);

        public static FunctionDecl FunctionAt(string header, ClassAtCaret c, int caret)
        {
            string b = Blank(header);
            caret = Math.Max(c.BodyOpen + 1, Math.Min(caret, c.BodyClose));
            int start = caret;
            while (start > c.BodyOpen + 1 && b[start - 1] != ';' && b[start - 1] != '{' && b[start - 1] != '}' && !(b[start - 1] == ':' && start >= 2 && IsAccessLabel(b, start - 1))) start--;
            int end = b.IndexOf(';', caret > start ? caret - 1 : caret);
            if (end < 0 || end > c.BodyClose) return null;
            string stmt = header.Substring(start, end - start + 1);
            if (Blank(stmt).IndexOf('{') >= 0) return null;
            var m = FunctionRe.Match(Blank(stmt));
            if (!m.Success) return null;
            string ret = stmt.Substring(m.Groups["ret"].Index, m.Groups["ret"].Length).Trim();
            if (ret.Length == 0 || ret == "return" || ret.EndsWith("::", StringComparison.Ordinal)) return null;
            int ls = header.LastIndexOf('\n', Math.Max(0, start + m.Groups["ret"].Index - 1)) + 1;
            string indent = header.Substring(ls, start + m.Groups["ret"].Index - ls);
            if (indent.Trim().Length != 0) indent = c.MemberIndent;
            return new FunctionDecl
            {
                Specifiers = m.Groups["spec"].Success ? stmt.Substring(m.Groups["spec"].Index, m.Groups["spec"].Length) : null,
                ReturnType = ret,
                Name = m.Groups["name"].Value,
                Parameters = stmt.Substring(m.Groups["params"].Index, m.Groups["params"].Length).Trim(),
                IsConst = m.Groups["const"].Success,
                Start = start,
                End = end + 1,
                Indent = indent,
            };
        }

        private static bool IsAccessLabel(string b, int colon)
        {
            int e = colon;
            int s = e;
            while (s > 0 && char.IsLetter(b[s - 1])) s--;
            string w = b.Substring(s, e - s);
            return w == "public" || w == "protected" || w == "private";
        }

        /// <summary>Parameter list without default values (for definitions).</summary>
        public static string StripDefaults(string parameters)
        {
            if (string.IsNullOrWhiteSpace(parameters)) return string.Empty;
            var parts = new List<string>();
            int depth = 0, start = 0;
            for (int i = 0; i <= parameters.Length; i++)
            {
                char ch = i < parameters.Length ? parameters[i] : ',';
                if (ch == '(' || ch == '<' || ch == '{' || ch == '[') depth++;
                else if (ch == ')' || ch == '>' || ch == '}' || ch == ']') depth--;
                else if (ch == ',' && depth == 0)
                {
                    string p = parameters.Substring(start, i - start);
                    int eq = TopLevelEquals(p);
                    if (eq >= 0) p = p.Substring(0, eq);
                    parts.Add(Regex.Replace(p.Trim(), @"\s+", " "));
                    start = i + 1;
                }
            }
            return string.Join(", ", parts.Where(p => p.Length > 0));
        }

        private static int TopLevelEquals(string p)
        {
            int depth = 0;
            for (int i = 0; i < p.Length; i++)
            {
                char ch = p[i];
                if (ch == '(' || ch == '<' || ch == '{' || ch == '[') depth++;
                else if (ch == ')' || ch == '>' || ch == '}' || ch == ']') depth--;
                else if (ch == '=' && depth == 0) return i;
            }
            return -1;
        }

        /// <summary>
        /// RPC trio. On a Server / Client / NetMulticast UFUNCTION declaration: the missing <c>_Implementation</c> (and <c>_Validate</c>
        /// with WithValidation) definitions. On any other function declaration <c>void Fire(…)</c>: a new
        /// <c>UFUNCTION(Server, Reliable, WithValidation) void ServerFire(…)</c> below it plus both definitions.
        /// </summary>
        public static GenerateResult Rpc(string header, int caret, string source, string nl)
        {
            var c = FindClass(header, caret);
            if (c == null) return GenerateResult.Fail("no-class");
            var f = FunctionAt(header, c, caret);
            if (f == null) return GenerateResult.Fail("no-function");
            var r = new GenerateResult { ClassName = c.Name };
            string rpcName = f.Name;
            bool validate;
            string spec = f.Specifiers ?? string.Empty;
            bool isRpc = HasSpecifier(spec, "Server") || HasSpecifier(spec, "Client") || HasSpecifier(spec, "NetMulticast");
            string ps = StripDefaults(f.Parameters);
            if (isRpc)
            {
                validate = HasSpecifier(spec, "WithValidation");
            }
            else
            {
                rpcName = f.Name.StartsWith("Server", StringComparison.Ordinal) ? f.Name + "Rpc" : "Server" + f.Name;
                if (DeclaresFunction(header, c, rpcName)) return GenerateResult.Fail("exists");
                validate = true;
                string decl = nl + nl + f.Indent + "UFUNCTION(Server, Reliable, WithValidation)" + nl + f.Indent + "void " + rpcName + "(" + f.Parameters + ");";
                r.Header.Add(new TextInsertion(f.End, decl));
                r.Generated.Add(rpcName);
            }
            if (source != null)
            {
                string blank = Blank(source);
                var sb = new StringBuilder();
                bool HasDef(string n) => Regex.IsMatch(blank, @"\b" + Regex.Escape(c.Name) + @"\s*::\s*" + Regex.Escape(n) + @"\s*\(");
                string impl = rpcName + "_Implementation";
                if (!HasDef(impl))
                {
                    sb.Append("void ").Append(c.Name).Append("::").Append(impl).Append('(').Append(ps).Append(')').Append(nl).Append('{').Append(nl);
                    if (!isRpc) sb.Append('\t').Append(f.Name).Append('(').Append(ArgumentNames(ps)).Append(");").Append(nl);
                    sb.Append('}');
                    r.Generated.Add(impl);
                }
                string val = rpcName + "_Validate";
                if (validate && !HasDef(val))
                {
                    if (sb.Length > 0) sb.Append(nl).Append(nl);
                    sb.Append("bool ").Append(c.Name).Append("::").Append(val).Append('(').Append(ps).Append(')').Append(nl).Append('{').Append(nl)
                      .Append("\treturn true;").Append(nl).Append('}');
                    r.Generated.Add(val);
                }
                if (sb.Length > 0) r.Source.Add(AppendToSource(source, nl, sb.ToString()));
            }
            if (!r.Ok) return GenerateResult.Fail("exists");
            return r;
        }

        /// <summary>"int32 A, const FVector& B" → "A, B".</summary>
        public static string ArgumentNames(string parameters)
        {
            var names = new List<string>();
            foreach (var p in (parameters ?? string.Empty).Split(','))
            {
                var m = Regex.Match(p, @"(\w+)\s*(?:\[[^\]]*\])?\s*$");
                if (m.Success && p.Trim() != "void") names.Add(m.Groups[1].Value);
            }
            return string.Join(", ", names);
        }
    }
}
