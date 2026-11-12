using System;
using System.IO;
using UEToVS.Core.Protocol;
using UEToVS.Indexer.Parsing;

namespace UEToVS.Indexer.Index
{
    internal sealed class FileRecord
    {
        public string Path;
        public string Name;
        public string NameLower;
        public string RelativePath;      // relative to its root, '/' separators
        public string DirectoryLower;    // relative directory, lowercase, '/' separators
        public string Module;
        public ItemOrigin Origin;
        public long MtimeTicks;
        public long Length;
        public bool Parseable;
        public ScannedSymbol[] Symbols = Array.Empty<ScannedSymbol>();

        public bool IsProject => Origin == ItemOrigin.Project || Origin == ItemOrigin.ProjectPlugin;

        public FileDto ToDto() => new FileDto
        {
            Path = Path,
            Name = Name,
            RelativePath = RelativePath,
            Module = Module,
            Origin = Origin,
        };

        public SymbolDto ToDto(ScannedSymbol s) => new SymbolDto
        {
            Name = s.Name,
            Container = s.Container,
            Kind = s.Kind,
            Ue = s.Ue,
            Specifiers = s.Specifiers,
            FilePath = Path,
            Line = s.Line,
            Column = s.Column,
            Module = Module,
            Origin = Origin,
            IsDefinition = s.IsDefinition,
            Signature = s.Signature,
        };

        public static FileRecord Create(string path, string root, string module, ItemOrigin origin, bool parseable, FileInfo info)
        {
            string rel = System.IO.Path.GetRelativePath(root, path).Replace('\\', '/');
            string name = System.IO.Path.GetFileName(path);
            int slash = rel.LastIndexOf('/');
            return new FileRecord
            {
                Path = path,
                Name = name,
                NameLower = name.ToLowerInvariant(),
                RelativePath = rel,
                DirectoryLower = slash < 0 ? string.Empty : rel.Substring(0, slash).ToLowerInvariant(),
                Module = module,
                Origin = origin,
                MtimeTicks = info?.LastWriteTimeUtc.Ticks ?? 0,
                Length = info?.Length ?? 0,
                Parseable = parseable,
            };
        }
    }
}
