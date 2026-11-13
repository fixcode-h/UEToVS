using System.Collections.Generic;
using System.Linq;

namespace UEToVS.Host.Logic.IntelliSense
{
    /// <summary>One concrete automation property that may implement a setting, with the value meaning "taken over".</summary>
    public sealed class CppPropertyCandidate
    {
        public CppPropertyCandidate(string page, string name, bool takeoverValue)
        {
            Page = page;
            Name = name;
            TakeoverValue = takeoverValue;
        }

        /// <summary>DTE "TextEditor" sub-page, e.g. "C/C++ Specific".</summary>
        public string Page { get; }

        /// <summary>Property name as exposed by DTE.Properties[TextEditor, Page].</summary>
        public string Name { get; }

        /// <summary>Value that turns VS's own feature off (true for DisableX, false for EnableX).</summary>
        public bool TakeoverValue { get; }

        public string QualifiedName => Page + "/" + Name;

        public override string ToString() => QualifiedName + "=" + TakeoverValue;
    }

    /// <summary>A logical VS C++ setting UEToVS turns off (§7.5) and the property names it may have across VS versions.</summary>
    public sealed class CppSettingDefinition
    {
        public CppSettingDefinition(string key, string description, CppSettingGate gate, params CppPropertyCandidate[] candidates)
        {
            Key = key;
            Description = description;
            Gate = gate;
            Candidates = candidates;
        }

        /// <summary>Stable id used in logs and in the backup file.</summary>
        public string Key { get; }

        public string Description { get; }

        public CppSettingGate Gate { get; }

        /// <summary>For <see cref="CppSettingGate.VsFeature"/>: the switch id (<see cref="VsUnrealFeatures"/>) that turns this setting off.</summary>
        public string Feature { get; set; }

        /// <summary>Tried in order; the first property that exists on this VS is used.</summary>
        public IReadOnlyList<CppPropertyCandidate> Candidates { get; }
    }

    /// <summary>Which option controls whether a setting is part of the takeover.</summary>
    public enum CppSettingGate
    {
        /// <summary>Always part of the takeover.</summary>
        Always = 0,

        /// <summary>Only when the "Disable Database" option is on (default on, §7.5).</summary>
        DisableDatabaseOption = 1,

        /// <summary>v0.5.6: a VS Unreal / C++ background feature with its own switch (<see cref="CppSettingDefinition.Feature"/>), default off with the takeover.</summary>
        VsFeature = 2,
    }

    /// <summary>
    /// The table of VS C/C++ settings UEToVS changes when it takes over C++ code insight (§7.5).
    /// Property names are not documented and vary by VS version, so every setting lists candidate names; the takeover
    /// logs which ones were found, changed or missing (event intellisense.property.*), and the full property list of the
    /// page once per session (intellisense.properties) so the table can be calibrated from real logs.
    /// </summary>
    /// <remarks>
    /// Deliberately NOT touched: the "TextEditor/C/C++" general page (AutoListMembers / ParameterInformation). Those
    /// options gate statement completion for the C/C++ content type as a whole and would also suppress UEToVS's own
    /// completion and signature help. Member-list popups of VS's engine go away with DisableIntelliSense anyway.
    /// </remarks>
    public static class CppIntelliSenseSettings
    {
        public const string SpecificPage = "C/C++ Specific";

        public const string KeyIntelliSense = "intellisense";
        public const string KeyAutoQuickInfo = "autoQuickInfo";
        public const string KeyMemberList = "memberList";
        public const string KeyErrorReporting = "errorReporting";
        public const string KeySquiggles = "squiggles";
        public const string KeySemanticColorization = "semanticColorization";
        public const string KeyIncludeAutoComplete = "includeAutoComplete";
        public const string KeyExternalDependencies = "externalDependencies";
        public const string KeyDatabase = "database";
        public const string KeyDatabaseUpdates = "databaseUpdates";
        public const string KeyAutomaticUpdates = "automaticUpdates";
        public const string KeySymbolIndex = "symbolIndex";
        public const string KeyBackgroundCodeAnalysis = "backgroundCodeAnalysis";

        private static CppPropertyCandidate S(string name, bool value) => new CppPropertyCandidate(SpecificPage, name, value);

        public static IReadOnlyList<CppSettingDefinition> All { get; } = new List<CppSettingDefinition>
        {
            new CppSettingDefinition(KeyIntelliSense, "Disable IntelliSense (turns off the EDG front end)", CppSettingGate.Always,
                S("DisableIntelliSense", true), S("IntelliSenseDisabled", true), S("DisableIntellisense", true)),
            new CppSettingDefinition(KeyAutoQuickInfo, "Disable Auto Quick Info", CppSettingGate.Always,
                S("DisableAutoQuickInfo", true), S("AutoQuickInfo", false), S("EnableAutoQuickInfo", false),
                // VS 18.x: the C/C++ page only has the tool-tip switch (UEToVS opens Quick Info on hover itself)
                S("EnableQuickInfoToolTips", false)),
            new CppSettingDefinition(KeyMemberList, "Disable member list auto popup", CppSettingGate.Always,
                S("DisableMemberList", true), S("DisableAutoMemberList", true), S("DisableMemberListAutoPopup", true)),
            new CppSettingDefinition(KeyErrorReporting, "Disable Error Reporting", CppSettingGate.Always,
                S("DisableErrorReporting", true), S("DisableIntelliSenseErrorReporting", true), S("EnableErrorReporting", false)),
            new CppSettingDefinition(KeySquiggles, "Disable Squiggles", CppSettingGate.Always,
                S("DisableSquiggles", true), S("DisableErrorSquiggles", true), S("EnableSquiggles", false)),
            new CppSettingDefinition(KeySemanticColorization, "Disable Semantic Colorization", CppSettingGate.Always,
                S("DisableSemanticColorization", true), S("EnableSemanticColorization", false), S("SemanticColorization", false),
                S("DisableSemanticColoring", true)), // VS 18.x name
            new CppSettingDefinition(KeyIncludeAutoComplete, "Disable #include Auto Complete", CppSettingGate.Always,
                S("DisableIncludeAutoComplete", true), S("DisableIncludeAutocomplete", true), S("DisableAutoIncludeCompletion", true)),
            new CppSettingDefinition(KeyExternalDependencies, "Disable External Dependencies Folders", CppSettingGate.Always,
                S("DisableExternalDependenciesFolders", true), S("DisableExternalDependencies", true), S("DisableExternalDependencyFolders", true)),
            new CppSettingDefinition(KeyDatabase, "Disable Database (browsing database .vc.db)", CppSettingGate.DisableDatabaseOption,
                S("DisableDatabase", true), S("DisableBrowsingDatabase", true)),
            // "正在更新 C/C++ IntelliSense 和浏览信息 / 正在分析包含文件": the browsing database scan. With only "Disable
            // Database" some VS versions still rescan includes, so the update switches go too (same gate, same backup).
            new CppSettingDefinition(KeyDatabaseUpdates, "Disable Database Updates", CppSettingGate.DisableDatabaseOption,
                S("DisableDatabaseUpdates", true), S("DisableDatabaseUpdate", true), S("DisableBrowsingDatabaseUpdates", true)),
            new CppSettingDefinition(KeyAutomaticUpdates, "Disable Automatic Updates", CppSettingGate.DisableDatabaseOption,
                S("DisableAutomaticUpdates", true), S("DisableAutoUpdates", true), S("DisableAutoUpdate", true),
                S("DisableDatabaseAutoUpdates", true)), // VS 18.x name
            // v0.5.6: VS 18 Unreal / C++ background features with their own switches (semantic index, Blueprint support,
            // UHT code analysis). Written last: some of them live only in settings.json (VsUnrealFeatures.JsonPage).
        }.Concat(VsUnrealFeatures.Table).ToList();

        /// <summary>
        /// VS's C++ background code analysis ("Disable Background Code Analysis" under Text Editor > C/C++ > Advanced >
        /// Code Analysis). On every open/save it compiles the whole translation unit with /analyze, which on UE sources
        /// takes tens of seconds per file. Separate from <see cref="All"/>: it has its own option (off by default),
        /// independent of the IntelliSense takeover, and its own backup file.
        /// </summary>
        public static IReadOnlyList<CppSettingDefinition> BackgroundCodeAnalysis { get; } = new[]
        {
            new CppSettingDefinition(KeyBackgroundCodeAnalysis, "Disable Background Code Analysis", CppSettingGate.Always,
                S("DisableBackgroundCodeAnalysis", true), S("DisableBackgroundAnalysis", true), S("EnableBackgroundCodeAnalysis", false),
                S("DisableCodeAnalysisInBackground", true)), // VS 18.x name (seen in a VS 18.6 diagnostics package)
        };

        /// <summary>Pages that must be read to evaluate the table.</summary>
        public static IEnumerable<string> Pages
        {
            get { yield return SpecificPage; }
        }
    }
}
