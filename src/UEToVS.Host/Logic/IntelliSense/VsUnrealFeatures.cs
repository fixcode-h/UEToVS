using System;
using System.Collections.Generic;
using System.Linq;

namespace UEToVS.Host.Logic.IntelliSense
{
    /// <summary>One VS feature with its own switch in the settings window (default: turned off with the takeover).</summary>
    public sealed class VsUnrealFeature
    {
        public VsUnrealFeature(string id, string settingKey, string titleZh, string titleEn, string descZh, string descEn)
        {
            Id = id;
            SettingKey = settingKey;
            TitleZh = titleZh;
            TitleEn = titleEn;
            DescriptionZh = descZh;
            DescriptionEn = descEn;
        }

        /// <summary>Stable id (<see cref="CppSettingDefinition.Feature"/>, logs).</summary>
        public string Id { get; }

        /// <summary>Key in the settings window / settings store; true = turn the VS feature off.</summary>
        public string SettingKey { get; }

        public string TitleZh { get; }
        public string TitleEn { get; }
        public string DescriptionZh { get; }
        public string DescriptionEn { get; }
    }

    /// <summary>
    /// v0.5.6: VS 18's Unreal Engine and C++ background features that UEToVS switches off while it takes over a UE solution,
    /// each with a switch of its own (on by default = the VS feature is turned off). They are part of the takeover table
    /// (<see cref="CppIntelliSenseSettings.All"/>, gate <see cref="CppSettingGate.VsFeature"/>), so they share its backup,
    /// its scope (UE solutions only / global) and its restore (scope "Off", 恢复 VS 原样).
    /// Some of them exist only in VS 18's unified settings file (settings.json, page <see cref="JsonPage"/>); the others are
    /// tried on the DTE C/C++ page first.
    /// </summary>
    public static class VsUnrealFeatures
    {
        /// <summary>Pseudo page for keys of VS's unified settings file (%LocalAppData%\Microsoft\VisualStudio\&lt;instance&gt;\settings.json).</summary>
        public const string JsonPage = "settings.json";

        public const string SemanticIndex = "semanticIndex";
        public const string Blueprints = "blueprints";
        public const string UhtAnalysis = "uhtAnalysis";
        public const string CodeLens = "codeLens";

        public const string JsonSemanticIndex = "languages.cpp.intelliSense.browsingNavigation.wholeCodebaseSemanticIndex.enabled";
        public const string JsonCodeLensReferences = "languages.cpp.intelliSense.browsingNavigation.wholeCodebaseSemanticIndex.enableCodeLensForReferences";
        public const string JsonCodeLensFields = "languages.cpp.intelliSense.browsingNavigation.wholeCodebaseSemanticIndex.enableCodeLensForReferencesFields";
        public const string JsonBlueprints = "unrealEngine.enableBlueprints";
        public const string JsonUhtAnalysis = "unrealEngine.enableCodeAnalysis";
        public const string JsonCodeLens = "textEditor.codeLens.enabled";

        public static IReadOnlyList<VsUnrealFeature> Features { get; } = new[]
        {
            new VsUnrealFeature(SemanticIndex, "vsFeatures.semanticIndex", "关闭 VS 全代码库语义索引", "Turn off VS's whole-codebase semantic index",
                "VS 18 新增的 C++ 语义索引，给 CodeLens 的引用计数和查找引用用。每次编译后它会用几十个线程把整个 UE 代码库重新索引一遍，" +
                "实测 devenv 内存涨 10 GB 以上、持续十几分钟。关掉后 CodeLens 不再显示 C++ 引用数，查找引用用 UEToVS 的（Shift+Alt+F）。",
                "VS 18's new C++ semantic index, used for CodeLens reference counts and Find All References. After every build it re-indexes the whole UE " +
                "codebase with dozens of threads; devenv was measured at +10 GB for over ten minutes. When off, CodeLens shows no C++ reference counts; use UEToVS's Find References (Shift+Alt+F)."),
            new VsUnrealFeature(Blueprints, "vsFeatures.blueprints", "关闭 VS 的蓝图支持", "Turn off VS's Blueprint support",
                "VS 自带的 Unreal Engine 集成（启用增强的 Blueprint 支持）：每次打开解决方案都扫描 .uproject 里的全部蓝图，给 CodeLens 显示蓝图引用。" +
                "大项目要扫几分钟、占几百 MB。关掉后蓝图查找用 UEToVS 的蓝图索引。下次启动 VS 生效。",
                "VS's own Unreal Engine integration (enhanced Blueprint support): scans every Blueprint of the .uproject on each solution load to show Blueprint references in CodeLens. " +
                "On a big project that takes minutes and hundreds of MB. When off, Blueprint lookups come from the UEToVS Blueprint index. Takes effect from the next VS start."),
            new VsUnrealFeature(UhtAnalysis, "vsFeatures.uhtAnalysis", "关闭 VS 的 UHT 代码分析", "Turn off VS's UHT code analysis",
                "VS 自带的 Unreal Header Tool 代码分析：打开或保存头文件时在后台跑 UHT 检查 UCLASS / UPROPERTY 等宏，每次都要启动 UHT 进程。" +
                "关掉后反射宏检查由 UEToVS 的项目检查提供。",
                "VS's own Unreal Header Tool code analysis: runs UHT in the background on opened or saved headers to check UCLASS / UPROPERTY macros, starting a UHT process each time. " +
                "When off, reflection macro checks come from UEToVS's project inspection."),
            new VsUnrealFeature(CodeLens, "vsFeatures.codeLens", "关闭 VS 的 CodeLens", "Turn off VS's CodeLens",
                "代码上方的引用数、蓝图引用、提交记录等提示（所有语言）。C++ 引用数靠语义索引、蓝图引用靠 VS 的蓝图支持，这两项关掉后 CodeLens 基本没有内容，" +
                "开着还会给每个可见函数排查询、占用 CodeLens 服务进程。UEToVS 不依赖它。",
                "The reference counts, Blueprint references and commit hints above code (all languages). C++ counts need the semantic index and Blueprint references need VS's " +
                "Blueprint support, so with those off CodeLens has almost nothing to show, yet it still queues a lookup for every visible function and keeps its service process busy. UEToVS does not use it."),
        };

        private static CppPropertyCandidate Json(string key) => new CppPropertyCandidate(JsonPage, key, false);

        private static CppPropertyCandidate Dte(string name, bool value) => new CppPropertyCandidate(CppIntelliSenseSettings.SpecificPage, name, value);

        private static CppSettingDefinition Def(string key, string feature, string description, params CppPropertyCandidate[] candidates) =>
            new CppSettingDefinition(key, description, CppSettingGate.VsFeature, candidates) { Feature = feature };

        /// <summary>The settings, in write order (DTE ones first, settings.json ones after).</summary>
        public static IReadOnlyList<CppSettingDefinition> Table { get; } = new[]
        {
            // "symbolIndex" key kept from v0.5.5 so older backups and logs line up.
            Def(CppIntelliSenseSettings.KeySymbolIndex, SemanticIndex, "Disable whole-codebase semantic index",
                Dte("EnableSymbolIndex", false), Dte("EnableWholeCodebaseSemanticIndex", false), Json(JsonSemanticIndex)),
            Def("codeLensReferences", SemanticIndex, "Disable C++ CodeLens references (semantic index)", Json(JsonCodeLensReferences)),
            Def("codeLensReferenceFields", SemanticIndex, "Disable C++ CodeLens references on fields (semantic index)", Json(JsonCodeLensFields)),
            Def("vsBlueprints", Blueprints, "Disable VS Unreal Engine Blueprint support", Json(JsonBlueprints)),
            Def("vsUhtAnalysis", UhtAnalysis, "Disable VS Unreal Header Tool code analysis", Json(JsonUhtAnalysis)),
            Def("vsCodeLens", CodeLens, "Disable CodeLens (all languages)", Json(JsonCodeLens)),
        };

        /// <summary>
        /// Values VS uses when a key is absent from settings.json (VS only writes keys that differ from the default). Writing
        /// the default value removes the key again, so a restore leaves the file as it was.
        /// </summary>
        public static IReadOnlyDictionary<string, bool> JsonDefaults { get; } = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase)
        {
            [JsonSemanticIndex] = true,
            [JsonCodeLensReferences] = true,
            [JsonCodeLensFields] = true,
            [JsonBlueprints] = true,
            [JsonUhtAnalysis] = true,
            [JsonCodeLens] = true,
        };

        public static VsUnrealFeature ById(string id) => Features.FirstOrDefault(f => string.Equals(f.Id, id, StringComparison.Ordinal));
    }
}
