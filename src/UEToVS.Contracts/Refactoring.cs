using System;
using System.Collections.Generic;

namespace UEToVS.Contracts
{
    // M4 refactoring / code generation contracts (Alt+Enter context, Create Declaration / Implementation, Implement
    // Virtual, Add Include, Rename). The engine only answers questions about the code; it never edits files: the host plans
    // and applies text edits (§6.6). Positions are 0-based like everywhere else.

    /// <summary>Function qualifiers as parsed (subset of the parser's modifiers that matter for code generation).</summary>
    [Flags]
    public enum FunctionModifiers : ushort
    {
        None = 0,
        Virtual = 1 << 0,
        Static = 1 << 1,
        Const = 1 << 2,
        Override = 1 << 3,
        Final = 1 << 4,
        /// <summary><c>= 0</c> or <c>PURE_VIRTUAL(...)</c>.</summary>
        Pure = 1 << 5,
        Inline = 1 << 6,
        Explicit = 1 << 7,
        Constexpr = 1 << 8,
        Noexcept = 1 << 9,
        Template = 1 << 10,
    }

    public enum MemberAccess : byte
    {
        None = 0,
        Public = 1,
        Protected = 2,
        Private = 3,
    }

    /// <summary>
    /// A function declaration or definition as the engine's parser sees it. <see cref="Parameters"/> is the text between
    /// the parentheses exactly as written (default arguments included when they are in the source).
    /// </summary>
    public sealed class FunctionSignature
    {
        public string Name { get; set; }

        /// <summary>Qualified owner (class or namespace), null at global scope.</summary>
        public string Container { get; set; }

        public SymbolKind Kind { get; set; }
        public string ReturnType { get; set; }
        public string Parameters { get; set; }
        public string TemplateParameters { get; set; }
        public FunctionModifiers Modifiers { get; set; }
        public MemberAccess Access { get; set; }

        /// <summary>UFUNCTION(...) text, or null.</summary>
        public string Specifiers { get; set; }

        public UeFlags Ue { get; set; }
        public SourceLocation Location { get; set; }
        public bool IsDefinition { get; set; }

        /// <summary>Overridable-method lists: the base type that declares it (qualified).</summary>
        public string DeclaringType { get; set; }

        public string Module { get; set; }
        public ItemOrigin Origin { get; set; }

        /// <summary>Line of the closing brace of the body (definitions), or of the terminating ';' (declarations); -1 when unknown.</summary>
        public int EndLine { get; set; } = -1;
    }

    public sealed class RefactoringContextRequest
    {
        public CaretRequest Caret { get; set; }

        /// <summary>Also decide whether the type at the caret is already included (bounded walk over the index's include lists).</summary>
        public bool CheckIncludes { get; set; }

        // ---- v0.4.5 refactoring batch 2 (Change Signature / Introduce Variable / Extract Method)

        /// <summary>Editor selection (0-based); -1 when there is none. Introduce Variable types the expression ending at the selection end.</summary>
        public int SelectionStartLine { get; set; } = -1;

        public int SelectionStartColumn { get; set; } = -1;
        public int SelectionEndLine { get; set; } = -1;
        public int SelectionEndColumn { get; set; } = -1;

        /// <summary>Also return <see cref="RefactoringContextResponse.EnclosingFunction"/> and its <see cref="RefactoringContextResponse.Locals"/>.</summary>
        public bool IncludeLocals { get; set; }

        /// <summary>Also return the override chain of <see cref="RefactoringContextResponse.Function"/> (bases and derived types).</summary>
        public bool IncludeSignatureFamily { get; set; }
    }

    /// <summary>A local variable or parameter of a function (v0.4.5, Extract Method). Positions are 0-based (UTF-16 columns).</summary>
    public sealed class LocalSymbol
    {
        public string Name { get; set; }

        /// <summary>Type as written (<c>auto</c> stays <c>auto</c> unless a Cast&lt;T&gt;-style initializer tells the type).</summary>
        public string Type { get; set; }

        public int Line { get; set; }
        public int Column { get; set; }
        public bool IsParameter { get; set; }

        /// <summary>Where the variable goes out of scope (the closing brace of its block).</summary>
        public int ScopeEndLine { get; set; } = -1;

        public int ScopeEndColumn { get; set; } = -1;
    }

    public enum IncludeState : byte
    {
        /// <summary>Not checked, or the index cannot tell.</summary>
        Unknown = 0,
        /// <summary>The type is declared in the caret's own file.</summary>
        SameFile = 1,
        Direct = 2,
        /// <summary>Reachable through the include lists of included headers (bounded depth).</summary>
        Transitive = 3,
        Missing = 4,
    }

    public sealed class TypeAtCaret
    {
        public SymbolInfo Symbol { get; set; }

        /// <summary>Header that declares the type (the definition when there are forward declarations).</summary>
        public string HeaderPath { get; set; }

        public IncludeState Include { get; set; }

        /// <summary>For <see cref="IncludeState.Transitive"/>: the directly included header through which it arrives.</summary>
        public string IncludedVia { get; set; }

        /// <summary>Files visited by the include walk (diagnostics).</summary>
        public int FilesVisited { get; set; }
    }

    public sealed class ClassAtCaret
    {
        public SymbolInfo Symbol { get; set; }
        public List<string> Bases { get; set; } = new List<string>();

        /// <summary>Lines of the class body braces in <see cref="SymbolInfo.Location"/>'s file (-1 when unknown).</summary>
        public int BodyStartLine { get; set; } = -1;

        public int BodyEndLine { get; set; } = -1;
    }

    public sealed class RefactoringContextResponse
    {
        public string Word { get; set; }

        /// <summary>Best symbol for the caret (null when unresolved).</summary>
        public SymbolInfo Symbol { get; set; }

        public ResolutionConfidence Confidence { get; set; }

        /// <summary>The caret is on the name of a declaration / definition (not on a use).</summary>
        public bool OnDeclaration { get; set; }

        /// <summary>The function whose declaration or definition is at the caret (name or signature line), with full details.</summary>
        public FunctionSignature Function { get; set; }

        /// <summary>For <see cref="Function"/>: its declarations (bodiless, in a class or namespace) found in the index / buffer.</summary>
        public List<SymbolInfo> Declarations { get; set; } = new List<SymbolInfo>();

        /// <summary>For <see cref="Function"/>: its definitions (with a body).</summary>
        public List<SymbolInfo> Definitions { get; set; } = new List<SymbolInfo>();

        /// <summary>For <see cref="Function"/>: existing definitions of Name_Implementation / Name_Validate in the same class.</summary>
        public List<SymbolInfo> UeCounterpartDefinitions { get; set; } = new List<SymbolInfo>();

        /// <summary>The class around the caret, or the class owning <see cref="Function"/> for an out-of-line definition.</summary>
        public ClassAtCaret Class { get; set; }

        /// <summary>The caret is on a type name (use or declaration) declared in some header.</summary>
        public TypeAtCaret Type { get; set; }

        public bool IndexReady { get; set; }
        public double ElapsedMs { get; set; }

        // ---- v0.4.5 refactoring batch 2

        /// <summary>Type of the expression ending at the selection end (best effort, as written), or null.</summary>
        public string ExpressionType { get; set; }

        /// <summary>The function whose body contains the caret (with RefactoringContextRequest.IncludeLocals); Location = its name, EndLine = closing brace.</summary>
        public FunctionSignature EnclosingFunction { get; set; }

        /// <summary>Locals and parameters of <see cref="EnclosingFunction"/>, in source order.</summary>
        public List<LocalSymbol> Locals { get; set; } = new List<LocalSymbol>();

        /// <summary>For <see cref="Function"/> (with IncludeSignatureFamily): declarations of the same name / arity in base types, nearest first.</summary>
        public List<SymbolInfo> BaseDeclarations { get; set; } = new List<SymbolInfo>();

        /// <summary>For <see cref="Function"/> (with IncludeSignatureFamily): overrides in derived types (Name and Name_Implementation).</summary>
        public List<SymbolInfo> Overrides { get; set; } = new List<SymbolInfo>();
    }

    public sealed class OverridableMethodsRequest
    {
        /// <summary>Class at the caret (or owning the caret's out-of-line function).</summary>
        public CaretRequest Caret { get; set; }

        /// <summary>Or name the class directly (wins over Caret).</summary>
        public string TypeName { get; set; }

        public string Container { get; set; }
        public int MaxResults { get; set; } = 500;
    }

    public sealed class OverridableMethodsResponse
    {
        public SymbolInfo Type { get; set; }

        /// <summary>Virtual functions of the bases not overridden yet (most derived declaration of each), nearest base first.</summary>
        public List<FunctionSignature> Methods { get; set; } = new List<FunctionSignature>();

        /// <summary>Methods the class already overrides (diagnostics / UI greying).</summary>
        public int AlreadyOverridden { get; set; }

        public int TypesVisited { get; set; }
        public bool Truncated { get; set; }
        public double ElapsedMs { get; set; }
    }
}
