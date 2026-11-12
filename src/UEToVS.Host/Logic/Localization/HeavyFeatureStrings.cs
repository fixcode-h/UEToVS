using UEToVS.Core.Localization;

namespace UEToVS.Host.Logic.Localization
{
    /// <summary>
    /// v0.4.5 heavy features: whole-project inspection, engine upgrade assistant, generic template instantiation (settings
    /// texts live in the schema). Chinese + English via <see cref="Loc.T"/>; separate file to ease merging.
    /// </summary>
    public static class HeavyFeatureStrings
    {
        // ---- commands / actions ----
        public static string CmdRunInspection => Loc.T("全项目 UE 规则检查", "Inspect Whole Project (UE Rules)");
        public static string CmdShowInspection => Loc.T("全项目检查结果", "Project Inspection Results");
        public static string CmdRunUpgrade => Loc.T("引擎升级助手：查找弃用 API", "Engine Upgrade Assistant: Find Deprecated APIs");
        public static string CmdShowUpgrade => Loc.T("引擎升级助手结果", "Engine Upgrade Assistant Results");
        public static string CmdCancelHeavyJob => Loc.T("取消全项目检查 / 升级扫描", "Cancel Project Inspection / Upgrade Scan");

        public static readonly string[] KwInspection = { "inspect", "inspection", "检查", "全项目", "规则", "rules", "UE0001", "GENERATED_BODY", "_Implementation", "code analysis", "分析" };
        public static readonly string[] KwUpgrade = { "upgrade", "升级", "deprecated", "弃用", "UE_DEPRECATED", "migrate", "迁移", "engine version", "引擎版本" };
        public static readonly string[] KwCancel = { "cancel", "取消", "stop", "停止", "inspection", "检查", "upgrade", "升级" };

        // ---- switches off / not ready ----
        public static string InspectionDisabled => Loc.T("全项目检查已在 UEToVS 设置 > 检查 中关闭", "Project inspection is switched off in UEToVS Settings > Inspections");
        public static string UpgradeDisabled => Loc.T("引擎升级助手已在 UEToVS 设置 > 检查 中关闭", "The engine upgrade assistant is switched off in UEToVS Settings > Inspections");
        public static string IndexNotReady => Loc.T("索引还没建好，完成后再运行", "The index is still being built; run it once indexing has finished");
        public static string EngineUnavailable => Loc.T("UEToVS 引擎未连接", "The UEToVS engine is not connected");
        public static string NothingRunning => Loc.T("没有正在运行的全项目检查或升级扫描", "No project inspection or upgrade scan is running");
        public static string NoResultsYet => Loc.T("还没有结果，先运行一次检查", "No results yet; run an inspection first");

        // ---- progress / status bar ----
        public static string InspectionProgress(int done, int total, int findings) =>
            Loc.F("全项目检查 {0}/{1} 个文件 · {2} 个问题", "Inspecting {0}/{1} files · {2} problems", done, total, findings);

        public static string InspectionDone(int files, int findings, double seconds) =>
            Loc.F("全项目检查完成：{0} 个文件，{1} 个问题，用时 {2:0.#} 秒", "Project inspection done: {0} files, {1} problems in {2:0.#} s", files, findings, seconds);

        public static string InspectionCancelled(int done, int total) => Loc.F("全项目检查已取消（{0}/{1}）", "Project inspection cancelled ({0}/{1})", done, total);
        public static string InspectionFailed(string error) => Loc.F("全项目检查失败：{0}", "Project inspection failed: {0}", error);

        public static string UpgradeProgress(int done, int total, int usages) =>
            Loc.F("升级助手 {0}/{1} 个弃用 API · {2} 处引用", "Upgrade assistant {0}/{1} deprecated APIs · {2} references", done, total, usages);

        public static string UpgradeDone(int apis, int usages) => Loc.F("升级助手：项目用到 {0} 个弃用 API，共 {1} 处", "Upgrade assistant: the project uses {0} deprecated APIs in {1} places", apis, usages);
        public static string UpgradeCancelled => Loc.T("升级扫描已取消", "Upgrade scan cancelled");
        public static string UpgradeFailed(string error) => Loc.F("升级扫描失败：{0}", "Upgrade scan failed: {0}", error);

        public static string UpgradeNoIndexData =>
            Loc.T("索引里还没有弃用信息（v0.4.5 之前建的索引会自动重建，完成后再试）", "The index has no deprecation data yet (indexes built before v0.4.5 are rebuilt automatically; try again afterwards)");

        // ---- result popups ----
        public static string InspectionTitle => Loc.T("全项目检查", "Project Inspection");
        public static string UpgradeTitle => Loc.T("引擎升级助手", "Engine Upgrade Assistant");
        public static string GroupByRule => Loc.T("按规则", "By rule");
        public static string GroupByFile => Loc.T("按文件", "By file");
        public static string FixableOnly => Loc.T("仅可修复", "Fixable only");
        public static string FixAll => Loc.T("批量修复…", "Fix all...");
        public static string ReplaceAll => Loc.T("批量替换…", "Replace all...");
        public static string Refresh => Loc.T("刷新", "Refresh");
        public static string FilterPlaceholder => Loc.T("筛选：文件 / 规则 / 文本", "Filter: file / rule / text");
        public static string Cancel => Loc.T("取消", "Cancel");
        public static string MoreResults(int shown, int total) => Loc.F("显示前 {0} 条，共 {1} 条（用筛选缩小范围）", "Showing {0} of {1} (filter to narrow down)", shown, total);

        public static string Summary(int findings, int files, int fixable) =>
            Loc.F("{0} 个问题 · {1} 个文件 · {2} 个可自动修复", "{0} problems · {1} files · {2} fixable", findings, files, fixable);

        public static string UpgradeSummary(int apis, int usages, int replaceable) =>
            Loc.F("{0} 个弃用 API · {1} 处引用 · {2} 处可自动替换", "{0} deprecated APIs · {1} references · {2} replaceable", apis, usages, replaceable);

        public static string RuleGroup(string code, string name, int count) => Loc.F("{0} {1}（{2}）", "{0} {1} ({2})", code, name, count);
        public static string VersionGroup(string version, int apis) =>
            string.IsNullOrEmpty(version) ? Loc.F("未标版本（{0}）", "No version ({0})", apis) : Loc.F("UE {0} 弃用（{1}）", "Deprecated in UE {0} ({1})", version, apis);

        public static string ReplaceWith(string replacement) => Loc.F("替换为 {0}", "Replace with {0}", replacement);
        public static string NoReplacement(string note)
        {
            switch (note)
            {
                case "no-suggestion": return Loc.T("弃用说明没有给出替代 API", "The deprecation message names no replacement");
                case "not-found": return Loc.T("索引里找不到说明中的替代 API", "The replacement named by the message is not in the index");
                case "signature": return Loc.T("替代 API 的参数个数不同，需要手动修改", "The replacement takes different parameters; change it by hand");
                case "kind": return Loc.T("替代 API 在别的类 / 命名空间里，需要手动修改", "The replacement lives in another class / namespace; change it by hand");
                default: return Loc.T("需要手动修改", "Change by hand");
            }
        }

        public static string NameOnly => Loc.T("仅名称匹配", "name match only");

        /// <summary>Short rule names for the group headers.</summary>
        public static string RuleName(string code)
        {
            switch (code)
            {
                case "UE0001": return Loc.T("未知成员", "Unknown member");
                case "UE0002": return Loc.T("缺少模块依赖", "Missing module dependency");
                case "UE0003": return Loc.T("缺少 GENERATED_BODY()", "Missing GENERATED_BODY()");
                case "UE0004": return Loc.T("手写了 UHT 生成的函数体", "Hand-written UHT-generated body");
                case "UE0005": return Loc.T("RepNotify 函数缺失", "Missing RepNotify function");
                case "UE0006": return Loc.T("复制属性未注册", "Replicated property not registered");
                case "UE0007": return Loc.T("未知 GameplayTag", "Unknown gameplay tag");
                case "UE0008": return Loc.T("未知资产路径", "Unknown asset path");
                case "UE0009": return Loc.T("UHT 不支持的属性类型", "Property type UHT rejects");
                case "UE0010": return Loc.T("UHT 不支持的参数类型", "Parameter type UHT rejects");
                default: return code ?? string.Empty;
            }
        }

        // ---- batch fix / replace ----
        public static string FixPreviewTitle(int count) => Loc.F("批量修复 {0} 处", "Fix {0} places", count);
        public static string ReplacePreviewTitle(int count) => Loc.F("批量替换 {0} 处", "Replace {0} places", count);
        public static string FixUndoName => Loc.T("UEToVS 批量修复", "UEToVS fix all");
        public static string ReplaceUndoName => Loc.T("UEToVS 替换弃用 API", "UEToVS replace deprecated APIs");
        public static string FixResult(int applied, int skipped, int files) =>
            Loc.F("已修改 {0} 处（{2} 个文件），跳过 {1} 处（文件已变化）", "Changed {0} places in {2} files, skipped {1} (file changed)", applied, skipped, files);

        public static string FixDescription(string code, string arg) =>
            code == "UE0004" ? Loc.F("重命名为 {0}_Implementation", "Rename to {0}_Implementation", arg) :
            code == "UE0003" ? Loc.T("插入 GENERATED_BODY()", "Insert GENERATED_BODY()") : code;
    }
}
