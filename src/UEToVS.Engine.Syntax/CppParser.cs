using System;
using System.Buffers;
using System.Collections.Generic;
using System.IO;
using System.Text;
using UEToVS.Core.Diagnostics;

namespace UEToVS.Engine.Syntax
{
    /// <summary>
    /// Entry point of the syntax layer: UTF-8 lexing, light preprocessing, tolerant declaration parsing and a shallow
    /// scan of function bodies. Thread-safe (each call uses its own state); never throws on malformed input.
    /// </summary>
    public static class CppParser
    {
        private static readonly Logger L = Log.For("Syntax");

        /// <summary>
        /// Parses one file's text. UTF-8 (with or without BOM) is parsed in place; UTF-16 (BOM FF FE / FE FF) is transcoded once
        /// to UTF-8 and <see cref="FileSummary.TranscodedText"/> holds the bytes offsets refer to.
        /// </summary>
        public static FileSummary Parse(ReadOnlySpan<byte> text, ParseOptions options = null)
        {
            options ??= ParseOptions.Default;
            byte[] buffer = null;
            bool rented = false;
            try
            {
                int len = SourceDecoder.Prepare(text, out buffer, out bool transcoded, out rented);
                return ParseCore(buffer, len, options, transcoded ? buffer : null);
            }
            finally
            {
                if (rented && buffer != null) ArrayPool<byte>.Shared.Return(buffer);
            }
        }

        /// <summary>Parses a string (convenience for tests and editor buffers).</summary>
        public static FileSummary Parse(string text, ParseOptions options = null) =>
            Parse(Encoding.UTF8.GetBytes(text ?? string.Empty), options);

        /// <summary>Reads and parses a file. IO errors are logged and yield <see cref="FileSummary.Empty"/>.</summary>
        public static FileSummary ParseFile(string path, ParseOptions options = null)
        {
            byte[] bytes;
            try
            {
                bytes = File.ReadAllBytes(path);
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException || ex is ArgumentException || ex is NotSupportedException)
            {
                L.Warn("syntax.read_failed", "Could not read source file", ex, ("file", path));
                return FileSummary.Empty;
            }
            var summary = Parse(bytes, options);
            if (summary.Diagnostics.Count > 0 && Log.IsEnabled(LogLevel.Debug))
                L.Debug("syntax.recovered", "Parse recovered from errors", ("file", path), ("count", summary.Diagnostics.Count),
                    ("first", summary.Diagnostics[0].ToString()));
            return summary;
        }

        /// <summary>
        /// Lexes the whole text into tokens (directives as single tokens; comments only when <paramref name="keepComments"/>).
        /// Intended for colorizers and tools; the parser itself does not allocate a list.
        /// </summary>
        public static List<Token> Tokenize(ReadOnlySpan<byte> utf8, bool keepComments = false)
        {
            var list = new List<Token>(utf8.Length / 5 + 4);
            var lexer = new CppLexer(utf8, 0, keepComments);
            while (true)
            {
                var t = lexer.Next();
                if (t.Kind == TokenKind.EndOfFile) break;
                list.Add(t);
            }
            return list;
        }

        /// <summary>Extracts the text of a doc comment without its decoration (/**, */, leading *, ///, //!).</summary>
        public static string GetDocCommentText(ReadOnlySpan<byte> text, TextRange range)
        {
            if (range.IsNone || range.End > text.Length) return null;
            var raw = Encoding.UTF8.GetString(text.Slice(range.Start, range.Length));
            var sb = new StringBuilder(raw.Length);
            foreach (var rawLine in raw.Split('\n'))
            {
                var line = rawLine.Trim();
                if (line.StartsWith("/**", StringComparison.Ordinal) || line.StartsWith("/*!", StringComparison.Ordinal)) line = line.Substring(3);
                else if (line.StartsWith("///", StringComparison.Ordinal) || line.StartsWith("//!", StringComparison.Ordinal)) line = line.Substring(3);
                if (line.EndsWith("*/", StringComparison.Ordinal)) line = line.Substring(0, line.Length - 2);
                line = line.Trim();
                if (line.StartsWith("*", StringComparison.Ordinal)) line = line.Substring(1).TrimStart();
                if (line.Length == 0 && sb.Length == 0) continue;
                if (sb.Length > 0) sb.Append('\n');
                sb.Append(line);
            }
            return sb.ToString().TrimEnd();
        }

        private static FileSummary ParseCore(byte[] src, int len, ParseOptions options, byte[] transcoded)
        {
            var kb = options.Knowledge ?? UeMacroKnowledge.Default;
            var names = options.Names ?? new NameTable();
            var scratch = new StringBuilder(256);
            var diagnostics = new List<SyntaxDiagnostic>();
            var lines = LineMap.Build(new ReadOnlySpan<byte>(src, 0, len));
            Preprocessor pp = null;
            Parser parser = null;
            try
            {
                pp = new Preprocessor(src, len, options, kb, lines, scratch, diagnostics);
                pp.Run();
                parser = new Parser(src, len, pp.Tokens.Items, pp.Tokens.Count, options, kb, names, lines, pp.Docs, pp.Macros, scratch, diagnostics);
                parser.Run();
                return new FileSummary(parser.Declarations, parser.Occurrences, pp.Includes, pp.Inactive, diagnostics, lines, len, transcoded);
            }
            catch (Exception ex)
            {
                // A bug in the parser must not take the indexer down: keep what we have.
                L.Warn("syntax.parse_failed", "Parser failed; returning partial result", ex, ("length", len));
                diagnostics.Add(new SyntaxDiagnostic(0, 0, "internal parser error: " + ex.GetType().Name));
                var decls = parser?.Declarations ?? (IReadOnlyList<Declaration>)(pp?.Macros ?? new List<Declaration>());
                return new FileSummary(decls, parser?.Occurrences ?? new List<Occurrence>(),
                    pp?.Includes ?? new List<IncludeDirective>(), pp?.Inactive ?? new List<TextRange>(), diagnostics, lines, len, transcoded);
            }
            finally
            {
                pp?.Tokens.Dispose();
            }
        }
    }
}
