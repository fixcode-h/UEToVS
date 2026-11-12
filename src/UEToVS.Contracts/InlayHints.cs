using System.Collections.Generic;

namespace UEToVS.Contracts
{
    // v0.4.5 inlay hints (design §7.6 "Inlay Hints"): parameter names before literal arguments (Rider rule) and deduced auto types.
    // Only the requested line range is computed. Positions are 0-based; columns count UTF-16 code units.

    public enum InlayHintKind : byte
    {
        /// <summary>"Name:" before a literal / nullptr / true / false argument.</summary>
        ParameterName = 0,

        /// <summary>": FVector" after the name of an auto variable.</summary>
        Type = 1,
    }

    public sealed class InlayHintsRequest
    {
        /// <summary>Document (FilePath, Text when dirty, TextVersion). Line/Column unused.</summary>
        public CaretRequest Document { get; set; }

        /// <summary>Line range (inclusive): the viewport plus a small margin.</summary>
        public int StartLine { get; set; }
        public int EndLine { get; set; }

        public bool ParameterNames { get; set; } = true;
        public bool AutoTypes { get; set; }
    }

    public sealed class InlayHint
    {
        /// <summary>Where the hint is drawn (before the argument / after the variable name).</summary>
        public int Line { get; set; }
        public int Column { get; set; }
        public InlayHintKind Kind { get; set; }

        /// <summary>Parameter name or type text, without decoration (the host adds ':' / ': ').</summary>
        public string Text { get; set; }

        /// <summary>Full parameter declaration or deduced type (tooltip), may be null.</summary>
        public string Detail { get; set; }
    }

    public sealed class InlayHintsResponse
    {
        public long TextVersion { get; set; }
        public int StartLine { get; set; }
        public int EndLine { get; set; }
        public List<InlayHint> Items { get; set; } = new List<InlayHint>();

        /// <summary>Calls with at least one literal argument (each one resolved once).</summary>
        public int Calls { get; set; }

        /// <summary>Calls whose callee was resolved to one parameter list.</summary>
        public int Resolved { get; set; }

        /// <summary>auto variables examined.</summary>
        public int AutoVariables { get; set; }

        public double ElapsedMs { get; set; }
    }
}
