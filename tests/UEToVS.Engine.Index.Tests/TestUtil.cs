using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using UEToVS.Contracts;
using UEToVS.Engine.Index;

namespace UEToVS.Engine.Index.Tests
{
    internal sealed class TempDir : IDisposable
    {
        public TempDir()
        {
            Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "uetovs-index-tests", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path);
        }

        public string Path { get; }

        public void Dispose()
        {
            try
            {
                Directory.Delete(Path, true);
            }
            catch (IOException)
            {
            }
            catch (UnauthorizedAccessException)
            {
            }
        }
    }

    internal static class T
    {
        public static IndexOptions Options(string dir, string kb = "kb1", string build = "ue5.4-1") =>
            new IndexOptions { Directory = dir, KnowledgeBaseVersion = kb, EngineBuildId = build };

        public static SymbolIndex Open(string dir, string kb = "kb1", string build = "ue5.4-1") => SymbolIndex.Open(Options(dir, kb, build), out _);

        public static DeclInput D(string name, SymbolKind kind = SymbolKind.Class, string container = null, int offset = 0, int line = 0,
            UeFlags ue = UeFlags.None, bool def = false, string sig = null, string spec = null, int col = 0) =>
            new DeclInput
            {
                Name = name, Container = container, Kind = kind, Offset = offset, Line = line, Column = col, Ue = ue, IsDefinition = def,
                Signature = sig, Specifiers = spec,
            };

        public static OccurrenceInput O(string name, int offset, byte role = 0) => new OccurrenceInput { Name = name, Offset = offset, Role = role };

        public static FileSymbols F(string path, ItemOrigin origin, params DeclInput[] decls) => F(path, origin, decls, null);

        public static FileSymbols F(string path, ItemOrigin origin, DeclInput[] decls, OccurrenceInput[] occ, string[] includes = null, string module = "Game")
        {
            // assign increasing offsets/lines if left at 0
            for (int i = 0; i < decls.Length; i++)
            {
                if (decls[i].Offset == 0 && i > 0) decls[i].Offset = i * 100;
                if (decls[i].Line == 0 && i > 0) decls[i].Line = i * 3;
            }
            return new FileSymbols
            {
                Path = path,
                Origin = origin,
                Module = module,
                MtimeTicks = 637000000000000000L + path.Length,
                Length = 1000 + decls.Length,
                ContentHash = (ulong)path.GetHashCode() * 31UL,
                Decls = decls,
                Occurrences = occ ?? Array.Empty<OccurrenceInput>(),
                Includes = includes ?? Array.Empty<string>(),
            };
        }

        public static string Describe(SymbolInfo s) =>
            $"{s.Kind}|{s.Container}|{s.Name}|{s.Location?.FilePath}|{s.Location?.Line}|{s.Location?.Column}|{s.Ue}|{s.IsDefinition}|{s.Module}|{s.Origin}|{s.Signature}|{s.Specifiers}";

        public static string Describe(SearchHit h) =>
            $"{h.Category}|{h.Score}|{string.Join(",", h.Highlights ?? Array.Empty<int>())}|" +
            (h.Symbol != null ? Describe(h.Symbol) : h.File != null ? $"{h.File.Path}|{h.File.Module}|{h.File.Origin}" : "");

        public static string Describe(SearchResponse r) =>
            string.Join("\n", r.Hits.Select(Describe)) + $"\nnonProject={r.NonProjectHitCount}";

        public static string Describe(IndexedFile f) =>
            $"{f.Path}|{f.Module}|{f.Origin}|{f.MtimeTicks}|{f.Length}|{f.ContentHash}|{f.SymbolCount}|{string.Join(";", f.Includes)}";

        public static SearchRequest Req(string pattern, bool nonProject = true, int max = 50) => new SearchRequest
        {
            Pattern = pattern,
            IncludeNonProject = nonProject,
            MaxResultsPerCategory = max,
        };

        public static List<string> Names(SearchResponse r, ResultCategory? cat = null) =>
            r.Hits.Where(h => cat == null || h.Category == cat).Select(h => h.Symbol?.Name ?? h.File?.Name).ToList();
    }
}
