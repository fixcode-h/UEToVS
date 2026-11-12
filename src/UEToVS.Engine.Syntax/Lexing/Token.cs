using System;
using System.Runtime.InteropServices;

namespace UEToVS.Engine.Syntax
{
    /// <summary>Lexical category of a <see cref="Token"/>.</summary>
    public enum TokenKind : byte
    {
        EndOfFile = 0,
        Identifier = 1,
        Keyword = 2,
        Number = 3,
        String = 4,
        Char = 5,
        Punctuator = 6,
        /// <summary>Ordinary comment; only produced when the lexer is asked to keep comments.</summary>
        Comment = 7,
        /// <summary><c>/** ... */</c>, <c>/*! ... */</c>, <c>/// ...</c> or <c>//! ...</c>.</summary>
        DocComment = 8,
        /// <summary>A whole preprocessor directive line (with continuations); <see cref="Token.Sub"/> is a <see cref="DirectiveKind"/>.</summary>
        Directive = 9,
        /// <summary>A byte that starts no valid token (stray <c>@</c>, control characters...).</summary>
        Unknown = 10,
    }

    [Flags]
    public enum TokenFlags : byte
    {
        None = 0,
        /// <summary>The token is the first one on its line.</summary>
        AtLineStart = 1,
        /// <summary>Whitespace or a comment precedes the token.</summary>
        PrecededBySpace = 2,
        /// <summary>The token sits in a preprocessor branch whose condition could not be evaluated.</summary>
        Conditional = 4,
        /// <summary>Identifier spelled like a macro: no lower-case letters, at least two characters.</summary>
        MacroLike = 8,
    }

    /// <summary>Punctuator ids (<see cref="Token.Sub"/> for <see cref="TokenKind.Punctuator"/>).</summary>
    public enum Punct : byte
    {
        None = 0,
        LBrace, RBrace, LParen, RParen, LBracket, RBracket,
        Semicolon, Colon, ColonColon, Comma, Dot, Ellipsis, Arrow, ArrowStar, DotStar,
        Plus, PlusPlus, PlusEq, Minus, MinusMinus, MinusEq,
        Star, StarEq, Slash, SlashEq, Percent, PercentEq,
        Amp, AmpAmp, AmpEq, Pipe, PipePipe, PipeEq, Caret, CaretEq,
        Tilde, Bang, BangEq, Eq, EqEq,
        Lt, LtEq, LtLt, LtLtEq, Spaceship, Gt, GtEq, GtGt, GtGtEq,
        Question, Hash, HashHash, Backslash, At,
    }

    /// <summary>Directive ids (<see cref="Token.Sub"/> for <see cref="TokenKind.Directive"/>).</summary>
    public enum DirectiveKind : byte
    {
        Other = 0,
        Null,
        If, Ifdef, Ifndef, Elif, Elifdef, Elifndef, Else, Endif,
        Define, Undef, Include, IncludeNext, Import, Pragma, Error, Warning, Line,
    }

    /// <summary>
    /// A lexical token: 12 bytes, no strings. <see cref="Start"/> is a byte offset into the UTF-8 text.
    /// <see cref="Sub"/> holds a <see cref="Punct"/>, <see cref="Keyword"/> or <see cref="DirectiveKind"/> depending on <see cref="Kind"/>;
    /// for identifiers it is an internal macro classification filled in by the preprocessor.
    /// </summary>
    [StructLayout(LayoutKind.Sequential, Pack = 4)]
    public struct Token
    {
        public int Start;
        public int Length;
        public TokenKind Kind;
        public byte Sub;
        public TokenFlags Flags;

        public Token(TokenKind kind, byte sub, int start, int length, TokenFlags flags)
        {
            Kind = kind;
            Sub = sub;
            Start = start;
            Length = length;
            Flags = flags;
        }

        public int End => Start + Length;
        public Punct Punct => Kind == TokenKind.Punctuator ? (Punct)Sub : Punct.None;
        public Keyword Keyword => Kind == TokenKind.Keyword ? (Keyword)Sub : Keyword.None;
        public DirectiveKind Directive => Kind == TokenKind.Directive ? (DirectiveKind)Sub : DirectiveKind.Other;

        public bool Is(Punct p) => Kind == TokenKind.Punctuator && Sub == (byte)p;
        public bool Is(Keyword k) => Kind == TokenKind.Keyword && Sub == (byte)k;

        public override string ToString() => $"{Kind}({Start},{Length})";
    }
}
