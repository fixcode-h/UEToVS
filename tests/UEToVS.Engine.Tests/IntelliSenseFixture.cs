using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using UEToVS.Contracts;
using UEToVS.Engine.Indexing;
using UEToVS.Engine.IntelliSense;
using UEToVS.Engine.Unreal;

namespace UEToVS.Engine.Tests
{
    /// <summary>An expected colour: the identifier after <c>/*@cls:Kind[+Declaration]*/</c>.</summary>
    public sealed class ClassMarker
    {
        public string File { get; set; }
        public int Line { get; set; }
        public int Column { get; set; }
        public string Word { get; set; }
        public SemanticKind Kind { get; set; }
        public bool Declaration { get; set; }
        public override string ToString() => $"{Kind}{(Declaration ? "+Decl" : string.Empty)} '{Word}' {Path.GetFileName(File)}:{Line + 1}:{Column + 1}";
    }

    /// <summary>
    /// Indexes the navigation corpus plus the IntelliSense corpus (real parser, segmented index) and parses the IntelliSense
    /// markers: <c>/*@complete:ID*/</c>, <c>/*@quick:ID*/</c>, <c>/*@sig:ID*/</c> put a caret at the marker's start;
    /// <c>/*@cls:Kind[+Declaration]*/</c> names the expected semantic kind of the identifier that follows.
    /// Engine/ files get Origin = Engine, everything else Origin = Project.
    /// </summary>
    public sealed class IntelliSenseFixture : IDisposable
    {
        private static readonly Regex CaretMarker = new Regex(@"/\*@(complete|quick|sig):([^*]+)\*/", RegexOptions.Compiled);
        private static readonly Regex ClsMarker = new Regex(@"/\*@cls:([A-Za-z]+)(\+Declaration)?\*/", RegexOptions.Compiled);
        private static readonly Regex AnyMarker = new Regex(@"/\*@[^*]*\*/", RegexOptions.Compiled);

        public IntelliSenseFixture()
        {
            Root = Path.Combine(Path.GetTempPath(), "uetovs-is-corpus", Guid.NewGuid().ToString("N"));
            Copy(Path.Combine(AppContext.BaseDirectory, "Corpus"), Root);
            Copy(Path.Combine(AppContext.BaseDirectory, "IntelliSenseCorpus"), Path.Combine(Root, "IS"));
            Backend = new SegmentedIndexBackend();
            Backend.TryLoad(Path.Combine(Root, ".index"), "is-corpus");
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
                Backend.UpdateParsed(path, parser.Parse(ctx, File.ReadAllBytes(path), CancellationToken.None));
                if (path.StartsWith(Path.Combine(Root, "IS"), StringComparison.Ordinal)) ParseMarkers(path, File.ReadAllText(path));
            }
            // project config (gameplay tags, ini sections / keys) and a few assets for string context completion
            foreach (var path in Directory.GetFiles(Root, "*.ini", SearchOption.AllDirectories).OrderBy(p => p))
                Files.Upsert(new ListedFile { Path = path, Name = Path.GetFileName(path), Origin = ItemOrigin.Project, Kind = SourceFileKind.Config });
            foreach (var (objectPath, cls) in TestAssets)
            {
                string name = objectPath.Substring(objectPath.LastIndexOf('.') + 1);
                Assets.Upsert(new AssetItem
                {
                    Name = name,
                    ObjectPath = objectPath,
                    ClassName = cls,
                    FilePath = Path.Combine(Root, "Content", objectPath.Substring(1, objectPath.LastIndexOf('.') - 1).Replace('/', Path.DirectorySeparatorChar) + (cls == "World" ? ".umap" : ".uasset")),
                    Origin = objectPath.StartsWith("/Game/", StringComparison.Ordinal) ? ItemOrigin.Project : ItemOrigin.ProjectPlugin,
                });
            }
            Service = new IntelliSenseService(Backend, Files, assets: Assets);
        }

        public static readonly (string ObjectPath, string Class)[] TestAssets =
        {
            ("/Game/Props/SM_Rock.SM_Rock", null),
            ("/Game/Props/SM_Crate.SM_Crate", null),
            ("/Game/Props/BP_Door.BP_Door", null),
            ("/Game/Props/T_Rock.T_Rock", null),
            ("/Game/Props/Decals/T_Dirt.T_Dirt", null),
            ("/Game/Maps/L_Arena.L_Arena", "World"),
            ("/Game/Maps/BP_MapHelper.BP_MapHelper", null),
            ("/Game/Characters/Hero/BP_Hero.BP_Hero", null),
            ("/MyPlugin/Icons/T_Icon.T_Icon", null),
        };

        public AssetTable Assets { get; } = new AssetTable();

        public string Root { get; }
        public SegmentedIndexBackend Backend { get; }
        public FileTable Files { get; } = new FileTable();
        public IntelliSenseService Service { get; }
        public Dictionary<string, CaretRequest> Carets { get; } = new Dictionary<string, CaretRequest>(StringComparer.Ordinal);
        public List<ClassMarker> Classes { get; } = new List<ClassMarker>();

        public string PathOf(string relative) => Path.Combine(Root, relative.Replace('/', Path.DirectorySeparatorChar));

        public CaretRequest Caret(string id) =>
            Carets.TryGetValue(id, out var c) ? new CaretRequest { FilePath = c.FilePath, Line = c.Line, Column = c.Column } : throw new KeyNotFoundException("no caret marker " + id);

        private static void Copy(string source, string target)
        {
            foreach (var f in Directory.GetFiles(source, "*", SearchOption.AllDirectories))
            {
                string dest = Path.Combine(target, Path.GetRelativePath(source, f));
                Directory.CreateDirectory(Path.GetDirectoryName(dest));
                File.Copy(f, dest);
            }
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

            foreach (Match m in CaretMarker.Matches(text))
            {
                var (line, col) = Pos(m.Index);
                string id = m.Groups[2].Value;
                if (Carets.ContainsKey(id)) throw new InvalidOperationException("duplicate marker " + id);
                Carets[id] = new CaretRequest { FilePath = path, Line = line, Column = col };
            }
            foreach (Match m in ClsMarker.Matches(text))
            {
                int p = m.Index + m.Length;
                while (true)
                {
                    while (p < text.Length && char.IsWhiteSpace(text[p])) p++;
                    var next = AnyMarker.Match(text, p);
                    if (next.Success && next.Index == p) { p = next.Index + next.Length; continue; }
                    break;
                }
                int e = p;
                while (e < text.Length && (char.IsLetterOrDigit(text[e]) || text[e] == '_')) e++;
                var (line, col) = Pos(p);
                Classes.Add(new ClassMarker
                {
                    File = path,
                    Line = line,
                    Column = col,
                    Word = text.Substring(p, e - p),
                    Kind = Enum.Parse<SemanticKind>(m.Groups[1].Value),
                    Declaration = m.Groups[2].Success,
                });
            }
        }

        public void Dispose()
        {
            Backend.Dispose();
            try { Directory.Delete(Root, true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }
    }
}
