using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using UEToVS.Contracts;
using UEToVS.Engine.Indexing;
using UEToVS.Engine.Navigation;
using UEToVS.Engine.Unreal;

namespace UEToVS.Engine.Tests
{
    /// <summary>A caret marker of the navigation corpus: <c>/*@caret:ID[:decl|toggle|impl]*/</c> or <c>//@caret:ID</c>.</summary>
    public sealed class CorpusCaret
    {
        public string Id { get; set; }
        public NavigationKind Kind { get; set; }
        public string File { get; set; }
        public int Line { get; set; }
        public int Column { get; set; }
        public override string ToString() => $"{Id} [{Kind}] {Path.GetFileName(File)}:{Line + 1}:{Column + 1}";
    }

    /// <summary>
    /// Copies the corpus to a temp folder, indexes it with the real parser and the segmented index, and parses the markers:
    /// <c>/*@def:ID*/Name</c> marks an expected target (the identifier after the marker chain), <c>//@deffile:ID</c> marks
    /// line 0 of the file (for #include). Engine/ files get Origin = Engine, Game/ files Origin = Project.
    /// </summary>
    public sealed class NavigationFixture : IDisposable
    {
        private static readonly Regex Marker = new Regex(@"/\*@(caret|def):([^*]+)\*/", RegexOptions.Compiled);
        private static readonly Regex LineMarker = new Regex(@"//@(caret|deffile):(\S+)", RegexOptions.Compiled);

        public NavigationFixture()
        {
            Root = Path.Combine(Path.GetTempPath(), "uetovs-nav-corpus", Guid.NewGuid().ToString("N"));
            string source = Path.Combine(AppContext.BaseDirectory, "Corpus");
            foreach (var f in Directory.GetFiles(source, "*", SearchOption.AllDirectories))
            {
                string dest = Path.Combine(Root, Path.GetRelativePath(source, f));
                Directory.CreateDirectory(Path.GetDirectoryName(dest));
                File.Copy(f, dest);
            }
            Backend = new SegmentedIndexBackend();
            Backend.TryLoad(Path.Combine(Root, ".index"), "corpus");
            var parser = new SyntaxFileParser();
            foreach (var path in Directory.GetFiles(Root, "*.*", SearchOption.AllDirectories).Where(p => p.EndsWith(".h") || p.EndsWith(".cpp")).OrderBy(p => p))
            {
                bool engine = path.Contains(Path.DirectorySeparatorChar + "Engine" + Path.DirectorySeparatorChar);
                var listed = new ListedFile
                {
                    Path = path,
                    Name = Path.GetFileName(path),
                    Module = engine ? "Engine" : "MyGame",
                    Origin = engine ? ItemOrigin.Engine : ItemOrigin.Project,
                    Kind = path.EndsWith(".h") ? SourceFileKind.Header : SourceFileKind.Source,
                };
                Files.Upsert(listed);
                var ctx = new FileContext { Path = path, Module = listed.Module, Origin = listed.Origin, Kind = listed.Kind };
                var result = parser.Parse(ctx, File.ReadAllBytes(path), CancellationToken.None);
                Backend.UpdateParsed(path, result);
                ParseMarkers(path, File.ReadAllText(path));
            }
            Navigation = new NavigationService(Backend, Files);
        }

        public string Root { get; }
        public SegmentedIndexBackend Backend { get; }
        public FileTable Files { get; } = new FileTable();
        public NavigationService Navigation { get; }
        public Dictionary<string, List<SourceLocation>> Defs { get; } = new Dictionary<string, List<SourceLocation>>(StringComparer.Ordinal);
        public List<CorpusCaret> Carets { get; } = new List<CorpusCaret>();

        public string PathOf(string relative) => Path.Combine(Root, relative.Replace('/', Path.DirectorySeparatorChar));

        /// <summary>Position of the identifier following the n-th occurrence of <paramref name="marker"/> text in a corpus file.</summary>
        public (int Line, int Column) Find(string relative, string text, int nth = 0)
        {
            var lines = File.ReadAllLines(PathOf(relative));
            int seen = 0;
            for (int l = 0; l < lines.Length; l++)
            {
                int i = -1;
                while ((i = lines[l].IndexOf(text, i + 1, StringComparison.Ordinal)) >= 0)
                    if (seen++ == nth) return (l, i);
            }
            throw new InvalidOperationException($"'{text}' #{nth} not found in {relative}");
        }

        public CaretRequest Caret(string relative, string text, int nth = 0, int into = 1)
        {
            // inside the identifier after the last marker of the pattern ("X*/Name" → in Name)
            var (l, c) = Find(relative, text, nth);
            int anchor = text.LastIndexOf("*/", StringComparison.Ordinal);
            return new CaretRequest { FilePath = PathOf(relative), Line = l, Column = c + (anchor >= 0 ? anchor + 2 : 0) + into };
        }

        private void ParseMarkers(string path, string text)
        {
            var lineStarts = new List<int> { 0 };
            for (int i = 0; i < text.Length; i++) if (text[i] == '\n') lineStarts.Add(i + 1);
            (int, int) Pos(int index)
            {
                int line = lineStarts.BinarySearch(index);
                if (line < 0) line = ~line - 1;
                return (line, index - lineStarts[line]);
            }

            foreach (Match m in Marker.Matches(text))
            {
                // the identifier after the chain of markers
                int p = m.Index + m.Length;
                while (true)
                {
                    while (p < text.Length && char.IsWhiteSpace(text[p])) p++;
                    var next = Marker.Match(text, p);
                    if (next.Success && next.Index == p) { p = next.Index + next.Length; continue; }
                    break;
                }
                var (line, col) = Pos(p);
                if (m.Groups[1].Value == "def") AddDef(m.Groups[2].Value, path, line, col);
                else AddCaret(m.Groups[2].Value, path, line, col + 1);
            }
            foreach (Match m in LineMarker.Matches(text))
            {
                var (line, _) = Pos(m.Index);
                if (m.Groups[1].Value == "deffile") AddDef(m.Groups[2].Value, path, 0, 0);
                else
                {
                    int start = lineStarts[line];
                    int col = 0;
                    while (start + col < text.Length && char.IsWhiteSpace(text[start + col])) col++;
                    AddCaret(m.Groups[2].Value, path, line, col + 1);
                }
            }
        }

        private void AddDef(string id, string path, int line, int col)
        {
            if (!Defs.TryGetValue(id, out var list)) Defs[id] = list = new List<SourceLocation>();
            list.Add(new SourceLocation { FilePath = path, Line = line, Column = col });
        }

        private void AddCaret(string spec, string path, int line, int col)
        {
            string id = spec;
            var kind = NavigationKind.Definition;
            int colon = spec.LastIndexOf(':');
            if (colon > 0)
            {
                id = spec.Substring(0, colon);
                kind = spec.Substring(colon + 1) switch
                {
                    "decl" => NavigationKind.Declaration,
                    "toggle" => NavigationKind.Toggle,
                    "impl" => NavigationKind.Implementation,
                    _ => throw new InvalidOperationException("bad caret kind: " + spec),
                };
            }
            Carets.Add(new CorpusCaret { Id = id, Kind = kind, File = path, Line = line, Column = col });
        }

        public static string Key(SourceLocation l) => $"{Path.GetFileName(l.FilePath)}:{l.Line + 1}:{l.Column + 1}";

        public void Dispose()
        {
            Backend.Dispose();
            try { Directory.Delete(Root, true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }
    }
}
