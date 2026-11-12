using System;
using System.Collections.Generic;
using System.Text;
using System.Threading;
using UEToVS.Engine.Index;
using UEToVS.Engine.Syntax;

namespace UEToVS.Engine.Indexing
{
    /// <summary>
    /// Identifier occurrences outside function bodies (field / parameter / return / base / template argument types,
    /// using-declarations...), which the parser's body scanner does not report. Reflection macro arguments
    /// (<c>UPROPERTY(EditAnywhere)</c>), export macros and the declared names themselves are skipped.
    /// </summary>
    public static class DeclarationContextOccurrences
    {
        /// <summary>Role bits as in <see cref="OccurrenceRole"/>.</summary>
        private const byte Read = (byte)OccurrenceRole.Read, MemberAccess = (byte)OccurrenceRole.MemberAccess, TypeUse = (byte)OccurrenceRole.TypeUse;

        private static readonly HashSet<string> ArgumentlessMacros = new HashSet<string>(StringComparer.Ordinal)
        {
            "UPROPERTY", "UFUNCTION", "UCLASS", "USTRUCT", "UENUM", "UINTERFACE", "UMETA", "UPARAM", "UDELEGATE",
            "GENERATED_BODY", "GENERATED_UCLASS_BODY", "GENERATED_USTRUCT_BODY", "GENERATED_IINTERFACE_BODY", "GENERATED_UINTERFACE_BODY",
            "GENERATED_BODY_LEGACY", "PURE_VIRTUAL", "UE_DEPRECATED", "DEPRECATED", "TEXT", "UE_DEPRECATED_FORGAME",
        };

        /// <summary>UE reflection / boilerplate macros whose arguments are not code (navigation must not jump into them either).</summary>
        public static bool IsUeBoilerplateMacro(string name) =>
            !string.IsNullOrEmpty(name) && (ArgumentlessMacros.Contains(name) || name.EndsWith("_API", StringComparison.Ordinal));

        public static List<OccurrenceInput> Collect(ReadOnlySpan<byte> text, FileSummary summary, CancellationToken ct)
        {
            var result = new List<OccurrenceInput>();
            var decls = summary.Declarations;

            // function bodies (already scanned) sorted by start; declared-name offsets
            var bodies = new List<TextRange>();
            var declOffsets = new HashSet<int>();
            foreach (var d in decls)
            {
                if (!d.IsImplicit) declOffsets.Add(d.Offset);
                if (!d.Body.IsNone && IsFunctionKind(d.Kind)) bodies.Add(d.Body);
            }
            bodies.Sort((a, b) => a.Start.CompareTo(b.Start));
            var occOffsets = new HashSet<int>();
            foreach (var o in summary.Occurrences) occOffsets.Add(o.Offset);

            ct.ThrowIfCancellationRequested();
            var tokens = CppParser.Tokenize(text);
            var names = new Dictionary<string, string>(StringComparer.Ordinal);
            int bodyIdx = 0;
            for (int i = 0; i < tokens.Count; i++)
            {
                var t = tokens[i];
                if (t.Kind != TokenKind.Identifier) continue;
                while (bodyIdx < bodies.Count && bodies[bodyIdx].End <= t.Start) bodyIdx++;
                if (bodyIdx < bodies.Count && bodies[bodyIdx].Start <= t.Start) continue; // inside a scanned body
                string name = Intern(names, text.Slice(t.Start, t.Length));
                if (IsUeBoilerplateMacro(name))
                {
                    if (i + 1 < tokens.Count && tokens[i + 1].Is(Punct.LParen)) i = SkipParens(tokens, i + 1);
                    continue;
                }
                if (declOffsets.Contains(t.Start) || occOffsets.Contains(t.Start)) continue;
                byte role;
                var prev = i > 0 ? tokens[i - 1] : default;
                var next = i + 1 < tokens.Count ? tokens[i + 1] : default;
                if (prev.Is(Punct.Dot) || prev.Is(Punct.Arrow)) role = (byte)(Read | MemberAccess);
                else if (next.Kind == TokenKind.Identifier || next.Is(Punct.Star) || next.Is(Punct.Amp) || next.Is(Punct.AmpAmp) ||
                         next.Is(Punct.Lt) || next.Is(Punct.ColonColon) || next.Kind == TokenKind.Keyword) role = TypeUse;
                else role = Read;
                result.Add(new OccurrenceInput { Name = name, Offset = t.Start, Role = role });
            }
            return result;
        }

        private static bool IsFunctionKind(UEToVS.Contracts.SymbolKind k) =>
            k == UEToVS.Contracts.SymbolKind.Function || k == UEToVS.Contracts.SymbolKind.Method ||
            k == UEToVS.Contracts.SymbolKind.Constructor || k == UEToVS.Contracts.SymbolKind.Destructor;

        private static int SkipParens(List<Token> tokens, int open)
        {
            int depth = 0;
            for (int j = open; j < tokens.Count; j++)
            {
                if (tokens[j].Is(Punct.LParen)) depth++;
                else if (tokens[j].Is(Punct.RParen) && --depth == 0) return j;
            }
            return tokens.Count - 1;
        }

        private static string Intern(Dictionary<string, string> names, ReadOnlySpan<byte> utf8)
        {
            string s = Encoding.UTF8.GetString(utf8);
            if (names.TryGetValue(s, out var existing)) return existing;
            names[s] = s;
            return s;
        }
    }
}
