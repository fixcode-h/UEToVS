using System;
using System.Collections.Generic;
using System.Text;
using System.Threading;
using UEToVS.Contracts;
using UEToVS.Engine.Unreal;

namespace UEToVS.Engine.Indexing
{
    /// <summary>
    /// Placeholder <see cref="IFileParser"/>: a line-based declaration finder (no regex, no preprocessor). Finds
    /// class/struct/union/enum/namespace heads, #define names, UE delegate declarations, and the declaration that
    /// follows UFUNCTION/UPROPERTY. Replaced by UEToVS.Engine.Syntax's tolerant parser.
    /// </summary>
    public sealed class SimpleDeclarationParser : IFileParser
    {
        public string Name => "simple-declarations";
        public string Version => "simple1";

        public bool CanParse(FileContext file)
        {
            if (file.Kind != SourceFileKind.Header && file.Kind != SourceFileKind.Source) return false;
            // Third-party code: headers only (their .cpp are noise for navigation).
            return file.Origin != ItemOrigin.ThirdParty || file.Kind == SourceFileKind.Header;
        }

        private static readonly (string Macro, UeFlags Flag)[] UeMacros =
        {
            ("UCLASS", UeFlags.UClass), ("USTRUCT", UeFlags.UStruct), ("UENUM", UeFlags.UEnum), ("UINTERFACE", UeFlags.UInterface),
            ("UFUNCTION", UeFlags.UFunction), ("UPROPERTY", UeFlags.UProperty), ("UDELEGATE", UeFlags.UDelegate),
        };

        public ParseResult Parse(FileContext file, ReadOnlyMemory<byte> utf8, CancellationToken ct)
        {
            var result = new ParseResult();
            string text = Encoding.UTF8.GetString(utf8.Span);
            var lines = text.Split('\n');
            bool inBlockComment = false;
            UeFlags pending = UeFlags.None;
            string pendingSpecifiers = null;
            string currentClass = null;

            for (int ln = 0; ln < lines.Length; ln++)
            {
                if ((ln & 511) == 0) ct.ThrowIfCancellationRequested();
                string raw = lines[ln].TrimEnd('\r');
                string line = StripComments(raw, ref inBlockComment);
                string trimmed = line.Trim();
                if (trimmed.Length == 0) continue;

                if (trimmed.StartsWith("#", StringComparison.Ordinal))
                {
                    var tk = Tokens(line);
                    if (tk.Count >= 2 && tk[0].Text == "define")
                        Add(result, file, tk[1].Text, null, SymbolKind.Macro, UeFlags.None, null, ln, tk[1].Start, trimmed, true);
                    continue;
                }

                bool macroLine = false;
                foreach (var (macro, flag) in UeMacros)
                {
                    if (trimmed.StartsWith(macro, StringComparison.Ordinal) && trimmed.Length > macro.Length && (trimmed[macro.Length] == '(' || trimmed[macro.Length] == ' '))
                    {
                        pending = flag;
                        int open = trimmed.IndexOf('(');
                        int close = trimmed.LastIndexOf(')');
                        pendingSpecifiers = open >= 0 ? (close > open ? trimmed.Substring(open + 1, close - open - 1) : trimmed.Substring(open + 1)).Trim() : null;
                        macroLine = true;
                        break;
                    }
                }
                if (macroLine) continue;

                var tokens = Tokens(line);
                if (tokens.Count == 0) continue;
                int t = 0;
                if (tokens[t].Text == "template") continue; // the declaration is on the next line in UE style
                if (tokens[0].Text.StartsWith("DECLARE_", StringComparison.Ordinal) && tokens[0].Text.Contains("DELEGATE", StringComparison.Ordinal) && tokens.Count >= 2)
                {
                    Add(result, file, tokens[1].Text, null, SymbolKind.Delegate, pending == UeFlags.UDelegate ? UeFlags.UDelegate : UeFlags.None, pendingSpecifiers, ln, tokens[1].Start, trimmed, true);
                    pending = UeFlags.None;
                    continue;
                }

                string head = tokens[t].Text;
                if (head == "namespace" && tokens.Count >= 2)
                {
                    Add(result, file, tokens[1].Text, null, SymbolKind.Namespace, UeFlags.None, null, ln, tokens[1].Start, trimmed, true);
                    continue;
                }

                if (head == "class" || head == "struct" || head == "union" || head == "enum")
                {
                    SymbolKind kind = head == "class" ? SymbolKind.Class : head == "struct" ? SymbolKind.Struct : head == "union" ? SymbolKind.Union : SymbolKind.Enum;
                    int i = t + 1;
                    if (kind == SymbolKind.Enum && i < tokens.Count && (tokens[i].Text == "class" || tokens[i].Text == "struct")) i++;
                    while (i < tokens.Count && IsApiMacro(tokens[i].Text)) i++;
                    if (i >= tokens.Count) continue;
                    var name = tokens[i];
                    bool forward = trimmed.EndsWith(";", StringComparison.Ordinal) && trimmed.IndexOf('{') < 0 && trimmed.IndexOf(':') < 0;
                    if (forward) continue;
                    UeFlags ue = pending is UeFlags.UClass or UeFlags.UStruct or UeFlags.UEnum or UeFlags.UInterface ? pending : UeFlags.None;
                    Add(result, file, name.Text, null, kind, ue, ue != UeFlags.None ? pendingSpecifiers : null, ln, name.Start, trimmed, true);
                    if (kind != SymbolKind.Enum) currentClass = name.Text;
                    pending = UeFlags.None;
                    continue;
                }

                if (pending == UeFlags.UFunction)
                {
                    int paren = line.IndexOf('(');
                    var name = LastTokenBefore(tokens, paren < 0 ? int.MaxValue : paren);
                    if (name.Text != null)
                        Add(result, file, name.Text, currentClass, SymbolKind.Method, UeFlags.UFunction, pendingSpecifiers, ln, name.Start, trimmed, line.IndexOf('{') >= 0);
                    pending = UeFlags.None;
                    continue;
                }
                if (pending == UeFlags.UProperty)
                {
                    int end = line.IndexOfAny(new[] { ';', '=', '{', '[', ':' });
                    var name = LastTokenBefore(tokens, end < 0 ? int.MaxValue : end);
                    if (name.Text != null)
                        Add(result, file, name.Text, currentClass, SymbolKind.Field, UeFlags.UProperty, pendingSpecifiers, ln, name.Start, trimmed, true);
                    pending = UeFlags.None;
                }
            }
            return result;
        }

        private static void Add(ParseResult r, FileContext file, string name, string container, SymbolKind kind, UeFlags ue, string specifiers,
            int line, int column, string signature, bool isDefinition)
        {
            r.Symbols.Add(new SymbolInfo
            {
                Name = name,
                Container = container,
                Kind = kind,
                Ue = ue,
                Specifiers = specifiers,
                Location = new SourceLocation { FilePath = file.Path, Line = line, Column = column },
                Module = file.Module,
                Origin = file.Origin,
                IsDefinition = isDefinition,
                Signature = signature.Length > 200 ? signature.Substring(0, 200) : signature,
            });
        }

        private static bool IsApiMacro(string s) => s.EndsWith("_API", StringComparison.Ordinal) || s == "final" || s == "alignas";

        private readonly struct Token
        {
            public Token(string text, int start)
            {
                Text = text;
                Start = start;
            }

            public string Text { get; }
            public int Start { get; }
        }

        private static Token LastTokenBefore(List<Token> tokens, int limit)
        {
            Token last = default;
            foreach (var t in tokens)
            {
                if (t.Start >= limit) break;
                last = t;
            }
            return last;
        }

        private static List<Token> Tokens(string line)
        {
            var list = new List<Token>();
            int i = 0;
            while (i < line.Length)
            {
                char c = line[i];
                if (char.IsLetter(c) || c == '_')
                {
                    int s = i;
                    while (i < line.Length && (char.IsLetterOrDigit(line[i]) || line[i] == '_')) i++;
                    list.Add(new Token(line.Substring(s, i - s), s));
                }
                else if (c == '"')
                {
                    i++;
                    while (i < line.Length && line[i] != '"') i += line[i] == '\\' ? 2 : 1;
                    i++;
                }
                else i++;
            }
            return list;
        }

        /// <summary>Blanks comments (keeps column offsets).</summary>
        private static string StripComments(string line, ref bool inBlock)
        {
            var sb = new StringBuilder(line.Length);
            for (int i = 0; i < line.Length; i++)
            {
                if (inBlock)
                {
                    if (line[i] == '*' && i + 1 < line.Length && line[i + 1] == '/') { inBlock = false; sb.Append("  "); i++; }
                    else sb.Append(' ');
                    continue;
                }
                if (line[i] == '/' && i + 1 < line.Length && line[i + 1] == '/') break;
                if (line[i] == '/' && i + 1 < line.Length && line[i + 1] == '*') { inBlock = true; sb.Append("  "); i++; continue; }
                sb.Append(line[i]);
            }
            return sb.ToString();
        }
    }
}
