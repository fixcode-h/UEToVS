using System.Collections.Generic;

namespace UEToVS.Contracts
{
    // v0.4.5 call hierarchy (replaces VS's View Call Hierarchy, which needs VS's C++ IntelliSense).
    // One request answers ONE level: the direct callers or the direct callees of one function. The host asks again when
    // the user expands a node (never a whole recursive tree in one call). Positions: 0-based, like Navigation.cs.

    public enum CallHierarchyDirection : byte
    {
        /// <summary>Who calls the function (incoming calls), delegate bindings included.</summary>
        Callers = 0,
        /// <summary>What the function calls (outgoing calls).</summary>
        Callees = 1,
    }

    public enum CallSiteKind : byte
    {
        /// <summary>A direct call: Foo(), this->Foo(), Super::Foo(), Obj.Foo().</summary>
        Call = 0,
        /// <summary>Bound to a delegate / input / timer: AddDynamic(this, &amp;UFoo::Bar), BindUObject, BindUFunction(this, "Bar"), SetTimer…</summary>
        DelegateBinding = 1,
    }

    public sealed class CallHierarchyRequest
    {
        /// <summary>The function at the caret (or the function enclosing the caret).</summary>
        public CaretRequest Caret { get; set; }

        /// <summary>When set (expanding a node), skip caret resolution: this function is the root.</summary>
        public SymbolInfo Function { get; set; }

        public CallHierarchyDirection Direction { get; set; }

        /// <summary>Callers only: also scan engine / third-party files (callees are always listed, wherever they are declared).</summary>
        public bool IncludeNonProject { get; set; }

        /// <summary>Upper bound on items (callers or callees) of this level.</summary>
        public int MaxItems { get; set; } = 500;
    }

    public sealed class CallSite
    {
        public SourceLocation Location { get; set; }
        public int Length { get; set; }
        public CallSiteKind Kind { get; set; }

        /// <summary>The source line, tabs expanded, trimmed (same rules as ReferenceHit.LineText).</summary>
        public string LineText { get; set; }
        public int LineTextColumn { get; set; }

        /// <summary>Delegate bindings: AddDynamic, BindUObject, BindUFunction, BindAction, SetTimer...</summary>
        public string BindMethod { get; set; }

        /// <summary>Delegate bindings: the delegate expression in front of the bind call (OnClicked, Button-&gt;OnClicked), when on the line.</summary>
        public string Delegate { get; set; }

        public ResolutionConfidence Confidence { get; set; }
    }

    public sealed class CallHierarchyItem
    {
        /// <summary>The caller (Callers) or the callee (Callees); null when it is not a function (file scope, unresolved callee).</summary>
        public SymbolInfo Function { get; set; }

        /// <summary>Text to show when <see cref="Function"/> is null, e.g. "(file scope) Foo.cpp" or an unresolved "Obj-&gt;Run".</summary>
        public string DisplayName { get; set; }

        /// <summary>Where the call / binding happens, in source order (at most a few hundred).</summary>
        public List<CallSite> Sites { get; set; } = new List<CallSite>();

        /// <summary>The function can be expanded again (it resolved to a function of the index).</summary>
        public bool CanExpand { get; set; }

        /// <summary>Best confidence among the sites (NameOnly items are grayed by the host).</summary>
        public ResolutionConfidence Confidence { get; set; }
    }

    public sealed class CallHierarchyResponse
    {
        /// <summary>The function the level belongs to (UFUNCTION _Implementation / _Validate are reported as the UFUNCTION).</summary>
        public SymbolInfo Root { get; set; }

        public CallHierarchyDirection Direction { get; set; }

        public List<CallHierarchyItem> Items { get; set; } = new List<CallHierarchyItem>();

        /// <summary>Names treated as the same function: Foo, Foo_Implementation, Foo_Validate.</summary>
        public List<string> Names { get; set; } = new List<string>();

        /// <summary>Machine-readable outcome for logs: "ok", "no-function", "no-body", "no-document".</summary>
        public string Reason { get; set; }

        public int FilesScanned { get; set; }
        public bool Truncated { get; set; }
        public bool IndexReady { get; set; }
        public double ElapsedMs { get; set; }
    }

    // v0.4.5 Quick Info on GENERATED_BODY(): the UHT output for that line (see QuickInfoResponse.GeneratedCode*).
    public enum GeneratedCodeState : byte
    {
        /// <summary>Not a GENERATED_BODY-style macro.</summary>
        None = 0,
        /// <summary>Found and read from the .generated.h.</summary>
        Available = 1,
        /// <summary>No .generated.h under Intermediate (the module has not been built yet).</summary>
        NotBuilt = 2,
        /// <summary>The .generated.h exists but has no macro for this line (stale: build again).</summary>
        Stale = 3,
    }
}
