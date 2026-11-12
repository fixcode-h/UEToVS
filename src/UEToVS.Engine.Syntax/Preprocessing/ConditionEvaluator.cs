using System;
using System.Collections.Generic;

namespace UEToVS.Engine.Syntax
{
    internal enum Tri : byte
    {
        False = 0,
        True = 1,
        Unknown = 2,
    }

    /// <summary>State of a macro for <c>#if</c> evaluation.</summary>
    internal sealed class MacroDef
    {
        public bool Defined;
        public bool FunctionLike;

        /// <summary>Replacement text as UTF-8 (trimmed); empty for <c>#define X</c>.</summary>
        public byte[] Body;
    }

    /// <summary>
    /// Evaluates a preprocessor condition with three-valued logic: anything depending on a macro whose state is unknown
    /// (or on a function-like macro) is <see cref="Tri.Unknown"/>, unless short-circuiting makes the result certain.
    /// </summary>
    internal sealed class ConditionEvaluator
    {
        private readonly struct Val
        {
            public readonly long V;
            public readonly bool Known;

            public Val(long v, bool known)
            {
                V = v;
                Known = known;
            }

            public static readonly Val Unknown = new Val(0, false);
            public static Val Of(long v) => new Val(v, true);
            public static Val Of(bool b) => new Val(b ? 1 : 0, true);
        }

        private const int MaxDepth = 16;

        private readonly Utf8Map<MacroDef> _macros;
        private readonly bool _unlistedUndefined;
        private readonly List<Token> _tok = new List<Token>(32);
        private byte[] _src;
        private int _p;
        private int _end;
        private int _depth;

        public ConditionEvaluator(Utf8Map<MacroDef> macros, bool unlistedUndefined)
        {
            _macros = macros;
            _unlistedUndefined = unlistedUndefined;
        }

        /// <summary>Evaluates the expression in <paramref name="src"/>[<paramref name="start"/>..<paramref name="end"/>).</summary>
        public Tri Evaluate(byte[] src, int start, int end)
        {
            try
            {
                _depth = 0;
                var v = EvalRange(src, start, end);
                return !v.Known ? Tri.Unknown : v.V != 0 ? Tri.True : Tri.False;
            }
            catch (Exception)
            {
                // malformed input; never let a directive break the parse
                return Tri.Unknown;
            }
        }

        /// <summary>Whether a macro is known defined (True), known undefined (False) or unknown.</summary>
        public Tri IsDefined(ReadOnlySpan<byte> name)
        {
            if (_macros.TryGetValue(name, out var d)) return d.Defined ? Tri.True : Tri.False;
            if (name.SequenceEqual("__cplusplus"u8) || name.SequenceEqual("__FILE__"u8) || name.SequenceEqual("__LINE__"u8)) return Tri.True;
            return _unlistedUndefined ? Tri.False : Tri.Unknown;
        }

        private Val EvalRange(byte[] src, int start, int end)
        {
            // save state for nested evaluation of macro bodies
            var savedSrc = _src;
            int savedP = _p, savedEnd = _end, savedCount = _tok.Count;
            int baseIndex = _tok.Count;
            var lexer = new CppLexer(new ReadOnlySpan<byte>(src, 0, end), start, false, true);
            while (true)
            {
                var t = lexer.Next();
                if (t.Kind == TokenKind.EndOfFile) break;
                if (t.Kind == TokenKind.DocComment || t.Kind == TokenKind.Comment) continue;
                _tok.Add(t);
                if (_tok.Count - baseIndex > 4096) break;
            }
            _src = src;
            _p = baseIndex;
            _end = _tok.Count;
            Val v;
            try
            {
                v = _p < _end ? Ternary() : Val.Unknown;
                if (_p < _end) v = Val.Unknown; // trailing garbage
            }
            finally
            {
                _tok.RemoveRange(savedCount, _tok.Count - savedCount);
                _src = savedSrc;
                _p = savedP;
                _end = savedEnd;
            }
            return v;
        }

        private bool AtEnd => _p >= _end;
        private Token Cur => _p < _end ? _tok[_p] : default;

        private bool IsP(Punct p) => _p < _end && _tok[_p].Is(p);

        private ReadOnlySpan<byte> Text(Token t) => new ReadOnlySpan<byte>(_src, t.Start, t.Length);

        private Val Ternary()
        {
            var c = Binary(1);
            if (!IsP(Punct.Question)) return c;
            _p++;
            var a = Ternary();
            if (IsP(Punct.Colon)) _p++;
            var b = Ternary();
            if (c.Known) return c.V != 0 ? a : b;
            if (a.Known && b.Known && a.V == b.V) return a;
            return Val.Unknown;
        }

        private static int Prec(Punct p)
        {
            switch (p)
            {
                case Punct.PipePipe: return 1;
                case Punct.AmpAmp: return 2;
                case Punct.Pipe: return 3;
                case Punct.Caret: return 4;
                case Punct.Amp: return 5;
                case Punct.EqEq: case Punct.BangEq: return 6;
                case Punct.Lt: case Punct.Gt: case Punct.LtEq: case Punct.GtEq: return 7;
                case Punct.LtLt: case Punct.GtGt: return 8;
                case Punct.Plus: case Punct.Minus: return 9;
                case Punct.Star: case Punct.Slash: case Punct.Percent: return 10;
                default: return 0;
            }
        }

        private Val Binary(int minPrec)
        {
            var left = Unary();
            while (!AtEnd)
            {
                var op = Cur.Punct;
                int prec = Prec(op);
                if (prec == 0 || prec < minPrec) break;
                _p++;
                var right = Binary(prec + 1);
                left = Apply(op, left, right);
            }
            return left;
        }

        private static Val Apply(Punct op, Val a, Val b)
        {
            if (op == Punct.AmpAmp)
            {
                if ((a.Known && a.V == 0) || (b.Known && b.V == 0)) return Val.Of(0);
                return a.Known && b.Known ? Val.Of(1) : Val.Unknown;
            }
            if (op == Punct.PipePipe)
            {
                if ((a.Known && a.V != 0) || (b.Known && b.V != 0)) return Val.Of(1);
                return a.Known && b.Known ? Val.Of(0) : Val.Unknown;
            }
            if (!a.Known || !b.Known) return Val.Unknown;
            long x = a.V, y = b.V;
            switch (op)
            {
                case Punct.Pipe: return Val.Of(x | y);
                case Punct.Caret: return Val.Of(x ^ y);
                case Punct.Amp: return Val.Of(x & y);
                case Punct.EqEq: return Val.Of(x == y);
                case Punct.BangEq: return Val.Of(x != y);
                case Punct.Lt: return Val.Of(x < y);
                case Punct.Gt: return Val.Of(x > y);
                case Punct.LtEq: return Val.Of(x <= y);
                case Punct.GtEq: return Val.Of(x >= y);
                case Punct.LtLt: return Val.Of(y is >= 0 and < 64 ? x << (int)y : 0);
                case Punct.GtGt: return Val.Of(y is >= 0 and < 64 ? x >> (int)y : 0);
                case Punct.Plus: return Val.Of(unchecked(x + y));
                case Punct.Minus: return Val.Of(unchecked(x - y));
                case Punct.Star: return Val.Of(unchecked(x * y));
                case Punct.Slash: return y == 0 ? Val.Unknown : Val.Of(x / y);
                case Punct.Percent: return y == 0 ? Val.Unknown : Val.Of(x % y);
                default: return Val.Unknown;
            }
        }

        private Val Unary()
        {
            if (AtEnd) return Val.Unknown;
            var t = Cur;
            if (t.Kind == TokenKind.Punctuator)
            {
                switch (t.Punct)
                {
                    case Punct.Bang: { _p++; var v = Unary(); return v.Known ? Val.Of(v.V == 0) : v; }
                    case Punct.Tilde: { _p++; var v = Unary(); return v.Known ? Val.Of(~v.V) : v; }
                    case Punct.Minus: { _p++; var v = Unary(); return v.Known ? Val.Of(-v.V) : v; }
                    case Punct.Plus: { _p++; return Unary(); }
                    case Punct.LParen:
                    {
                        _p++;
                        var v = Ternary();
                        if (IsP(Punct.RParen)) _p++;
                        return v;
                    }
                }
                _p++;
                return Val.Unknown;
            }
            return Primary();
        }

        private Val Primary()
        {
            var t = Cur;
            _p++;
            switch (t.Kind)
            {
                case TokenKind.Number:
                    return ParseNumber(Text(t));
                case TokenKind.Char:
                {
                    var s = Text(t);
                    int q = s.IndexOf((byte)'\'');
                    if (q >= 0 && s.Length == q + 3) return Val.Of(s[q + 1]);
                    return Val.Unknown;
                }
                case TokenKind.Keyword:
                    if (t.Keyword == Keyword.True) return Val.Of(1);
                    if (t.Keyword == Keyword.False) return Val.Of(0);
                    return Val.Unknown;
                case TokenKind.Identifier:
                {
                    var name = Text(t);
                    if (name.SequenceEqual("defined"u8))
                    {
                        bool paren = IsP(Punct.LParen);
                        if (paren) _p++;
                        if (AtEnd) return Val.Unknown;
                        var n = Cur;
                        _p++;
                        if (paren && IsP(Punct.RParen)) _p++;
                        var d = IsDefined(Text(n));
                        return d == Tri.Unknown ? Val.Unknown : Val.Of(d == Tri.True);
                    }
                    if (IsP(Punct.LParen))
                    {
                        // function-like macro or __has_include(...): skip the arguments
                        SkipParens();
                        return Val.Unknown;
                    }
                    if (_macros.TryGetValue(name, out var def))
                    {
                        if (!def.Defined) return Val.Of(0);
                        if (def.FunctionLike || def.Body == null || def.Body.Length == 0 || _depth >= MaxDepth) return Val.Unknown;
                        _depth++;
                        try { return EvalRange(def.Body, 0, def.Body.Length); }
                        finally { _depth--; }
                    }
                    return _unlistedUndefined ? Val.Of(0) : Val.Unknown;
                }
                default:
                    return Val.Unknown;
            }
        }

        private void SkipParens()
        {
            int depth = 0;
            while (!AtEnd)
            {
                var t = Cur;
                _p++;
                if (t.Is(Punct.LParen)) depth++;
                else if (t.Is(Punct.RParen) && --depth == 0) return;
            }
        }

        private static Val ParseNumber(ReadOnlySpan<byte> s)
        {
            // strip suffixes
            int e = s.Length;
            while (e > 0 && (s[e - 1] == (byte)'u' || s[e - 1] == (byte)'U' || s[e - 1] == (byte)'l' || s[e - 1] == (byte)'L')) e--;
            s = s.Slice(0, e);
            if (s.Length == 0) return Val.Unknown;
            int radix = 10;
            int i = 0;
            if (s.Length > 2 && s[0] == (byte)'0' && (s[1] == (byte)'x' || s[1] == (byte)'X')) { radix = 16; i = 2; }
            else if (s.Length > 2 && s[0] == (byte)'0' && (s[1] == (byte)'b' || s[1] == (byte)'B')) { radix = 2; i = 2; }
            else if (s.Length > 1 && s[0] == (byte)'0') { radix = 8; i = 1; }
            long v = 0;
            for (; i < s.Length; i++)
            {
                byte b = s[i];
                if (b == (byte)'\'') continue;
                int d = b >= (byte)'0' && b <= (byte)'9' ? b - '0'
                      : b >= (byte)'a' && b <= (byte)'f' ? b - 'a' + 10
                      : b >= (byte)'A' && b <= (byte)'F' ? b - 'A' + 10 : 99;
                if (d >= radix) return Val.Unknown; // floats and odd literals
                v = unchecked(v * radix + d);
            }
            return Val.Of(v);
        }
    }
}
