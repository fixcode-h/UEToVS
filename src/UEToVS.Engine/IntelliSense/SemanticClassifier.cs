using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Runtime.CompilerServices;
using UEToVS.Contracts;
using UEToVS.Engine.Navigation;
using UEToVS.Engine.Syntax;

namespace UEToVS.Engine.IntelliSense
{
    /// <summary>
    /// VA-style semantic colouring of a line range. Per document version the spans of each requested line are cached; across
    /// versions of the same file the name → kind decisions are memoised, so typing re-classifies the visible lines without
    /// repeating index lookups. Only the requested range is processed.
    /// </summary>
    public sealed class SemanticClassifier
    {
        private const int MaxPaths = 32;
        private static readonly TimeSpan MemoLifetime = TimeSpan.FromSeconds(20);

        private sealed class PathState
        {
            public long Version = long.MinValue;
            public DocumentModel Doc;
            public readonly Dictionary<int, List<SemanticSpan>> Lines = new Dictionary<int, List<SemanticSpan>>();
            public Dictionary<string, (SemanticKind Kind, SemanticModifiers Mods)> Memo = new Dictionary<string, (SemanticKind, SemanticModifiers)>(StringComparer.Ordinal);
            public long MemoSince = Stopwatch.GetTimestamp();
            public long LastUse;
        }

        private readonly Dictionary<string, PathState> _paths = new Dictionary<string, PathState>(StringComparer.OrdinalIgnoreCase);
        private readonly ConditionalWeakTable<DocumentModel, DocFacts> _facts = new ConditionalWeakTable<DocumentModel, DocFacts>();
        private long _lineHits, _lineMisses, _memoHits, _memoMisses;

        public long LineHits => _lineHits;
        public long LineMisses => _lineMisses;
        public long MemoHits => _memoHits;
        public long MemoMisses => _memoMisses;

        private PathState StateFor(string path, DocumentModel doc)
        {
            lock (_paths)
            {
                if (!_paths.TryGetValue(path, out var st))
                {
                    if (_paths.Count >= MaxPaths)
                    {
                        var oldest = _paths.OrderBy(p => p.Value.LastUse).First().Key;
                        _paths.Remove(oldest);
                    }
                    st = new PathState();
                    _paths[path] = st;
                }
                st.LastUse = Stopwatch.GetTimestamp();
                return st;
            }
        }

        public ClassifyResponse Classify(IsSession s, int startLine, int endLine, out int computedLines, out int cachedLines)
        {
            computedLines = cachedLines = 0;
            var doc = s.Doc;
            var resp = new ClassifyResponse { TextVersion = doc.Version };
            int lineCount = doc.Lines.LineCount;
            startLine = Math.Max(0, startLine);
            endLine = Math.Min(lineCount - 1, Math.Max(startLine, endLine));
            if (lineCount == 0 || startLine > endLine) return resp;
            var st = StateFor(NavContext.NormalizePath(doc.Path), doc);
            lock (st)
            {
                if (!ReferenceEquals(st.Doc, doc))
                {
                    st.Doc = doc;
                    st.Version = doc.Version;
                    st.Lines.Clear();
                    // the memo survives edits for a while (decisions depend on names, not positions)
                    if (Stopwatch.GetTimestamp() - st.MemoSince > MemoLifetime.TotalSeconds * Stopwatch.Frequency || st.Memo.Count > 20000)
                    {
                        st.Memo = new Dictionary<string, (SemanticKind, SemanticModifiers)>(StringComparer.Ordinal);
                        st.MemoSince = Stopwatch.GetTimestamp();
                    }
                }
                int runStart = -1;
                for (int l = startLine; l <= endLine + 1; l++)
                {
                    bool have = l <= endLine && st.Lines.ContainsKey(l);
                    bool missing = l <= endLine && !have;
                    if (missing && runStart < 0) runStart = l;
                    if (!missing && runStart >= 0)
                    {
                        ComputeRange(s, st, runStart, l - 1);
                        computedLines += l - runStart;
                        runStart = -1;
                    }
                    if (have) cachedLines++;
                }
                for (int l = startLine; l <= endLine; l++)
                    if (st.Lines.TryGetValue(l, out var spans)) resp.Spans.AddRange(spans);
            }
            _lineHits += cachedLines;
            _lineMisses += computedLines;
            return resp;
        }

        private void ComputeRange(IsSession s, PathState st, int fromLine, int toLine)
        {
            var doc = s.Doc;
            for (int l = fromLine; l <= toLine; l++) st.Lines[l] = new List<SemanticSpan>();
            int start = doc.Lines.GetLineStart(fromLine);
            int end = toLine + 1 < doc.Lines.LineCount ? doc.Lines.GetLineStart(toLine + 1) : doc.Text.Length;
            var facts = _facts.GetValue(doc, d => new DocFacts(d));
            var (_, origin) = s.Ctx.FileOrigin(doc.Path);
            facts.External = origin == ItemOrigin.Engine || origin == ItemOrigin.EnginePlugin || origin == ItemOrigin.ThirdParty;

            // #define names in range
            foreach (int idx in facts.MacroDecls)
            {
                var d = doc.Decls[idx];
                if (d.Offset < start || d.Offset >= end) continue;
                Emit(doc, st, d.Offset, d.NameLength > 0 ? d.NameLength : d.Name.Length, IsUeMacro(s, d.Name) ? SemanticKind.UeMacro : SemanticKind.Macro, SemanticModifiers.Declaration);
            }

            int first = doc.TokenIndexFrom(start);
            for (int k = first; k < doc.Tokens.Length && doc.Tokens[k].Start < end; k++)
            {
                var t = doc.Tokens[k];
                if (t.Kind == TokenKind.Directive)
                {
                    DirectiveMacros(s, st, t);
                    continue;
                }
                if (t.Kind != TokenKind.Identifier) continue;
                var (kind, mods) = ClassifyToken(s, st, facts, k);
                if (kind == SemanticKind.None && mods == SemanticModifiers.None) continue;
                Emit(doc, st, t.Start, t.Length, kind, mods);
            }
        }

        private static void Emit(DocumentModel doc, PathState st, int offset, int byteLength, SemanticKind kind, SemanticModifiers mods)
        {
            var (line, col) = doc.PositionOf(offset);
            if (!st.Lines.TryGetValue(line, out var list)) return;
            list.Add(new SemanticSpan
            {
                Line = line,
                Column = col,
                Length = LineMap.Utf16Length(new ReadOnlySpan<byte>(doc.Text, offset, Math.Min(byteLength, doc.Text.Length - offset))),
                Kind = kind,
                Modifiers = mods,
            });
        }

        /// <summary>Identifiers after #ifdef / #ifndef / #if defined(...) / #undef that name macros.</summary>
        private void DirectiveMacros(IsSession s, PathState st, Token t)
        {
            var dk = t.Directive;
            if (dk != DirectiveKind.If && dk != DirectiveKind.Ifdef && dk != DirectiveKind.Ifndef && dk != DirectiveKind.Elif && dk != DirectiveKind.Undef &&
                dk != DirectiveKind.Elifdef && dk != DirectiveKind.Elifndef) return;
            var doc = s.Doc;
            var text = doc.Text;
            int i = t.Start;
            // skip '#' and the directive keyword
            while (i < t.End && (text[i] == '#' || text[i] == ' ' || text[i] == '\t')) i++;
            while (i < t.End && IsIdent(text[i])) i++;
            while (i < t.End)
            {
                if (text[i] == '/' && i + 1 < t.End && (text[i + 1] == '/' || text[i + 1] == '*')) break;
                if (IsIdent(text[i]) && !(text[i] >= '0' && text[i] <= '9'))
                {
                    int j = i;
                    while (j < t.End && IsIdent(text[j])) j++;
                    string w = System.Text.Encoding.UTF8.GetString(text, i, j - i);
                    if (w != "defined" && w != "__has_include")
                    {
                        bool isMacro = s.Ctx.ByName(w).Any(x => x.Kind == SymbolKind.Macro) || s.Knowledge.Lookup(w) != null || IsMacroLike(w);
                        if (isMacro) Emit(doc, st, i, j - i, IsUeMacro(s, w) ? SemanticKind.UeMacro : SemanticKind.Macro, SemanticModifiers.None);
                    }
                    i = j;
                    continue;
                }
                if (text[i] >= '0' && text[i] <= '9') { while (i < t.End && IsIdent(text[i])) i++; continue; }
                i++;
            }
        }

        private static bool IsIdent(byte b) => b == '_' || (b >= 'a' && b <= 'z') || (b >= 'A' && b <= 'Z') || (b >= '0' && b <= '9');

        private static bool IsMacroLike(string w)
        {
            if (w.Length < 2) return false;
            bool upper = false;
            foreach (char c in w)
            {
                if (char.IsLower(c)) return false;
                if (char.IsUpper(c)) upper = true;
            }
            return upper;
        }

        private static bool IsUeMacro(IsSession s, string name)
        {
            var info = s.Knowledge.Lookup(name);
            if (info == null) return false;
            return info.Role == MacroRole.Reflection || info.Role == MacroRole.GeneratedBody || info.Role == MacroRole.SymbolDeclaration ||
                   info.Role == MacroRole.StringLiteral || info.Role == MacroRole.PureVirtual;
        }

        private (SemanticKind, SemanticModifiers) ClassifyToken(IsSession s, PathState st, DocFacts facts, int k)
        {
            var doc = s.Doc;
            var t = doc.Tokens[k];
            int offset = t.Start;
            string word = doc.TokenText(k);

            // 1. a declaration's name
            int di = doc.DeclAtOffset(offset);
            if (di >= 0)
            {
                var d = doc.Decls[di];
                if (d.Kind != SymbolKind.Macro) return (KindOf(s, d), ModsOf(d) | SemanticModifiers.Declaration | (facts.External ? SemanticModifiers.External : SemanticModifiers.None));
            }

            // 2. reflection specifiers inside UPROPERTY(...) etc.
            var refl = facts.ReflectionAt(k);
            if (refl.Macro != null)
            {
                if (doc.Is(k - 1, Punct.Eq)) return (SemanticKind.None, SemanticModifiers.None);
                if (string.Equals(word, "meta", StringComparison.OrdinalIgnoreCase) && doc.Is(k + 1, Punct.Eq)) return (SemanticKind.UeSpecifier, SemanticModifiers.None);
                bool known = s.Knowledge.FindSpecifier(refl.Macro, word, refl.Meta) != null || s.Knowledge.IsKnownSpecifier(word);
                return known ? (SemanticKind.UeSpecifier, SemanticModifiers.None) : (SemanticKind.None, SemanticModifiers.None);
            }

            // 3. UE / known macros
            var info = s.Knowledge.Lookup(word);
            if (info != null && info.Role != MacroRole.None) return (IsUeMacro(s, word) ? SemanticKind.UeMacro : SemanticKind.Macro, SemanticModifiers.None);
            if ((t.Flags & TokenFlags.MacroLike) != 0 && s.Knowledge.IsExportMacro(word)) return (SemanticKind.Macro, SemanticModifiers.None);

            bool qualified = doc.Is(k - 1, Punct.ColonColon) || doc.Is(k - 1, Punct.Dot) || doc.Is(k - 1, Punct.Arrow);

            // 4. parameters of prototypes, locals and parameters of functions, template parameters
            if (facts.PrototypeParams.Contains(offset)) return (SemanticKind.Parameter, SemanticModifiers.Declaration);
            if (!qualified)
            {
                int fn = doc.EnclosingFunction(offset);
                if (fn >= 0)
                {
                    var l = doc.FindLocal(fn, word, offset);
                    if (l != null)
                        return (l.IsParameter ? SemanticKind.Parameter : SemanticKind.LocalVariable, l.Offset == offset ? SemanticModifiers.Declaration : SemanticModifiers.None);
                }
                if (facts.IsTemplateParameter(word, offset)) return (SemanticKind.TemplateParameter, SemanticModifiers.None);
            }

            // 5. everything else: resolved through the index, memoised by name and syntactic situation
            string cls = doc.EnclosingClass(offset, out _);
            bool call = doc.Is(k + 1, Punct.LParen) || doc.Is(k + 1, Punct.Lt) && TypeText.TypedFactories.Contains(word);
            bool typeUse = doc.Is(k + 1, Punct.ColonColon) || (doc.OccurrenceRoleAt(offset) & OccurrenceRole.TypeUse) != 0;
            string flavor = doc.Is(k - 1, Punct.Dot) || doc.Is(k - 1, Punct.Arrow) ? "m" : doc.Is(k - 1, Punct.ColonColon) ? "q" + (doc.IsIdentifier(k - 2) ? doc.TokenText(k - 2) : string.Empty) : "u";
            string key = word + "|" + flavor + "|" + (call ? "c" : typeUse ? "t" : "v") + "|" + cls;
            if (st.Memo.TryGetValue(key, out var hit))
            {
                _memoHits++;
                return hit;
            }
            _memoMisses++;
            var result = Resolve(s, k, word, flavor, call, typeUse);
            st.Memo[key] = result;
            return result;
        }

        private (SemanticKind, SemanticModifiers) Resolve(IsSession s, int k, string word, string flavor, bool call, bool typeUse)
        {
            var doc = s.Doc;
            List<SymbolInfo> cands;
            if (flavor == "m")
            {
                // member access: the kind is all we need; any member with this name decides
                cands = s.Ctx.ByName(word).Where(c => !string.IsNullOrEmpty(c.Container) && (TypeText.IsCallableKind(c.Kind) || c.Kind == SymbolKind.Field || c.Kind == SymbolKind.Variable)).ToList();
                if (cands.Count == 0) return call ? (SemanticKind.Method, SemanticModifiers.None) : (SemanticKind.Field, SemanticModifiers.None);
                var pick = call ? cands.FirstOrDefault(c => TypeText.IsCallableKind(c.Kind)) ?? cands[0] : cands.FirstOrDefault(c => !TypeText.IsCallableKind(c.Kind)) ?? cands[0];
                return (KindOf(pick), ModsOf(pick));
            }
            var r = SymbolResolver.ResolveToken(s.Ctx, doc, k, 1);
            cands = r.Candidates;
            if (cands.Count == 0)
            {
                if (r.Reason == "ue-macro") return (SemanticKind.UeMacro, SemanticModifiers.None);
                if (IsMacroLike(word)) return (SemanticKind.Macro, SemanticModifiers.None);
                if (word == "Super" || word == "ThisClass") return (SemanticKind.Typedef, SemanticModifiers.None);
                return (SemanticKind.None, SemanticModifiers.Unresolved);
            }
            SymbolInfo best = null;
            if (typeUse) best = cands.FirstOrDefault(c => TypeText.IsTypeKind(c.Kind));
            if (best == null && call) best = cands.FirstOrDefault(c => TypeText.IsCallableKind(c.Kind) && c.Kind != SymbolKind.Constructor) ?? cands.FirstOrDefault(c => TypeText.IsTypeKind(c.Kind) || c.Kind == SymbolKind.Macro);
            best ??= cands.FirstOrDefault(c => c.Kind != SymbolKind.Constructor) ?? cands[0];
            if (best.Kind == SymbolKind.Constructor)
            {
                // a class name used as a type, not its constructor
                return (best.Ue != UeFlags.None ? SemanticKind.UeType : SemanticKind.Class, ModsOf(best));
            }
            return (KindOf(best), ModsOf(best));
        }

        // ------------------------------------------------------------------ kind mapping

        private static SemanticKind KindOf(IsSession s, Declaration d)
        {
            var m = d.Modifiers;
            switch (d.Kind)
            {
                case SymbolKind.Class:
                case SymbolKind.Union:
                case SymbolKind.Concept:
                    return (d.Ue & (UeFlags.UClass | UeFlags.UInterface)) != 0 ? SemanticKind.UeType : SemanticKind.Class;
                case SymbolKind.Struct:
                    return (d.Ue & UeFlags.UStruct) != 0 ? SemanticKind.UeType : SemanticKind.Struct;
                case SymbolKind.Enum:
                    return (d.Ue & UeFlags.UEnum) != 0 ? SemanticKind.UeType : SemanticKind.Enum;
                case SymbolKind.EnumValue:
                    return SemanticKind.EnumValue;
                case SymbolKind.Typedef:
                    return SemanticKind.Typedef;
                case SymbolKind.Namespace:
                    return SemanticKind.Namespace;
                case SymbolKind.Function:
                    return SemanticKind.Function;
                case SymbolKind.Method:
                case SymbolKind.Constructor:
                case SymbolKind.Destructor:
                    if ((m & DeclModifiers.Static) != 0) return SemanticKind.StaticMethod;
                    if ((m & (DeclModifiers.Virtual | DeclModifiers.Override | DeclModifiers.Pure)) != 0) return SemanticKind.VirtualMethod;
                    return SemanticKind.Method;
                case SymbolKind.Field:
                    return (m & DeclModifiers.Static) != 0 ? SemanticKind.StaticField : SemanticKind.Field;
                case SymbolKind.Variable:
                    return d.Parent >= 0 && d.Parent < s.Doc.Decls.Count && TypeText.IsRecordKind(s.Doc.Decls[d.Parent].Kind) ? SemanticKind.StaticField : SemanticKind.GlobalVariable;
                case SymbolKind.Macro:
                    return IsUeMacro(s, d.Name) ? SemanticKind.UeMacro : SemanticKind.Macro;
                case SymbolKind.Delegate:
                    return SemanticKind.Delegate;
                default:
                    return SemanticKind.None;
            }
        }

        private static SemanticModifiers ModsOf(Declaration d)
        {
            var mods = SemanticModifiers.None;
            if ((d.Modifiers & DeclModifiers.Static) != 0) mods |= SemanticModifiers.Static;
            if ((d.Modifiers & DeclModifiers.Constexpr) != 0) mods |= SemanticModifiers.Const;
            if (d.Type != null && (d.Kind == SymbolKind.Field || d.Kind == SymbolKind.Variable) && d.Type.StartsWith("const ", StringComparison.Ordinal)) mods |= SemanticModifiers.Const;
            return mods;
        }

        private static SemanticKind KindOf(SymbolInfo s)
        {
            string sig = s.Signature ?? string.Empty;
            switch (s.Kind)
            {
                case SymbolKind.Class:
                case SymbolKind.Union:
                case SymbolKind.Concept:
                    return (s.Ue & (UeFlags.UClass | UeFlags.UInterface)) != 0 ? SemanticKind.UeType : SemanticKind.Class;
                case SymbolKind.Struct:
                    return (s.Ue & UeFlags.UStruct) != 0 ? SemanticKind.UeType : SemanticKind.Struct;
                case SymbolKind.Enum:
                    return (s.Ue & UeFlags.UEnum) != 0 ? SemanticKind.UeType : SemanticKind.Enum;
                case SymbolKind.EnumValue:
                    return SemanticKind.EnumValue;
                case SymbolKind.Typedef:
                    return SemanticKind.Typedef;
                case SymbolKind.Namespace:
                    return SemanticKind.Namespace;
                case SymbolKind.Function:
                    return SemanticKind.Function;
                case SymbolKind.Method:
                case SymbolKind.Constructor:
                case SymbolKind.Destructor:
                    if (sig.StartsWith("static ", StringComparison.Ordinal)) return SemanticKind.StaticMethod;
                    if (sig.StartsWith("virtual ", StringComparison.Ordinal) || sig.EndsWith(" override", StringComparison.Ordinal) || sig.Contains(") override", StringComparison.Ordinal))
                        return SemanticKind.VirtualMethod;
                    return SemanticKind.Method;
                case SymbolKind.Field:
                    return sig.StartsWith("static ", StringComparison.Ordinal) ? SemanticKind.StaticField : SemanticKind.Field;
                case SymbolKind.Variable:
                    return string.IsNullOrEmpty(s.Container) || !char.IsUpper(TypeText.LastSegment(s.Container)[0]) ? SemanticKind.GlobalVariable : SemanticKind.StaticField;
                case SymbolKind.Macro:
                    return UeMacroKnowledge.Default.Lookup(s.Name) != null ? SemanticKind.UeMacro : SemanticKind.Macro;
                case SymbolKind.Delegate:
                    return SemanticKind.Delegate;
                default:
                    return SemanticKind.None;
            }
        }

        private static SemanticModifiers ModsOf(SymbolInfo s)
        {
            var mods = SemanticModifiers.None;
            string sig = s.Signature ?? string.Empty;
            if (sig.StartsWith("static ", StringComparison.Ordinal)) mods |= SemanticModifiers.Static;
            if (s.Origin == ItemOrigin.Engine || s.Origin == ItemOrigin.EnginePlugin || s.Origin == ItemOrigin.ThirdParty) mods |= SemanticModifiers.External;
            return mods;
        }

        // ------------------------------------------------------------------ per-document facts

        /// <summary>Things computed once per parsed document: reflection macro argument ranges, template parameter scopes, prototype parameters.</summary>
        private sealed class DocFacts
        {
            private readonly List<(int Open, int Close, string Macro, bool Meta)> _refl = new List<(int, int, string, bool)>();
            private readonly List<(int Start, int End, HashSet<string> Names)> _templates = new List<(int, int, HashSet<string>)>();

            public HashSet<int> PrototypeParams { get; } = new HashSet<int>();
            public bool External { get; set; }
            public List<int> MacroDecls { get; } = new List<int>();

            public DocFacts(DocumentModel doc)
            {
                var toks = doc.Tokens;
                for (int k = 0; k < toks.Length; k++)
                {
                    var t = toks[k];
                    if (t.Kind == TokenKind.Identifier && doc.Is(k + 1, Punct.LParen) && CompletionSiteAnalyzer.IsReflectionMacro(doc.TokenText(k)))
                    {
                        int close = Close(doc, k + 1);
                        string macro = doc.TokenText(k);
                        _refl.Add((k + 1, close, macro, false));
                        for (int j = k + 2; j < close; j++)
                        {
                            if (doc.IsIdentifier(j) && string.Equals(doc.TokenText(j), "meta", StringComparison.OrdinalIgnoreCase) && doc.Is(j + 1, Punct.Eq) && doc.Is(j + 2, Punct.LParen))
                                _refl.Add((j + 2, Close(doc, j + 2), macro, true));
                        }
                    }
                    else if (t.Is(Keyword.Template) && doc.Is(k + 1, Punct.Lt))
                    {
                        Template(doc, k);
                    }
                }
                // inner ranges (meta) after outer ones: search from the end
                _refl.Sort((a, b) => a.Open.CompareTo(b.Open));
                for (int i = 0; i < doc.Decls.Count; i++)
                {
                    var d = doc.Decls[i];
                    if (d.Kind == SymbolKind.Macro) MacroDecls.Add(i);
                    else if (TypeText.IsCallableKind(d.Kind) && d.Body.IsNone && !d.IsImplicit) Prototype(doc, d);
                }
            }

            private static int Close(DocumentModel doc, int open)
            {
                int depth = 0;
                for (int k = open; k < doc.Tokens.Length && k < open + 2000; k++)
                {
                    if (doc.Is(k, Punct.LParen)) depth++;
                    else if (doc.Is(k, Punct.RParen) && --depth == 0) return k;
                    else if (doc.Is(k, Punct.Semicolon) || doc.Is(k, Punct.LBrace)) return k;
                }
                return Math.Min(doc.Tokens.Length - 1, open + 2000);
            }

            public (string Macro, bool Meta) ReflectionAt(int k)
            {
                (string, bool) found = (null, false);
                // few ranges per file: linear over the ones starting before k (sorted by start)
                int lo = 0, hi = _refl.Count - 1, last = -1;
                while (lo <= hi)
                {
                    int mid = (lo + hi) >> 1;
                    if (_refl[mid].Open < k) { last = mid; lo = mid + 1; }
                    else hi = mid - 1;
                }
                for (int i = last; i >= 0 && i > last - 4; i--)
                {
                    var r = _refl[i];
                    if (k > r.Open && k < r.Close)
                    {
                        if (r.Meta) return (r.Macro, true);
                        if (found.Item1 == null) found = (r.Macro, false);
                    }
                }
                return found;
            }

            private void Template(DocumentModel doc, int k)
            {
                var names = new HashSet<string>(StringComparer.Ordinal);
                int depth = 0, j = k + 1;
                for (; j < doc.Tokens.Length && j < k + 200; j++)
                {
                    if (doc.Is(j, Punct.Lt)) depth++;
                    else if (doc.Is(j, Punct.Gt)) { if (--depth == 0) break; }
                    else if (doc.Is(j, Punct.GtGt)) { depth -= 2; if (depth <= 0) break; }
                    else if (depth == 1 && doc.IsIdentifier(j) && (doc.Is(j + 1, Punct.Comma) || doc.Is(j + 1, Punct.Gt) || doc.Is(j + 1, Punct.Eq) || doc.Is(j + 1, Punct.GtGt) || doc.Is(j + 1, Punct.Ellipsis)) &&
                             (doc.Is(j - 1, Keyword.Typename) || doc.Is(j - 1, Keyword.Class) || doc.IsIdentifier(j - 1) || doc.Tokens[j - 1].Kind == TokenKind.Keyword || doc.Is(j - 1, Punct.Ellipsis)))
                        names.Add(doc.TokenText(j));
                }
                if (names.Count == 0) return;
                int start = doc.Tokens[k].Start;
                int end = start;
                int braces = 0, parens = 0;
                for (j++; j < doc.Tokens.Length; j++)
                {
                    var t = doc.Tokens[j];
                    if (t.Is(Punct.LParen)) parens++;
                    else if (t.Is(Punct.RParen)) parens--;
                    else if (t.Is(Punct.LBrace)) braces++;
                    else if (t.Is(Punct.RBrace))
                    {
                        if (--braces == 0 && parens == 0)
                        {
                            end = t.End;
                            // class templates end with "};"
                            if (doc.Is(j + 1, Punct.Semicolon)) end = doc.Tokens[j + 1].End;
                            break;
                        }
                    }
                    else if (t.Is(Punct.Semicolon) && braces == 0 && parens == 0)
                    {
                        end = t.End;
                        break;
                    }
                }
                if (end <= start) end = doc.Text.Length;
                _templates.Add((start, end, names));
            }

            public bool IsTemplateParameter(string name, int offset)
            {
                foreach (var (s, e, names) in _templates)
                    if (offset >= s && offset < e && names.Contains(name)) return true;
                return false;
            }

            private void Prototype(DocumentModel doc, Declaration d)
            {
                int k = doc.TokenIndexFrom(d.Offset);
                // name may be "operator ..."; find the parameter list
                int open = -1;
                for (int j = k; j < doc.Tokens.Length && j < k + 8; j++) if (doc.Is(j, Punct.LParen)) { open = j; break; }
                if (open < 0) return;
                int depth = 0;
                for (int j = open; j < doc.Tokens.Length && j < open + 400; j++)
                {
                    if (doc.Is(j, Punct.LParen) || doc.Is(j, Punct.LBrace) || doc.Is(j, Punct.LBracket)) depth++;
                    else if (doc.Is(j, Punct.RParen) || doc.Is(j, Punct.RBrace) || doc.Is(j, Punct.RBracket)) { if (--depth == 0) break; }
                    else if (depth == 1 && doc.IsIdentifier(j) && (doc.Is(j + 1, Punct.Comma) || doc.Is(j + 1, Punct.RParen) || doc.Is(j + 1, Punct.Eq) || doc.Is(j + 1, Punct.LBracket)))
                    {
                        var p = doc.Tokens[j - 1];
                        if (p.Kind == TokenKind.Identifier || p.Kind == TokenKind.Keyword && !p.Is(Keyword.Const) || p.Is(Punct.Star) || p.Is(Punct.Amp) || p.Is(Punct.AmpAmp) || p.Is(Punct.Gt) ||
                            p.Is(Keyword.Const) && (doc.Is(j - 2, Punct.Star) || doc.Is(j - 2, Punct.Amp)))
                        {
                            // not a type name alone: "void F(FVector)" has no parameter name
                            if (p.Is(Punct.LParen) || p.Is(Punct.Comma)) continue;
                            PrototypeParams.Add(doc.Tokens[j].Start);
                        }
                    }
                }
            }
        }
    }
}
