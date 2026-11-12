using System;
using System.Buffers;
using System.Collections.Generic;
using System.Text;
using UEToVS.Contracts;

namespace UEToVS.Engine.Syntax
{
    /// <summary>Growable token array rented from the shared pool.</summary>
    internal sealed class TokenBuffer : IDisposable
    {
        public Token[] Items;
        public int Count;

        public TokenBuffer(int capacity)
        {
            Items = ArrayPool<Token>.Shared.Rent(Math.Max(64, capacity));
        }

        public void Add(in Token t)
        {
            if (Count == Items.Length)
            {
                var bigger = ArrayPool<Token>.Shared.Rent(Items.Length * 2);
                Array.Copy(Items, bigger, Count);
                ArrayPool<Token>.Shared.Return(Items);
                Items = bigger;
            }
            Items[Count++] = t;
        }

        public void Dispose()
        {
            if (Items != null) ArrayPool<Token>.Shared.Return(Items);
            Items = null;
        }
    }

    /// <summary>
    /// Light preprocessor: runs the lexer, evaluates conditional directives against known macros, collects
    /// <c>#define</c>s and <c>#include</c>s, and produces the significant token stream for the parser (comments and
    /// directives removed, identifiers classified against the UE macro knowledge base). No include or macro expansion.
    /// </summary>
    internal sealed class Preprocessor
    {
        private struct Frame
        {
            public bool ParentActive;
            public bool ParentCond;
            public bool Active;
            public bool Cond;
            public bool Taken;
            public bool AnyUnknown;
            public bool CurUnknown;
            public bool DropRest;
            public bool KnownInactive;
            public int Brace;
            public int InactiveStart;
        }

        private readonly byte[] _src;
        private readonly int _len;
        private readonly ParseOptions _opt;
        private readonly UeMacroKnowledge _kb;
        private readonly LineMap _lines;
        private readonly StringBuilder _sb;
        private readonly Utf8Map<MacroDef> _macros = new Utf8Map<MacroDef>(64);
        private readonly ConditionEvaluator _eval;
        private Frame[] _frames = new Frame[16];
        private int _depth;
        private int _lastSignificantEnd;

        public readonly TokenBuffer Tokens;
        public readonly List<TextRange> Docs = new List<TextRange>();
        public readonly List<IncludeDirective> Includes = new List<IncludeDirective>();
        public readonly List<Declaration> Macros = new List<Declaration>();
        public readonly List<TextRange> Inactive = new List<TextRange>();
        public readonly List<SyntaxDiagnostic> Diagnostics;

        public Preprocessor(byte[] src, int length, ParseOptions options, UeMacroKnowledge kb, LineMap lines, StringBuilder scratch, List<SyntaxDiagnostic> diagnostics)
        {
            _src = src;
            _len = length;
            _opt = options;
            _kb = kb;
            _lines = lines;
            _sb = scratch;
            Diagnostics = diagnostics;
            Tokens = new TokenBuffer(length / 5 + 16);
            if (options.Defines != null)
                foreach (var kv in options.Defines)
                {
                    if (string.IsNullOrEmpty(kv.Key)) continue;
                    var body = Encoding.UTF8.GetBytes((kv.Value ?? string.Empty).Trim());
                    _macros.Set(kv.Key, new MacroDef { Defined = true, Body = body });
                }
            if (options.Undefines != null)
                foreach (var u in options.Undefines)
                    if (!string.IsNullOrEmpty(u)) _macros.Set(u, new MacroDef { Defined = false });
            _eval = new ConditionEvaluator(_macros, options.UnlistedMacrosAreUndefined);
        }

        private bool Active => _depth == 0 || _frames[_depth - 1].Active;
        private bool Cond => _depth > 0 && _frames[_depth - 1].Cond;

        private ReadOnlySpan<byte> Span(int start, int length) => new ReadOnlySpan<byte>(_src, start, length);

        public void Run()
        {
            var lexer = new CppLexer(new ReadOnlySpan<byte>(_src, 0, _len));
            while (true)
            {
                var t = lexer.Next();
                if (t.Kind == TokenKind.EndOfFile) break;
                if (t.Kind == TokenKind.Directive)
                {
                    var la = lexer; // copy for lookahead (include guards)
                    OnDirective(t, ref la);
                    _lastSignificantEnd = t.End;
                    continue;
                }
                if (!Active) continue;
                if (t.Kind == TokenKind.DocComment)
                {
                    AddDoc(t);
                    continue;
                }
                if (t.Kind == TokenKind.Comment) continue;

                if (_depth > 0)
                {
                    ref var f = ref _frames[_depth - 1];
                    if (f.Cond)
                    {
                        t.Flags |= TokenFlags.Conditional;
                        if (f.CurUnknown && t.Kind == TokenKind.Punctuator)
                        {
                            if (t.Sub == (byte)Punct.LBrace) f.Brace++;
                            else if (t.Sub == (byte)Punct.RBrace) f.Brace--;
                        }
                    }
                }
                if (t.Kind == TokenKind.Identifier) Classify(ref t);
                Tokens.Add(t);
                _lastSignificantEnd = t.End;
            }
            // unterminated conditionals
            while (_depth > 0)
            {
                ref var f = ref _frames[_depth - 1];
                if (f.KnownInactive && f.ParentActive) Inactive.Add(new TextRange(f.InactiveStart, _len - f.InactiveStart));
                _depth--;
            }
            Tokens.Add(new Token(TokenKind.EndOfFile, 0, _len, 0, TokenFlags.AtLineStart));
        }

        /// <summary>Records a doc comment; consecutive "///" lines separated only by whitespace merge into one range.</summary>
        private void AddDoc(Token t)
        {
            if (Docs.Count > 0 && _src[t.Start + 1] == (byte)'/')
            {
                var last = Docs[Docs.Count - 1];
                if (last.End <= t.Start && _src[last.Start + 1] == (byte)'/' && last.End >= _lastSignificantEnd &&
                    TextUtil.Trim(Span(last.End, t.Start - last.End)).Length == 0)
                {
                    Docs[Docs.Count - 1] = new TextRange(last.Start, t.End - last.Start);
                    return;
                }
            }
            Docs.Add(new TextRange(t.Start, t.Length));
        }

        /// <summary>Sets <see cref="TokenFlags.MacroLike"/> and the knowledge-base role (in <see cref="Token.Sub"/>).</summary>
        private void Classify(ref Token t)
        {
            var name = Span(t.Start, t.Length);
            byte first = name[0];
            bool lower = false, underscore = false;
            for (int i = 0; i < name.Length; i++)
            {
                byte b = name[i];
                if (b >= (byte)'a' && b <= (byte)'z') lower = true;
                else if (b == (byte)'_') underscore = true;
            }
            if (!lower && name.Length >= 2) t.Flags |= TokenFlags.MacroLike;
            bool candidate = (!lower && name.Length >= 2) || first == (byte)'_' || (underscore && first >= (byte)'A' && first <= (byte)'Z');
            if (!candidate) return;

            var info = _kb.Lookup(name);
            if (info != null)
            {
                t.Sub = (byte)info.Role;
                return;
            }
            if (_kb.IsExportMacro(name))
            {
                t.Sub = (byte)MacroRole.Export;
                return;
            }
            if (_macros.Count > 0 && _macros.TryGetValue(name, out var def) && def.Defined && (def.Body == null || def.Body.Length == 0))
                t.Sub = (byte)(def.FunctionLike ? MacroRole.AttributeCall : MacroRole.Attribute);
        }

        private void OnDirective(Token dir, ref CppLexer lookahead)
        {
            var kind = (DirectiveKind)dir.Sub;
            // operand range: after the directive name
            int p = dir.Start + 1;
            int end = dir.End;
            while (p < end && (_src[p] == (byte)' ' || _src[p] == (byte)'\t')) p++;
            while (p < end && CppLexer.IsIdentChar(_src[p])) p++;
            int operand = p;

            switch (kind)
            {
                case DirectiveKind.If:
                case DirectiveKind.Ifdef:
                case DirectiveKind.Ifndef:
                {
                    var f = new Frame { ParentActive = Active, ParentCond = Cond };
                    if (f.ParentActive) Enter(ref f, EvalIf(kind, operand, end, ref lookahead), dir);
                    else f.Active = false;
                    Push(f);
                    return;
                }
                case DirectiveKind.Elif:
                case DirectiveKind.Elifdef:
                case DirectiveKind.Elifndef:
                case DirectiveKind.Else:
                {
                    if (_depth == 0)
                    {
                        Diag(dir.Start, "#" + kind.ToString().ToLowerInvariant() + " without #if");
                        return;
                    }
                    ref var f = ref _frames[_depth - 1];
                    CloseInactive(ref f, dir.Start);
                    if (f.Active && f.CurUnknown && f.Brace != 0) f.DropRest = true;
                    if (!f.ParentActive)
                    {
                        f.Active = false;
                        return;
                    }
                    if (f.Taken || f.DropRest)
                    {
                        f.Active = false;
                        f.CurUnknown = false;
                        f.KnownInactive = f.Taken && !f.AnyUnknown;
                        f.InactiveStart = dir.End;
                        return;
                    }
                    Tri v = kind == DirectiveKind.Else ? Tri.True
                          : kind == DirectiveKind.Elif ? _eval.Evaluate(_src, operand, end)
                          : EvalDefined(kind == DirectiveKind.Elifndef, operand, end);
                    Enter(ref f, v, dir);
                    return;
                }
                case DirectiveKind.Endif:
                {
                    if (_depth == 0)
                    {
                        Diag(dir.Start, "#endif without #if");
                        return;
                    }
                    ref var f = ref _frames[_depth - 1];
                    CloseInactive(ref f, dir.Start);
                    _depth--;
                    return;
                }
            }

            if (!Active) return;

            switch (kind)
            {
                case DirectiveKind.Define:
                    OnDefine(dir, operand, end);
                    break;
                case DirectiveKind.Undef:
                {
                    var name = FirstIdent(operand, end);
                    if (name.Length > 0) _macros.Set(Span(name.Start, name.Length), new MacroDef { Defined = false });
                    break;
                }
                case DirectiveKind.Include:
                case DirectiveKind.IncludeNext:
                case DirectiveKind.Import:
                    OnInclude(dir, operand, end);
                    break;
            }
        }

        private void Push(in Frame f)
        {
            if (_depth == _frames.Length) Array.Resize(ref _frames, _frames.Length * 2);
            _frames[_depth++] = f;
        }

        private void Enter(ref Frame f, Tri v, Token dir)
        {
            f.Brace = 0;
            if (v == Tri.True)
            {
                f.Active = true;
                f.Taken = true;
                f.CurUnknown = false;
            }
            else if (v == Tri.False)
            {
                f.Active = false;
                f.CurUnknown = false;
                f.KnownInactive = true;
                f.InactiveStart = dir.End;
            }
            else
            {
                f.Active = true;
                f.AnyUnknown = true;
                f.CurUnknown = true;
            }
            f.Cond = f.ParentCond || f.AnyUnknown;
        }

        private void CloseInactive(ref Frame f, int at)
        {
            if (!f.Active && f.ParentActive && f.KnownInactive && at > f.InactiveStart)
                Inactive.Add(new TextRange(f.InactiveStart, at - f.InactiveStart));
            f.KnownInactive = false;
        }

        private Tri EvalIf(DirectiveKind kind, int operand, int end, ref CppLexer lookahead)
        {
            Tri v;
            ReadOnlySpan<byte> guard = default;
            if (kind == DirectiveKind.If)
            {
                v = _eval.Evaluate(_src, operand, end);
                if (v == Tri.Unknown) guard = NotDefinedPattern(operand, end);
            }
            else
            {
                v = EvalDefined(kind == DirectiveKind.Ifndef, operand, end);
                if (v == Tri.Unknown && kind == DirectiveKind.Ifndef)
                {
                    var r = FirstIdent(operand, end);
                    guard = Span(r.Start, r.Length);
                }
            }
            // include guard: "#ifndef X" / "#if !defined(X)" immediately followed by "#define X"
            if (v == Tri.Unknown && guard.Length > 0)
            {
                Token next;
                do next = lookahead.Next(); while (next.Kind == TokenKind.DocComment || next.Kind == TokenKind.Comment);
                if (next.Kind == TokenKind.Directive && next.Sub == (byte)DirectiveKind.Define)
                {
                    int p = next.Start + 1;
                    while (p < next.End && (_src[p] == (byte)' ' || _src[p] == (byte)'\t')) p++;
                    p += 6; // "define"
                    var name = FirstIdent(p, next.End);
                    if (name.Length > 0 && Span(name.Start, name.Length).SequenceEqual(guard)) return Tri.True;
                }
            }
            return v;
        }

        private Tri EvalDefined(bool negate, int operand, int end)
        {
            var name = FirstIdent(operand, end);
            if (name.Length == 0) return Tri.Unknown;
            var d = _eval.IsDefined(Span(name.Start, name.Length));
            if (d == Tri.Unknown) return d;
            return (d == Tri.True) != negate ? Tri.True : Tri.False;
        }

        /// <summary>For "!defined(X)" / "!defined X" returns X.</summary>
        private ReadOnlySpan<byte> NotDefinedPattern(int operand, int end)
        {
            var lx = new CppLexer(new ReadOnlySpan<byte>(_src, 0, end), operand, false, true);
            var a = lx.Next();
            if (!a.Is(Punct.Bang)) return default;
            var b = lx.Next();
            if (b.Kind != TokenKind.Identifier || !Span(b.Start, b.Length).SequenceEqual("defined"u8)) return default;
            var c = lx.Next();
            bool paren = c.Is(Punct.LParen);
            if (paren) c = lx.Next();
            if (c.Kind != TokenKind.Identifier) return default;
            var d = lx.Next();
            if (paren)
            {
                if (!d.Is(Punct.RParen)) return default;
                d = lx.Next();
            }
            return d.Kind == TokenKind.EndOfFile ? Span(c.Start, c.Length) : default;
        }

        private TextRange FirstIdent(int start, int end)
        {
            var lx = new CppLexer(new ReadOnlySpan<byte>(_src, 0, end), start, false, true);
            var t = lx.Next();
            return t.Kind == TokenKind.Identifier || t.Kind == TokenKind.Keyword ? new TextRange(t.Start, t.Length) : default;
        }

        private void OnDefine(Token dir, int operand, int end)
        {
            var lx = new CppLexer(new ReadOnlySpan<byte>(_src, 0, end), operand, false, true);
            var name = lx.Next();
            if (name.Kind != TokenKind.Identifier && name.Kind != TokenKind.Keyword)
            {
                Diag(dir.Start, "#define without a name");
                return;
            }
            int bodyStart = name.End;
            string parameters = null;
            bool functionLike = false;
            if (bodyStart < end && _src[bodyStart] == (byte)'(')
            {
                functionLike = true;
                int close = Span(bodyStart, end - bodyStart).IndexOf((byte)')');
                int pe = close < 0 ? end : bodyStart + close;
                parameters = TextUtil.Collapse(Span(bodyStart + 1, Math.Max(0, pe - bodyStart - 1)), _sb, 200);
                bodyStart = close < 0 ? end : pe + 1;
            }
            var body = StripComments(TextUtil.Trim(Span(bodyStart, end - bodyStart)));
            _macros.Set(Span(name.Start, name.Length), new MacroDef { Defined = true, FunctionLike = functionLike, Body = body });

            string nameText = Encoding.UTF8.GetString(Span(name.Start, name.Length));
            string value = body.Length == 0 ? string.Empty : TextUtil.Collapse(body, _sb, 200);
            _sb.Clear();
            _sb.Append("#define ").Append(nameText);
            if (functionLike) _sb.Append('(').Append(parameters).Append(')');
            if (value.Length > 0) _sb.Append(' ').Append(value);
            TextUtil.Truncate(_sb, _opt.MaxSignatureLength);

            var pos = _lines.GetPosition(new ReadOnlySpan<byte>(_src, 0, _len), name.Start);
            var doc = TextRange.None;
            if (Docs.Count > 0)
            {
                var last = Docs[Docs.Count - 1];
                if (last.Start >= _lastSignificantEnd && last.End <= dir.Start) doc = last;
            }
            Macros.Add(new Declaration
            {
                Name = nameText,
                Kind = SymbolKind.Macro,
                Signature = _sb.ToString(),
                Offset = name.Start,
                NameLength = name.Length,
                Line = pos.Line,
                Column = pos.Column,
                IsDefinition = true,
                IsConditional = Cond,
                Parameters = parameters,
                Value = value,
                Modifiers = functionLike ? DeclModifiers.FunctionLike : DeclModifiers.None,
                DocComment = doc,
            });
        }

        /// <summary>Removes comments from a macro body (rare; only allocates when there is one).</summary>
        private static byte[] StripComments(ReadOnlySpan<byte> body)
        {
            if (body.IndexOf((byte)'/') < 0) return body.ToArray();
            var outBytes = new List<byte>(body.Length);
            var lx = new CppLexer(body, 0, true, true);
            int prevEnd = 0;
            while (true)
            {
                var t = lx.Next();
                if (t.Kind == TokenKind.EndOfFile) break;
                if (t.Kind == TokenKind.Comment || t.Kind == TokenKind.DocComment)
                {
                    for (int i = prevEnd; i < t.Start; i++) outBytes.Add(body[i]);
                    outBytes.Add((byte)' ');
                    prevEnd = t.End;
                }
            }
            for (int i = prevEnd; i < body.Length; i++) outBytes.Add(body[i]);
            return TextUtil.Trim(outBytes.ToArray()).ToArray();
        }

        private void OnInclude(Token dir, int operand, int end)
        {
            var text = TextUtil.Trim(Span(operand, end - operand));
            int textStart = operand;
            while (textStart < end && (_src[textStart] == (byte)' ' || _src[textStart] == (byte)'\t')) textStart++;
            if (text.Length == 0) return;
            string path;
            bool angled = false, isMacro = false;
            if (text[0] == (byte)'"')
            {
                int close = text.Slice(1).IndexOf((byte)'"');
                path = Encoding.UTF8.GetString(close < 0 ? text.Slice(1) : text.Slice(1, close));
            }
            else if (text[0] == (byte)'<')
            {
                int close = text.Slice(1).IndexOf((byte)'>');
                path = Encoding.UTF8.GetString(close < 0 ? text.Slice(1) : text.Slice(1, close));
                angled = true;
            }
            else
            {
                path = TextUtil.Collapse(text, _sb, 300);
                isMacro = true;
            }
            Includes.Add(new IncludeDirective
            {
                Path = path,
                IsAngled = angled,
                Line = _lines.GetLine(dir.Start),
                Offset = dir.Start,
                IsConditional = Cond,
                IsMacro = isMacro,
            });
        }

        private void Diag(int offset, string message)
        {
            if (Diagnostics.Count < 100) Diagnostics.Add(new SyntaxDiagnostic(offset, _lines.GetLine(offset), message));
        }
    }
}
