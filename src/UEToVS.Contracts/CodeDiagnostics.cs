using System.Collections.Generic;

namespace UEToVS.Contracts
{
    // M3 squiggles (design §7.6 "波浪线"): only high-confidence problems. After the IntelliSense takeover VS's own squiggles
    // are off, so these are the only error marks the user sees: a missed problem is acceptable, a false one is not.
    // Positions are 0-based; columns and lengths count UTF-16 code units (same as Navigation.cs / IntelliSense.cs).

    public enum CodeDiagnosticSeverity : byte
    {
        Info = 0,
        Warning = 1,
        Error = 2,
    }

    /// <summary>Stable rule ids. The host localizes messages by code (with <see cref="CodeDiagnostic.Args"/>).</summary>
    public static class CodeDiagnosticCodes
    {
        /// <summary>Unknown member: <c>expr.Name</c> / <c>expr-&gt;Name</c> where the type of expr and all of its bases are fully known. Args: member, type.</summary>
        public const string UnknownMember = "UE0001";

        /// <summary>#include of a header whose module is not a dependency of the including module (Build.cs). Args: include, module, owner module.</summary>
        public const string MissingModuleDependency = "UE0002";

        /// <summary>UCLASS / USTRUCT / UINTERFACE type without GENERATED_BODY() (UHT error). Args: macro, type.</summary>
        public const string MissingGeneratedBody = "UE0003";

        /// <summary>A UFUNCTION whose body UHT generates (Server / Client / NetMulticast / BlueprintNativeEvent / BlueprintImplementableEvent) is defined by hand. Args: function, specifier, class.</summary>
        public const string GeneratedFunctionBody = "UE0004";

        /// <summary>ReplicatedUsing=X but the class (and its bases) has no X, or X is not a UFUNCTION. Args: property, function, class, "missing" | "not-ufunction".</summary>
        public const string MissingRepNotify = "UE0005";

        /// <summary>A Replicated / ReplicatedUsing property of the class is not registered in its GetLifetimeReplicatedProps. Args: property, class.</summary>
        public const string UnregisteredReplicatedProperty = "UE0006";

        /// <summary>RequestGameplayTag("X") with a tag that is not in the known tag tree (warning). Args: tag.</summary>
        public const string UnknownGameplayTag = "UE0007";

        /// <summary>A /Game/... asset path that is not in the asset table (warning). Args: path.</summary>
        public const string UnknownAssetPath = "UE0008";

        /// <summary>UPROPERTY on a type UHT rejects (TSharedPtr family, std::, pointer to a struct / builtin). Args: property, type.</summary>
        public const string UnsupportedPropertyType = "UE0009";

        /// <summary>UFUNCTION parameter of a type UHT rejects (pointer to struct / builtin, TSharedPtr family; int8/int16/uint16/uint32/uint64 in BlueprintCallable). Args: parameter, type, function, "pointer" | "unsupported" | "blueprint".</summary>
        public const string UnsupportedParameterType = "UE0010";

        // ---- v0.4.5 编辑体验 (opt-in per request: CodeDiagnosticsRequest.CheckDeprecated / CheckNaming)

        /// <summary>Use of a deprecated API (UE_DEPRECATED / [[deprecated]] / DeprecatedFunction meta / _DEPRECATED), warning. Args: symbol, version ("" when none), message ("" when none), replacement ("" when not mechanical).</summary>
        public const string DeprecatedApi = "UE0011";

        /// <summary>UE naming convention (A / U / F / E / T / I / S prefixes, b for bool members) of a type or bool member declared in this file, info (suggestion). Args: name, expected name, rule letter, "type" | "enum" | "bool".</summary>
        public const string NamingConvention = "UE0012";

        // ---- v0.7.3 more UHT rules (always on, like UE0003…UE0010)

        /// <summary>Server / Client / NetMulticast UFUNCTION without Reliable or Unreliable (UHT error). Args: function, specifier.</summary>
        public const string RpcMissingReliability = "UE0013";

        /// <summary>BlueprintPure UFUNCTION returning void with no output (reference) parameter (UHT error). Args: function.</summary>
        public const string PureWithoutResult = "UE0014";

        /// <summary>Private UPROPERTY with BlueprintReadWrite / BlueprintReadOnly and no meta AllowPrivateAccess (UHT error). Args: property, specifier.</summary>
        public const string PrivateBlueprintProperty = "UE0015";

        /// <summary>UFUNCTION declared in a USTRUCT (UHT error). Args: function, struct.</summary>
        public const string UFunctionInStruct = "UE0016";

        // ---- v0.11.0 headers, syntax and names (design: 后续规划-代码诊断与提示, stage 1–2)

        /// <summary>#include of a file that exists nowhere (index or disk under the module's include roots), error. Args: include, suggestion ("" when none).</summary>
        public const string IncludeNotFound = "UE0017";

        /// <summary>#include of a header in another module's Private folder (UBT never puts it on the include path), error. Args: include, module.</summary>
        public const string IncludeOfPrivateHeader = "UE0018";

        /// <summary>X.generated.h problem in a header with reflected types (UHT error). Args: "missing" | "name" | "not-last", expected include, the include written ("" when none).</summary>
        public const string GeneratedInclude = "UE0019";

        /// <summary>The include resolves but can be written shorter from the module's include roots, info. Args: include, shorter spelling.</summary>
        public const string IncludeSpelling = "UE0020";

        /// <summary>Syntax error the parser is certain about, error. Args: kind (unbalanced-brace | unclosed-brace | endif-without-if | else-without-if | missing-semicolon-after-type | missing-semicolon | unbalanced-paren), detail.</summary>
        public const string SyntaxError = "UE0021";

        /// <summary>
        /// A name declared only in headers this file does not include (directly or through its includes), warning; or a type used
        /// with . / -> that is only forward-declared here. Args: name, include to add, "missing" | "incomplete".
        /// </summary>
        public const string MissingInclude = "UE0022";

        /// <summary>A name declared nowhere (no local, member, macro or symbol in the index), error. Args: name, suggestions separated by '|' ("" when none).</summary>
        public const string UnknownIdentifier = "UE0023";
    }

    public sealed class CodeDiagnosticsRequest
    {
        /// <summary>Document (FilePath, Text when dirty, TextVersion). Line/Column is the caret: the identifier being typed there is not reported.</summary>
        public CaretRequest Document { get; set; }

        /// <summary>Line range to check (inclusive). The host asks for the viewport ± 50 lines.</summary>
        public int StartLine { get; set; }
        public int EndLine { get; set; }

        /// <summary>
        /// Other files open with unsaved changes. The engine sees their saved text only, so types declared there are treated as
        /// uncertain (a member just added to an unsaved header must not be reported as unknown in the .cpp).
        /// </summary>
        public List<string> UnsavedFiles { get; set; } = new List<string>();

        /// <summary>v0.4.5: report uses of deprecated APIs (UE0011) in the range.</summary>
        public bool CheckDeprecated { get; set; }

        /// <summary>v0.4.5: report UE naming convention problems (UE0012) of the file's own declarations in the range.</summary>
        public bool CheckNaming { get; set; }

        /// <summary>v0.11.0: rule codes the user switched off (UE0017…UE0023 have settings of their own). Null / empty = all on.</summary>
        public List<string> DisabledRules { get; set; }
    }

    public sealed class CodeDiagnostic
    {
        public int Line { get; set; }
        public int Column { get; set; }
        public int Length { get; set; }
        public CodeDiagnosticSeverity Severity { get; set; }

        /// <summary>Rule id, see <see cref="CodeDiagnosticCodes"/>.</summary>
        public string Code { get; set; }

        /// <summary>Message arguments (documented per code); the host builds the localized text from code + args.</summary>
        public List<string> Args { get; set; } = new List<string>();

        /// <summary>English fallback text (logs, hosts that do not know the code).</summary>
        public string Message { get; set; }

        /// <summary>v0.11.0: mechanical fixes the engine computed (light bulb / Alt+Enter); null or empty when none.</summary>
        public List<CodeDiagnosticFix> Fixes { get; set; }
    }

    /// <summary>
    /// v0.11.0: one fix of a diagnostic. <see cref="Kind"/> names it for the host's localized title ("replace" with Args[0] the new
    /// text, "add-include" with Args[0] the include to insert — the host plans the line like Add Include and offers the module
    /// dependency); <see cref="Edits"/> are applied as one undo step when present.
    /// </summary>
    public sealed class CodeDiagnosticFix
    {
        public string Kind { get; set; }
        public List<string> Args { get; set; } = new List<string>();
        public List<FileTextEdit> Edits { get; set; } = new List<FileTextEdit>();
    }

    public sealed class CodeDiagnosticsResponse
    {
        public long TextVersion { get; set; }

        /// <summary>The line range actually checked (clamped to the document).</summary>
        public int StartLine { get; set; }
        public int EndLine { get; set; }

        public List<CodeDiagnostic> Items { get; set; } = new List<CodeDiagnostic>();

        /// <summary>Candidate sites examined (member accesses, reflected types).</summary>
        public int Checked { get; set; }

        /// <summary>Candidates skipped because something was uncertain (templates, macros, unindexed bases...): the miss rate indicator.</summary>
        public int Uncertain { get; set; }

        public double ElapsedMs { get; set; }
    }
}
