using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Text;
using UEToVS.Contracts;

namespace UEToVS.Engine.Syntax
{
    /// <summary>
    /// Tolerant recursive-descent declaration parser over the preprocessed token stream. Produces declarations and,
    /// from a shallow scan of function bodies, identifier occurrences. It is not a C++ front end: it recognises the
    /// declaration structure and resynchronises at ';', '}' and top-level keywords whenever something does not fit.
    /// </summary>
    internal sealed partial class Parser
    {
        private const int MaxScopeDepth = 128;

        private enum ScopeKind : byte
        {
            File,
            Namespace,
            Class,
            Linkage,
        }

        private sealed class Scope
        {
            public ScopeKind Kind;
            public string Name;
            public string FullName;
            public AccessLevel Access;
            public Declaration Decl;
            public int DeclIndex = -1;
            public Scope Parent;
            public string[] Bases;
        }

        private enum DeclMode : byte
        {
            Normal,
            Typedef,
        }

        private struct Prefix
        {
            public int First;
            public UeFlags Ue;
            public int SpecOpen;   // token index of '(' of the reflection macro, or -1
            public int SpecClose;  // token index of ')'
            public bool IsTemplate;
            public int TplOpen;    // token index of '<'
            public int TplEnd;     // token index after '>'
        }

        private readonly byte[] _src;
        private readonly int _len;
        private readonly Token[] _t;
        private readonly int _n;
        private readonly ParseOptions _opt;
        private readonly UeMacroKnowledge _kb;
        private readonly NameTable _names;
        private readonly LineMap _lines;
        private readonly List<TextRange> _docs;
        private readonly List<Declaration> _macros;
        private readonly StringBuilder _sb;
        private readonly StringBuilder _sb2 = new StringBuilder(128);
        private readonly HashSet<string> _namespaces = new HashSet<string>(StringComparer.Ordinal);
        private readonly int _maxSig;
        private int _p;
        private int _depth;
        private int _macroCursor;
        private string _joinA, _joinB, _joinResult;

        public readonly List<Declaration> Declarations = new List<Declaration>();
        public readonly List<Occurrence> Occurrences = new List<Occurrence>();
        public readonly List<SyntaxDiagnostic> Diagnostics;

        public Parser(byte[] src, int len, Token[] tokens, int count, ParseOptions options, UeMacroKnowledge kb, NameTable names,
            LineMap lines, List<TextRange> docs, List<Declaration> macros, StringBuilder scratch, List<SyntaxDiagnostic> diagnostics)
        {
            _src = src;
            _len = len;
            _t = tokens;
            _n = count;
            _opt = options;
            _kb = kb;
            _names = names;
            _lines = lines;
            _docs = docs;
            _macros = macros;
            _sb = scratch;
            _maxSig = Math.Max(16, options.MaxSignatureLength);
            Diagnostics = diagnostics;
        }

        public void Run()
        {
            var root = new Scope { Kind = ScopeKind.File, Access = AccessLevel.None };
            _p = 0;
            ParseScopeBody(root, false);
            FlushMacros(int.MaxValue);
        }

        // ------------------------------------------------------------------ token helpers

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private ref Token T(int i) => ref _t[(uint)i < (uint)_n ? i : _n - 1];

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private bool P(int i, Punct p) => T(i).Is(p);

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private bool K(int i, Keyword k) => T(i).Is(k);

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private bool IsIdent(int i) => T(i).Kind == TokenKind.Identifier;

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private bool IsEof(int i) => T(i).Kind == TokenKind.EndOfFile;

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private MacroRole Role(int i)
        {
            ref var t = ref T(i);
            return t.Kind == TokenKind.Identifier ? (MacroRole)t.Sub : MacroRole.None;
        }

        private ReadOnlySpan<byte> Text(int i)
        {
            ref var t = ref T(i);
            return new ReadOnlySpan<byte>(_src, t.Start, t.Length);
        }

        private string Name(int i) => _names.Intern(Text(i));

        private bool TextIs(int i, ReadOnlySpan<byte> s) => Text(i).SequenceEqual(s);

        private void Diag(int tokenIndex, string message)
        {
            if (Diagnostics.Count >= 100) return;
            int off = T(tokenIndex).Start;
            Diagnostics.Add(new SyntaxDiagnostic(off, _lines.GetLine(off), message));
        }

        // ------------------------------------------------------------------ skipping

        /// <summary>
        /// Index after the ')' or ']' matching the opener at <paramref name="open"/>. Stops (without consuming) at an
        /// unbalanced '}' or EOF, and in strict mode also at a ';' outside braces.
        /// </summary>
        private int MatchParen(int open, bool strict = false)
        {
            var openP = T(open).Punct;
            var closeP = openP == Punct.LBracket ? Punct.RBracket : Punct.RParen;
            int depth = 0, braces = 0;
            int i = open;
            while (true)
            {
                ref var t = ref T(i);
                if (t.Kind == TokenKind.EndOfFile) return i;
                if (t.Kind == TokenKind.Punctuator)
                {
                    var p = (Punct)t.Sub;
                    if (p == openP) depth++;
                    else if (p == closeP)
                    {
                        if (--depth == 0) return i + 1;
                    }
                    else if (p == Punct.LBrace) braces++;
                    else if (p == Punct.RBrace)
                    {
                        if (braces == 0) return i;
                        braces--;
                    }
                    else if (p == Punct.Semicolon && strict && braces == 0) return i;
                }
                else if (braces == 0 && i > open && IsHardRecovery(i)) return i;
                i++;
            }
        }

        /// <summary>Index after the '}' matching the '{' at <paramref name="open"/>; stops at EOF or a hard recovery point.</summary>
        private int SkipBraces(int open)
        {
            int depth = 0;
            int i = open;
            while (true)
            {
                ref var t = ref _t[i];
                if (t.Kind == TokenKind.EndOfFile) return i;
                if (t.Kind == TokenKind.Punctuator)
                {
                    if (t.Sub == (byte)Punct.LBrace) depth++;
                    else if (t.Sub == (byte)Punct.RBrace && --depth == 0) return i + 1;
                }
                else if (depth > 0 && i > open && IsHardRecovery(i))
                {
                    Diag(open, "unterminated block");
                    return i;
                }
                i++;
            }
        }

        /// <summary>
        /// Template argument list starting at '&lt;': index after the matching '&gt;', or -1 when this does not look like one.
        /// </summary>
        private int SkipAngles(int lt)
        {
            int depth = 0, parens = 0;
            int limit = lt + 2000;
            for (int i = lt; i < limit; i++)
            {
                ref var t = ref T(i);
                switch (t.Kind)
                {
                    case TokenKind.EndOfFile:
                        return -1;
                    case TokenKind.Punctuator:
                        switch ((Punct)t.Sub)
                        {
                            case Punct.Lt: if (parens == 0) depth++; break;
                            case Punct.Gt:
                                if (parens == 0 && --depth == 0) return i + 1;
                                break;
                            case Punct.GtGt:
                                if (parens == 0)
                                {
                                    depth -= 2;
                                    if (depth <= 0) return i + 1;
                                }
                                break;
                            case Punct.LParen: case Punct.LBracket: parens++; break;
                            case Punct.RParen: case Punct.RBracket:
                                if (--parens < 0) return -1;
                                break;
                            case Punct.Semicolon: case Punct.LBrace: case Punct.RBrace:
                                if (parens == 0) return -1;
                                break;
                        }
                        break;
                    case TokenKind.Identifier:
                        if (IsHardRecovery(i)) return -1;
                        break;
                }
            }
            return -1;
        }

        private int SkipAttribute(int i)
        {
            // [[ ... ]]
            int j = MatchParen(i);
            return j <= i ? i + 1 : j;
        }

        /// <summary>
        /// Skips the rest of a statement: past ';' or a top-level {...} block (and a ';' right after it), stopping without
        /// consuming at an unbalanced '}', EOF or a recovery point on a new line.
        /// </summary>
        private int SkipStatement(int i)
        {
            int start = i;
            int parens = 0;
            while (true)
            {
                ref var t = ref T(i);
                if (t.Kind == TokenKind.EndOfFile) return i;
                if (t.Kind == TokenKind.Punctuator)
                {
                    switch ((Punct)t.Sub)
                    {
                        case Punct.LParen: case Punct.LBracket: parens++; break;
                        case Punct.RParen: case Punct.RBracket: if (parens > 0) parens--; break;
                        case Punct.LBrace:
                        {
                            i = SkipBraces(i);
                            if (parens == 0)
                            {
                                if (P(i, Punct.Semicolon)) i++;
                                return i;
                            }
                            continue;
                        }
                        case Punct.RBrace: return i;
                        case Punct.Semicolon:
                            if (parens == 0) return i + 1;
                            break;
                    }
                }
                else if (i > start && IsRecoveryPoint(i)) return i;
                i++;
            }
        }

        /// <summary>Tokens that never appear inside a function body or expression: UCLASS-family macros, GENERATED_BODY, 'namespace'.</summary>
        private bool IsHardRecovery(int i)
        {
            ref var t = ref T(i);
            if (t.Kind == TokenKind.Keyword) return t.Sub == (byte)Keyword.Namespace;
            if (t.Kind != TokenKind.Identifier) return false;
            var role = (MacroRole)t.Sub;
            if (role == MacroRole.GeneratedBody) return true;
            if (role != MacroRole.Reflection) return false;
            var info = _kb.Lookup(Text(i));
            return info != null && info.Flag != UeFlags.None && info.Flag != UeFlags.UMeta;
        }

        /// <summary>A place to resynchronise when a statement runs on: hard recovery tokens, or line-leading template/access specifiers.</summary>
        private bool IsRecoveryPoint(int i)
        {
            if (IsHardRecovery(i)) return true;
            ref var t = ref T(i);
            if ((t.Flags & TokenFlags.AtLineStart) == 0 || t.Kind != TokenKind.Keyword) return false;
            switch ((Keyword)t.Sub)
            {
                case Keyword.Template:
                    return P(i + 1, Punct.Lt);
                case Keyword.Public:
                case Keyword.Protected:
                case Keyword.Private:
                    return P(i + 1, Punct.Colon);
                default:
                    return false;
            }
        }

        // ------------------------------------------------------------------ scopes

        private void ParseScopeBody(Scope scope, bool braced)
        {
            while (true)
            {
                ref var t = ref T(_p);
                if (t.Kind == TokenKind.EndOfFile)
                {
                    if (braced) Diag(_p, "unexpected end of file inside a block");
                    return;
                }
                if (t.Is(Punct.RBrace))
                {
                    _p++;
                    if (braced) return;
                    Diag(_p - 1, "unbalanced '}'");
                    continue;
                }
                int before = _p;
                ParseDeclaration(scope);
                if (_p <= before) _p = before + 1;
            }
        }

        private void ParseNestedScope(Scope inner, int open)
        {
            if (_depth >= MaxScopeDepth)
            {
                Diag(open, "nesting too deep");
                _p = SkipBraces(open);
                return;
            }
            _depth++;
            _p = open + 1;
            ParseScopeBody(inner, true);
            _depth--;
        }

        private void ParseDeclaration(Scope scope)
        {
            var pre = new Prefix { First = _p, SpecOpen = -1 };
            while (true)
            {
                ref var t = ref T(_p);
                switch (t.Kind)
                {
                    case TokenKind.EndOfFile:
                        return;

                    case TokenKind.Punctuator:
                        switch ((Punct)t.Sub)
                        {
                            case Punct.Semicolon:
                                _p++;
                                return;
                            case Punct.RBrace:
                                return;
                            case Punct.LBrace:
                                _p = ScanBodyOrSkip(_p, -1);
                                return;
                            case Punct.LBracket when P(_p + 1, Punct.LBracket):
                                _p = SkipAttribute(_p);
                                continue;
                        }
                        goto General;

                    case TokenKind.Keyword:
                        switch ((Keyword)t.Sub)
                        {
                            case Keyword.Public:
                            case Keyword.Protected:
                            case Keyword.Private:
                                if (P(_p + 1, Punct.Colon))
                                {
                                    if (scope.Kind == ScopeKind.Class)
                                        scope.Access = t.Sub == (byte)Keyword.Public ? AccessLevel.Public
                                                     : t.Sub == (byte)Keyword.Protected ? AccessLevel.Protected : AccessLevel.Private;
                                    _p += 2;
                                    return;
                                }
                                _p++;
                                return;
                            case Keyword.Template:
                                if (P(_p + 1, Punct.Lt))
                                {
                                    int end = SkipAngles(_p + 1);
                                    if (end < 0)
                                    {
                                        _p = SkipStatement(_p);
                                        return;
                                    }
                                    pre.IsTemplate = true;
                                    pre.TplOpen = _p + 1;
                                    pre.TplEnd = end;
                                    _p = end;
                                    continue;
                                }
                                _p = SkipStatement(_p); // explicit instantiation
                                return;
                            case Keyword.Extern:
                                if (T(_p + 1).Kind == TokenKind.String)
                                {
                                    if (P(_p + 2, Punct.LBrace))
                                    {
                                        var linkage = new Scope
                                        {
                                            Kind = ScopeKind.Linkage, Name = scope.Name, FullName = scope.FullName, Parent = scope,
                                            Access = scope.Access, DeclIndex = scope.DeclIndex,
                                        };
                                        ParseNestedScope(linkage, _p + 2);
                                        return;
                                    }
                                    _p += 2;
                                    continue;
                                }
                                if (K(_p + 1, Keyword.Template))
                                {
                                    _p = SkipStatement(_p);
                                    return;
                                }
                                goto General;
                            case Keyword.Inline:
                                if (K(_p + 1, Keyword.Namespace))
                                {
                                    _p++;
                                    continue;
                                }
                                goto General;
                            case Keyword.Namespace:
                                ParseNamespace(scope, ref pre);
                                return;
                            case Keyword.Using:
                                ParseUsing(scope, ref pre);
                                return;
                            case Keyword.Typedef:
                                _p++;
                                ParseTypedef(scope, ref pre);
                                return;
                            case Keyword.Friend:
                            case Keyword.Static_assert:
                            case Keyword.Asm:
                                _p = SkipStatement(_p);
                                return;
                            case Keyword.Enum:
                                if (TryParseEnum(scope, ref pre, DeclMode.Normal)) return;
                                goto General;
                            case Keyword.Class:
                            case Keyword.Struct:
                            case Keyword.Union:
                                if (TryParseClass(scope, ref pre, DeclMode.Normal)) return;
                                goto General;
                            case Keyword.Export:
                                _p++;
                                continue;
                        }
                        goto General;

                    case TokenKind.Identifier:
                        switch ((MacroRole)t.Sub)
                        {
                            case MacroRole.Reflection:
                                if (P(_p + 1, Punct.LParen))
                                {
                                    var info = _kb.Lookup(Text(_p));
                                    int after = MatchParen(_p + 1);
                                    if (info != null) pre.Ue |= info.Flag;
                                    pre.SpecOpen = _p + 1;
                                    pre.SpecClose = P(after - 1, Punct.RParen) ? after - 1 : after;
                                    _p = after;
                                    continue;
                                }
                                _p++;
                                continue;
                            case MacroRole.GeneratedBody:
                                HandleGeneratedBody(scope);
                                return;
                            case MacroRole.SymbolDeclaration:
                                if (P(_p + 1, Punct.LParen))
                                {
                                    HandleSymbolMacro(scope, ref pre);
                                    return;
                                }
                                goto General;
                            case MacroRole.Attribute:
                            case MacroRole.Export:
                                _p++;
                                continue;
                            case MacroRole.AttributeCall:
                                _p = P(_p + 1, Punct.LParen) ? MatchParen(_p + 1) : _p + 1;
                                continue;
                            case MacroRole.StringLiteral:
                            case MacroRole.PureVirtual:
                                _p = SkipStatement(_p);
                                return;
                        }
                        if ((t.Flags & TokenFlags.MacroLike) != 0 && !(scope.Kind == ScopeKind.Class && Name(_p) == scope.Name))
                        {
                            if (P(_p + 1, Punct.LParen))
                            {
                                // unknown macro invocation at declaration start
                                int after = MatchParen(_p + 1);
                                ref var a = ref T(after);
                                if (a.Is(Punct.Semicolon))
                                {
                                    _p = after + 1;
                                    return;
                                }
                                if (a.Is(Punct.LBrace))
                                {
                                    _p = ScanBodyOrSkip(after, -1);
                                    return;
                                }
                                // either a statement-like macro without ';' (next token on a new line) or an
                                // attribute-like macro before the declaration: both continue with the next tokens
                                _p = after;
                                if ((a.Flags & TokenFlags.AtLineStart) != 0) pre.First = _p;
                                continue;
                            }
                            ref var nx = ref T(_p + 1);
                            if ((nx.Flags & TokenFlags.AtLineStart) != 0 && nx.Kind != TokenKind.EndOfFile &&
                                !nx.Is(Punct.ColonColon) && !nx.Is(Punct.Semicolon) && !nx.Is(Punct.Eq) && !nx.Is(Punct.Comma) &&
                                !nx.Is(Punct.Lt) && !nx.Is(Punct.LParen))
                            {
                                // bare macro alone on its line (PRAGMA_..., a project-specific attribute)
                                _p++;
                                continue;
                            }
                        }
                        goto General;

                    default:
                        _p = SkipStatement(_p);
                        return;
                }

                General:
                ParseGeneral(scope, ref pre, DeclMode.Normal);
                return;
            }
        }

        private int ScanBodyOrSkip(int open, int func) => _opt.CollectOccurrences ? ScanBody(open, func) : SkipBraces(open);

        // ------------------------------------------------------------------ namespace / using / typedef

        private void ParseNamespace(Scope scope, ref Prefix pre)
        {
            int kw = _p;
            int i = _p + 1;
            while (P(i, Punct.LBracket) && P(i + 1, Punct.LBracket)) i = SkipAttribute(i);
            var nameIdx = new List<int>(2);
            while (IsIdent(i))
            {
                if (Role(i) == MacroRole.Attribute || Role(i) == MacroRole.Export) { i++; continue; }
                nameIdx.Add(i);
                i++;
                if (P(i, Punct.ColonColon))
                {
                    i++;
                    if (K(i, Keyword.Inline)) i++;
                    continue;
                }
                break;
            }
            if (P(i, Punct.Eq) && nameIdx.Count == 1)
            {
                int end = SkipStatement(i);
                int stop = P(end - 1, Punct.Semicolon) ? end - 1 : end;
                var d = NewDecl(Name(nameIdx[0]), scope.FullName, SymbolKind.Typedef, nameIdx[0], scope, ref pre);
                d.Type = Render(i + 1, stop, true);
                d.Signature = Render(kw, stop, true);
                d.IsDefinition = true;
                Add(d);
                _p = end;
                return;
            }
            if (!P(i, Punct.LBrace))
            {
                _p = SkipStatement(i);
                return;
            }
            if (nameIdx.Count == 0)
            {
                // anonymous namespace: transparent for containers
                var anon = new Scope { Kind = ScopeKind.Namespace, Name = scope.Name, FullName = scope.FullName, Parent = scope, DeclIndex = scope.DeclIndex };
                ParseNestedScope(anon, i);
                return;
            }
            string full = scope.FullName;
            Declaration last = null;
            int lastIndex = scope.DeclIndex;
            foreach (int ni in nameIdx)
            {
                string name = Name(ni);
                _namespaces.Add(name);
                var d = NewDecl(name, full, SymbolKind.Namespace, ni, scope, ref pre);
                d.Parent = lastIndex;
                d.IsDefinition = true;
                d.Signature = "namespace " + Join(full, name);
                lastIndex = Add(d);
                last = d;
                full = Join(full, name);
            }
            var inner = new Scope { Kind = ScopeKind.Namespace, Name = Name(nameIdx[nameIdx.Count - 1]), FullName = full, Parent = scope, Decl = last, DeclIndex = lastIndex };
            int open = i;
            ParseNestedScope(inner, open);
            SetBody(last, open);
        }

        private void SetBody(Declaration d, int open)
        {
            if (d == null) return;
            int closeTok = _p - 1;
            int end = P(closeTok, Punct.RBrace) && closeTok > open ? T(closeTok).End : T(Math.Max(open, _p - 1)).End;
            d.Body = new TextRange(T(open).Start, Math.Max(1, end - T(open).Start));
        }

        private void ParseUsing(Scope scope, ref Prefix pre)
        {
            int kw = _p;
            int i = _p + 1;
            if (K(i, Keyword.Namespace) || K(i, Keyword.Enum) || !IsIdent(i))
            {
                _p = SkipStatement(i);
                return;
            }
            int nameTok = i;
            int j = i + 1;
            // attributes between the name and '='
            while (true)
            {
                if (P(j, Punct.LBracket) && P(j + 1, Punct.LBracket)) { j = SkipAttribute(j); continue; }
                var role = Role(j);
                if (role == MacroRole.Attribute || role == MacroRole.Export) { j++; continue; }
                if (role == MacroRole.AttributeCall && P(j + 1, Punct.LParen)) { j = MatchParen(j + 1); continue; }
                break;
            }
            if (!P(j, Punct.Eq))
            {
                _p = SkipStatement(i); // using-declaration: using Base::Member;
                return;
            }
            int end = SkipStatement(j);
            int stop = P(end - 1, Punct.Semicolon) ? end - 1 : end;
            var d = NewDecl(Name(nameTok), scope.FullName, SymbolKind.Typedef, nameTok, scope, ref pre);
            d.Type = Render(j + 1, stop, true);
            d.Signature = "using " + d.Name + " = " + d.Type;
            if (d.Signature.Length > _maxSig) d.Signature = d.Signature.Substring(0, _maxSig - 1) + "…";
            d.IsDefinition = true;
            Add(d);
            _p = end;
        }

        private void ParseTypedef(Scope scope, ref Prefix pre)
        {
            if (K(_p, Keyword.Struct) || K(_p, Keyword.Class) || K(_p, Keyword.Union))
            {
                if (TryParseClass(scope, ref pre, DeclMode.Typedef)) return;
            }
            else if (K(_p, Keyword.Enum))
            {
                if (TryParseEnum(scope, ref pre, DeclMode.Typedef)) return;
            }
            ParseGeneral(scope, ref pre, DeclMode.Typedef);
        }

        // ------------------------------------------------------------------ class / struct / union

        /// <summary>Skips attributes, export and attribute macros in a class or enum head.</summary>
        private int SkipHeadNoise(int i)
        {
            while (true)
            {
                ref var t = ref T(i);
                if (t.Is(Punct.LBracket) && P(i + 1, Punct.LBracket)) { i = SkipAttribute(i); continue; }
                if (t.Is(Keyword.Alignas) && P(i + 1, Punct.LParen)) { i = MatchParen(i + 1); continue; }
                if (t.Kind == TokenKind.Identifier)
                {
                    var role = (MacroRole)t.Sub;
                    if (role == MacroRole.Export || role == MacroRole.Attribute) { i++; continue; }
                    if (role == MacroRole.AttributeCall && P(i + 1, Punct.LParen)) { i = MatchParen(i + 1); continue; }
                    // unknown macro-looking word directly before the real name: "class DLLEXPORT_X FFoo"
                    if ((t.Flags & TokenFlags.MacroLike) != 0 && role == MacroRole.None && IsIdent(i + 1) &&
                        (T(i + 1).Flags & TokenFlags.AtLineStart) == 0)
                    {
                        i++;
                        continue;
                    }
                }
                return i;
            }
        }

        private bool TryParseClass(Scope scope, ref Prefix pre, DeclMode mode)
        {
            int kw = _p;
            var kwType = T(kw).Keyword;
            int i = SkipHeadNoise(kw + 1);
            int nameIdx = -1;
            List<string> quals = null;
            while (IsIdent(i) && Role(i) == MacroRole.None)
            {
                nameIdx = i;
                i++;
                if (P(i, Punct.Lt))
                {
                    int a = SkipAngles(i);
                    if (a > 0) i = a;
                }
                if (P(i, Punct.ColonColon) && IsIdent(i + 1))
                {
                    (quals ??= new List<string>()).Add(Name(nameIdx));
                    i++;
                    continue;
                }
                break;
            }
            i = SkipHeadNoise(i);
            var mods = pre.IsTemplate ? DeclModifiers.Template : DeclModifiers.None;
            while (K(i, Keyword.Final) || (IsIdent(i) && (TextIs(i, "sealed"u8) || TextIs(i, "abstract"u8))))
            {
                if (K(i, Keyword.Final)) mods |= DeclModifiers.Final;
                i++;
            }
            string[] bases = null;
            int headEnd = i;
            if (P(i, Punct.Colon))
            {
                int j = ParseBaseList(i + 1, out bases);
                if (!P(j, Punct.LBrace))
                {
                    if (nameIdx < 0 || mode == DeclMode.Typedef) return false;
                    _p = SkipStatement(j);
                    return true;
                }
                i = j;
            }
            if (!P(i, Punct.LBrace))
            {
                if (P(i, Punct.Semicolon) && nameIdx >= 0 && mode == DeclMode.Normal)
                {
                    if (_opt.IncludeForwardDeclarations)
                    {
                        var fwd = NewDecl(Name(nameIdx), JoinQuals(scope.FullName, quals), ClassKind(kwType), nameIdx, scope, ref pre);
                        fwd.Modifiers |= DeclModifiers.Forward | mods;
                        fwd.Signature = Render(kw, i, true);
                        Add(fwd);
                    }
                    _p = i + 1;
                    return true;
                }
                return false; // elaborated type specifier in a declaration
            }

            string name = nameIdx >= 0 ? Name(nameIdx) : null;
            string container = JoinQuals(scope.FullName, quals);
            Declaration d = null;
            int dIndex = scope.DeclIndex;
            if (name != null)
            {
                d = NewDecl(name, container, ClassKind(kwType), nameIdx, scope, ref pre);
                d.IsDefinition = true;
                d.Bases = bases;
                d.Modifiers |= mods;
                d.Signature = Render(kw, headEnd, true, bases != null ? i : -1);
                dIndex = Add(d);
            }
            var inner = new Scope
            {
                Kind = ScopeKind.Class,
                Name = name ?? scope.Name,
                FullName = name != null ? Join(container, name) : scope.FullName,
                Access = kwType == Keyword.Class ? AccessLevel.Private : AccessLevel.Public,
                Decl = d,
                DeclIndex = dIndex,
                Parent = scope,
                Bases = bases,
            };
            int open = i;
            ParseNestedScope(inner, open);
            SetBody(d, open);
            ParseTrailingDeclarators(scope, ref pre, name, mode);
            return true;
        }

        private static SymbolKind ClassKind(Keyword k) =>
            k == Keyword.Struct ? SymbolKind.Struct : k == Keyword.Union ? SymbolKind.Union : SymbolKind.Class;

        /// <summary>Parses "public A, private B&lt;C, D&gt;" up to '{' (returned index). Stops at ';', '}' or EOF.</summary>
        private int ParseBaseList(int i, out string[] bases)
        {
            var list = new List<string>(2);
            int segStart = i;
            while (true)
            {
                ref var t = ref T(i);
                if (t.Kind == TokenKind.EndOfFile) break;
                if (t.Kind == TokenKind.Punctuator)
                {
                    var p = (Punct)t.Sub;
                    if (p == Punct.LBrace || p == Punct.Semicolon || p == Punct.RBrace)
                    {
                        AddBase(list, segStart, i);
                        break;
                    }
                    if (p == Punct.Comma)
                    {
                        AddBase(list, segStart, i);
                        segStart = i + 1;
                        i++;
                        continue;
                    }
                    if (p == Punct.Lt)
                    {
                        int a = SkipAngles(i);
                        if (a > 0) { i = a; continue; }
                    }
                    if (p == Punct.LParen) { i = MatchParen(i); continue; }
                }
                else if (IsHardRecovery(i)) break;
                i++;
            }
            bases = list.Count > 0 ? list.ToArray() : null;
            return i;
        }

        private void AddBase(List<string> list, int from, int to)
        {
            while (from < to && (K(from, Keyword.Public) || K(from, Keyword.Protected) || K(from, Keyword.Private) || K(from, Keyword.Virtual)))
                from++;
            if (from >= to) return;
            var s = Render(from, to, true);
            if (s.Length > 0) list.Add(s);
        }

        /// <summary>After a class or enum body: "} Var, *Ptr;" (or typedef names), then the ';'.</summary>
        private void ParseTrailingDeclarators(Scope scope, ref Prefix pre, string typeName, DeclMode mode)
        {
            int i = _p;
            while (true)
            {
                while (P(i, Punct.Star) || P(i, Punct.Amp) || P(i, Punct.AmpAmp) || K(i, Keyword.Const) || K(i, Keyword.Volatile)) i++;
                if (IsIdent(i) && Role(i) == MacroRole.None)
                {
                    ref var nx = ref T(i + 1);
                    bool ok = nx.Is(Punct.Semicolon) || nx.Is(Punct.Comma) || nx.Is(Punct.Eq) || nx.Is(Punct.LBracket) ||
                              nx.Is(Punct.LBrace) || nx.Is(Punct.RBrace) || nx.Kind == TokenKind.EndOfFile;
                    if (!ok) break;
                    var kind = mode == DeclMode.Typedef ? SymbolKind.Typedef : scope.Kind == ScopeKind.Class ? SymbolKind.Field : SymbolKind.Variable;
                    var d = NewDecl(Name(i), scope.FullName, kind, i, scope, ref pre);
                    d.Type = typeName;
                    d.IsDefinition = true;
                    d.Signature = (mode == DeclMode.Typedef ? "typedef " : "") + (typeName ?? "") + " " + d.Name;
                    Add(d);
                    i++;
                    if (P(i, Punct.LBracket)) i = MatchParen(i);
                    if (P(i, Punct.Eq)) i = SkipInitializer(i + 1);
                    else if (P(i, Punct.LBrace)) i = SkipBraces(i);
                    if (P(i, Punct.Comma))
                    {
                        i++;
                        continue;
                    }
                }
                break;
            }
            if (P(i, Punct.Semicolon)) i++;
            _p = i;
        }

        // ------------------------------------------------------------------ enums

        private bool TryParseEnum(Scope scope, ref Prefix pre, DeclMode mode)
        {
            int kw = _p;
            int i = kw + 1;
            bool scoped = false;
            if (K(i, Keyword.Class) || K(i, Keyword.Struct))
            {
                scoped = true;
                i++;
            }
            i = SkipHeadNoise(i);
            int nameIdx = -1;
            while (IsIdent(i) && Role(i) == MacroRole.None)
            {
                nameIdx = i;
                i++;
                if (P(i, Punct.ColonColon) && IsIdent(i + 1)) { i++; continue; }
                break;
            }
            i = SkipHeadNoise(i);
            string underlying = null;
            int headEnd = i;
            if (P(i, Punct.Colon))
            {
                int s = i + 1;
                int j = s;
                while (!IsEof(j) && !P(j, Punct.LBrace) && !P(j, Punct.Semicolon) && !P(j, Punct.RBrace) && j - s < 32)
                {
                    if (P(j, Punct.Lt)) { int a = SkipAngles(j); if (a > 0) { j = a; continue; } }
                    j++;
                }
                underlying = Render(s, j, true);
                i = j;
                headEnd = j;
            }
            if (!P(i, Punct.LBrace))
            {
                if (P(i, Punct.Semicolon) && nameIdx >= 0 && mode == DeclMode.Normal)
                {
                    if (_opt.IncludeForwardDeclarations)
                    {
                        var fwd = NewDecl(Name(nameIdx), scope.FullName, SymbolKind.Enum, nameIdx, scope, ref pre);
                        fwd.Modifiers |= DeclModifiers.Forward | (scoped ? DeclModifiers.Scoped : 0);
                        fwd.Value = underlying;
                        fwd.Signature = Render(kw, i, true);
                        Add(fwd);
                    }
                    _p = i + 1;
                    return true;
                }
                return false;
            }

            string name = nameIdx >= 0 ? Name(nameIdx) : null;
            Declaration d = null;
            int dIndex = scope.DeclIndex;
            if (name != null)
            {
                d = NewDecl(name, scope.FullName, SymbolKind.Enum, nameIdx, scope, ref pre);
                d.IsDefinition = true;
                d.Value = underlying;
                if (scoped) d.Modifiers |= DeclModifiers.Scoped;
                d.Signature = Render(kw, headEnd, true);
                dIndex = Add(d);
            }
            string valueContainer = name != null ? Join(scope.FullName, name) : scope.FullName;
            int open = i;
            ParseEnumValues(open, valueContainer, name, dIndex, scope);
            SetBody(d, open);
            ParseTrailingDeclarators(scope, ref pre, name, mode);
            return true;
        }

        private void ParseEnumValues(int open, string container, string enumName, int parent, Scope scope)
        {
            int i = open + 1;
            Declaration last = null;
            bool expectName = true;
            while (true)
            {
                ref var t = ref T(i);
                if (t.Kind == TokenKind.EndOfFile) break;
                if (t.Is(Punct.RBrace)) { i++; break; }
                if (t.Is(Punct.Comma)) { expectName = true; i++; continue; }
                if (IsHardRecovery(i))
                {
                    Diag(open, "unterminated enum");
                    break;
                }
                if (t.Kind == TokenKind.Identifier)
                {
                    var role = (MacroRole)t.Sub;
                    if (role == MacroRole.Reflection && P(i + 1, Punct.LParen))
                    {
                        int after = MatchParen(i + 1);
                        if (last != null)
                        {
                            var info = _kb.Lookup(Text(i));
                            if (info != null) last.Ue |= info.Flag;
                            last.Specifiers = SpecText(i + 1, P(after - 1, Punct.RParen) ? after - 1 : after);
                        }
                        i = after;
                        continue;
                    }
                    if (role == MacroRole.Attribute || role == MacroRole.Export) { i++; continue; }
                    if (role == MacroRole.AttributeCall && P(i + 1, Punct.LParen)) { i = MatchParen(i + 1); continue; }
                    if (expectName)
                    {
                        var pre = new Prefix { First = i, SpecOpen = -1 };
                        var d = NewDecl(Name(i), container, SymbolKind.EnumValue, i, scope, ref pre);
                        d.Parent = parent;
                        d.Access = AccessLevel.None;
                        d.IsDefinition = true;
                        expectName = false;
                        i++;
                        while (P(i, Punct.LBracket) && P(i + 1, Punct.LBracket)) i = SkipAttribute(i);
                        while (Role(i) == MacroRole.AttributeCall && P(i + 1, Punct.LParen)) i = MatchParen(i + 1);
                        if (P(i, Punct.Eq))
                        {
                            int e = SkipInitializer(i + 1);
                            d.Value = Render(i + 1, e, true);
                            i = e;
                        }
                        d.Signature = (enumName != null ? enumName + "::" : "") + d.Name + (d.Value != null ? " = " + d.Value : "");
                        Add(d);
                        last = d;
                        continue;
                    }
                }
                if (t.Is(Punct.LParen) || t.Is(Punct.LBracket)) { i = MatchParen(i); continue; }
                if (t.Is(Punct.LBrace)) { i = SkipBraces(i); continue; }
                i++;
            }
            _p = i;
        }

        // ------------------------------------------------------------------ UE macros

        private void HandleGeneratedBody(Scope scope)
        {
            int at = _p;
            var info = _kb.Lookup(Text(at));
            _p++;
            if (P(_p, Punct.LParen)) _p = MatchParen(_p);
            if (P(_p, Punct.Semicolon)) _p++;
            if (scope.Kind != ScopeKind.Class || scope.Decl == null || info == null) return;
            var cls = scope.Decl;
            cls.Ue |= UeFlags.GeneratedBody;
            if (info.Legacy) scope.Access = AccessLevel.Public;

            var family = info.Family;
            if (family == GeneratedFamily.Auto)
            {
                if ((cls.Ue & UeFlags.UInterface) != 0) family = GeneratedFamily.UInterface;
                else if ((cls.Ue & UeFlags.UClass) != 0) family = GeneratedFamily.UClass;
                else if ((cls.Ue & UeFlags.UStruct) != 0) family = GeneratedFamily.UStruct;
                else if (cls.Name.Length > 1 && cls.Name[0] == 'I' && char.IsUpper(cls.Name[1])) family = GeneratedFamily.IInterface;
                else family = cls.Kind == SymbolKind.Struct ? GeneratedFamily.UStruct : GeneratedFamily.UClass;
            }
            if (!_opt.EmitInjectedMembers) return;

            string super = scope.Bases != null && scope.Bases.Length > 0 ? scope.Bases[0] : null;
            string noPrefix = cls.Name.Length > 1 ? cls.Name.Substring(1) : cls.Name;
            foreach (var m in _kb.GetInjectedMembers(family))
            {
                if (m.RequiresSuper && super == null) continue;
                var d = NewImplicit(m.Name, m.Kind, at, scope);
                d.Signature = (m.Signature ?? m.Name).Replace("{Class}", cls.Name).Replace("{ClassNoPrefix}", noPrefix).Replace("{Super}", super ?? "");
                if (m.IsStatic) d.Modifiers |= DeclModifiers.Static;
                if (m.Kind == SymbolKind.Typedef) d.Type = m.Name == "Super" ? super : m.Name == "UClassType" ? "U" + noPrefix : cls.Name;
                d.IsDefinition = m.Kind == SymbolKind.Typedef;
                Add(d);
            }
            if (info.Legacy && _kb.LegacyConstructorSignature != null)
            {
                var ctor = NewImplicit(cls.Name, SymbolKind.Constructor, at, scope);
                ctor.Signature = _kb.LegacyConstructorSignature.Replace("{Class}", cls.Name);
                ctor.Parameters = "const FObjectInitializer& ObjectInitializer";
                Add(ctor);
            }
        }

        private Declaration NewImplicit(string name, SymbolKind kind, int at, Scope scope)
        {
            ref var t = ref T(at);
            var pos = _lines.GetPosition(new ReadOnlySpan<byte>(_src, 0, _len), t.Start);
            return new Declaration
            {
                Name = name,
                Container = scope.FullName,
                Kind = kind,
                Ue = UeFlags.GeneratedBody,
                Offset = t.Start,
                NameLength = t.Length,
                Line = pos.Line,
                Column = pos.Column,
                IsConditional = (t.Flags & TokenFlags.Conditional) != 0,
                IsImplicit = true,
                Access = AccessLevel.Public,
                Parent = scope.DeclIndex,
            };
        }

        private void HandleSymbolMacro(Scope scope, ref Prefix pre)
        {
            int m = _p;
            var info = _kb.Lookup(Text(m));
            int open = m + 1;
            int after = MatchParen(open);
            int close = P(after - 1, Punct.RParen) ? after - 1 : after;
            _p = after;
            if (P(_p, Punct.Semicolon)) _p++;
            if (info == null) return;

            // split arguments at top-level commas
            Span<int> argStart = stackalloc int[32];
            Span<int> argEnd = stackalloc int[32];
            int argc = 0;
            int s = open + 1, depth = 0;
            for (int i = open + 1; i <= close && argc < 32; i++)
            {
                ref var t = ref T(i);
                if (i == close || (depth == 0 && t.Is(Punct.Comma)))
                {
                    argStart[argc] = s;
                    argEnd[argc] = i;
                    argc++;
                    s = i + 1;
                    continue;
                }
                if (t.Is(Punct.LParen) || t.Is(Punct.LBracket) || t.Is(Punct.LBrace) || t.Is(Punct.Lt)) depth++;
                else if (t.Is(Punct.RParen) || t.Is(Punct.RBracket) || t.Is(Punct.RBrace) || t.Is(Punct.Gt)) depth = Math.Max(0, depth - 1);
                else if (t.Is(Punct.GtGt)) depth = Math.Max(0, depth - 2);
            }
            if (info.NameArg >= argc) return;
            int nameTok = -1;
            for (int i = argEnd[info.NameArg] - 1; i >= argStart[info.NameArg]; i--)
                if (IsIdent(i)) { nameTok = i; break; }
            if (nameTok < 0) return;

            var kind = info.Kind == SymbolKind.Unknown ? SymbolKind.Delegate : info.Kind;
            var d = NewDecl(Name(nameTok), scope.FullName, kind, nameTok, scope, ref pre);
            d.Ue |= info.Flag;
            d.Modifiers |= info.Traits;
            d.IsDefinition = info.IsDefinition;
            d.Signature = Render(m, after, false);
            if (info.ReturnArg >= 0 && info.ReturnArg < argc) d.Type = Render(argStart[info.ReturnArg], argEnd[info.ReturnArg], true);
            if (kind == SymbolKind.Delegate && info.NameArg + 1 < argc)
            {
                int ps = argStart[info.NameArg + 1];
                d.Parameters = Render(ps, close, true);
            }
            Add(d);
        }

        // ------------------------------------------------------------------ declarations (functions, variables, typedefs)

        private struct NameInfo
        {
            public string Simple;
            public List<string> Quals;
            public int NameTok;
            public int NameEnd;
            public bool IsDtor;
            public bool IsOperator;
            public bool IsConversion;
        }

        private void ParseGeneral(Scope scope, ref Prefix pre, DeclMode mode)
        {
            int start = _p;
            var mods = pre.IsTemplate ? DeclModifiers.Template : DeclModifiers.None;
            int i = _p;
            int nameStart = -1, nameEnd = -1;
            bool typeSeen = false;
            bool isOperator = false;

            while (true)
            {
                ref var t = ref T(i);
                if (t.Kind == TokenKind.Keyword)
                {
                    var k = (Keyword)t.Sub;
                    switch (k)
                    {
                        case Keyword.Virtual: mods |= DeclModifiers.Virtual; i++; continue;
                        case Keyword.Static: mods |= DeclModifiers.Static; i++; continue;
                        case Keyword.Inline: mods |= DeclModifiers.Inline; i++; continue;
                        case Keyword.Mutable: mods |= DeclModifiers.Mutable; i++; continue;
                        case Keyword.Thread_local: case Keyword.Register: i++; continue;
                        case Keyword.Constexpr: case Keyword.Consteval: case Keyword.Constinit: mods |= DeclModifiers.Constexpr; i++; continue;
                        case Keyword.Explicit:
                            mods |= DeclModifiers.Explicit;
                            i = P(i + 1, Punct.LParen) ? MatchParen(i + 1) : i + 1;
                            continue;
                        case Keyword.Extern:
                            mods |= DeclModifiers.Extern;
                            i++;
                            if (T(i).Kind == TokenKind.String) i++;
                            continue;
                        case Keyword.Const: case Keyword.Volatile:
                        case Keyword.Typename: case Keyword.Class: case Keyword.Struct: case Keyword.Union: case Keyword.Enum:
                            i++;
                            continue;
                        case Keyword.Decltype:
                            typeSeen = true;
                            nameStart = -1;
                            i = P(i + 1, Punct.LParen) ? MatchParen(i + 1) : i + 1;
                            continue;
                        case Keyword.Alignas:
                            i = P(i + 1, Punct.LParen) ? MatchParen(i + 1) : i + 1;
                            continue;
                        case Keyword.Operator:
                            if (nameStart >= 0) typeSeen = true;
                            nameStart = i;
                            nameEnd = ParseQualifiedName(i, out isOperator);
                            i = nameEnd;
                            goto Stop;
                    }
                    if (Keywords.IsBuiltinType(k))
                    {
                        typeSeen = true;
                        nameStart = -1;
                        i++;
                        continue;
                    }
                    goto Stop;
                }
                if (t.Kind == TokenKind.Identifier)
                {
                    var role = (MacroRole)t.Sub;
                    if (role == MacroRole.Attribute || role == MacroRole.Export) { i++; continue; }
                    if (role == MacroRole.AttributeCall && P(i + 1, Punct.LParen)) { i = MatchParen(i + 1); continue; }
                    if ((role == MacroRole.Reflection || role == MacroRole.GeneratedBody) && i > start) goto Stop;
                    if (role == MacroRole.PureVirtual) goto Stop;
                    if (i > start && IsRecoveryPoint(i)) goto Stop;
                    if (nameStart >= 0) typeSeen = true;
                    nameStart = i;
                    nameEnd = ParseQualifiedName(i, out isOperator);
                    i = nameEnd;
                    if (isOperator) goto Stop;
                    continue;
                }
                if (t.Kind == TokenKind.Punctuator)
                {
                    switch ((Punct)t.Sub)
                    {
                        case Punct.ColonColon:
                            if (IsIdent(i + 1) || K(i + 1, Keyword.Operator) || P(i + 1, Punct.Tilde))
                            {
                                if (nameStart >= 0) typeSeen = true;
                                nameStart = i;
                                nameEnd = ParseQualifiedName(i, out isOperator);
                                i = nameEnd;
                                if (isOperator) goto Stop;
                                continue;
                            }
                            goto Stop;
                        case Punct.Tilde:
                            if (IsIdent(i + 1))
                            {
                                if (nameStart >= 0) typeSeen = true;
                                nameStart = i;
                                nameEnd = i + 2;
                                i += 2;
                                continue;
                            }
                            goto Stop;
                        case Punct.Star:
                        case Punct.Amp:
                        case Punct.AmpAmp:
                        case Punct.Caret:
                            if (nameStart >= 0)
                            {
                                typeSeen = true;
                                nameStart = -1;
                            }
                            i++;
                            continue;
                        case Punct.Ellipsis:
                            i++;
                            continue;
                        case Punct.LBracket:
                            if (P(i + 1, Punct.LBracket)) { i = SkipAttribute(i); continue; }
                            goto Stop;
                    }
                }
                goto Stop;
            }

            Stop:
            if (P(i, Punct.LParen))
            {
                if (typeSeen && IsFunctionPointerDeclarator(i, out int fpName, out int fpEnd))
                {
                    ParseVariables(scope, ref pre, start, mods, fpName, fpName + 1, fpEnd, mode);
                    return;
                }
                if (nameStart >= 0)
                {
                    var nm = AnalyzeName(nameStart, nameEnd, isOperator);
                    bool isCtor = !nm.IsDtor && !nm.IsOperator &&
                                  ((nm.Quals != null && nm.Quals[nm.Quals.Count - 1] == nm.Simple) ||
                                   (nm.Quals == null && scope.Kind == ScopeKind.Class && scope.Name == nm.Simple));
                    if (!typeSeen && !isCtor && !nm.IsDtor && !nm.IsConversion)
                    {
                        _p = SkipStatement(start); // macro invocation or expression statement
                        return;
                    }
                    if (typeSeen && !nm.IsOperator && LooksLikeDirectInit(i))
                    {
                        ParseVariables(scope, ref pre, start, mods, nameStart, nameEnd, i, mode);
                        return;
                    }
                    ParseFunction(scope, ref pre, start, mods, nm, isCtor, i, mode);
                    return;
                }
                _p = SkipStatement(start);
                return;
            }
            if (nameStart < 0 || !typeSeen || isOperator)
            {
                _p = SkipStatement(start);
                return;
            }
            ParseVariables(scope, ref pre, start, mods, nameStart, nameEnd, i, mode);
        }

        /// <summary>
        /// Parses a possibly qualified name starting at <paramref name="i"/>: "::A::B&lt;T&gt;::C", "A::~A", "A::operator==".
        /// Returns the index after it.
        /// </summary>
        private int ParseQualifiedName(int i, out bool isOperator)
        {
            isOperator = false;
            int j = i;
            if (P(j, Punct.ColonColon)) j++;
            while (true)
            {
                if (K(j, Keyword.Operator))
                {
                    isOperator = true;
                    return ParseOperatorName(j);
                }
                if (P(j, Punct.Tilde) && IsIdent(j + 1)) return j + 2;
                if (!IsIdent(j)) return j;
                j++;
                if (P(j, Punct.Lt))
                {
                    int a = SkipAngles(j);
                    if (a > 0) j = a;
                }
                if (P(j, Punct.ColonColon) && (IsIdent(j + 1) || K(j + 1, Keyword.Operator) || P(j + 1, Punct.Tilde) || K(j + 1, Keyword.Template)))
                {
                    j++;
                    if (K(j, Keyword.Template)) j++;
                    continue;
                }
                return j;
            }
        }

        private int ParseOperatorName(int j)
        {
            j++; // 'operator'
            ref var t = ref T(j);
            if (t.Is(Punct.LParen) && P(j + 1, Punct.RParen)) return j + 2;
            if (t.Is(Punct.LBracket) && P(j + 1, Punct.RBracket)) return j + 2;
            if (t.Is(Keyword.New) || t.Is(Keyword.Delete))
            {
                j++;
                if (P(j, Punct.LBracket) && P(j + 1, Punct.RBracket)) j += 2;
                return j;
            }
            if (t.Kind == TokenKind.String)
            {
                j++;
                if (IsIdent(j)) j++;
                return j;
            }
            if (t.Kind == TokenKind.Punctuator && !t.Is(Punct.LParen)) return j + 1;
            // conversion operator: type tokens up to '('
            int limit = j + 32;
            while (j < limit && !P(j, Punct.LParen) && !IsEof(j) && !P(j, Punct.Semicolon) && !P(j, Punct.LBrace))
            {
                if (P(j, Punct.Lt)) { int a = SkipAngles(j); if (a > 0) { j = a; continue; } }
                j++;
            }
            return j;
        }

        private NameInfo AnalyzeName(int start, int end, bool isOperator)
        {
            var nm = new NameInfo { NameEnd = end, IsOperator = isOperator };
            int i = start;
            if (P(i, Punct.ColonColon)) i++;
            int lastIdent = -1;
            while (i < end)
            {
                if ((K(i, Keyword.Operator) || P(i, Punct.Tilde)) && lastIdent >= 0)
                {
                    (nm.Quals ??= new List<string>(2)).Add(Name(lastIdent));
                    lastIdent = -1;
                }
                if (K(i, Keyword.Operator))
                {
                    nm.NameTok = i;
                    _sb2.Clear();
                    _sb2.Append("operator");
                    ref var first = ref T(i + 1);
                    bool word = first.Kind == TokenKind.Identifier || first.Kind == TokenKind.Keyword;
                    nm.IsConversion = word && !first.Is(Keyword.New) && !first.Is(Keyword.Delete);
                    if (word) _sb2.Append(' ');
                    AppendRange(_sb2, i + 1, end, false, false);
                    nm.Simple = _sb2.ToString();
                    return nm;
                }
                if (P(i, Punct.Tilde))
                {
                    nm.NameTok = i;
                    nm.IsDtor = true;
                    nm.Simple = "~" + Name(i + 1);
                    return nm;
                }
                if (IsIdent(i))
                {
                    if (lastIdent >= 0) (nm.Quals ??= new List<string>(2)).Add(Name(lastIdent));
                    lastIdent = i;
                    i++;
                    if (P(i, Punct.Lt))
                    {
                        int a = SkipAngles(i);
                        if (a > 0 && a <= end) i = a;
                    }
                    if (P(i, Punct.ColonColon)) i++;
                    if (K(i, Keyword.Template)) i++;
                    continue;
                }
                i++;
            }
            nm.NameTok = lastIdent >= 0 ? lastIdent : start;
            nm.Simple = lastIdent >= 0 ? Name(lastIdent) : "";
            return nm;
        }

        /// <summary>"R (*Name)(Args)", "R (Class::*Name)(Args)", "R (&amp;Name)[N]".</summary>
        private bool IsFunctionPointerDeclarator(int open, out int nameIdx, out int end)
        {
            nameIdx = end = -1;
            int j = open + 1;
            while (IsIdent(j) && (Role(j) == MacroRole.Attribute || (T(j).Flags & TokenFlags.MacroLike) != 0) && !P(j + 1, Punct.ColonColon)) j++;
            while (IsIdent(j) && P(j + 1, Punct.ColonColon)) j += 2;
            if (!(P(j, Punct.Star) || P(j, Punct.Amp) || P(j, Punct.AmpAmp) || P(j, Punct.Caret))) return false;
            j++;
            while (P(j, Punct.Star) || K(j, Keyword.Const) || K(j, Keyword.Volatile)) j++;
            if (!IsIdent(j)) return false;
            nameIdx = j;
            j++;
            while (P(j, Punct.LBracket)) j = MatchParen(j);
            if (!P(j, Punct.RParen)) return false;
            j++;
            if (P(j, Punct.LParen)) j = MatchParen(j, true);
            while (K(j, Keyword.Const) || K(j, Keyword.Noexcept)) j++;
            end = j;
            return true;
        }

        /// <summary>"FFoo Bar(1, 2)" at namespace or class scope: a variable with direct initialisation.</summary>
        private bool LooksLikeDirectInit(int open)
        {
            ref var t = ref T(open + 1);
            switch (t.Kind)
            {
                case TokenKind.Number:
                case TokenKind.String:
                case TokenKind.Char:
                    return true;
                case TokenKind.Keyword:
                    return t.Is(Keyword.True) || t.Is(Keyword.False) || t.Is(Keyword.Nullptr) || t.Is(Keyword.This);
                case TokenKind.Identifier:
                    return (MacroRole)t.Sub == MacroRole.StringLiteral;
                case TokenKind.Punctuator:
                    return t.Is(Punct.Amp) && IsIdent(open + 2) && (P(open + 3, Punct.RParen) || P(open + 3, Punct.Comma));
                default:
                    return false;
            }
        }

        private bool IsNamespaceQualifier(List<string> quals) => quals != null && _namespaces.Contains(quals[quals.Count - 1]);

        private void ParseFunction(Scope scope, ref Prefix pre, int start, DeclModifiers mods, NameInfo nm, bool isCtor, int open, DeclMode mode)
        {
            int afterParams = MatchParen(open, true);
            int closeParen = P(afterParams - 1, Punct.RParen) && afterParams - 1 > open ? afterParams - 1 : afterParams;
            int j = afterParams;
            int sigEnd = afterParams;
            string trailingType = null;
            bool macroBody = false;

            while (true)
            {
                ref var t = ref T(j);
                if (t.Kind == TokenKind.Keyword)
                {
                    switch ((Keyword)t.Sub)
                    {
                        case Keyword.Const: mods |= DeclModifiers.Const; j++; sigEnd = j; continue;
                        case Keyword.Volatile: j++; sigEnd = j; continue;
                        case Keyword.Override: mods |= DeclModifiers.Override; j++; sigEnd = j; continue;
                        case Keyword.Final: mods |= DeclModifiers.Final; j++; sigEnd = j; continue;
                        case Keyword.Noexcept:
                            mods |= DeclModifiers.Noexcept;
                            j++;
                            if (P(j, Punct.LParen)) j = MatchParen(j);
                            sigEnd = j;
                            continue;
                        case Keyword.Throw:
                            j++;
                            if (P(j, Punct.LParen)) j = MatchParen(j);
                            continue;
                        case Keyword.Requires:
                        {
                            j++;
                            while (!IsEof(j) && !P(j, Punct.LBrace) && !P(j, Punct.Semicolon) && !P(j, Punct.Eq) && !P(j, Punct.RBrace))
                                j = P(j, Punct.LParen) ? MatchParen(j) : j + 1;
                            continue;
                        }
                    }
                    break;
                }
                if (t.Kind == TokenKind.Punctuator)
                {
                    if (t.Is(Punct.Amp) || t.Is(Punct.AmpAmp)) { j++; sigEnd = j; continue; }
                    if (t.Is(Punct.Arrow))
                    {
                        int s = j + 1;
                        int k = s;
                        while (!IsEof(k) && !P(k, Punct.LBrace) && !P(k, Punct.Semicolon) && !P(k, Punct.Eq) && !P(k, Punct.RBrace))
                        {
                            if (P(k, Punct.LParen)) { k = MatchParen(k); continue; }
                            if (P(k, Punct.Lt)) { int a = SkipAngles(k); if (a > 0) { k = a; continue; } }
                            if (K(k, Keyword.Override) || K(k, Keyword.Final)) break;
                            k++;
                        }
                        trailingType = Render(s, k, true);
                        j = k;
                        sigEnd = j;
                        continue;
                    }
                    if (t.Is(Punct.LBracket) && P(j + 1, Punct.LBracket)) { j = SkipAttribute(j); continue; }
                    break;
                }
                if (t.Kind == TokenKind.Identifier && (t.Flags & TokenFlags.AtLineStart) == 0)
                {
                    var role = (MacroRole)t.Sub;
                    if (role == MacroRole.Attribute || role == MacroRole.Export) { j++; continue; }
                    if (role == MacroRole.PureVirtual && P(j + 1, Punct.LParen))
                    {
                        mods |= DeclModifiers.Pure;
                        macroBody = true;
                        j = MatchParen(j + 1);
                        continue;
                    }
                    if ((role == MacroRole.AttributeCall || (role == MacroRole.None && (t.Flags & TokenFlags.MacroLike) != 0)) && P(j + 1, Punct.LParen))
                    {
                        j = MatchParen(j + 1);
                        continue;
                    }
                    if (role == MacroRole.None && (t.Flags & TokenFlags.MacroLike) != 0) { j++; continue; }
                }
                break;
            }

            bool isDefinition = macroBody;
            int bodyOpen = -1;
            int next;
            ref var e = ref T(j);
            if (e.Is(Punct.Eq))
            {
                ref var v = ref T(j + 1);
                if (v.Kind == TokenKind.Number && v.Length == 1 && _src[v.Start] == (byte)'0') mods |= DeclModifiers.Pure;
                else if (v.Is(Keyword.Default)) { mods |= DeclModifiers.Defaulted; isDefinition = true; }
                else if (v.Is(Keyword.Delete)) { mods |= DeclModifiers.Deleted; isDefinition = true; }
                next = SkipStatement(j);
            }
            else if (e.Is(Punct.Semicolon))
            {
                next = j + 1;
            }
            else if (e.Is(Punct.LBrace))
            {
                bodyOpen = j;
                isDefinition = true;
                next = -1;
            }
            else if (e.Is(Punct.Colon) && !P(j + 1, Punct.Colon))
            {
                // constructor initializer list (occurrences are emitted after the declaration is added)
                bodyOpen = -2;
                isDefinition = true;
                next = -1;
            }
            else if (e.Is(Keyword.Try) && P(j + 1, Punct.LBrace) || e.Is(Keyword.Try) && P(j + 1, Punct.Colon))
            {
                bodyOpen = -3;
                isDefinition = true;
                next = -1;
            }
            else if (macroBody)
            {
                next = j;
            }
            else
            {
                Diag(j, "unexpected token after function declarator");
                next = SkipStatement(j);
            }

            SymbolKind kind;
            if (mode == DeclMode.Typedef) kind = SymbolKind.Typedef;
            else if (isCtor) kind = SymbolKind.Constructor;
            else if (nm.IsDtor) kind = SymbolKind.Destructor;
            else if (scope.Kind == ScopeKind.Class || (nm.Quals != null && !IsNamespaceQualifier(nm.Quals))) kind = SymbolKind.Method;
            else kind = SymbolKind.Function;

            var d = NewDecl(nm.Simple, JoinQuals(scope.FullName, nm.Quals), kind, nm.NameTok, scope, ref pre);
            d.NameLength = Math.Max(1, T(nm.NameEnd - 1).End - T(nm.NameTok).Start);
            d.Modifiers |= mods;
            d.IsDefinition = isDefinition;
            d.Parameters = Render(open + 1, closeParen, true);
            if (!isCtor && !nm.IsDtor)
            {
                d.Type = Render(start, NameQualStart(nm, start), true, -1, true);
                if (trailingType != null && (d.Type == "auto" || d.Type.Length == 0)) d.Type = trailingType;
            }
            _sb.Clear();
            AppendRange(_sb, start, sigEnd, true, false);
            if ((mods & DeclModifiers.Pure) != 0 && !macroBody) _sb.Append(" = 0");
            TextUtil.Truncate(_sb, _maxSig);
            d.Signature = _sb.ToString();
            int index = Add(d);

            if (bodyOpen == -2) bodyOpen = ScanInitList(j + 1, index);
            else if (bodyOpen == -3)
            {
                j++;
                bodyOpen = P(j, Punct.Colon) ? ScanInitList(j + 1, index) : j;
            }
            if (bodyOpen >= 0)
            {
                if (!P(bodyOpen, Punct.LBrace))
                {
                    _p = bodyOpen > j ? bodyOpen : SkipStatement(bodyOpen);
                    return;
                }
                next = ScanBodyOrSkip(bodyOpen, index);
                int last = next - 1;
                d.Body = new TextRange(T(bodyOpen).Start, Math.Max(1, (P(last, Punct.RBrace) ? T(last).End : T(Math.Max(bodyOpen, next - 1)).End) - T(bodyOpen).Start));
                // function-try-block handlers
                while (K(next, Keyword.Catch) && P(next + 1, Punct.LParen))
                {
                    int h = MatchParen(next + 1);
                    next = P(h, Punct.LBrace) ? ScanBodyOrSkip(h, index) : h;
                }
                if (P(next, Punct.Semicolon)) next++;
            }
            _p = next;
        }

        /// <summary>Index of the first token of the (possibly qualified) name, so the type text stops before "A::".</summary>
        private int NameQualStart(NameInfo nm, int start)
        {
            int i = nm.NameTok;
            while (i - 2 >= start && P(i - 1, Punct.ColonColon))
            {
                if (IsIdent(i - 2)) { i -= 2; continue; }
                if (P(i - 2, Punct.Gt) || P(i - 2, Punct.GtGt))
                {
                    // A<T>::Name: walk back over the template argument list
                    int depth = 0, k = i - 2;
                    for (; k >= start; k--)
                    {
                        if (P(k, Punct.Gt)) depth++;
                        else if (P(k, Punct.GtGt)) depth += 2;
                        else if (P(k, Punct.Lt) && --depth <= 0) break;
                    }
                    if (k - 1 >= start && IsIdent(k - 1)) { i = k - 1; continue; }
                }
                break;
            }
            if (i - 1 >= start && P(i - 1, Punct.ColonColon)) i--; // ::Name
            return i;
        }

        private void ParseVariables(Scope scope, ref Prefix pre, int start, DeclModifiers mods, int nameStart, int nameEnd, int stop, DeclMode mode)
        {
            var nm = AnalyzeName(nameStart, nameEnd, false);
            int typeEnd = NameQualStart(nm, start);
            bool fnPtr = nameEnd < stop && P(nameEnd, Punct.RParen);
            string type = fnPtr ? Render(start, stop, true, -1, true) : Render(start, typeEnd, true, -1, true);
            int typeTokEnd = typeEnd;
            // the type part shared by all declarators excludes leading '*'/'&' that belong to the first declarator
            while (typeTokEnd - 1 >= start && (P(typeTokEnd - 1, Punct.Star) || P(typeTokEnd - 1, Punct.Amp) || P(typeTokEnd - 1, Punct.AmpAmp)))
                typeTokEnd--;
            int baseTypeTokEnd = typeTokEnd;
            string baseType = null;

            int declStart = start;
            int j = stop;
            while (true)
            {
                SymbolKind kind;
                if (mode == DeclMode.Typedef) kind = SymbolKind.Typedef;
                else if (scope.Kind == ScopeKind.Class || (nm.Quals != null && !IsNamespaceQualifier(nm.Quals))) kind = SymbolKind.Field;
                else kind = SymbolKind.Variable;
                var d = NewDecl(nm.Simple, JoinQuals(scope.FullName, nm.Quals), kind, nm.NameTok, scope, ref pre);
                d.Modifiers |= mods;
                d.IsDefinition = (mods & DeclModifiers.Extern) == 0;
                d.Type = type;
                _sb.Clear();
                if (mode == DeclMode.Typedef) _sb.Append("typedef ");
                if (declStart == start)
                {
                    AppendRange(_sb, start, nm.NameEnd, true, false);
                }
                else
                {
                    _sb.Append(baseType);
                    _sb.Append(' ');
                    AppendRange(_sb, declStart, nm.NameEnd, true, false);
                }
                // function pointer declarators keep their parameter list
                if (nm.NameEnd < stop && P(nm.NameEnd, Punct.RParen))
                {
                    AppendRange(_sb, nm.NameEnd, stop, true, false);
                }
                TextUtil.Truncate(_sb, _maxSig);
                d.Signature = _sb.ToString();
                Add(d);

                // rest of this declarator
                bool more = false;
                while (true)
                {
                    ref var t = ref T(j);
                    if (t.Kind == TokenKind.EndOfFile) break;
                    if (t.Is(Punct.LBracket)) { j = MatchParen(j); continue; }
                    if (t.Is(Punct.Colon))
                    {
                        // bit-field width
                        j++;
                        while (!IsEof(j) && !P(j, Punct.Comma) && !P(j, Punct.Semicolon) && !P(j, Punct.Eq) && !P(j, Punct.LBrace) && !P(j, Punct.RBrace))
                            j = P(j, Punct.LParen) ? MatchParen(j) : j + 1;
                        continue;
                    }
                    if (t.Is(Punct.Eq)) { j = SkipInitializer(j + 1); continue; }
                    if (t.Is(Punct.LBrace)) { j = SkipBraces(j); continue; }
                    if (t.Is(Punct.LParen)) { j = MatchParen(j, true); continue; }
                    if (t.Is(Punct.Comma)) { more = true; j++; break; }
                    if (t.Is(Punct.Semicolon)) { j++; break; }
                    if (t.Is(Punct.RBrace)) break;
                    if (t.Kind == TokenKind.Identifier && ((MacroRole)t.Sub == MacroRole.Attribute || (MacroRole)t.Sub == MacroRole.Export)) { j++; continue; }
                    if (t.Kind == TokenKind.Identifier && (MacroRole)t.Sub == MacroRole.AttributeCall && P(j + 1, Punct.LParen)) { j = MatchParen(j + 1); continue; }
                    if (P(j, Punct.LBracket) && P(j + 1, Punct.LBracket)) { j = SkipAttribute(j); continue; }
                    // something unexpected (missing ';'?): resynchronise
                    if ((t.Flags & TokenFlags.AtLineStart) != 0) break;
                    j = SkipStatement(j);
                    break;
                }
                if (!more) break;

                // next declarator: [*&]* name
                baseType ??= Render(start, baseTypeTokEnd, true, -1, true);
                declStart = j;
                while (P(j, Punct.Star) || P(j, Punct.Amp) || P(j, Punct.AmpAmp) || K(j, Keyword.Const)) j++;
                if (P(j, Punct.LParen) && IsFunctionPointerDeclarator(j, out int fpName, out int fpEnd))
                {
                    nm = AnalyzeName(fpName, fpName + 1, false);
                    j = fpEnd;
                }
                else if (IsIdent(j))
                {
                    nm = AnalyzeName(j, j + 1, false);
                    j++;
                }
                else
                {
                    j = SkipStatement(j);
                    break;
                }
                // type text for this declarator: base type plus its own pointer/ref tokens
                _sb2.Clear();
                _sb2.Append(baseType);
                for (int k = declStart; k < nm.NameTok; k++)
                {
                    if (P(k, Punct.Star) || P(k, Punct.Amp) || P(k, Punct.AmpAmp)) AppendTokenText(_sb2, k);
                }
                type = _sb2.ToString();
            }
            _p = j;
        }

        /// <summary>Skips an initializer up to a top-level ',' or ';' (not consumed), or an unbalanced ')' / '}'.</summary>
        private int SkipInitializer(int i)
        {
            int depth = 0;
            int start = i;
            while (true)
            {
                ref var t = ref T(i);
                if (t.Kind == TokenKind.EndOfFile) return i;
                if (t.Kind == TokenKind.Punctuator)
                {
                    switch ((Punct)t.Sub)
                    {
                        case Punct.LParen: case Punct.LBracket: depth++; break;
                        case Punct.LBrace:
                            i = SkipBraces(i);
                            continue;
                        case Punct.RParen: case Punct.RBracket:
                            if (depth == 0) return i;
                            depth--;
                            break;
                        case Punct.RBrace: return i;
                        case Punct.Comma: if (depth == 0) return i; break;
                        case Punct.Semicolon: if (depth == 0) return i; break;
                    }
                }
                else if (i > start && (t.Flags & TokenFlags.AtLineStart) != 0 && IsRecoveryPoint(i)) return i;
                i++;
            }
        }

        // ------------------------------------------------------------------ declaration records

        private Declaration NewDecl(string name, string container, SymbolKind kind, int nameTok, Scope scope, ref Prefix pre)
        {
            ref var t = ref T(nameTok);
            var pos = _lines.GetPosition(new ReadOnlySpan<byte>(_src, 0, _len), t.Start);
            var d = new Declaration
            {
                Name = name,
                Container = container,
                Kind = kind,
                Offset = t.Start,
                NameLength = t.Length,
                Line = pos.Line,
                Column = pos.Column,
                IsConditional = (t.Flags & TokenFlags.Conditional) != 0,
                Access = scope.Kind == ScopeKind.Class ? scope.Access : AccessLevel.None,
                Parent = scope.DeclIndex,
                DocComment = FindDoc(pre.First),
            };
            if (pre.Ue != UeFlags.None || pre.SpecOpen >= 0)
            {
                d.Ue = pre.Ue;
                if (pre.SpecOpen >= 0) d.Specifiers = SpecText(pre.SpecOpen, pre.SpecClose);
            }
            if (pre.IsTemplate)
            {
                d.Modifiers |= DeclModifiers.Template;
                d.TemplateParameters = Render(pre.TplOpen + 1, P(pre.TplEnd - 1, Punct.Gt) ? pre.TplEnd - 1 : pre.TplEnd, false);
            }
            return d;
        }

        /// <summary>Raw specifier text between the parentheses at token indices <paramref name="open"/>..<paramref name="close"/>.</summary>
        private string SpecText(int open, int close)
        {
            int s = T(open).End;
            int e = close > open ? T(close).Start : s;
            if (e <= s) return string.Empty;
            return TextUtil.Collapse(new ReadOnlySpan<byte>(_src, s, e - s), _sb2, 1000);
        }

        private TextRange FindDoc(int firstTok)
        {
            if (_docs.Count == 0) return TextRange.None;
            int start = T(firstTok).Start;
            int prevEnd = firstTok > 0 ? T(firstTok - 1).End : 0;
            int lo = 0, hi = _docs.Count - 1, found = -1;
            while (lo <= hi)
            {
                int mid = (lo + hi) >> 1;
                if (_docs[mid].Start < start) { found = mid; lo = mid + 1; }
                else hi = mid - 1;
            }
            if (found < 0) return TextRange.None;
            var doc = _docs[found];
            return doc.End <= start && doc.Start >= prevEnd ? doc : TextRange.None;
        }

        private int Add(Declaration d)
        {
            FlushMacros(d.Offset);
            Declarations.Add(d);
            return Declarations.Count - 1;
        }

        /// <summary>Interleaves #define declarations (collected by the preprocessor) in source order.</summary>
        private void FlushMacros(int upTo)
        {
            while (_macroCursor < _macros.Count && _macros[_macroCursor].Offset <= upTo)
                Declarations.Add(_macros[_macroCursor++]);
        }

        private static string Join(string a, string b) => string.IsNullOrEmpty(a) ? b : a + "::" + b;

        private string JoinQuals(string container, List<string> quals)
        {
            if (quals == null || quals.Count == 0) return container;
            string q = quals.Count == 1 ? quals[0] : string.Join("::", quals);
            if (string.IsNullOrEmpty(container)) return q;
            if (ReferenceEquals(container, _joinA) && q == _joinB) return _joinResult;
            _joinA = container;
            _joinB = q;
            _joinResult = container + "::" + q;
            return _joinResult;
        }

        // ------------------------------------------------------------------ rendering

        /// <summary>
        /// Renders tokens [<paramref name="from"/>, <paramref name="to"/>) on one line, inserting a single space where the source
        /// had whitespace. <paramref name="skipNoise"/> drops export/attribute macros; <paramref name="skipModifiers"/> drops
        /// storage/function specifiers (virtual, static...). When <paramref name="cut"/> is ≥ 0 the base list (from ':' to it) is kept.
        /// </summary>
        private string Render(int from, int to, bool skipNoise, int cut = -1, bool skipModifiers = false)
        {
            _sb2.Clear();
            AppendRange(_sb2, from, cut >= 0 ? cut : to, skipNoise, skipModifiers);
            TextUtil.Truncate(_sb2, _maxSig);
            return _sb2.ToString();
        }

        private void AppendRange(StringBuilder sb, int from, int to, bool skipNoise, bool skipModifiers)
        {
            int startLen = sb.Length;
            bool prevOpen = true; // suppress a space after '(' / '[' / '::' / '~' and at the start
            for (int k = from; k < to && k < _n; k++)
            {
                ref var t = ref _t[k];
                if (t.Kind == TokenKind.EndOfFile) break;
                if (skipNoise)
                {
                    if (t.Kind == TokenKind.Identifier)
                    {
                        var role = (MacroRole)t.Sub;
                        if (role == MacroRole.Attribute || role == MacroRole.Export) continue;
                        if (role == MacroRole.AttributeCall && P(k + 1, Punct.LParen))
                        {
                            k = MatchParen(k + 1) - 1;
                            continue;
                        }
                    }
                    else if (t.Is(Punct.LBracket) && P(k + 1, Punct.LBracket))
                    {
                        k = SkipAttribute(k) - 1;
                        continue;
                    }
                }
                if (skipModifiers && t.Kind == TokenKind.Keyword)
                {
                    switch ((Keyword)t.Sub)
                    {
                        case Keyword.Virtual: case Keyword.Static: case Keyword.Inline: case Keyword.Explicit: case Keyword.Constexpr:
                        case Keyword.Consteval: case Keyword.Constinit: case Keyword.Extern: case Keyword.Mutable: case Keyword.Friend:
                        case Keyword.Thread_local: case Keyword.Register: case Keyword.Typedef:
                            continue;
                    }
                }
                bool closeLike = t.Kind == TokenKind.Punctuator &&
                                 (t.Is(Punct.RParen) || t.Is(Punct.RBracket) || t.Is(Punct.Comma) || t.Is(Punct.Semicolon) || t.Is(Punct.ColonColon));
                if (sb.Length > startLen && !prevOpen && !closeLike &&
                    ((t.Flags & TokenFlags.PrecededBySpace) != 0 || (k > from && _t[k - 1].End < t.Start)))
                    sb.Append(' ');
                AppendTokenText(sb, k);
                prevOpen = t.Kind == TokenKind.Punctuator &&
                           (t.Is(Punct.LParen) || t.Is(Punct.LBracket) || t.Is(Punct.ColonColon) || t.Is(Punct.Tilde));
                if (sb.Length - startLen > _maxSig) break;
            }
        }

        private void AppendTokenText(StringBuilder sb, int k)
        {
            ref var t = ref _t[k];
            var span = new ReadOnlySpan<byte>(_src, t.Start, t.Length);
            if (t.Kind == TokenKind.String && span.Length > 80)
            {
                TextUtil.AppendUtf8(sb, span.Slice(0, 76));
                sb.Append("…\"");
                return;
            }
            if (span.IndexOf((byte)'\n') >= 0) TextUtil.AppendCollapsed(sb, span);
            else TextUtil.AppendUtf8(sb, span);
        }
    }
}
