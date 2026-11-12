using System;
using UEToVS.Contracts;

namespace UEToVS.Engine.Index
{
    /// <summary>
    /// Everything the index stores about one source file. Produced by the parser (or any other producer);
    /// the index does not depend on the parser assembly.
    /// </summary>
    public sealed class FileSymbols
    {
        public string Path;
        public long MtimeTicks;
        public long Length;

        /// <summary>Content hash (xxHash64 recommended) used for incremental decisions.</summary>
        public ulong ContentHash;

        public string Module;
        public ItemOrigin Origin;
        public DeclInput[] Decls = Array.Empty<DeclInput>();
        public OccurrenceInput[] Occurrences = Array.Empty<OccurrenceInput>();

        /// <summary>Include directives as written or resolved (stored verbatim).</summary>
        public string[] Includes = Array.Empty<string>();
    }

    /// <summary>One declaration or definition.</summary>
    public struct DeclInput
    {
        public string Name;

        /// <summary>Qualified container ("AActor", "UE::Math::TVector") or null.</summary>
        public string Container;

        public SymbolKind Kind;
        public UeFlags Ue;
        public string Specifiers;
        public string Signature;

        /// <summary>Byte (or char) offset in the file; stored as given.</summary>
        public int Offset;

        /// <summary>0-based line (stored up to 2^20-1).</summary>
        public int Line;

        /// <summary>0-based column (stored up to 4095).</summary>
        public int Column;

        public bool IsDefinition;

        /// <summary>Declared inside a preprocessor conditional.</summary>
        public bool IsConditional;
    }

    /// <summary>One identifier occurrence (reference). Role is producer defined (read/write/call/...).</summary>
    public struct OccurrenceInput
    {
        public string Name;
        public int Offset;
        public byte Role;
    }
}
