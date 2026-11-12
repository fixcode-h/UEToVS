using System.Collections.Generic;
using System.Linq;
using System.Text;
using UEToVS.Contracts;
using Xunit;

namespace UEToVS.Engine.Syntax.Tests
{
    internal static class TestUtil
    {
        public static FileSummary Parse(string code, ParseOptions options = null) => CppParser.Parse(code, options);

        public static Declaration One(FileSummary s, string name, SymbolKind? kind = null) =>
            Assert.Single(s.Declarations, d => d.Name == name && (kind == null || d.Kind == kind) && !d.IsImplicit);

        public static Declaration One(string code, string name, SymbolKind? kind = null) => One(Parse(code), name, kind);

        public static List<Token> Lex(string code, bool keepComments = false) =>
            CppParser.Tokenize(Encoding.UTF8.GetBytes(code), keepComments);

        public static string Text(string code, Token t) => Encoding.UTF8.GetString(Encoding.UTF8.GetBytes(code), t.Start, t.Length);

        public static string[] Texts(string code, bool keepComments = false)
        {
            var bytes = Encoding.UTF8.GetBytes(code);
            return CppParser.Tokenize(bytes, keepComments).Select(t => Encoding.UTF8.GetString(bytes, t.Start, t.Length)).ToArray();
        }

        public static Occurrence[] Occ(FileSummary s, string name) => s.Occurrences.Where(o => o.Name == name).ToArray();
    }
}
