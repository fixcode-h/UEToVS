using System.Collections.Generic;

namespace UEToVS.Contracts
{
    // M3 UE-aware IntelliSense contracts: completion, Quick Info, parameter info, semantic classification (VA colours).
    // Positions are 0-based; columns count UTF-16 code units (same as Navigation.cs).

    public enum CompletionContextKind : byte
    {
        /// <summary>Plain identifier in a statement / declaration: locals, members of the enclosing class, namespaces, globals, keywords.</summary>
        General = 0,
        /// <summary>After '.' or '-&gt;': members of the left-hand type (and its bases).</summary>
        Member = 1,
        /// <summary>After 'X::': members of class / namespace / enum X.</summary>
        Scope = 2,
        /// <summary>Inside UCLASS(...), UPROPERTY(...), UFUNCTION(...), USTRUCT(...), UENUM(...), meta=(...): reflection specifiers.</summary>
        Specifier = 3,
        /// <summary>Inside #include "..." or &lt;...&gt;: header paths.</summary>
        Include = 4,
        /// <summary>After 'override' position / 'virtual': overridable base methods.</summary>
        Override = 5,
        /// <summary>In a GameplayTag string (RequestGameplayTag("…"), UE_DEFINE_GAMEPLAY_TAG, meta=(Categories="…")): next tag segment.</summary>
        GameplayTag = 6,
        /// <summary>In an asset path string (FObjectFinder, FSoftObjectPath, LoadObject, "/Game/…"): folders and assets of the path.</summary>
        AssetPath = 7,
        /// <summary>In the function name string of BindUFunction / AddUFunction / FindFunction: UFUNCTIONs of the target class.</summary>
        UFunctionName = 8,
        /// <summary>In the section / key string of a GConfig-&gt;Get…/Set… call: sections and keys of the project's Config/*.ini.</summary>
        IniKey = 9,
    }

    public sealed class CompletionRequest
    {
        public CaretRequest Caret { get; set; }

        /// <summary>Character that triggered completion ('.', '&gt;', ':', '(', '"', '&lt;', ',' …) or '\0' for typing / explicit invoke.</summary>
        public char TriggerChar { get; set; }

        /// <summary>User invoked completion explicitly (Ctrl+Space): never return an empty list just because the prefix is short.</summary>
        public bool Explicit { get; set; }

        public int MaxResults { get; set; } = 500;
    }

    public sealed class CompletionItem
    {
        /// <summary>Shown text, e.g. "BeginPlay".</summary>
        public string Label { get; set; }

        /// <summary>Text inserted on commit (may differ, e.g. "BeginPlay()" or a header path).</summary>
        public string InsertText { get; set; }

        public SymbolKind Kind { get; set; }
        public UeFlags Ue { get; set; }

        /// <summary>Short right-aligned detail: return type / signature / specifier description.</summary>
        public string Detail { get; set; }

        /// <summary>Declaring type or namespace.</summary>
        public string Container { get; set; }

        /// <summary>Inherited from a base (shown dimmed, VA style).</summary>
        public bool IsInherited { get; set; }

        /// <summary>Keyword / snippet / reflection specifier rather than a symbol.</summary>
        public bool IsKeyword { get; set; }

        /// <summary>Engine ranking (higher first); the host may re-rank by fuzzy match and recent use.</summary>
        public int Score { get; set; }

        public SourceLocation Location { get; set; }
    }

    /// <summary>VA-style '.' / '-&gt;' correction: replace the accessor the user typed with the right one for the left-hand type.</summary>
    public sealed class AccessorFix
    {
        public int Line { get; set; }
        public int Column { get; set; }
        public int Length { get; set; }
        public string Replacement { get; set; }
    }

    public sealed class CompletionResponse
    {
        public CompletionContextKind Context { get; set; }
        public List<CompletionItem> Items { get; set; } = new List<CompletionItem>();

        /// <summary>The word being completed on the caret line (replaced on commit).</summary>
        public int ReplaceColumn { get; set; }
        public int ReplaceLength { get; set; }

        /// <summary>Left-hand type resolved for member / scope completion (for logs and the completion header).</summary>
        public string ResolvedType { get; set; }

        /// <summary>Non-null when '.' should become '-&gt;' (pointer) or the reverse.</summary>
        public AccessorFix AccessorFix { get; set; }

        /// <summary>More items exist (MaxResults hit); the host re-queries as the prefix grows.</summary>
        public bool IsIncomplete { get; set; }

        public bool IndexReady { get; set; }
        public double ElapsedMs { get; set; }
    }

    public sealed class QuickInfoResponse
    {
        /// <summary>Range of the hovered identifier.</summary>
        public int Line { get; set; }
        public int Column { get; set; }
        public int Length { get; set; }

        public SymbolInfo Symbol { get; set; }

        /// <summary>Formatted declaration, e.g. "virtual void AMyActor::BeginPlay() override".</summary>
        public string Declaration { get; set; }

        /// <summary>Doc comment text above the declaration (/** */, ///, //), cleaned of comment markers.</summary>
        public string Documentation { get; set; }

        /// <summary>UE reflection summary, e.g. "UFUNCTION(BlueprintCallable, Category=\"Combat\")" plus meta.</summary>
        public string Reflection { get; set; }

        /// <summary>When hovering a macro: its expansion (one level, then fully expanded if different), capped in size.</summary>
        public string MacroExpansion { get; set; }

        /// <summary>The macro body with the arguments substituted (null when not a macro).</summary>
        public string MacroExpansionOneLevel { get; set; }

        /// <summary>Every nested macro expanded (equal to <see cref="MacroExpansionOneLevel"/> when nothing nests).</summary>
        public string MacroExpansionFull { get; set; }

        /// <summary>Other overloads / declarations (count shown as "+N overloads").</summary>
        public int OverloadCount { get; set; }

        public ResolutionConfidence Confidence { get; set; }
        public double ElapsedMs { get; set; }
    }

    public sealed class SignatureParameter
    {
        public string Name { get; set; }

        /// <summary>Start / length of the parameter inside SignatureInfo.Label (for bold highlighting).</summary>
        public int LabelStart { get; set; }
        public int LabelLength { get; set; }

        public string Documentation { get; set; }
    }

    public sealed class SignatureInfo
    {
        /// <summary>e.g. "void SetActorLocation(const FVector& NewLocation, bool bSweep = false)".</summary>
        public string Label { get; set; }
        public List<SignatureParameter> Parameters { get; set; } = new List<SignatureParameter>();
        public string Documentation { get; set; }
        public SymbolInfo Symbol { get; set; }
    }

    public sealed class SignatureHelpResponse
    {
        public List<SignatureInfo> Signatures { get; set; } = new List<SignatureInfo>();
        public int ActiveSignature { get; set; }
        public int ActiveParameter { get; set; }

        /// <summary>Position of the opening '(' of the call (the host dismisses the session when the caret leaves the call).</summary>
        public int OpenParenLine { get; set; }
        public int OpenParenColumn { get; set; }

        public double ElapsedMs { get; set; }
    }

    /// <summary>Semantic token kinds for VA-style colouring (mapped to VS classification types in the host).</summary>
    public enum SemanticKind : byte
    {
        None = 0,
        Class = 1,
        Struct = 2,
        Enum = 3,
        EnumValue = 4,
        Typedef = 5,
        TemplateParameter = 6,
        Namespace = 7,
        Function = 8,
        Method = 9,
        StaticMethod = 10,
        VirtualMethod = 11,
        Field = 12,
        StaticField = 13,
        LocalVariable = 14,
        Parameter = 15,
        GlobalVariable = 16,
        Macro = 17,
        /// <summary>UE reflection / boilerplate macros: UCLASS, UPROPERTY, GENERATED_BODY, UE_LOG …</summary>
        UeMacro = 18,
        /// <summary>Reflected types (UCLASS / USTRUCT / UENUM / UINTERFACE): VA shows them like classes; kept separate so they can be tinted.</summary>
        UeType = 19,
        Delegate = 20,
        /// <summary>Text inside UCLASS(...) etc. that is a known reflection specifier.</summary>
        UeSpecifier = 21,
    }

    [System.Flags]
    public enum SemanticModifiers : byte
    {
        None = 0,
        Declaration = 1,
        Static = 2,
        Const = 4,
        Deprecated = 8,
        /// <summary>Name could not be resolved (shown without colour or with a squiggle when enabled).</summary>
        Unresolved = 16,
        /// <summary>Symbol declared in engine / engine plugin / third-party code (VA "stable symbols in italic").</summary>
        External = 32,
    }

    public sealed class SemanticSpan
    {
        public int Line { get; set; }
        public int Column { get; set; }
        public int Length { get; set; }
        public SemanticKind Kind { get; set; }
        public SemanticModifiers Modifiers { get; set; }
    }

    public sealed class ClassifyRequest
    {
        /// <summary>Document (FilePath, Text when dirty, TextVersion); Line/Column are ignored.</summary>
        public CaretRequest Document { get; set; }

        /// <summary>Visible line range to classify (inclusive). The host asks for the viewport plus a margin.</summary>
        public int StartLine { get; set; }
        public int EndLine { get; set; }
    }

    public sealed class ClassifyResponse
    {
        public long TextVersion { get; set; }
        public List<SemanticSpan> Spans { get; set; } = new List<SemanticSpan>();
        public double ElapsedMs { get; set; }
    }
}
