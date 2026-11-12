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
