using System;
using System.Runtime.CompilerServices;

namespace UEToVS.Engine.Syntax
{
    /// <summary>
    /// Zero-allocation C++ lexer over UTF-8 bytes. Produces <see cref="Token"/> values (kind, offset, length) one at a time.
    /// Ordinary comments are skipped unless <c>keepComments</c> is set; doc comments are always returned.
    /// Preprocessor directives are returned as single <see cref="TokenKind.Directive"/> tokens covering the whole logical line.
    /// A UTF-8 BOM at offset 0 is skipped (offsets still count it). Never throws.
    /// </summary>
    public ref struct CppLexer
    {
        // character classes
        private const byte CIdent = 1;   // [A-Za-z_$] and bytes >= 0x80
        private const byte CDigit = 2;
        private const byte CSpace = 4;   // space, tab, \v, \f, \r
        private const byte CLower = 8;

        private static readonly byte[] Class = BuildClasses();

        private readonly ReadOnlySpan<byte> _s;
        private readonly bool _directiveMode;
        private readonly bool _keepComments;
        private int _pos;
        private bool _atLineStart;
        private bool _space;

        /// <param name="text">The UTF-8 text. Offsets in tokens are relative to it.</param>
        /// <param name="start">Offset to start lexing at.</param>
        /// <param name="keepComments">Also return ordinary comments as <see cref="TokenKind.Comment"/>.</param>
        /// <param name="directiveMode">Lex the inside of a directive: '#' is a punctuator and no directives are recognised.</param>
        public CppLexer(ReadOnlySpan<byte> text, int start = 0, bool keepComments = false, bool directiveMode = false)
        {
            _s = text;
            _pos = start;
            if (start == 0 && text.Length >= 3 && text[0] == 0xEF && text[1] == 0xBB && text[2] == 0xBF) _pos = 3;
            _directiveMode = directiveMode;
            _keepComments = keepComments;
            _atLineStart = true;
            _space = false;
        }

        public int Position => _pos;

        private static byte[] BuildClasses()
        {
            var c = new byte[256];
            for (int i = 'a'; i <= 'z'; i++) c[i] = CIdent | CLower;
            for (int i = 'A'; i <= 'Z'; i++) c[i] = CIdent;
            c['_'] = CIdent;
            c['$'] = CIdent;
            for (int i = 0x80; i < 256; i++) c[i] = CIdent;
            for (int i = '0'; i <= '9'; i++) c[i] = CDigit;
            c[' '] = CSpace; c['\t'] = CSpace; c['\r'] = CSpace; c['\v'] = CSpace; c['\f'] = CSpace;
            return c;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        internal static bool IsIdentChar(byte b) => (Class[b] & (CIdent | CDigit)) != 0;

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        internal static bool IsIdentStart(byte b) => (Class[b] & CIdent) != 0;

        /// <summary>Returns the next token, or a <see cref="TokenKind.EndOfFile"/> token at the end.</summary>
        public Token Next()
        {
            var s = _s;
            while (true)
            {
                // whitespace, newlines, line continuations
                while (_pos < s.Length)
                {
                    byte b = s[_pos];
                    if ((Class[b] & CSpace) != 0) { _pos++; _space = true; continue; }
                    if (b == (byte)'\n') { _pos++; _atLineStart = true; _space = true; continue; }
                    if (b == (byte)'\\')
                    {
                        int k = _pos + 1;
                        while (k < s.Length && (s[k] == (byte)' ' || s[k] == (byte)'\t' || s[k] == (byte)'\r')) k++;
                        if (k < s.Length && s[k] == (byte)'\n') { _pos = k + 1; _space = true; continue; }
                        if (k >= s.Length) { _pos = k; _space = true; continue; }
                    }
                    break;
                }

                if (_pos >= s.Length) return new Token(TokenKind.EndOfFile, 0, s.Length, 0, Flags());

                int start = _pos;
                byte c = s[_pos];

                if (c == (byte)'/' && _pos + 1 < s.Length)
                {
                    byte n = s[_pos + 1];
                    if (n == (byte)'/')
                    {
                        bool doc = _pos + 2 < s.Length && (s[_pos + 2] == (byte)'!' ||
                                   (s[_pos + 2] == (byte)'/' && (_pos + 3 >= s.Length || s[_pos + 3] != (byte)'/')));
                        if (doc && _pos + 3 < s.Length && s[_pos + 3] == (byte)'<') doc = false; // trailing "///<" member doc
                        _pos = SkipLineComment(s, _pos + 2);
                        if (doc) return Make(TokenKind.DocComment, 0, start);
                        if (_keepComments) return Make(TokenKind.Comment, 0, start);
                        _space = true;
                        continue;
                    }
                    if (n == (byte)'*')
                    {
                        bool doc = _pos + 2 < s.Length && (s[_pos + 2] == (byte)'!' ||
                                   (s[_pos + 2] == (byte)'*' && _pos + 3 < s.Length && s[_pos + 3] != (byte)'/' && s[_pos + 3] != (byte)'*' && s[_pos + 3] != (byte)'<'));
                        int end = s.Slice(_pos + 2).IndexOf("*/"u8);
                        _pos = end < 0 ? s.Length : _pos + 2 + end + 2;
                        if (doc) return Make(TokenKind.DocComment, 0, start);
                        if (_keepComments) return Make(TokenKind.Comment, 0, start);
                        _space = true;
                        continue;
                    }
                }

                if (c == (byte)'#' && _atLineStart && !_directiveMode)
                {
                    var kind = LexDirective(s);
                    return Make(TokenKind.Directive, (byte)kind, start);
                }

                if ((Class[c] & CIdent) != 0)
                {
                    int p = _pos + 1;
                    while (p < s.Length && (Class[s[p]] & (CIdent | CDigit)) != 0) p++;
                    if (p < s.Length && (s[p] == (byte)'"' || s[p] == (byte)'\'') && IsLiteralPrefix(s.Slice(_pos, p - _pos)))
                    {
                        bool raw = s[p - 1] == (byte)'R';
                        if (s[p] == (byte)'"')
                        {
                            _pos = raw ? LexRawString(s, p) : LexQuoted(s, p, (byte)'"');
                            return Make(TokenKind.String, 0, start);
                        }
                        if (!raw)
                        {
                            _pos = LexQuoted(s, p, (byte)'\'');
                            return Make(TokenKind.Char, 0, start);
                        }
                    }
                    _pos = p;
                    var kw = Keywords.Lookup(s.Slice(start, p - start));
                    if (kw != Keyword.None) return Make(TokenKind.Keyword, (byte)kw, start);
                    return Make(TokenKind.Identifier, 0, start);
                }

                if ((Class[c] & CDigit) != 0 || (c == (byte)'.' && _pos + 1 < s.Length && (Class[s[_pos + 1]] & CDigit) != 0))
                {
                    _pos = LexNumber(s, _pos);
                    return Make(TokenKind.Number, 0, start);
                }

                if (c == (byte)'"')
                {
                    _pos = LexQuoted(s, _pos, (byte)'"');
                    return Make(TokenKind.String, 0, start);
                }
                if (c == (byte)'\'')
                {
                    _pos = LexQuoted(s, _pos, (byte)'\'');
                    return Make(TokenKind.Char, 0, start);
                }

                var punct = LexPunct(s);
                if (punct == Punct.None)
                {
                    _pos++;
                    return Make(TokenKind.Unknown, 0, start);
                }
                return Make(TokenKind.Punctuator, (byte)punct, start);
            }
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private TokenFlags Flags()
        {
            var f = TokenFlags.None;
            if (_atLineStart) f |= TokenFlags.AtLineStart;
            if (_space) f |= TokenFlags.PrecededBySpace;
            return f;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private Token Make(TokenKind kind, byte sub, int start)
        {
            var t = new Token(kind, sub, start, _pos - start, Flags());
            _atLineStart = kind == TokenKind.Directive; // the directive consumed its line; the next token starts a new one
            _space = false;
            return t;
        }

        private static bool IsLiteralPrefix(ReadOnlySpan<byte> p)
        {
            switch (p.Length)
            {
                case 1: return p[0] == (byte)'L' || p[0] == (byte)'u' || p[0] == (byte)'U' || p[0] == (byte)'R';
                case 2:
                    return (p[0] == (byte)'u' && p[1] == (byte)'8') ||
                           ((p[0] == (byte)'L' || p[0] == (byte)'u' || p[0] == (byte)'U') && p[1] == (byte)'R');
                case 3: return p[0] == (byte)'u' && p[1] == (byte)'8' && p[2] == (byte)'R';
                default: return false;
            }
        }

        private static int SkipLineComment(ReadOnlySpan<byte> s, int p)
        {
            while (true)
            {
                int nl = s.Slice(p).IndexOf((byte)'\n');
                if (nl < 0) return s.Length;
                int e = p + nl;
                // a backslash before the newline (optionally followed by \r) continues the comment
                int k = e - 1;
                if (k >= p && s[k] == (byte)'\r') k--;
                if (k >= p && s[k] == (byte)'\\') { p = e + 1; continue; }
                return e;
            }
        }

        /// <summary>Quoted literal starting at the opening quote; stops at the closing quote or an unescaped newline.</summary>
        private static int LexQuoted(ReadOnlySpan<byte> s, int p, byte quote)
        {
            p++;
            while (p < s.Length)
            {
                byte b = s[p];
                if (b == quote) return p + 1;
                if (b == (byte)'\\') { p += 2; continue; }
                if (b == (byte)'\n') return p;
                p++;
            }
            return s.Length;
        }

        /// <summary>R"delim( ... )delim" starting at the quote.</summary>
        private static int LexRawString(ReadOnlySpan<byte> s, int quote)
        {
            int p = quote + 1;
            int d = p;
            while (p < s.Length && p - d <= 16)
            {
                byte b = s[p];
                if (b == (byte)'(') break;
                if (b == (byte)')' || b == (byte)'\\' || b == (byte)' ' || b == (byte)'\n' || b == (byte)'"') return LexQuoted(s, quote, (byte)'"');
                p++;
            }
            if (p >= s.Length || s[p] != (byte)'(') return LexQuoted(s, quote, (byte)'"');
            var delim = s.Slice(d, p - d);
            p++;
            while (p < s.Length)
            {
                int close = s.Slice(p).IndexOf((byte)')');
                if (close < 0) return s.Length;
                int c = p + close + 1;
                if (c + delim.Length < s.Length && s.Slice(c, delim.Length).SequenceEqual(delim) && s[c + delim.Length] == (byte)'"')
                    return c + delim.Length + 1;
                p = c;
            }
            return s.Length;
        }

        private static int LexNumber(ReadOnlySpan<byte> s, int p)
        {
            p++;
            while (p < s.Length)
            {
                byte b = s[p];
                if ((Class[b] & (CIdent | CDigit)) != 0 && b < 0x80 || b == (byte)'.')
                {
                    if ((b == (byte)'e' || b == (byte)'E' || b == (byte)'p' || b == (byte)'P') && p + 1 < s.Length &&
                        (s[p + 1] == (byte)'+' || s[p + 1] == (byte)'-'))
                    {
                        // a hex digit 'e' followed by '-' is still an exponent only for decimal floats; good enough
                        p += 2;
                        continue;
                    }
                    p++;
                    continue;
                }
                if (b == (byte)'\'' && p + 1 < s.Length && (Class[s[p + 1]] & (CIdent | CDigit)) != 0) { p += 2; continue; }
                break;
            }
            return p;
        }

        private DirectiveKind LexDirective(ReadOnlySpan<byte> s)
        {
            int p = _pos + 1;
            while (p < s.Length && (s[p] == (byte)' ' || s[p] == (byte)'\t')) p++;
            int ns = p;
            while (p < s.Length && (Class[s[p]] & (CIdent | CDigit)) != 0) p++;
            var kind = ClassifyDirective(s.Slice(ns, p - ns));

            // find the end of the logical line, honouring continuations, block comments and literals
            while (p < s.Length)
            {
                byte b = s[p];
                if (b == (byte)'\n')
                {
                    int k = p - 1;
                    if (k >= 0 && s[k] == (byte)'\r') k--;
                    if (k >= 0 && s[k] == (byte)'\\') { p++; continue; }
                    break;
                }
                if (b == (byte)'/' && p + 1 < s.Length)
                {
                    if (s[p + 1] == (byte)'*')
                    {
                        int end = s.Slice(p + 2).IndexOf("*/"u8);
                        p = end < 0 ? s.Length : p + 2 + end + 2;
                        continue;
                    }
                    if (s[p + 1] == (byte)'/')
                    {
                        p = SkipLineComment(s, p + 2);
                        continue;
                    }
                }
                if ((b == (byte)'"' || b == (byte)'\'') && kind != DirectiveKind.Error && kind != DirectiveKind.Warning)
                {
                    // only treat as a literal when it closes on this line (apostrophes in #pragma message text...)
                    int e = p + 1;
                    while (e < s.Length && s[e] != b && s[e] != (byte)'\n') e += s[e] == (byte)'\\' ? 2 : 1;
                    if (e < s.Length && s[e] == b) { p = e + 1; continue; }
                }
                p++;
            }
            // trim a trailing \r from the token
            int endPos = Math.Min(p, s.Length);
            _pos = endPos;
            return kind;
        }

        internal static DirectiveKind ClassifyDirective(ReadOnlySpan<byte> n)
        {
            if (n.Length == 0) return DirectiveKind.Null;
            switch (n.Length)
            {
                case 2: if (n.SequenceEqual("if"u8)) return DirectiveKind.If; break;
                case 4:
                    if (n.SequenceEqual("elif"u8)) return DirectiveKind.Elif;
                    if (n.SequenceEqual("else"u8)) return DirectiveKind.Else;
                    if (n.SequenceEqual("line"u8)) return DirectiveKind.Line;
                    break;
                case 5:
                    if (n.SequenceEqual("ifdef"u8)) return DirectiveKind.Ifdef;
                    if (n.SequenceEqual("endif"u8)) return DirectiveKind.Endif;
                    if (n.SequenceEqual("undef"u8)) return DirectiveKind.Undef;
                    if (n.SequenceEqual("error"u8)) return DirectiveKind.Error;
                    break;
                case 6:
                    if (n.SequenceEqual("ifndef"u8)) return DirectiveKind.Ifndef;
                    if (n.SequenceEqual("define"u8)) return DirectiveKind.Define;
                    if (n.SequenceEqual("pragma"u8)) return DirectiveKind.Pragma;
                    if (n.SequenceEqual("import"u8)) return DirectiveKind.Import;
                    break;
                case 7:
                    if (n.SequenceEqual("include"u8)) return DirectiveKind.Include;
                    if (n.SequenceEqual("elifdef"u8)) return DirectiveKind.Elifdef;
                    if (n.SequenceEqual("warning"u8)) return DirectiveKind.Warning;
                    break;
                case 8: if (n.SequenceEqual("elifndef"u8)) return DirectiveKind.Elifndef; break;
                case 12: if (n.SequenceEqual("include_next"u8)) return DirectiveKind.IncludeNext; break;
            }
            return DirectiveKind.Other;
        }

        private Punct LexPunct(ReadOnlySpan<byte> s)
        {
            int p = _pos;
            byte c = s[p];
            byte n = p + 1 < s.Length ? s[p + 1] : (byte)0;
            byte n2 = p + 2 < s.Length ? s[p + 2] : (byte)0;
            Punct r;
            int len = 1;
            switch (c)
            {
                case (byte)'{': r = Punct.LBrace; break;
                case (byte)'}': r = Punct.RBrace; break;
                case (byte)'(': r = Punct.LParen; break;
                case (byte)')': r = Punct.RParen; break;
                case (byte)'[': r = Punct.LBracket; break;
                case (byte)']': r = Punct.RBracket; break;
                case (byte)';': r = Punct.Semicolon; break;
                case (byte)',': r = Punct.Comma; break;
                case (byte)'?': r = Punct.Question; break;
                case (byte)'~': r = Punct.Tilde; break;
                case (byte)'@': r = Punct.At; break;
                case (byte)'\\': r = Punct.Backslash; break;
                case (byte)':': if (n == (byte)':') { r = Punct.ColonColon; len = 2; } else r = Punct.Colon; break;
                case (byte)'.':
                    if (n == (byte)'.' && n2 == (byte)'.') { r = Punct.Ellipsis; len = 3; }
                    else if (n == (byte)'*') { r = Punct.DotStar; len = 2; }
                    else r = Punct.Dot;
                    break;
                case (byte)'+':
                    if (n == (byte)'+') { r = Punct.PlusPlus; len = 2; }
                    else if (n == (byte)'=') { r = Punct.PlusEq; len = 2; }
                    else r = Punct.Plus;
                    break;
                case (byte)'-':
                    if (n == (byte)'-') { r = Punct.MinusMinus; len = 2; }
                    else if (n == (byte)'=') { r = Punct.MinusEq; len = 2; }
                    else if (n == (byte)'>') { if (n2 == (byte)'*') { r = Punct.ArrowStar; len = 3; } else { r = Punct.Arrow; len = 2; } }
                    else r = Punct.Minus;
                    break;
                case (byte)'*': if (n == (byte)'=') { r = Punct.StarEq; len = 2; } else r = Punct.Star; break;
                case (byte)'/': if (n == (byte)'=') { r = Punct.SlashEq; len = 2; } else r = Punct.Slash; break;
                case (byte)'%': if (n == (byte)'=') { r = Punct.PercentEq; len = 2; } else r = Punct.Percent; break;
                case (byte)'^': if (n == (byte)'=') { r = Punct.CaretEq; len = 2; } else r = Punct.Caret; break;
                case (byte)'&':
                    if (n == (byte)'&') { r = Punct.AmpAmp; len = 2; }
                    else if (n == (byte)'=') { r = Punct.AmpEq; len = 2; }
                    else r = Punct.Amp;
                    break;
                case (byte)'|':
                    if (n == (byte)'|') { r = Punct.PipePipe; len = 2; }
                    else if (n == (byte)'=') { r = Punct.PipeEq; len = 2; }
                    else r = Punct.Pipe;
                    break;
                case (byte)'!': if (n == (byte)'=') { r = Punct.BangEq; len = 2; } else r = Punct.Bang; break;
                case (byte)'=': if (n == (byte)'=') { r = Punct.EqEq; len = 2; } else r = Punct.Eq; break;
                case (byte)'<':
                    if (n == (byte)'<') { if (n2 == (byte)'=') { r = Punct.LtLtEq; len = 3; } else { r = Punct.LtLt; len = 2; } }
                    else if (n == (byte)'=') { if (n2 == (byte)'>') { r = Punct.Spaceship; len = 3; } else { r = Punct.LtEq; len = 2; } }
                    else r = Punct.Lt;
                    break;
                case (byte)'>':
                    if (n == (byte)'>') { if (n2 == (byte)'=') { r = Punct.GtGtEq; len = 3; } else { r = Punct.GtGt; len = 2; } }
                    else if (n == (byte)'=') { r = Punct.GtEq; len = 2; }
                    else r = Punct.Gt;
                    break;
                case (byte)'#': if (n == (byte)'#') { r = Punct.HashHash; len = 2; } else r = Punct.Hash; break;
                default: return Punct.None;
            }
            _pos = p + len;
            return r;
        }
    }
}
