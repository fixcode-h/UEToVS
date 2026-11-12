using System;
using System.Collections.Generic;
using System.Text;
using System.Threading;
using UEToVS.Contracts;
using UEToVS.Engine.Syntax;

namespace UEToVS.Engine.Navigation
{
    /// <summary>A local variable or parameter found by a token scan of a function.</summary>
    public sealed class LocalVariable
    {
        public string Name { get; set; }
        public int Offset { get; set; }

        /// <summary>Type text as written (<c>auto</c> is replaced by the template argument of a Cast&lt;T&gt;-style initializer when present).</summary>
        public string Type { get; set; }

        /// <summary>Index of the function declaration in <see cref="DocumentModel.Decls"/>.</summary>
        public int Function { get; set; }

        public bool IsParameter { get; set; }

        /// <summary>Byte offset where the local goes out of scope (closing brace of its block).</summary>
        public int ScopeEnd { get; set; } = int.MaxValue;
    }

    /// <summary>
    /// One parsed document (editor buffer or file on disk): UTF-8 text, the parser summary, the token stream and lazily
    /// computed per-function locals. Immutable after construction except for the lazy caches (thread-safe).
    /// Offsets are UTF-8 byte offsets into <see cref="Text"/> (a UTF-8 BOM is stripped, UTF-16 transcoded).
    /// </summary>
    public sealed class DocumentModel
    {
        private readonly Dictionary<int, int> _declByOffset = new Dictionary<int, int>();
        private readonly Dictionary<int, OccurrenceRole> _occByOffset = new Dictionary<int, OccurrenceRole>();
        private readonly List<LocalVariable>[] _locals;
        private readonly string[] _tokenText;
        private readonly HashSet<string> _namespaces = new HashSet<string>(StringComparer.Ordinal);

        private DocumentModel(string path, long version, byte[] text, FileSummary summary, Token[] tokens)
        {
            Path = path;
            Version = version;
            Text = text;
            Summary = summary;
            Tokens = tokens;
            _tokenText = new string[tokens.Length];
            _locals = new List<LocalVariable>[summary.Declarations.Count];
            for (int i = 0; i < summary.Declarations.Count; i++)
            {
                var d = summary.Declarations[i];
                if (d.Kind == SymbolKind.Namespace) _namespaces.Add(d.QualifiedName);
                if (d.IsImplicit || string.IsNullOrEmpty(d.Name)) continue;
                if (!_declByOffset.ContainsKey(d.Offset)) _declByOffset[d.Offset] = i;
            }
            foreach (var o in summary.Occurrences) _occByOffset[o.Offset] = o.Role;
            ApproxBytes = 256 + text.Length * 2L + tokens.Length * 12L + summary.Declarations.Count * 200L + summary.Occurrences.Count * 24L;
        }

        public string Path { get; }

        /// <summary>Editor TextVersion, or the file's mtime ticks for disk reads.</summary>
        public long Version { get; }

        public byte[] Text { get; }
        public FileSummary Summary { get; }
        public Token[] Tokens { get; }
        public IReadOnlyList<Declaration> Decls => Summary.Declarations;
        public LineMap Lines => Summary.Lines;
        public long ApproxBytes { get; }

        public static DocumentModel Parse(string path, long version, byte[] raw)
        {
            raw ??= Array.Empty<byte>();
            byte[] text = raw;
            if (raw.Length >= 3 && raw[0] == 0xEF && raw[1] == 0xBB && raw[2] == 0xBF) text = raw.AsSpan(3).ToArray();
            var summary = CppParser.Parse(text);
            if (summary.TranscodedText != null)
            {
                text = summary.TranscodedText.Length == summary.TextLength ? summary.TranscodedText : summary.TranscodedText.AsSpan(0, summary.TextLength).ToArray();
            }
            var tokens = CppParser.Tokenize(text).ToArray();
            return new DocumentModel(path, version, text, summary, tokens);
        }

        public static DocumentModel Parse(string path, long version, string text) => Parse(path, version, Encoding.UTF8.GetBytes(text ?? string.Empty));

        // ------------------------------------------------------------------ positions

        public int OffsetOf(int line, int column) => Lines.GetOffset(Text, line, column);

        public (int Line, int Column) PositionOf(int offset) => Lines.GetPosition(Text, offset);

        /// <summary>Text of one line without the line break.</summary>
        public string LineText(int line)
        {
            if (line < 0 || line >= Lines.LineCount) return string.Empty;
            int start = Lines.GetLineStart(line);
            int end = line + 1 < Lines.LineCount ? Lines.GetLineStart(line + 1) : Text.Length;
            while (end > start && (Text[end - 1] == '\n' || Text[end - 1] == '\r')) end--;
            return Encoding.UTF8.GetString(Text, start, end - start);
        }

        // ------------------------------------------------------------------ tokens

        public string TokenText(int i)
        {
            var s = _tokenText[i];
            if (s != null) return s;
            s = Encoding.UTF8.GetString(Text, Tokens[i].Start, Tokens[i].Length);
            _tokenText[i] = s;
            return s;
        }

        public bool IsIdentifier(int i) => i >= 0 && i < Tokens.Length && Tokens[i].Kind == TokenKind.Identifier;

        public bool Is(int i, Punct p) => i >= 0 && i < Tokens.Length && Tokens[i].Is(p);

        public bool Is(int i, Keyword k) => i >= 0 && i < Tokens.Length && Tokens[i].Is(k);

        /// <summary>
        /// Index of the token under the caret. A caret right after an identifier (<c>Foo|</c>) counts as on it.
        /// -1 when the caret is in whitespace or a comment.
        /// </summary>
        public int TokenAt(int offset)
        {
            int lo = 0, hi = Tokens.Length - 1, found = -1;
            while (lo <= hi)
            {
                int mid = (lo + hi) >>> 1;
                if (Tokens[mid].Start <= offset) { found = mid; lo = mid + 1; }
                else hi = mid - 1;
            }
            if (found >= 0 && offset < Tokens[found].End) return found;
            if (found >= 0 && offset == Tokens[found].End && (Tokens[found].Kind == TokenKind.Identifier || Tokens[found].Kind == TokenKind.Keyword)) return found;
            return -1;
        }

        /// <summary>Index of the first token starting at or after <paramref name="offset"/>.</summary>
        public int TokenIndexFrom(int offset)
        {
            int lo = 0, hi = Tokens.Length;
            while (lo < hi)
            {
                int mid = (lo + hi) >>> 1;
                if (Tokens[mid].Start < offset) lo = mid + 1;
                else hi = mid;
            }
            return lo;
        }

        /// <summary>Index of the matching opening bracket scanning backwards from a closing one (or -1).</summary>
        public int MatchBackward(int close, Punct open, Punct closeP)
        {
            int depth = 0;
            for (int i = close; i >= 0; i--)
            {
                if (Tokens[i].Is(closeP)) depth++;
                else if (Tokens[i].Is(open) && --depth == 0) return i;
                else if (Tokens[i].Is(Punct.LBrace) || Tokens[i].Is(Punct.Semicolon)) { if (open != Punct.LBrace) return -1; }
            }
            return -1;
        }

        // ------------------------------------------------------------------ declarations

        /// <summary>Non-implicit declaration whose name token starts at <paramref name="offset"/>, or -1.</summary>
        public int DeclAtOffset(int offset) => _declByOffset.TryGetValue(offset, out int i) ? i : -1;

        public OccurrenceRole OccurrenceRoleAt(int offset) => _occByOffset.TryGetValue(offset, out var r) ? r : OccurrenceRole.None;

        public bool IsKnownNamespace(string qualified) => qualified != null && _namespaces.Contains(qualified);

        /// <summary>The byte range a declaration "covers" for scope purposes (namespace body, class from its name to the closing brace, function from its name to the end of its body).</summary>
        public TextRange ScopeRange(int declIndex)
        {
            var d = Decls[declIndex];
            if (d.Body.IsNone) return TextRange.None;
            // namespaces and types: their body (a class's base clause is not inside the class); functions: from the name
            // (parameters are in scope) to the end of the body
            if (!TypeText.IsCallableKind(d.Kind)) return d.Body;
            int start = Math.Min(d.Offset, d.Body.Start);
            return new TextRange(start, d.Body.End - start);
        }

        public static bool IsScopeKind(SymbolKind k) => k == SymbolKind.Namespace || TypeText.IsRecordKind(k) || k == SymbolKind.Enum || TypeText.IsCallableKind(k);

        private int[] _scopeOrder;   // scope declarations sorted by (start asc, end desc)
        private int[] _scopeParent;  // index into _scopeOrder of the enclosing scope, or -1
        private int[] _scopeStarts;

        private void EnsureScopeTree()
        {
            if (Volatile.Read(ref _scopeStarts) != null) return;
            var order = new List<int>();
            for (int i = 0; i < Decls.Count; i++)
            {
                var d = Decls[i];
                if (d.IsImplicit || !IsScopeKind(d.Kind) || ScopeRange(i).IsNone) continue;
                order.Add(i);
            }
            order.Sort((a, b) =>
            {
                var ra = ScopeRange(a);
                var rb = ScopeRange(b);
                int c = ra.Start.CompareTo(rb.Start);
                return c != 0 ? c : rb.End.CompareTo(ra.End);
            });
            var parent = new int[order.Count];
            var starts = new int[order.Count];
            var stack = new Stack<int>();
            for (int k = 0; k < order.Count; k++)
            {
                var r = ScopeRange(order[k]);
                starts[k] = r.Start;
                while (stack.Count > 0 && ScopeRange(order[stack.Peek()]).End <= r.Start) stack.Pop();
                parent[k] = stack.Count > 0 ? stack.Peek() : -1;
                stack.Push(k);
            }
            _scopeOrder = order.ToArray();
            _scopeParent = parent;
            Volatile.Write(ref _scopeStarts, starts);
        }

        /// <summary>Declarations whose scope contains the offset, outer → inner. O(log n + depth).</summary>
        public List<int> EnclosingScopes(int offset)
        {
            EnsureScopeTree();
            var list = new List<int>();
            int k = Array.BinarySearch(_scopeStarts, offset);
            if (k < 0) k = ~k - 1;
            else while (k + 1 < _scopeStarts.Length && _scopeStarts[k + 1] == offset) k++;
            while (k >= 0)
            {
                var r = ScopeRange(_scopeOrder[k]);
                if (offset >= r.Start && offset < r.End) break;
                k = _scopeParent[k];
            }
            for (; k >= 0; k = _scopeParent[k])
            {
                var r = ScopeRange(_scopeOrder[k]);
                if (offset >= r.Start && offset < r.End) list.Add(_scopeOrder[k]);
            }
            list.Reverse();
            return list;
        }

        /// <summary>Innermost function (with a body) containing the offset, or -1.</summary>
        public int EnclosingFunction(int offset)
        {
            var scopes = EnclosingScopes(offset);
            for (int k = scopes.Count - 1; k >= 0; k--)
                if (TypeText.IsCallableKind(Decls[scopes[k]].Kind) && !Decls[scopes[k]].Body.IsNone) return scopes[k];
            return -1;
        }

        /// <summary>
        /// Qualified name of the class whose members are in scope at the offset: the innermost class body, otherwise the
        /// container of the enclosing (out-of-line) function unless that container is a namespace.
        /// </summary>
        public string EnclosingClass(int offset, out int classDecl)
        {
            classDecl = -1;
            string fromFunction = null;
            var scopes = EnclosingScopes(offset);
            for (int k = scopes.Count - 1; k >= 0; k--)
            {
                var d = Decls[scopes[k]];
                if (TypeText.IsRecordKind(d.Kind))
                {
                    classDecl = scopes[k];
                    return d.QualifiedName;
                }
                if (fromFunction == null && TypeText.IsCallableKind(d.Kind) && !string.IsNullOrEmpty(d.Container) && !IsKnownNamespace(d.Container))
                    fromFunction = d.Container;
            }
            return fromFunction;
        }

        /// <summary>Namespace path at the offset (innermost namespace, or the namespace part of an out-of-line function's container).</summary>
        public string EnclosingNamespace(int offset)
        {
            var scopes = EnclosingScopes(offset);
            for (int k = scopes.Count - 1; k >= 0; k--)
            {
                var d = Decls[scopes[k]];
                if (d.Kind == SymbolKind.Namespace) return d.QualifiedName;
                if (TypeText.IsRecordKind(d.Kind) || TypeText.IsCallableKind(d.Kind))
                {
                    string c = d.Container;
                    while (!string.IsNullOrEmpty(c) && !IsKnownNamespace(c) && TypeText.IsCallableKind(d.Kind)) c = TypeText.Parent(c);
                    if (!string.IsNullOrEmpty(c) && IsKnownNamespace(c)) return c;
                    if (TypeText.IsRecordKind(d.Kind)) return d.Container;
                }
            }
            return null;
        }

        // ------------------------------------------------------------------ locals

        private static readonly HashSet<string> TypeKeywords = new HashSet<string>(StringComparer.Ordinal)
        {
            "auto", "bool", "char", "short", "int", "long", "float", "double", "unsigned", "signed", "wchar_t", "char16_t", "char32_t", "char8_t", "void",
        };

        private static readonly HashSet<string> DeclPrefixKeywords = new HashSet<string>(StringComparer.Ordinal)
        {
            "const", "static", "constexpr", "volatile", "mutable", "register", "thread_local", "class", "struct", "typename", "enum", "inline",
        };

        /// <summary>Locals and parameters of a function declaration (empty for non-functions). Computed once.</summary>
        public IReadOnlyList<LocalVariable> LocalsOf(int function)
        {
            if (function < 0 || function >= Decls.Count) return Array.Empty<LocalVariable>();
            var cached = System.Threading.Volatile.Read(ref _locals[function]);
            if (cached != null) return cached;
            var list = ScanLocals(function);
            System.Threading.Volatile.Write(ref _locals[function], list);
            return list;
        }

        private List<LocalVariable> ScanLocals(int function)
        {
            var list = new List<LocalVariable>();
            var d = Decls[function];
            if (!TypeText.IsCallableKind(d.Kind) || d.Body.IsNone) return list;
            int first = TokenIndexFrom(d.Offset);
            int bodyStart = d.Body.Start;
            int end = d.Body.End;
            var blocks = new Stack<int>(); // open '{' token indices
            for (int j = first + 1; j < Tokens.Length && Tokens[j].Start < end; j++)
            {
                if (Tokens[j].Is(Punct.LBrace)) { blocks.Push(j); continue; }
                if (Tokens[j].Is(Punct.RBrace)) { if (blocks.Count > 0) blocks.Pop(); continue; }
                if (Tokens[j].Kind != TokenKind.Identifier) continue;
                bool inParams = Tokens[j].Start < bodyStart;
                if (!LooksLikeDeclarator(j, inParams, out int typeStart)) continue;
                string type = TokensText(typeStart, j);
                if (type == "auto" || type.StartsWith("auto", StringComparison.Ordinal) || type.StartsWith("const auto", StringComparison.Ordinal))
                {
                    string inferred = InferFromInitializer(j);
                    if (inferred != null) type = inferred + "*";
                }
                int scopeEnd = inParams || blocks.Count == 0 ? end : ClosingBraceEnd(blocks.Peek(), end);
                list.Add(new LocalVariable { Name = TokenText(j), Offset = Tokens[j].Start, Type = type, Function = function, IsParameter = inParams, ScopeEnd = scopeEnd });
            }
            return list;
        }

        /// <summary>Text of tokens [from, to) joined with single spaces where the source had whitespace or comments.</summary>
        public string TokensText(int from, int to)
        {
            var sb = new StringBuilder();
            for (int k = from; k < to && k < Tokens.Length; k++)
            {
                if (k > from && (Tokens[k].Flags & TokenFlags.PrecededBySpace) != 0) sb.Append(' ');
                sb.Append(TokenText(k));
            }
            return sb.ToString();
        }

        /// <summary>
        /// <c>Type Name</c> followed by <c>= ; , ) [ { :</c> (or <c>(</c> for direct initialisation) where the token before the
        /// type is a statement boundary. Returns the token index where the type starts.
        /// </summary>
        private bool LooksLikeDeclarator(int j, bool inParams, out int typeStart)
        {
            typeStart = -1;
            int n = j + 1;
            if (n >= Tokens.Length) return false;
            var next = Tokens[n];
            bool okNext = next.Is(Punct.Eq) || next.Is(Punct.Semicolon) || next.Is(Punct.Comma) || next.Is(Punct.RParen) ||
                          next.Is(Punct.LBracket) || next.Is(Punct.LBrace) || next.Is(Punct.Colon) || next.Is(Punct.LParen);
            if (!okNext) return false;
            if (next.Is(Punct.Colon) && Is(n + 1, Punct.Colon)) return false;

            int p = j - 1;
            while (p >= 0 && (Tokens[p].Is(Punct.Star) || Tokens[p].Is(Punct.Amp) || Tokens[p].Is(Punct.AmpAmp) || IsKeywordText(p, "const")))
                p--;
            if (p < 0) return false;
            int typeEnd = p;
            // the type: identifier (possibly A::B, possibly with template args) or a built-in type keyword sequence
            if (Tokens[p].Is(Punct.Gt) || Tokens[p].Is(Punct.GtGt))
            {
                int depth = 0;
                for (; p >= 0; p--)
                {
                    if (Tokens[p].Is(Punct.Gt)) depth++;
                    else if (Tokens[p].Is(Punct.GtGt)) depth += 2;
                    else if (Tokens[p].Is(Punct.Lt)) { if (--depth == 0) break; }
                    else if (Tokens[p].Is(Punct.Semicolon) || Tokens[p].Is(Punct.LBrace) || Tokens[p].Is(Punct.RBrace)) return false;
                }
                p--;
                if (p < 0 || Tokens[p].Kind != TokenKind.Identifier) return false;
            }
            else if (Tokens[p].Kind == TokenKind.Keyword)
            {
                if (!TypeKeywords.Contains(TokenText(p))) return false;
                while (p - 1 >= 0 && Tokens[p - 1].Kind == TokenKind.Keyword && TypeKeywords.Contains(TokenText(p - 1))) p--;
            }
            else if (Tokens[p].Kind != TokenKind.Identifier) return false;
            else if (Tokens[p].Flags.HasFlag(TokenFlags.MacroLike) && !inParams && TokenText(p).EndsWith("_API", StringComparison.Ordinal)) return false;

            // qualified type A::B::C (and, v0.4.5, dependent members TBox<T>::ValueType)
            while (p - 2 >= 0 && Tokens[p - 1].Is(Punct.ColonColon))
            {
                if (Tokens[p - 2].Kind == TokenKind.Identifier) { p -= 2; continue; }
                if (!Tokens[p - 2].Is(Punct.Gt) && !Tokens[p - 2].Is(Punct.GtGt)) break;
                int q = p - 2, angle = 0;
                for (; q >= 0; q--)
                {
                    if (Tokens[q].Is(Punct.Gt)) angle++;
                    else if (Tokens[q].Is(Punct.GtGt)) angle += 2;
                    else if (Tokens[q].Is(Punct.Lt)) { if (--angle == 0) break; }
                    else if (Tokens[q].Is(Punct.Semicolon) || Tokens[q].Is(Punct.LBrace) || Tokens[q].Is(Punct.RBrace)) return false;
                }
                if (q - 1 < 0 || Tokens[q - 1].Kind != TokenKind.Identifier) return false;
                p = q - 1;
            }
            if (p - 1 >= 0 && Tokens[p - 1].Is(Punct.ColonColon)) p--;
            // leading cv / storage keywords belong to the type
            while (p - 1 >= 0 && Tokens[p - 1].Kind == TokenKind.Keyword && DeclPrefixKeywords.Contains(TokenText(p - 1))) p--;
            typeStart = p;
            if (typeStart == j || typeEnd >= j) return false;

            int b = p - 1;
            if (b < 0) return false;
            var before = Tokens[b];
            if (inParams) return before.Is(Punct.LParen) || before.Is(Punct.Comma);
            if (next.Is(Punct.LParen) && Tokens[typeEnd].Kind == TokenKind.Identifier && typeEnd == j - 1 && !before.Is(Punct.LBrace) &&
                !before.Is(Punct.Semicolon) && !before.Is(Punct.RBrace))
                return false;
            if (before.Is(Punct.LBrace) || before.Is(Punct.Semicolon) || before.Is(Punct.RBrace)) return true;
            if (before.Is(Punct.Colon)) return b > 0 && (Is(b - 1, Keyword.Public) || Is(b - 1, Keyword.Private) || Is(b - 1, Keyword.Protected) || IsKeywordText(b - 1, "default"));
            if (before.Is(Punct.LParen))
            {
                // for (Type X : ...), if (Type* X = ...), while (...), switch (...), catch (...)
                return b > 0 && Tokens[b - 1].Kind == TokenKind.Keyword && TokenText(b - 1) is "for" or "if" or "while" or "switch" or "catch";
            }
            if (before.Is(Punct.RParen))
            {
                // else / do bodies without braces are rare; `if (x) Type Y;` is not valid without braces in practice
                return false;
            }
            return before.Kind == TokenKind.Keyword && TokenText(b) is "else" or "do";
        }

        private bool IsKeywordText(int i, string text) => i >= 0 && i < Tokens.Length && Tokens[i].Kind == TokenKind.Keyword && TokenText(i) == text;

        /// <summary><c>auto* X = Cast&lt;T&gt;(...)</c> → <c>T</c>.</summary>
        private string InferFromInitializer(int nameToken)
        {
            int i = nameToken + 1;
            if (!Is(i, Punct.Eq) && !Is(i, Punct.LBrace) && !Is(i, Punct.LParen)) return null;
            for (int k = i + 1; k < Tokens.Length && k < i + 40; k++)
            {
                if (Tokens[k].Is(Punct.Semicolon)) break;
                if (Tokens[k].Kind == TokenKind.Identifier && Is(k + 1, Punct.Lt) && TypeText.TypedFactories.Contains(TokenText(k)))
                {
                    int t = k + 2;
                    while (Is(t, Punct.ColonColon) || IsIdentifier(t) && Is(t + 1, Punct.ColonColon)) t++;
                    if (IsIdentifier(t)) return TokenText(t);
                }
            }
            return null;
        }

        /// <summary>The local (or parameter) named <paramref name="name"/> visible at <paramref name="offset"/> in <paramref name="function"/>.</summary>
        public LocalVariable FindLocal(int function, string name, int offset)
        {
            if (function < 0) return null;
            LocalVariable best = null;
            foreach (var l in LocalsOf(function))
            {
                if (l.Offset > offset || !string.Equals(l.Name, name, StringComparison.Ordinal)) continue;
                if (offset >= l.ScopeEnd) continue;
                if (best == null || l.Offset > best.Offset) best = l;
            }
            return best;
        }

        /// <summary>End offset of the '}' matching the '{' at token <paramref name="open"/> (or <paramref name="fallback"/>).</summary>
        private int ClosingBraceEnd(int open, int fallback)
        {
            int depth = 0;
            for (int k = open; k < Tokens.Length; k++)
            {
                if (Tokens[k].Is(Punct.LBrace)) depth++;
                else if (Tokens[k].Is(Punct.RBrace) && --depth == 0) return Tokens[k].End;
            }
            return fallback;
        }
    }
}
