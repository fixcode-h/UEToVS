using System;
using System.Collections.Generic;
using System.Threading;
using UEToVS.Contracts;
using UEToVS.Engine.Unreal;

namespace UEToVS.Engine.Indexing
{
    // ------------------------------------------------------------------------------------------------------------
    // Integration seams for UEToVS.Engine.Syntax (IFileParser) and UEToVS.Engine.Index (ISymbolIndexBackend).
    // The engine process only talks to these two interfaces; SimpleDeclarationParser and InMemorySymbolIndex are
    // placeholders so the process works end to end until the real implementations are plugged in (EngineHost ctor).
    // ------------------------------------------------------------------------------------------------------------

    /// <summary>What the parser knows about a file besides its bytes.</summary>
    public sealed class FileContext
    {
        public string Path { get; set; }
        public string Module { get; set; }
        public ItemOrigin Origin { get; set; }
        public SourceFileKind Kind { get; set; }
    }

    public sealed class ParseResult
    {
        /// <summary>Declarations in source order. FilePath/Module/Origin are filled in by the engine if left empty.</summary>
        public List<SymbolInfo> Symbols { get; set; } = new List<SymbolInfo>();

        /// <summary>Non-fatal problem (file still indexed with what was found).</summary>
        public string Warning { get; set; }

        /// <summary>
        /// Parser-specific extra data handed to the backend unchanged (e.g. occurrences and includes for the
        /// segmented index). Backends that don't understand it ignore it.
        /// </summary>
        public object Payload { get; set; }

        /// <summary>File state the result was parsed from (set by the indexer; stored by backends that keep it).</summary>
        public long MtimeTicks { get; set; }
        public long Length { get; set; }
        public ulong ContentHash { get; set; }
    }

    /// <summary>Turns one file into declarations. Must be thread-safe; called in parallel on BelowNormal threads.</summary>
    public interface IFileParser
    {
        /// <summary>Short name for logs.</summary>
        string Name { get; }

        /// <summary>Part of the cache key: changing it invalidates every cached file.</summary>
        string Version { get; }

        bool CanParse(FileContext file);

        /// <summary>Must honour <paramref name="ct"/> for very large files; exceptions are caught and logged per file.</summary>
        ParseResult Parse(FileContext file, ReadOnlyMemory<byte> utf8, CancellationToken ct);
    }

    public sealed class SymbolQuery
    {
        public string Pattern { get; set; }
        public string Container { get; set; }
        public bool IncludeTypes { get; set; } = true;
        public bool IncludeSymbols { get; set; } = true;
        public bool IncludeNonProject { get; set; }
        public UeFlags RequireUe { get; set; }

        /// <summary>Per category (types / symbols).</summary>
        public int MaxResults { get; set; } = 100;
    }

    public sealed class SymbolMatch
    {
        public SymbolInfo Symbol { get; set; }
        public ResultCategory Category { get; set; }
        public int Score { get; set; }

        /// <summary>[start, length] pairs in Symbol.Name.</summary>
        public int[] Highlights { get; set; }
    }

    public sealed class SymbolSearchResult
    {
        public List<SymbolMatch> Matches { get; set; } = new List<SymbolMatch>();

        /// <summary>Matches dropped because they were not project items (IncludeNonProject = false).</summary>
        public int NonProjectMatchCount { get; set; }
    }

    public sealed class IndexStats
    {
        public int Files { get; set; }
        public int Symbols { get; set; }
        public long ManagedBytes { get; set; }
        public long MappedBytes { get; set; }
        public long DiskBytes { get; set; }
    }

    /// <summary>Symbol storage and search. All members must be thread-safe (search runs while files are updated).</summary>
    public interface ISymbolIndexBackend : IDisposable
    {
        string Name { get; }

        /// <summary>Part of the cache key.</summary>
        string FormatVersion { get; }

        SymbolSearchResult Search(SymbolQuery query, CancellationToken ct);

        /// <summary>Declarations of one file in source order; empty when the file is unknown.</summary>
        IReadOnlyList<SymbolInfo> GetFileSymbols(string filePath);

        /// <summary>Exact name; <paramref name="container"/> (optional) matches the last segment of Symbol.Container.</summary>
        IReadOnlyList<SymbolInfo> FindSymbols(string name, string container);

        /// <summary>Replaces every symbol of the file.</summary>
        void UpdateFile(string filePath, IReadOnlyList<SymbolInfo> symbols);

        /// <summary>Replaces the file with a full parse result (symbols + parser payload). Default: symbols only.</summary>
        void UpdateParsed(string filePath, ParseResult result) => UpdateFile(filePath, result.Symbols);

        void RemoveFile(string filePath);

        /// <summary>
        /// Bulk path for initial indexing / rebuilds: stores a batch of parsed files at once (the segmented backend
        /// writes one segment instead of pushing every file through the delta). Default: <see cref="UpdateParsed"/> per file.
        /// </summary>
        void AppendBatch(bool engine, IReadOnlyList<(string Path, ParseResult Result)> batch, CancellationToken ct)
        {
            foreach (var (path, result) in batch) UpdateParsed(path, result);
        }

        /// <summary>End of a bulk stage: the backend may merge what <see cref="AppendBatch"/> produced.</summary>
        void FinishBulk(bool engine, CancellationToken ct)
        {
        }

        /// <summary>Drops the project part, the engine part or both (rebuild). Default: nothing (re-adding replaces files).</summary>
        void Reset(bool project, bool engine)
        {
        }

        bool ContainsFile(string filePath);

        IndexStats GetStats();

        /// <summary>Memory / disk breakdown for the performance panel (string pool, symbols, occurrences, delta…). Cheap.</summary>
        IReadOnlyList<MemoryPart> GetMemoryParts();

        /// <summary>Loads persisted data written by <see cref="Save"/> with the same key; false = start empty.</summary>
        bool TryLoad(string indexDir, string key);

        void Save(string indexDir, string key);

        /// <summary>
        /// Shutdown: writes the in-memory delta to disk cheaply (no full compaction) so readers of the on-disk index see it.
        /// Returns the number of files written. Default: nothing to persist.
        /// </summary>
        int PersistDelta(CancellationToken ct) => 0;

        // ---- M2 navigation (defaults keep simple backends working without inheritance / occurrence data) ----

        /// <summary>Classes/structs/unions named <paramref name="name"/> with their base texts (container rule as <see cref="FindSymbols"/>).</summary>
        IReadOnlyList<UEToVS.Engine.Index.TypeEntry> FindTypes(string name, string container)
        {
            var list = new List<UEToVS.Engine.Index.TypeEntry>();
            foreach (var s in FindSymbols(name, container))
                if (UEToVS.Engine.Index.IndexSnapshot.IsRecordKind(s.Kind)) list.Add(new UEToVS.Engine.Index.TypeEntry { Symbol = s });
            return list;
        }

        /// <summary>Types listing a base whose simple name (<see cref="UEToVS.Engine.Index.BaseNames.Simple"/>) matches.</summary>
        IReadOnlyList<UEToVS.Engine.Index.TypeEntry> FindDerivedTypes(string simpleBaseName) => Array.Empty<UEToVS.Engine.Index.TypeEntry>();

        /// <summary>Distinct files that contain an occurrence of <paramref name="name"/> (streamed, may be large).</summary>
        IEnumerable<string> FindOccurrenceFiles(string name, CancellationToken ct) => Array.Empty<string>();
    }

    // ---- v0.4.5 heavy features
    /// <summary>
    /// Optional backend capability (the segmented index has it): deprecation markers stored at index time and project-only
    /// occurrence lookups. Used by the upgrade assistant; a backend without it reports "no deprecation data".
    /// </summary>
    public interface IDeprecationIndex
    {
        IReadOnlyList<UEToVS.Engine.Index.DeprecatedSymbol> ListDeprecated(CancellationToken ct);

        IReadOnlyList<UEToVS.Engine.Index.OccurrenceHit> FindProjectOccurrences(string name, bool includePlugins, CancellationToken ct);
    }
}
