using System.Collections.Generic;

namespace UEToVS.Contracts
{
    // ---- v0.4.5 UE editor link: automation tests and log categories found in the project source (on demand) ----

    public enum AutomationTestKind : byte
    {
        /// <summary>IMPLEMENT_SIMPLE_AUTOMATION_TEST / IMPLEMENT_CUSTOM_SIMPLE_AUTOMATION_TEST.</summary>
        Simple = 0,

        /// <summary>IMPLEMENT_COMPLEX_AUTOMATION_TEST / IMPLEMENT_CUSTOM_COMPLEX_AUTOMATION_TEST.</summary>
        Complex = 1,

        /// <summary>BEGIN_DEFINE_SPEC / DEFINE_SPEC.</summary>
        Spec = 2,
    }

    public sealed class AutomationTestsRequest
    {
        /// <summary>Also scan engine / engine plugin sources (slow on a cold cache; off by default).</summary>
        public bool IncludeEngine { get; set; }

        public int MaxResults { get; set; } = 5000;
    }

    public sealed class AutomationTestInfo
    {
        /// <summary>Test path as the automation framework shows it: the string argument ("Project.Feature.Case").</summary>
        public string Name { get; set; }

        /// <summary>The test class (first macro argument).</summary>
        public string ClassName { get; set; }

        public AutomationTestKind Kind { get; set; }

        /// <summary>The macro name as written (IMPLEMENT_SIMPLE_AUTOMATION_TEST, DEFINE_SPEC, ...).</summary>
        public string Macro { get; set; }

        public SourceLocation Location { get; set; }
        public ItemOrigin Origin { get; set; }
    }

    public sealed class AutomationTestsResponse
    {
        public List<AutomationTestInfo> Tests { get; set; } = new List<AutomationTestInfo>();
        public int FilesScanned { get; set; }
        public int FilesRead { get; set; }
        public bool Truncated { get; set; }

        /// <summary>False while the file list is not known yet (engine just started): the result may be incomplete.</summary>
        public bool IndexListed { get; set; }

        public double ElapsedMs { get; set; }
    }

    public enum LogSiteKind : byte
    {
        /// <summary>UE_LOG / UE_CLOG / UE_LOGFMT.</summary>
        Usage = 0,

        /// <summary>DECLARE_LOG_CATEGORY_EXTERN / DECLARE_LOG_CATEGORY_CLASS.</summary>
        Declaration = 1,

        /// <summary>DEFINE_LOG_CATEGORY / DEFINE_LOG_CATEGORY_CLASS.</summary>
        Definition = 2,

        /// <summary>DEFINE_LOG_CATEGORY_STATIC (declaration and definition in one).</summary>
        StaticDefinition = 3,
    }

    public sealed class LogSitesRequest
    {
        /// <summary>Category name (LogTemp, LogMyGame). Required.</summary>
        public string Category { get; set; }

        public bool IncludeUsages { get; set; } = true;
        public bool IncludeDeclarations { get; set; } = true;

        /// <summary>Also scan engine / engine plugin sources (slow on a cold cache; off by default).</summary>
        public bool IncludeEngine { get; set; }

        public int MaxResults { get; set; } = 2000;
    }

    public sealed class LogSiteInfo
    {
        public LogSiteKind Kind { get; set; }
        public string Category { get; set; }

        /// <summary>Macro as written: UE_LOG, UE_CLOG, UE_LOGFMT, DECLARE_LOG_CATEGORY_EXTERN, ...</summary>
        public string Macro { get; set; }

        /// <summary>Usages: the verbosity argument as written (Warning, Error, or a variable name).</summary>
        public string Verbosity { get; set; }

        /// <summary>
        /// Usages: the format string, decoded (TEXT("a") TEXT("b") / "a" "b" concatenated, escapes resolved); null when the
        /// format is not a literal (a variable, a macro).
        /// </summary>
        public string Format { get; set; }

        public SourceLocation Location { get; set; }
        public ItemOrigin Origin { get; set; }
    }

    public sealed class LogSitesResponse
    {
        public List<LogSiteInfo> Sites { get; set; } = new List<LogSiteInfo>();
        public int FilesScanned { get; set; }
        public int FilesRead { get; set; }
        public bool Truncated { get; set; }
        public bool IndexListed { get; set; }
        public double ElapsedMs { get; set; }
    }
}
