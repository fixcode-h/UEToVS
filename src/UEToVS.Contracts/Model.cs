using System;
using System.Collections.Generic;

namespace UEToVS.Contracts
{
    /// <summary>Wire protocol version. Bump on any breaking change to <see cref="IEngineService"/> or its DTOs.</summary>
    public static class ProtocolInfo
    {
        public const int Version = 1;

        /// <summary>Named pipe name used between the VS host and the engine process.</summary>
        public static string PipeName(int hostProcessId, string solutionDir)
        {
            uint h = 2166136261;
            foreach (char c in (solutionDir ?? string.Empty).ToLowerInvariant())
            {
                h ^= c;
                h *= 16777619;
            }
            return $"uetovs-{hostProcessId}-{h:x8}";
        }
    }

    public enum SymbolKind : byte
    {
        Unknown = 0,
        Namespace = 1,
        Class = 2,
        Struct = 3,
        Union = 4,
        Enum = 5,
        EnumValue = 6,
        Typedef = 7,
        Function = 8,
        Method = 9,
        Constructor = 10,
        Destructor = 11,
        Field = 12,
        Variable = 13,
        Macro = 14,
        Delegate = 15,
        Concept = 16,
    }

    /// <summary>UE reflection markers attached to a declaration.</summary>
    [Flags]
    public enum UeFlags : ushort
    {
        None = 0,
        UClass = 1 << 0,
        UStruct = 1 << 1,
        UEnum = 1 << 2,
        UInterface = 1 << 3,
        UFunction = 1 << 4,
        UProperty = 1 << 5,
        UDelegate = 1 << 6,
        UMeta = 1 << 7,
        GeneratedBody = 1 << 8,
    }

    public enum ItemOrigin : byte
    {
        Project = 0,
        ProjectPlugin = 1,
        Engine = 2,
        EnginePlugin = 3,
        ThirdParty = 4,
    }

    public enum ResultCategory : byte
    {
        Type = 0,
        Symbol = 1,
        File = 2,
        Asset = 3,
    }

    /// <summary>A position in a file. Line and column are 0-based; column counts UTF-16 code units.</summary>
    public sealed class SourceLocation
    {
        public string FilePath { get; set; }
        public int Line { get; set; }
        public int Column { get; set; }
    }

    public sealed class SymbolInfo
    {
        public string Name { get; set; }
        public string Container { get; set; }
        public SymbolKind Kind { get; set; }
        public UeFlags Ue { get; set; }

        /// <summary>Text inside the reflection macro, e.g. "BlueprintCallable, Category=Foo".</summary>
        public string Specifiers { get; set; }

        public SourceLocation Location { get; set; }
        public string Module { get; set; }
        public ItemOrigin Origin { get; set; }
        public bool IsDefinition { get; set; }

        /// <summary>One-line signature for display.</summary>
        public string Signature { get; set; }
    }

    public sealed class FileItem
    {
        public string Path { get; set; }
        public string Name { get; set; }
        public string RelativePath { get; set; }
        public string Module { get; set; }
        public ItemOrigin Origin { get; set; }
    }

    public sealed class AssetItem
    {
        public string Name { get; set; }
        public string FilePath { get; set; }

        /// <summary>UE object path like /Game/Characters/BP_Hero.BP_Hero.</summary>
        public string ObjectPath { get; set; }

        public string ClassName { get; set; }
        public ItemOrigin Origin { get; set; }
    }

    public sealed class SearchHit
    {
        public ResultCategory Category { get; set; }
        public int Score { get; set; }

        /// <summary>Matched ranges in the display name as [start, length] pairs.</summary>
        public int[] Highlights { get; set; }

        public SymbolInfo Symbol { get; set; }
        public FileItem File { get; set; }
        public AssetItem Asset { get; set; }
    }

    public sealed class SearchRequest
    {
        public string Pattern { get; set; }
        public string Container { get; set; }
        public bool IncludeTypes { get; set; } = true;
        public bool IncludeSymbols { get; set; } = true;
        public bool IncludeFiles { get; set; } = true;
        public bool IncludeAssets { get; set; }

        /// <summary>False = project items only.</summary>
        public bool IncludeNonProject { get; set; }

        public UeFlags RequireUe { get; set; }
        public int MaxResultsPerCategory { get; set; } = 100;

        /// <summary>Recently used paths, most recent first; boosts ranking.</summary>
        public string[] RecentPaths { get; set; }
    }

    public sealed class SearchResponse
    {
        public List<SearchHit> Hits { get; set; } = new List<SearchHit>();
        public int NonProjectHitCount { get; set; }
        public bool IndexReady { get; set; }
        public double ElapsedMs { get; set; }
    }

    public sealed class HandshakeRequest
    {
        public int ProtocolVersion { get; set; }
        public string HostVersion { get; set; }
        public string VsVersion { get; set; }
        public string SolutionDir { get; set; }
        public string ProjectFile { get; set; }
        public string EngineRootOverride { get; set; }
    }

    public sealed class HandshakeResponse
    {
        public int ProtocolVersion { get; set; }
        public string EngineVersion { get; set; }
        public int ProcessId { get; set; }
        public string LogDirectory { get; set; }
        public string IndexDirectory { get; set; }
    }

    public enum IndexPhase : byte
    {
        NotStarted = 0,
        DiscoveringProject = 1,
        ListingFiles = 2,
        LoadingCache = 3,
        Parsing = 4,
        Merging = 5,
        Ready = 6,
        Failed = 7,
    }

    public sealed class EngineStatus
    {
        public IndexPhase Phase { get; set; }
        public int FileCount { get; set; }
        public int SymbolCount { get; set; }
        public int AssetCount { get; set; }
        public int FilesParsed { get; set; }
        public int FilesToParse { get; set; }
        public string ProjectRoot { get; set; }
        public string EngineRoot { get; set; }
        public string UnrealVersion { get; set; }
        public ResourceSnapshot Resources { get; set; }
        public string LastError { get; set; }
    }

    public sealed class ResourceSnapshot
    {
        public long PrivateBytes { get; set; }
        public long WorkingSetBytes { get; set; }
        public long ManagedHeapBytes { get; set; }
        public int Gen0Collections { get; set; }
        public int Gen1Collections { get; set; }
        public int Gen2Collections { get; set; }
        public int ThreadCount { get; set; }
        public long IndexBytesOnDisk { get; set; }
    }

    public sealed class FileChange
    {
        public string FilePath { get; set; }
        public FileChangeKind Kind { get; set; }
    }

    public enum FileChangeKind : byte
    {
        Changed = 0,
        Created = 1,
        Deleted = 2,
        Renamed = 3,
    }
}
