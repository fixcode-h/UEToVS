using System;
using System.Collections.Generic;
using UEToVS.Contracts;

namespace UEToVS.Engine.Syntax
{
    /// <summary>Declaration modifiers and qualifiers.</summary>
    [Flags]
    public enum DeclModifiers : uint
    {
        None = 0,
        Const = 1 << 0,
        Virtual = 1 << 1,
        Static = 1 << 2,
        Override = 1 << 3,
        Final = 1 << 4,
        /// <summary><c>= 0</c> or UE's <c>PURE_VIRTUAL(...)</c>.</summary>
        Pure = 1 << 5,
        Inline = 1 << 6,
        Explicit = 1 << 7,
        Constexpr = 1 << 8,
        Extern = 1 << 9,
        Template = 1 << 10,
        Defaulted = 1 << 11,
        Deleted = 1 << 12,
        Noexcept = 1 << 13,
        Mutable = 1 << 14,
        /// <summary>Scoped enum (<c>enum class</c>).</summary>
        Scoped = 1 << 15,
        /// <summary>Delegate macro of the multicast family.</summary>
        Multicast = 1 << 16,
        /// <summary>Delegate macro of the dynamic (reflected) family.</summary>
        Dynamic = 1 << 17,
        /// <summary>Delegate macro of the event family.</summary>
        Event = 1 << 18,
        /// <summary>Macro with a parameter list.</summary>
        FunctionLike = 1 << 19,
        /// <summary>Forward declaration of a class/struct/enum (only reported with <see cref="ParseOptions.IncludeForwardDeclarations"/>).</summary>
        Forward = 1 << 20,
    }

    public enum AccessLevel : byte
    {
        None = 0,
        Public = 1,
        Protected = 2,
        Private = 3,
    }

    /// <summary>
    /// One declaration found in a file. The first block of properties maps 1:1 to the index's symbol record.
    /// Offsets are UTF-8 byte offsets into the parsed text; <see cref="Line"/>/<see cref="Column"/> are 0-based, column in UTF-16 units.
    /// </summary>
    public sealed class Declaration
    {
        public string Name { get; set; }

        /// <summary>Fully qualified container, e.g. <c>UE::Math::FBox</c>; null at global scope.</summary>
        public string Container { get; set; }

        public SymbolKind Kind { get; set; }
        public UeFlags Ue { get; set; }

        /// <summary>Raw text inside the reflection macro (UCLASS/UPROPERTY/UMETA...), whitespace collapsed; null without one.</summary>
        public string Specifiers { get; set; }

        /// <summary>One-line display signature.</summary>
        public string Signature { get; set; }

        /// <summary>Byte offset of the name token.</summary>
        public int Offset { get; set; }

        public int Line { get; set; }
        public int Column { get; set; }
        public bool IsDefinition { get; set; }

        /// <summary>The declaration sits in a preprocessor branch whose condition could not be evaluated.</summary>
        public bool IsConditional { get; set; }

        // ---- extras ----

        /// <summary>Length in bytes of the name token(s) at <see cref="Offset"/>.</summary>
        public int NameLength { get; set; }

        public DeclModifiers Modifiers { get; set; }
        public AccessLevel Access { get; set; }

        /// <summary>Return type text of functions; type text of fields, variables, typedefs and aliases.</summary>
        public string Type { get; set; }

        /// <summary>Parameter list text without the parentheses (functions, function-like macros, delegate macro arguments after the name).</summary>
        public string Parameters { get; set; }

        /// <summary>Template parameter list text without the angle brackets, when templated.</summary>
        public string TemplateParameters { get; set; }

        /// <summary>Base class texts for classes/structs, e.g. <c>public ACharacter</c> is reported as <c>ACharacter</c>.</summary>
        public string[] Bases { get; set; }

        /// <summary>Enum value initializer, enum underlying type, or macro replacement text.</summary>
        public string Value { get; set; }

        /// <summary>Doc comment immediately preceding the declaration (<c>/** */</c>, <c>///</c>), or <see cref="TextRange.None"/>.</summary>
        public TextRange DocComment { get; set; } = TextRange.None;

        /// <summary>Body range for function definitions (from '{' to '}' inclusive) and class/namespace/enum bodies.</summary>
        public TextRange Body { get; set; } = TextRange.None;

        /// <summary>Member injected by a GENERATED_BODY-style macro (StaticClass, Super, ...); located at the macro.</summary>
        public bool IsImplicit { get; set; }

        /// <summary>Index of the enclosing declaration (class, namespace, enum) in <see cref="FileSummary.Declarations"/>, or -1.</summary>
        public int Parent { get; set; } = -1;

        public string QualifiedName => string.IsNullOrEmpty(Container) ? Name : Container + "::" + Name;

        public override string ToString() => $"{Kind} {QualifiedName} @{Line}:{Column}";
    }

    /// <summary>How an identifier is used at an occurrence.</summary>
    [Flags]
    public enum OccurrenceRole : byte
    {
        None = 0,
        Read = 1,
        /// <summary>Left of an assignment, or operand of ++/--.</summary>
        Write = 2,
        Call = 4,
        /// <summary>Accessed through a qualifier (see <see cref="Occurrence.Qualifier"/>).</summary>
        MemberAccess = 8,
        TypeUse = 16,
        AddressOf = 32,
    }

    public enum MemberQualifier : byte
    {
        None = 0,
        /// <summary><c>a.b</c></summary>
        Dot = 1,
        /// <summary><c>a-&gt;b</c></summary>
        Arrow = 2,
        /// <summary><c>A::b</c></summary>
        Scope = 3,
    }

    /// <summary>An identifier occurrence inside a function body (input of the reference index).</summary>
    public struct Occurrence
    {
        /// <summary>Interned name.</summary>
        public string Name;

        /// <summary>Byte offset of the identifier.</summary>
        public int Offset;

        public OccurrenceRole Role;
        public MemberQualifier Qualifier;

        /// <summary>Index of the function declaration whose body contains the occurrence, or -1.</summary>
        public int Function;

        public override string ToString() => $"{Name}@{Offset} {Role} {Qualifier}";
    }

    /// <summary>An <c>#include</c> (or <c>#import</c>/<c>#include_next</c>) directive.</summary>
    public sealed class IncludeDirective
    {
        /// <summary>The path as written, without quotes or brackets. For a macro include (<c>#include FOO</c>) the macro text.</summary>
        public string Path { get; set; }

        public bool IsAngled { get; set; }

        /// <summary>0-based line.</summary>
        public int Line { get; set; }

        public int Offset { get; set; }
        public bool IsConditional { get; set; }

        /// <summary>The operand was not a literal path (e.g. <c>#include COMPILED_PLATFORM_HEADER(X.h)</c>).</summary>
        public bool IsMacro { get; set; }

        public override string ToString() => IsAngled ? "<" + Path + ">" : "\"" + Path + "\"";
    }

    public readonly struct SyntaxDiagnostic
    {
        public SyntaxDiagnostic(int offset, int line, string message)
        {
            Offset = offset;
            Line = line;
            Message = message;
        }

        public int Offset { get; }
        public int Line { get; }
        public string Message { get; }
        public override string ToString() => $"{Line}: {Message}";
    }

    /// <summary>Everything the syntax layer extracts from one file.</summary>
    public sealed class FileSummary
    {
        internal static readonly IReadOnlyList<Declaration> NoDecls = Array.Empty<Declaration>();

        public FileSummary(
            IReadOnlyList<Declaration> declarations,
            IReadOnlyList<Occurrence> occurrences,
            IReadOnlyList<IncludeDirective> includes,
            IReadOnlyList<TextRange> inactiveRanges,
            IReadOnlyList<SyntaxDiagnostic> diagnostics,
            LineMap lines,
            int textLength,
            byte[] transcodedText)
        {
            Declarations = declarations;
            Occurrences = occurrences;
            Includes = includes;
            InactiveRanges = inactiveRanges;
            Diagnostics = diagnostics;
            Lines = lines;
            TextLength = textLength;
            TranscodedText = transcodedText;
        }

        public static FileSummary Empty { get; } = new FileSummary(NoDecls, Array.Empty<Occurrence>(), Array.Empty<IncludeDirective>(),
            Array.Empty<TextRange>(), Array.Empty<SyntaxDiagnostic>(), LineMap.Build(default), 0, null);

        /// <summary>Declarations in source order (including <c>#define</c> macros and GENERATED_BODY-injected members).</summary>
        public IReadOnlyList<Declaration> Declarations { get; }

        /// <summary>Identifier occurrences inside function bodies, in source order.</summary>
        public IReadOnlyList<Occurrence> Occurrences { get; }

        public IReadOnlyList<IncludeDirective> Includes { get; }

        /// <summary>Byte ranges excluded by preprocessor conditions known to be false (for greying out).</summary>
        public IReadOnlyList<TextRange> InactiveRanges { get; }

        /// <summary>Recovery points and other parse problems (capped).</summary>
        public IReadOnlyList<SyntaxDiagnostic> Diagnostics { get; }

        public LineMap Lines { get; }
        public int TextLength { get; }

        /// <summary>
        /// When the input was UTF-16, the UTF-8 text that all offsets refer to; null for UTF-8 input
        /// (offsets then refer to the input bytes, BOM included).
        /// </summary>
        public byte[] TranscodedText { get; }
    }

    /// <summary>Options for <see cref="CppParser"/>. Instances are immutable after construction by convention; share freely.</summary>
    public sealed class ParseOptions
    {
        public static ParseOptions Default { get; } = new ParseOptions();

        /// <summary>Macros known to be defined (name → replacement text, may be empty). Used to evaluate <c>#if</c>.</summary>
        public IReadOnlyDictionary<string, string> Defines { get; set; }

        /// <summary>Macros known to be undefined.</summary>
        public IReadOnlyCollection<string> Undefines { get; set; }

        /// <summary>
        /// When true, a macro that is neither in <see cref="Defines"/> nor defined in the file counts as undefined
        /// (normal compiler behaviour). When false (default) conditions on it are unknown: both branches are parsed
        /// and their declarations flagged <see cref="Declaration.IsConditional"/>.
        /// </summary>
        public bool UnlistedMacrosAreUndefined { get; set; }

        /// <summary>UE macro knowledge base; defaults to the embedded one.</summary>
        public UeMacroKnowledge Knowledge { get; set; }

        /// <summary>Emit identifier occurrences from function bodies (default true).</summary>
        public bool CollectOccurrences { get; set; } = true;

        /// <summary>Emit members injected by GENERATED_BODY-style macros as implicit declarations (default true).</summary>
        public bool EmitInjectedMembers { get; set; } = true;

        /// <summary>Report forward declarations (<c>class AActor;</c>) with <see cref="Declaration.IsDefinition"/> false (default false).</summary>
        public bool IncludeForwardDeclarations { get; set; }

        /// <summary>Optional name table shared across parses on the same thread (not thread-safe).</summary>
        public NameTable Names { get; set; }

        /// <summary>Maximum signature length in characters; longer ones are truncated with an ellipsis.</summary>
        public int MaxSignatureLength { get; set; } = 240;
    }
}
