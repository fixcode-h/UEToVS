using System;
using System.Collections.Generic;
using UEToVS.UI.Logic;

namespace UEToVS.Host.Logic.Settings
{
    /// <summary>
    /// Which page and which card of the settings window every setting sits on (v0.7.1). The descriptors in
    /// <see cref="HostSettingsSchema.Build"/> say what a setting is; this table alone says where it is shown, in order, so the
    /// layout can be rearranged in one place. Pages are grouped by what the user is trying to do (take over VS, write code,
    /// find code, the UE project, the machine), not by the module that implements them.
    /// Rows not listed here keep the page and card of their descriptor (外观 > 字体 and anything added later); the unit
    /// tests require every other row to be listed exactly once.
    /// </summary>
    public static class HostSettingsPages
    {
        /// <summary>One card: page, card title (null = keep the descriptor's own card), keys in display order.</summary>
        public sealed class Card
        {
            public Card(string category, string groupZh, string groupEn, params string[] keys)
            {
                Category = category;
                GroupZh = groupZh;
                GroupEn = groupEn;
                Keys = keys;
            }

            public string Category { get; }
            public string GroupZh { get; }
            public string GroupEn { get; }
            public IReadOnlyList<string> Keys { get; }
        }

        private const string General = HostSettingsSchema.General;
        private const string Takeover = HostSettingsSchema.Takeover;
        private const string Completion = HostSettingsSchema.IntelliSense;
        private const string Highlighting = HostSettingsSchema.Highlighting;
        private const string Editing = HostSettingsSchema.Editing;
        private const string Navigation = HostSettingsSchema.Navigation;
        private const string Translation = HostSettingsSchema.Translation;
        private const string Indexing = HostSettingsSchema.Indexing;
        private const string Unreal = HostSettingsSchema.Unreal;
        private const string HordePage = UEToVS.Host.Logic.Horde.HordeSettingsSchema.Category;
        private const string Agent = HostSettingsSchema.Agent;
        private const string Diagnostics = HostSettingsSchema.Logging;
        private const string Updates = HostSettingsSchema.Updates;

        public static IReadOnlyList<Card> Cards { get; } = new[]
        {
            // ---- 常规：UEToVS 自己的入口和退路
            new Card(General, "工具栏", "Toolbar", "toolbar.show", "toolbar.button.searchEverywhere", "toolbar.button.goToDefinition",
                     "toolbar.button.findReferences", "toolbar.button.generateProjectFiles", "toolbar.button.performance"),
            new Card(General, "双击 Shift", "Double Shift", "keyboard.doubleShift", "keyboard.doubleShiftIntervalMs", "keyboard.doubleShiftLeftOnly"),
            new Card(General, "禁用或卸载前", "Before disabling or uninstalling", "vs.restoreAll"),

            // ---- 接管 VS：UEToVS 关掉 / 改掉的 VS 自带功能，都在这一页
            new Card(Takeover, "C++ IntelliSense", "C++ IntelliSense", "intelliSense.status", "intelliSense.mode", "intelliSense.keepVsErrors", "intelliSense.toggle"),
            new Card(Takeover, "VS 自带的 Unreal Engine 功能", "VS's own Unreal Engine features", "vs.unrealIntegration",
                     "vsFeatures.semanticIndex", "vsFeatures.blueprints", "vsFeatures.uhtAnalysis", "vsFeatures.codeLens"),
            new Card(Takeover, "调试器", "Debugger", "vsFeatures.adjacentSymbols"),
            new Card(Takeover, "接管后 VS 自带功能", "VS features after the takeover", "intelliSense.featureMatrix"),

            // ---- 补全与提示：写代码时弹出来的东西
            new Card(Completion, "代码补全", "Completion", "completion.enabled", "completion.scope", "completion.onIdentifier",
                     "completion.accessorFix", "completion.maxResults"),
            new Card(Completion, "悬停信息与参数信息", "Quick Info & parameter info", "quickInfo.enabled", "signatureHelp.enabled"),
            new Card(Completion, "内嵌提示", "Inlay hints", "inlay.parameterNames", "inlay.autoTypes", "inlay.codeVision"),
            new Card(Completion, "代码模板", "Templates", "snippets.enabled", "postfix.enabled"),
            new Card(Completion, "模板类型推导", "Template type deduction", "templates.enabled", "templates.cacheCapacity"),

            // ---- 着色与检查：代码上画出来的东西
            new Card(Highlighting, "语义着色", "Semantic coloring", "coloring.enabled", "coloring.overrides", "coloring.marginLines",
                     "coloring.importVa", "coloring.reset"),
            new Card(Highlighting, "引用高亮", "Reference highlighting", "nav.highlightReferences", "nav.highlightReadColor", "nav.highlightWriteColor"),
            new Card(Highlighting, "代码检查", "Code checks", "diagnostics.enabled", "diagnostics.deprecated", "diagnostics.naming"),
            new Card(Highlighting, "编译错误", "Build errors", "build.squiggles", "errorList.enabled", "errorList.includeBuild"),

            // ---- 编辑
            new Card(Editing, "自动保存", "Auto-save", "editor.autoSave", "editor.autoSaveIdleSeconds", "editor.gitTabMarker"),
            new Card(Editing, "引擎代码", "Engine code", "editor.engineReadOnly"),
            new Card(Editing, "重命名", "Rename", "editor.renameMode"),
            new Card(Editing, "用代码块包围", "Surround With", "editor.surroundWith"),
            new Card(Editing, "剪贴板历史", "Clipboard history", "editor.clipboardHistory", "editor.clipboardHistorySize"),
            new Card(Editing, "#tag 注释", "#tag comments", "editor.hashtags"),

            // ---- 导航
            new Card(Navigation, "跳转", "Go to", "nav.ctrlClick", "nav.navigationBar", "nav.syncSolutionExplorer"),
            new Card(Navigation, "导航历史", "Navigation history", "nav.mouseButtons", "nav.lineThreshold", "nav.historyCapacity"),
            new Card(Navigation, "全局搜索", "Search Everywhere", "search.maxResultsPerCategory"),

            // ---- 注释翻译
            new Card(Translation, "注释翻译", "Comment translation", "translate.enabled", "translate.display", "translate.target",
                     "translate.provider", "translate.googleUrl", "translate.clearCache"),

            // ---- 索引
            new Card(Indexing, "索引", "Indexing", "indexing.status", "indexing.cppThreads", "indexing.blueprintThreads",
                     "indexing.duringBuild", "indexing.includeEngine"),
            new Card(Indexing, "引擎索引", "Engine index", "indexing.shareEngineIndex", "indexing.engineIndexPath"),
            new Card(Indexing, "重建索引", "Rebuild", "indexing.rebuildProject", "indexing.rebuildEngine", "indexing.rebuildBlueprint"),

            // ---- Unreal Engine：和 UE 项目、UE 编辑器相关的
            new Card(Unreal, "引擎位置", "Engine location", "engine.unrealEngineRoot"),
            new Card(Unreal, "模块依赖", "Module dependencies", "unreal.checkIncludesOnSave"),
            new Card(Unreal, "Live Coding", "Live Coding", "link.ueLiveCodingManual"),
            new Card(Unreal, "调试", "Debugging", "debug.stepFilter"),
            new Card(Unreal, "全项目检查", "Whole-project inspection", "inspection.enabled", "inspection.includePlugins", "inspection.run", "inspection.results"),
            new Card(Unreal, "引擎升级助手", "Engine upgrade assistant", "upgrade.enabled", "upgrade.run"),

            // ---- 联合编译（卡片沿用 HordeSettingsSchema 里的分组）
            new Card(HordePage, null, null, "horde.status", "horde.apply", "horde.openConfig", "horde.restore",
                     "horde.enabled", "horde.server", "horde.pool", "horde.maxCores", "horde.linkRemote", "horde.printSummary",
                     "horde.localCores", "horde.agentPolicy", "horde.installPath",
                     "horde.target", "horde.disableRemote", "horde.forceRemote", "horde.allowRetry", "horde.launchVisualizer",
                     "horde.storeObjCompressed", "horde.cluster", "horde.zone", "horde.encryption", "horde.connectionMode",
                     "horde.rootDir", "horde.storeCapacityGb"),

            // ---- Agent / API（API 和模型是自定义页面，生成的行只有 MCP）
            new Card(Agent, "MCP（给 AI Agent 用）", "MCP (for AI agents)", "mcp.enable", "mcp.debugControl", "mcp.debugKeepFocus", "mcp.writeConfig"),

            // ---- 诊断：UEToVS 自己的进程、日志和性能
            new Card(Diagnostics, "UEToVS 代码引擎", "UEToVS code engine", "engine.path", "engine.requestTimeoutSeconds", "engine.restart"),
            new Card(Diagnostics, "日志", "Logs", "log.level", "log.retentionDays", "log.maxTotalMB", "log.directory", "log.openFolder", "log.openCurrent"),
            new Card(Diagnostics, "性能面板", "Performance panel", "perf.sampleSeconds", "perf.open"),
            new Card(Diagnostics, "反馈问题", "Reporting problems", "diagnostics.createPackage"),
            new Card(Diagnostics, "开发", "Development", "diagnostics.componentGallery"),

            // ---- 检查更新（最后一页）
            new Card(Updates, "更新", "Updates", "update.check"),
        };

        /// <summary>
        /// Moves every listed row to its page and card and puts the rows in table order (rows that are not listed keep
        /// their place after the listed ones of their page). Returns the keys listed but not found, for the log / tests.
        /// </summary>
        public static List<string> Apply(List<SettingDescriptor> list)
        {
            var byKey = new Dictionary<string, SettingDescriptor>(StringComparer.Ordinal);
            foreach (var d in list) byKey[d.Key] = d;
            var ordered = new List<SettingDescriptor>(list.Count);
            var placed = new HashSet<string>(StringComparer.Ordinal);
            var missing = new List<string>();
            foreach (var card in Cards)
            {
                foreach (var key in card.Keys)
                {
                    if (!byKey.TryGetValue(key, out var d)) { missing.Add(key); continue; }
                    if (!placed.Add(key)) continue;
                    d.Category = card.Category;
                    if (card.GroupZh != null)
                    {
                        d.GroupZh = card.GroupZh;
                        d.GroupEn = card.GroupEn;
                    }
                    ordered.Add(d);
                }
            }
            foreach (var d in list)
                if (!placed.Contains(d.Key)) ordered.Add(d);
            list.Clear();
            list.AddRange(ordered);
            return missing;
        }
    }
}
