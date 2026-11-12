using System.Collections.Generic;

namespace UEToVS.Contracts
{
    // v0.4.5 heavy features: whole-project UE rule inspection and the engine upgrade assistant. Both are on demand only
    // (a command, a settings button or the MCP tool starts them), each behind its own switch in the plugin settings
    // (IndexingSettings.ProjectInspectionEnabled / UpgradeAssistantEnabled): switched off, every request answers
    // HeavyJobState.Disabled and nothing runs. Positions are 0-based; columns and lengths count UTF-16 code units.

    /// <summary>What a heavy-job request does: start a run, poll it, page through its results, or cancel it.</summary>
    public enum HeavyJobAction : byte
    {
        Start = 0,
        Status = 1,
        Results = 2,
        Cancel = 3,
    }

    public enum HeavyJobState : byte
    {
        /// <summary>No run yet in this engine session.</summary>
        Idle = 0,
        Running = 1,
        Completed = 2,
        Cancelled = 3,
        Failed = 4,
        /// <summary>The feature is switched off in the plugin settings: nothing ran.</summary>
        Disabled = 5,
    }

    /// <summary>One text change of a mechanical fix (same meaning as the host's TextEdit: OldText must still be there).</summary>
    public sealed class FileTextEdit
    {
        public string FilePath { get; set; }
        public int Line { get; set; }
        public int Column { get; set; }
        public int Length { get; set; }
        public string OldText { get; set; }
        public string NewText { get; set; }
    }

    // ------------------------------------------------------------------ whole-project inspection

    public sealed class ProjectInspectionRequest
    {
        public HeavyJobAction Action { get; set; }

        /// <summary>Start: also inspect project plugins (Plugins/ under the project). Default: project modules only.</summary>
        public bool IncludePlugins { get; set; }

        /// <summary>Results: only this rule (UE0001…), null = all.</summary>
        public string Rule { get; set; }

        /// <summary>Results: only files whose path contains this text (case-insensitive), null = all.</summary>
        public string File { get; set; }

        /// <summary>Results: only findings with a mechanical fix.</summary>
        public bool FixableOnly { get; set; }

        public int Offset { get; set; }

        /// <summary>Results page size (1..2000).</summary>
        public int Limit { get; set; } = 200;
    }

    public sealed class InspectionRuleCount
    {
        public string Code { get; set; }
        public int Count { get; set; }
        public int Fixable { get; set; }
    }

    public sealed class InspectionFileCount
    {
        public string FilePath { get; set; }
        public int Count { get; set; }
    }

    public sealed class InspectionFinding
    {
        public string FilePath { get; set; }
        public int Line { get; set; }
        public int Column { get; set; }
        public int Length { get; set; }
        public CodeDiagnosticSeverity Severity { get; set; }
        public string Code { get; set; }
        public List<string> Args { get; set; } = new List<string>();

        /// <summary>English fallback text; the host localizes by Code + Args like squiggles.</summary>
        public string Message { get; set; }

        /// <summary>The source line (trimmed, at most 240 characters) for the result list.</summary>
        public string LineText { get; set; }

        /// <summary>Mechanical fix (UE0004 rename to _Implementation, UE0003 insert GENERATED_BODY()), or null.</summary>
        public List<FileTextEdit> Fix { get; set; }
    }

    public sealed class ProjectInspectionResponse
    {
        public HeavyJobState State { get; set; }
        public string Error { get; set; }

        /// <summary>The JSON-lines file the findings stream into (&lt;solution&gt;\.vs\UEToVS\inspections\&lt;time&gt;.jsonl).</summary>
        public string ResultsPath { get; set; }

        public bool IncludePlugins { get; set; }
        public int FilesTotal { get; set; }
        public int FilesDone { get; set; }
        public int Findings { get; set; }
        public int Threads { get; set; }
        public double ElapsedMs { get; set; }
        public long PeakWorkingSetBytes { get; set; }

        /// <summary>Per-rule counts (whole run, sorted by count).</summary>
        public List<InspectionRuleCount> Rules { get; set; } = new List<InspectionRuleCount>();

        /// <summary>The files with most findings (at most 200).</summary>
        public List<InspectionFileCount> TopFiles { get; set; } = new List<InspectionFileCount>();

        /// <summary>Results: findings matching the filter (before paging), and the requested page.</summary>
        public int Matched { get; set; }
        public List<InspectionFinding> Items { get; set; } = new List<InspectionFinding>();
    }

    // ------------------------------------------------------------------ engine upgrade assistant

    public sealed class DeprecationScanRequest
    {
        public HeavyJobAction Action { get; set; }

        /// <summary>Start: also look in project plugins.</summary>
        public bool IncludePlugins { get; set; }
    }

    /// <summary>One project reference to a deprecated engine (or plugin) API.</summary>
    public sealed class DeprecatedUsage
    {
        public string FilePath { get; set; }
        public int Line { get; set; }
        public int Column { get; set; }
        public int Length { get; set; }
        public string LineText { get; set; }

        /// <summary>Exact: the name resolves to the deprecated symbol only; NameOnly: other symbols share the name.</summary>
        public ResolutionConfidence Confidence { get; set; }
    }

    public sealed class DeprecatedApi
    {
        public SymbolInfo Symbol { get; set; }

        /// <summary>Deprecation version from UE_DEPRECATED(5.1, ...); empty for [[deprecated]].</summary>
        public string Version { get; set; }
        public string Message { get; set; }

        /// <summary>Replacement named by the message and found in the index with a compatible signature, or null.</summary>
        public string Replacement { get; set; }

        /// <summary>Why no automatic replacement is offered (logs / tooltip): "no-suggestion", "not-found", "signature", "kind".</summary>
        public string ReplacementNote { get; set; }

        public List<DeprecatedUsage> Usages { get; set; } = new List<DeprecatedUsage>();
    }

    public sealed class DeprecationScanResponse
    {
        public HeavyJobState State { get; set; }
        public string Error { get; set; }

        /// <summary>False when the index was built before v0.4.5 (no deprecation data yet; rebuild runs on its own after the upgrade).</summary>
        public bool IndexHasDeprecations { get; set; }

        public int DeprecatedSymbols { get; set; }
        public int SymbolsDone { get; set; }
        public int Usages { get; set; }
        public double ElapsedMs { get; set; }

        /// <summary>Completed: deprecated APIs the project uses, newest deprecation version first.</summary>
        public List<DeprecatedApi> Apis { get; set; } = new List<DeprecatedApi>();
    }
}
