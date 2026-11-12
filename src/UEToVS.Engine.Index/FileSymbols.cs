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

        /// <summary>
        /// Base classes of a class/struct as written, without access specifiers or <c>virtual</c> but with template
        /// arguments (e.g. <c>ACharacter</c>, <c>TSharedFromThis&lt;FFoo&gt;</c>); null for everything else.
        /// </summary>
        public string[] Bases;

        /// <summary>v0.4.5: deprecation marker of the declaration (UE_DEPRECATED(ver, msg) / [[deprecated(msg)]]); null for almost everything.</summary>
        public DeclDeprecation Deprecation;
    }

    /// <summary>
    /// Deprecation marker of a declaration as written: version (<c>5.1</c>, empty for <c>[[deprecated]]</c>) and message.
    /// Stored in the segment's string pool (the symbol record only gets a flag bit), so it costs nothing for the
    /// overwhelming majority of symbols that are not deprecated.
    /// </summary>
    public sealed class DeclDeprecation
    {
        public DeclDeprecation(string version, string message)
        {
            Version = version ?? string.Empty;
            Message = message ?? string.Empty;
        }

        public string Version { get; }
        public string Message { get; }

        public override string ToString() => "deprecated(" + Version + ", \"" + Message + "\")";
    }

    /// <summary>A deprecated symbol listed from the index.</summary>
    public sealed class DeprecatedSymbol
    {
        public UEToVS.Contracts.SymbolInfo Symbol { get; set; }
        public string Version { get; set; }
        public string Message { get; set; }
        public override string ToString() => Symbol?.Container + "::" + Symbol?.Name + " " + Version;
    }

    /// <summary>One identifier occurrence (reference). Role is producer defined (read/write/call/...).</summary>
    public struct OccurrenceInput
    {
        public string Name;
        public int Offset;
        public byte Role;
    }
}
