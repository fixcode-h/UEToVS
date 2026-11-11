using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace UEToVS.Core.Protocol
{
    public enum SymbolKind
    {
        Class = 0,
        Struct = 1,
        Enum = 2,
        Union = 3,
        Typedef = 4,
        Namespace = 5,
        Function = 10,
        Method = 11,
        Field = 12,
        Macro = 13,
        EnumValue = 14,
        Variable = 15,
        Delegate = 16,
    }

    /// <summary>UE reflection markers found in front of a declaration.</summary>
    [System.Flags]
    public enum UeFlags
    {
        None = 0,
        UClass = 1,
        UStruct = 2,
        UEnum = 4,
        UInterface = 8,
        UFunction = 16,
        UProperty = 32,
        UDelegate = 64,
    }

    public enum ItemOrigin
    {
        Project = 0,
        ProjectPlugin = 1,
        Engine = 2,
        EnginePlugin = 3,
        ThirdParty = 4,
    }

    public sealed class SymbolDto
    {
        public string Name { get; set; }
        public string Container { get; set; }
        public SymbolKind Kind { get; set; }
        public UeFlags Ue { get; set; }

        /// <summary>Specifiers inside UFUNCTION(...)/UPROPERTY(...), e.g. "BlueprintCallable, Category=Foo".</summary>
        public string Specifiers { get; set; }

        public string FilePath { get; set; }

        /// <summary>0-based.</summary>
        public int Line { get; set; }

        /// <summary>0-based.</summary>
        public int Column { get; set; }

        public string Module { get; set; }
        public ItemOrigin Origin { get; set; }

        /// <summary>True for a definition with a body (as opposed to a declaration).</summary>
        public bool IsDefinition { get; set; }

        /// <summary>One-line signature for display.</summary>
        public string Signature { get; set; }
    }

    public sealed class FileDto
    {
        public string Path { get; set; }
        public string Name { get; set; }
        public string RelativePath { get; set; }
        public string Module { get; set; }
        public ItemOrigin Origin { get; set; }
    }

    public sealed class AssetDto
    {
        public string Name { get; set; }
        public string FilePath { get; set; }

        /// <summary>UE object path like /Game/Characters/BP_Hero.BP_Hero.</summary>
        public string ObjectPath { get; set; }

        public bool IsMap { get; set; }
        public ItemOrigin Origin { get; set; }
    }

    public enum ResultCategory
    {
        Type = 0,
        Symbol = 1,
        File = 2,
        Asset = 3,
    }

    public sealed class SearchHit
    {
        public ResultCategory Category { get; set; }
        public int Score { get; set; }

        /// <summary>Matched ranges in the display name, as [start, length] pairs.</summary>
        public int[] Highlights { get; set; }

        public SymbolDto Symbol { get; set; }
        public FileDto File { get; set; }
        public AssetDto Asset { get; set; }
    }

    public sealed class SearchRequest
    {
        public string Pattern { get; set; }
        public string Container { get; set; }
        public string[] PathFragments { get; set; }
        public bool IncludeTypes { get; set; } = true;
        public bool IncludeSymbols { get; set; } = true;
        public bool IncludeFiles { get; set; } = true;
        public bool IncludeAssets { get; set; }

        /// <summary>False = project items only ("include non-project items" unchecked).</summary>
        public bool IncludeNonProject { get; set; }

        /// <summary>Only UE reflected symbols with these flags (None = no filter).</summary>
        public UeFlags RequireUe { get; set; }

        /// <summary>File extensions to keep, lowercase without dot; null = all.</summary>
        public string[] FileExtensions { get; set; }

        public int MaxResultsPerCategory { get; set; } = 100;

        /// <summary>Recently used paths, most recent first; boosts ranking.</summary>
        public string[] RecentPaths { get; set; }
    }

    public sealed class SearchResponse
    {
        public List<SearchHit> Hits { get; set; } = new List<SearchHit>();

        /// <summary>Hits that exist outside the project when IncludeNonProject was false.</summary>
        public int NonProjectHitCount { get; set; }

        public bool IndexReady { get; set; }
    }

    public sealed class TextSearchRequest
    {
        public string Query { get; set; }
        public bool CaseSensitive { get; set; }
        public bool WholeWord { get; set; }
        public bool Regex { get; set; }

        /// <summary>Glob like "*.h;*.cpp", or null.</summary>
        public string FileMask { get; set; }

        public bool IncludeNonProject { get; set; }
        public int MaxResults { get; set; } = 500;
    }

    public sealed class TextMatch
    {
        public string FilePath { get; set; }

        /// <summary>0-based.</summary>
        public int Line { get; set; }

        public int Column { get; set; }
        public int Length { get; set; }
        public string LineText { get; set; }
    }

    public sealed class IndexStatus
    {
        public bool FilesReady { get; set; }
        public bool SymbolsReady { get; set; }
        public int FileCount { get; set; }
        public int SymbolCount { get; set; }
        public int AssetCount { get; set; }
        public int FilesParsed { get; set; }
        public int FilesToParse { get; set; }
        public string ProjectRoot { get; set; }
        public string EngineRoot { get; set; }
    }

    public sealed class ModuleInfo
    {
        public string Name { get; set; }
        public string BuildCsPath { get; set; }
        public ItemOrigin Origin { get; set; }
    }

    public sealed class IndexerOptions
    {
        public string ProjectFile { get; set; }
        public string EngineRoot { get; set; }
        public string CacheDirectory { get; set; }
    }

    /// <summary>JSON-RPC contract between the VS extension and UEToVS.Indexer.</summary>
    public interface IIndexService
    {
        Task<IndexStatus> GetStatusAsync(CancellationToken ct);

        Task<SearchResponse> SearchAsync(SearchRequest request, CancellationToken ct);

        Task<List<SymbolDto>> GetFileSymbolsAsync(string filePath, CancellationToken ct);

        /// <summary>Exact-name lookup, optionally restricted to a container (class) name.</summary>
        Task<List<SymbolDto>> FindSymbolsAsync(string[] names, string container, CancellationToken ct);

        Task<List<FileDto>> FindFilesByNameAsync(string[] fileNames, CancellationToken ct);

        Task<List<TextMatch>> SearchTextAsync(TextSearchRequest request, CancellationToken ct);

        /// <summary>Module that owns a header matching an include path such as "GameFramework/Actor.h".</summary>
        Task<ModuleInfo> FindModuleForIncludeAsync(string includePath, CancellationToken ct);

        Task<ModuleInfo> GetModuleForFileAsync(string filePath, CancellationToken ct);

        Task NotifyFileChangedAsync(string filePath, CancellationToken ct);

        Task ShutdownAsync();
    }
}
