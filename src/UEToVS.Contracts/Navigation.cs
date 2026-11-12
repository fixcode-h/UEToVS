using System.Collections.Generic;

namespace UEToVS.Contracts
{
    // M2 core navigation contracts (go to definition / implementation, find references, type hierarchy,
    // reference highlighting, navigation bar context, member list).
    //
    // Positions: Line and Column are 0-based everywhere (same as SourceLocation, the parser and VS ITextSnapshot);
    // Column counts UTF-16 code units. Length is in UTF-16 code units.

    /// <summary>A caret in a document. Text carries the unsaved buffer when the document is dirty; null means "read the file on disk".</summary>
    public sealed class CaretRequest
    {
        public string FilePath { get; set; }
        public int Line { get; set; }
        public int Column { get; set; }

        /// <summary>Full buffer text when the editor has unsaved changes; null otherwise.</summary>
        public string Text { get; set; }

        /// <summary>Editor snapshot version; lets the engine reuse a parse of the same buffer across requests.</summary>
        public long TextVersion { get; set; }
    }

    public enum NavigationKind : byte
    {
        /// <summary>Prefer the definition (body); fall back to the declaration.</summary>
        Definition = 0,
        /// <summary>Prefer the declaration (header).</summary>
        Declaration = 1,
        /// <summary>VA Alt+G behaviour: at a declaration go to the definition, at a definition go to the declaration, elsewhere go to the definition.</summary>
        Toggle = 2,
        /// <summary>Overrides / implementations in derived types (virtual methods, interfaces); for a type, its derived types.</summary>
        Implementation = 3,
    }

    public enum NavigationTargetRole : byte
    {
        Declaration = 0,
        Definition = 1,
        Override = 2,
        /// <summary>UE-generated counterpart, e.g. Foo_Implementation / Foo_Validate for a BlueprintNativeEvent or RPC UFUNCTION.</summary>
        UeCounterpart = 3,
        DerivedType = 4,
    }

    /// <summary>How the engine decided; logged and shown in tooltips so wrong jumps can be diagnosed.</summary>
    public enum ResolutionConfidence : byte
    {
        None = 0,
        /// <summary>Only the name matched; several unrelated candidates may be listed.</summary>
        NameOnly = 1,
        /// <summary>Scope rules narrowed it down (enclosing class, bases, namespace, qualifier).</summary>
        Scoped = 2,
        /// <summary>Unique match after scope rules (or the caret is on the declaration itself).</summary>
        Exact = 3,
    }

    public sealed class NavigationRequest
    {
        public CaretRequest Caret { get; set; }
        public NavigationKind Kind { get; set; }
    }

    public sealed class NavigationTarget
    {
        public SymbolInfo Symbol { get; set; }
        public NavigationTargetRole Role { get; set; }
    }

    public sealed class NavigationResponse
    {
        /// <summary>Identifier under the caret (empty when the caret is not on an identifier).</summary>
        public string Word { get; set; }

        /// <summary>Best symbol for the caret, or null.</summary>
        public SymbolInfo Resolved { get; set; }

        /// <summary>Ranked targets; one target → jump directly, several → show a chooser.</summary>
        public List<NavigationTarget> Targets { get; set; } = new List<NavigationTarget>();

        public ResolutionConfidence Confidence { get; set; }

        /// <summary>Short machine-readable reason, e.g. "member-of-enclosing", "qualified", "super", "include". For logs and tooltips.</summary>
        public string Reason { get; set; }

        public bool IndexReady { get; set; }
        public double ElapsedMs { get; set; }
    }

    // ---------------- references ----------------

    [System.Flags]
    public enum ReferenceKind : byte
    {
        None = 0,
        Read = 1,
        Write = 2,
        Call = 4,
        TypeUse = 8,
        Declaration = 16,
        Definition = 32,
    }

    public sealed class ReferencesRequest
    {
        public CaretRequest Caret { get; set; }

        /// <summary>When set, skip caret resolution and find references of this symbol.</summary>
        public SymbolInfo Symbol { get; set; }

        public bool IncludeDeclarations { get; set; } = true;
        public bool IncludeNonProject { get; set; }
        public int MaxResults { get; set; } = 5000;
    }

    public sealed class ReferenceHit
    {
        public SourceLocation Location { get; set; }
        public int Length { get; set; }
        public ReferenceKind Kind { get; set; }

        /// <summary>The source line, tabs expanded, trimmed to at most ~200 chars.</summary>
        public string LineText { get; set; }

        /// <summary>Column of the match inside LineText (after trimming).</summary>
        public int LineTextColumn { get; set; }

        /// <summary>Enclosing function or type, e.g. "AMyActor::BeginPlay"; null at file scope.</summary>
        public string Context { get; set; }

        public ResolutionConfidence Confidence { get; set; }
        public string Module { get; set; }
        public ItemOrigin Origin { get; set; }
    }

    public sealed class ReferencesResponse
    {
        public SymbolInfo Symbol { get; set; }
        public List<ReferenceHit> Hits { get; set; } = new List<ReferenceHit>();
        public int FilesScanned { get; set; }
        public bool Truncated { get; set; }
        public bool IndexReady { get; set; }
        public double ElapsedMs { get; set; }
    }

    // ---------------- type hierarchy ----------------

    public enum HierarchyDirection : byte
    {
        Bases = 0,
        Derived = 1,
        Both = 2,
    }

    public sealed class TypeHierarchyRequest
    {
        /// <summary>Resolve the type at the caret (type name, or the enclosing class when on a member).</summary>
        public CaretRequest Caret { get; set; }

        /// <summary>Or name the type directly (wins over Caret when set).</summary>
        public string TypeName { get; set; }

        public string Container { get; set; }
        public HierarchyDirection Direction { get; set; } = HierarchyDirection.Both;
        public int MaxDepth { get; set; } = 32;
        public int MaxNodes { get; set; } = 5000;
    }

    public sealed class TypeHierarchyNode
    {
        /// <summary>Null when the type is not in the index (e.g. a template argument or an external type).</summary>
        public SymbolInfo Symbol { get; set; }

        /// <summary>Name as written, e.g. "TSharedFromThis&lt;FFoo&gt;" for unresolved bases.</summary>
        public string DisplayName { get; set; }

        /// <summary>Bases tree: the node's bases. Derived tree: the node's derived types.</summary>
        public List<TypeHierarchyNode> Children { get; set; } = new List<TypeHierarchyNode>();

        /// <summary>More children exist but were cut by MaxDepth / MaxNodes.</summary>
        public bool Truncated { get; set; }
    }

    public sealed class TypeHierarchyResponse
    {
        public SymbolInfo Root { get; set; }

        /// <summary>Direct bases of Root, each with its own bases below it.</summary>
        public List<TypeHierarchyNode> Bases { get; set; } = new List<TypeHierarchyNode>();

        /// <summary>Direct derived types of Root, each with its own derived types below it.</summary>
        public List<TypeHierarchyNode> Derived { get; set; } = new List<TypeHierarchyNode>();

        public int NodeCount { get; set; }
        public bool IndexReady { get; set; }
        public double ElapsedMs { get; set; }
    }

    // ---------------- reference highlighting ----------------

    public sealed class HighlightSpan
    {
        public int Line { get; set; }
        public int Column { get; set; }
        public int Length { get; set; }
        public ReferenceKind Kind { get; set; }
    }

    public sealed class HighlightResponse
    {
        public string Word { get; set; }

        /// <summary>Occurrences of the caret's symbol in this document only.</summary>
        public List<HighlightSpan> Spans { get; set; } = new List<HighlightSpan>();

        public double ElapsedMs { get; set; }
    }

    // ---------------- navigation bar / members ----------------

    public sealed class CaretContextResponse
    {
        /// <summary>Enclosing scopes outer → inner: namespace, type, function (with Location of each declaration).</summary>
        public List<SymbolInfo> Scopes { get; set; } = new List<SymbolInfo>();

        /// <summary>All declarations of the document in source order (navigation bar drop-downs, Alt+M list).</summary>
        public List<SymbolInfo> FileSymbols { get; set; } = new List<SymbolInfo>();

        public double ElapsedMs { get; set; }
    }

    public sealed class MembersRequest
    {
        public string TypeName { get; set; }
        public string Container { get; set; }
        public bool IncludeInherited { get; set; } = true;
        public int MaxResults { get; set; } = 2000;
    }

    public sealed class MembersResponse
    {
        public SymbolInfo Type { get; set; }

        /// <summary>Members of Type and (optionally) its bases; SymbolInfo.Container is the declaring type.</summary>
        public List<SymbolInfo> Members { get; set; } = new List<SymbolInfo>();

        public bool Truncated { get; set; }
        public double ElapsedMs { get; set; }
    }
}

namespace UEToVS.Contracts
{
    /// <summary>Indexing settings pushed by the host (settings window). 0 means "default".</summary>
    public sealed class IndexingSettings
    {
        /// <summary>Threads for C++ parsing. Default: max(1, logical cores / 4).</summary>
        public int CppThreads { get; set; }

        /// <summary>Threads for Blueprint / asset scanning. Default: max(1, logical cores / 4).</summary>
        public int AssetThreads { get; set; }

        /// <summary>Behaviour while VS/UBT builds: 0 = pause, 1 = keep going with one thread, 2 = keep going at full speed.</summary>
        public int DuringBuild { get; set; } = 1;

        /// <summary>Index engine sources (headers first, then .cpp).</summary>
        public bool IncludeEngine { get; set; } = true;
    }

    public enum RebuildScope : byte
    {
        Cpp = 0,
        Assets = 1,
        All = 2,
    }
}
