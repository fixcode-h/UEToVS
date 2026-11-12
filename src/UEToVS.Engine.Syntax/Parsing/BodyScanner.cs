using System;

namespace UEToVS.Engine.Syntax
{
    internal sealed partial class Parser
    {
        /// <summary>
        /// Shallow scan of a function body starting at '{': emits identifier occurrences with their roles and returns the
        /// index after the matching '}'. Stops early (without consuming) at tokens that cannot appear in a body, so a
        /// missing '}' does not swallow the rest of the file.
        /// </summary>
        private int ScanBody(int open, int function)
        {
            int depth = 0;
            int i = open;
            int typeUntil = -1;
            while (true)
            {
                ref var t = ref _t[i];
                switch (t.Kind)
                {
                    case TokenKind.EndOfFile:
                        Diag(open, "unterminated function body");
                        return i;
                    case TokenKind.Punctuator:
                        if (t.Sub == (byte)Punct.LBrace) depth++;
                        else if (t.Sub == (byte)Punct.RBrace && --depth == 0) return i + 1;
                        break;
                    case TokenKind.Identifier:
                        if (t.Sub != 0 && IsHardRecovery(i))
                        {
                            Diag(open, "unterminated function body");
                            return i;
                        }
                        Emit(i, ref typeUntil, function, OccurrenceRole.None);
                        break;
                    case TokenKind.Keyword:
                        if (t.Sub == (byte)Keyword.Namespace)
                        {
                            Diag(open, "unterminated function body");
                            return i;
                        }
                        break;
                }
                i++;
            }
        }

        /// <summary>
        /// Constructor initializer list after ':'. Member names get <see cref="OccurrenceRole.Write"/>; arguments are scanned
        /// like body code. Returns the index of the body '{' (or where it stopped).
        /// </summary>
        private int ScanInitList(int i, int function)
        {
            int depth = 0;
            int start = i;
            int typeUntil = -1;
            bool collect = _opt.CollectOccurrences;
            while (true)
            {
                ref var t = ref T(i);
                if (t.Kind == TokenKind.EndOfFile) return i;
                if (t.Kind == TokenKind.Punctuator)
                {
                    switch ((Punct)t.Sub)
                    {
                        case Punct.LBrace:
                            if (depth == 0)
                            {
                                // "Member{...}" is a brace initializer; anything else opens the body
                                bool braceInit = i > start && (IsIdent(i - 1) || P(i - 1, Punct.Gt) || P(i - 1, Punct.GtGt));
                                if (!braceInit) return i;
                            }
                            depth++;
                            break;
                        case Punct.LParen: case Punct.LBracket: depth++; break;
                        case Punct.RParen: case Punct.RBracket: case Punct.RBrace:
                            if (depth == 0) return i;
                            depth--;
                            break;
                        case Punct.Semicolon:
                            if (depth == 0) return i;
                            break;
                    }
                }
                else if (t.Kind == TokenKind.Identifier)
                {
                    if (IsHardRecovery(i)) return i;
                    if (collect)
                    {
                        bool member = depth == 0 && (P(i + 1, Punct.LParen) || P(i + 1, Punct.LBrace)) && !P(i - 1, Punct.ColonColon);
                        Emit(i, ref typeUntil, function, member ? OccurrenceRole.Write : OccurrenceRole.None);
                    }
                }
                i++;
            }
        }

        private void Emit(int i, ref int typeUntil, int function, OccurrenceRole forced)
        {
            ref var t = ref _t[i];
            var macro = (MacroRole)t.Sub;
            if (macro == MacroRole.StringLiteral || macro == MacroRole.Export || macro == MacroRole.Attribute) return;

            ref var prev = ref _t[i - 1];
            ref var next = ref _t[i + 1];
            var role = OccurrenceRole.None;
            var q = MemberQualifier.None;
            if (prev.Kind == TokenKind.Punctuator)
            {
                switch ((Punct)prev.Sub)
                {
                    case Punct.Dot: q = MemberQualifier.Dot; break;
                    case Punct.Arrow: q = MemberQualifier.Arrow; break;
                    case Punct.ColonColon: q = MemberQualifier.Scope; break;
                }
            }
            if (q != MemberQualifier.None) role |= OccurrenceRole.MemberAccess;

            if (forced != OccurrenceRole.None)
            {
                role |= forced;
            }
            else if (i <= typeUntil)
            {
                role |= OccurrenceRole.TypeUse;
            }
            else if (next.Kind == TokenKind.Punctuator)
            {
                switch ((Punct)next.Sub)
                {
                    case Punct.LParen:
                        role |= OccurrenceRole.Call;
                        break;
                    case Punct.Lt:
                    {
                        int after = TemplateArgsEnd(i + 1);
                        if (after > 0)
                        {
                            role |= P(after, Punct.LParen) ? OccurrenceRole.Call : OccurrenceRole.TypeUse;
                            typeUntil = after - 1;
                        }
                        else role |= OccurrenceRole.Read;
                        break;
                    }
                    case Punct.ColonColon:
                        role |= OccurrenceRole.TypeUse;
                        break;
                    case Punct.Eq: case Punct.PlusEq: case Punct.MinusEq: case Punct.StarEq: case Punct.SlashEq: case Punct.PercentEq:
                    case Punct.AmpEq: case Punct.PipeEq: case Punct.CaretEq: case Punct.LtLtEq: case Punct.GtGtEq:
                    case Punct.PlusPlus: case Punct.MinusMinus:
                        role |= OccurrenceRole.Write;
                        break;
                    case Punct.Star: case Punct.Amp: case Punct.AmpAmp:
                        // "AActor* Actor = ..." at statement start
                        if (IsStatementStart(ref prev) && q == MemberQualifier.None && IsIdent(i + 2) &&
                            (P(i + 3, Punct.Eq) || P(i + 3, Punct.Semicolon) || P(i + 3, Punct.Comma) || P(i + 3, Punct.RParen) || P(i + 3, Punct.Colon) || P(i + 3, Punct.LBrace)))
                            role |= OccurrenceRole.TypeUse;
                        else role |= OccurrenceRole.Read;
                        break;
                    default:
                        role |= OccurrenceRole.Read;
                        break;
                }
            }
            else if (next.Kind == TokenKind.Identifier && q == MemberQualifier.None && (MacroRole)next.Sub == MacroRole.None)
            {
                role |= OccurrenceRole.TypeUse; // "FVector Location"
            }
            else
            {
                role |= OccurrenceRole.Read;
            }

            if ((role & (OccurrenceRole.Write | OccurrenceRole.TypeUse)) == 0 && (prev.Is(Punct.PlusPlus) || prev.Is(Punct.MinusMinus)))
                role = (role & ~OccurrenceRole.Read) | OccurrenceRole.Write;
            if ((role & OccurrenceRole.TypeUse) == 0 && prev.Is(Keyword.New)) role = (role & ~(OccurrenceRole.Read | OccurrenceRole.Call)) | OccurrenceRole.TypeUse;
            if (prev.Is(Punct.Amp) && i >= 2 && !IsOperandEnd(ref _t[i - 2])) role |= OccurrenceRole.AddressOf;

            Occurrences.Add(new Occurrence
            {
                Name = _names.Intern(new ReadOnlySpan<byte>(_src, t.Start, t.Length)),
                Offset = t.Start,
                Role = role,
                Qualifier = q,
                Function = function,
            });
        }

        private static bool IsStatementStart(ref Token prev) =>
            prev.Kind == TokenKind.Punctuator &&
            (prev.Is(Punct.LBrace) || prev.Is(Punct.Semicolon) || prev.Is(Punct.RBrace) || prev.Is(Punct.LParen) || prev.Is(Punct.Comma)) ||
            prev.Is(Keyword.Const);

        private static bool IsOperandEnd(ref Token t) =>
            t.Kind == TokenKind.Identifier || t.Kind == TokenKind.Number || t.Kind == TokenKind.String || t.Kind == TokenKind.Char ||
            t.Is(Punct.RParen) || t.Is(Punct.RBracket) || t.Is(Keyword.This);

        /// <summary>
        /// In bodies: "Name&lt;...&gt;" followed by '(' '::' '{' or an identifier is a template-id. Returns the index after
        /// '&gt;' or -1 (then '&lt;' is a less-than).
        /// </summary>
        private int TemplateArgsEnd(int lt)
        {
            int depth = 0, parens = 0;
            for (int i = lt; i < lt + 48; i++)
            {
                ref var t = ref T(i);
                switch (t.Kind)
                {
                    case TokenKind.Identifier:
                    case TokenKind.Number:
                        continue;
                    case TokenKind.Keyword:
                        if (t.Is(Keyword.Return) || t.Is(Keyword.If) || t.Is(Keyword.For) || t.Is(Keyword.While)) return -1;
                        continue;
                    case TokenKind.Punctuator:
                        switch ((Punct)t.Sub)
                        {
                            case Punct.Lt: depth++; continue;
                            case Punct.Gt:
                                if (--depth == 0) return Follows(i + 1);
                                continue;
                            case Punct.GtGt:
                                depth -= 2;
                                if (depth <= 0) return depth == 0 ? Follows(i + 1) : -1;
                                continue;
                            case Punct.ColonColon: case Punct.Comma: case Punct.Star: case Punct.Amp: case Punct.Ellipsis:
                                continue;
                            case Punct.LParen: parens++; continue;
                            case Punct.RParen:
                                if (--parens < 0) return -1;
                                continue;
                            default:
                                return -1;
                        }
                    default:
                        return -1;
                }
            }
            return -1;
        }

        private int Follows(int i)
        {
            ref var t = ref T(i);
            if (t.Is(Punct.LParen) || t.Is(Punct.ColonColon) || t.Is(Punct.LBrace) || t.Kind == TokenKind.Identifier ||
                t.Is(Punct.Star) || t.Is(Punct.Amp) || t.Is(Punct.RParen) || t.Is(Punct.Comma) || t.Is(Punct.Gt) || t.Is(Punct.Semicolon))
                return i;
            return -1;
        }
    }
}
