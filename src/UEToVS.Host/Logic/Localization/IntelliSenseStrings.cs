using System;
using System.Collections.Generic;
using UEToVS.Core.Localization;

namespace UEToVS.Host.Logic.Localization
{
    /// <summary>
    /// User-visible strings of the M3 IntelliSense features (completion, Quick Info, parameter info, semantic colouring,
    /// VA colour import). Kept apart from <see cref="Strings"/> so parallel work merges cleanly. Pure.
    /// </summary>
    public static class IntelliSenseStrings
    {
        // ---- commands (0x0600–0x06FF; not on the top-level menu) ----
        public static string CmdImportVaColors => Loc.T("导入 Visual Assist 配色…", "Import Visual Assist Colors...");
        public static string CmdToggleSemanticColoring => Loc.T("切换语义着色", "Toggle Semantic Coloring");
        public static string CmdResetSemanticColors => Loc.T("恢复默认语义配色", "Reset Semantic Colors to Defaults");

        public static readonly string[] KwColors = { "color", "colour", "visual assist", "va", "vssettings", "theme", "fonts and colors", "配色", "颜色", "着色", "主题", "导入" };
        public static readonly string[] KwSemanticColoring = { "semantic", "coloring", "highlighting", "syntax", "语义", "着色", "高亮" };

        // ---- status bar / banners ----
        public static string SemanticColoringOn => Loc.T("UEToVS：语义着色已开启", "UEToVS: semantic coloring is on");
        public static string SemanticColoringOff => Loc.T("UEToVS：语义着色已关闭", "UEToVS: semantic coloring is off");
        public static string SemanticColoringNeedsSettings => Loc.T("UEToVS：设置服务尚未就绪，无法切换语义着色", "UEToVS: settings are not ready yet; semantic coloring cannot be toggled");
        public static string ImportVaDialogTitle => Loc.T("选择 Visual Assist 导出的 .vssettings 文件", "Choose a .vssettings file exported with Visual Assist");
        public static string ImportVaFilter => Loc.T("Visual Studio 设置 (*.vssettings)|*.vssettings|所有文件 (*.*)|*.*", "Visual Studio settings (*.vssettings)|*.vssettings|All files (*.*)|*.*");
        public static string ImportVaNoItems => Loc.T("该文件中没有 Visual Assist 的颜色项（VA Class / VA Method / VA Variable / VA Macro …）。请在装有 VA 的 VS 中通过 工具 > 导入和导出设置 导出“字体和颜色”。",
                                                      "The file has no Visual Assist color items (VA Class / VA Method / VA Variable / VA Macro ...). Export \"Fonts and Colors\" via Tools > Import and Export Settings in a VS with VA installed.");
        public static string ImportVaFailed(string reason) => Loc.F("导入 VA 配色失败：{0}（详见日志）", "Importing VA colors failed: {0} (see the log)", reason);
        public static string ImportVaDone(int applied, int vaItems) => Loc.F("已从 Visual Assist 配色导入 {0} 个 UEToVS 着色项（文件中有 {1} 个 VA 项）。可在 工具 > 选项 > 字体和颜色 中继续调整。",
                                                                           "Imported {0} UEToVS color items from {1} Visual Assist items. Fine-tune them in Tools > Options > Fonts and Colors.", applied, vaItems);
        public static string ResetColorsDone => Loc.T("UEToVS 语义配色已恢复为当前主题的默认值。", "UEToVS semantic colors were reset to the current theme's defaults.");
        public static string CompletionEngineUnavailable => Loc.T("UEToVS：引擎不可用，暂无补全", "UEToVS: the engine is unavailable; no completion for now");

        // ---- completion ----
        public static string InheritedFrom(string container) => Loc.F("继承自 {0}", "inherited from {0}", container);
        public static string UeBadgeTooltip(string badge) => Loc.F("UE 反射：{0}", "UE reflection: {0}", badge);
        public static string AccessorFixTooltip(string replacement) => Loc.F("提交时把访问符改为 {0}", "The accessor becomes {0} on commit", replacement);

        // ---- Quick Info ----
        public static string Overloads(int n) => Loc.F("(+{0} 个重载)", "(+{0} overloads)", n);
        public static string MacroExpansionHeader(int lines) => Loc.F("宏展开（{0} 行）", "Macro expansion ({0} lines)", lines);
        public static string MacroExpansionTitle => Loc.T("宏展开", "Macro expansion");

        public static string MacroExpansionCopy => Loc.T("复制最终展开结果", "Copy the final expansion");

        public static string MacroExpansionCopyLink => Loc.T("复制", "Copy");

        public static string MacroExpansionCopied => Loc.T("已复制", "Copied");

        public static string MacroStepLabel(IntelliSense.MacroStepKind kind)
        {
            switch (kind)
            {
                case IntelliSense.MacroStepKind.Full: return Loc.T("完全展开", "Fully expanded");
                case IntelliSense.MacroStepKind.Folded: return Loc.T("计算常量后", "Constants computed");
                default: return Loc.T("展开一层", "One level");
            }
        }

        public static string MacroExpansionTruncated => Loc.T("…（已截断）", "... (truncated)");
        public static string HeuristicNote => Loc.T("（仅按名称匹配，可能不准确）", "(matched by name only; may be inaccurate)");
        public static string ReflectionHeader => Loc.T("UE 反射", "UE reflection");

        /// <summary>Readable tag for well-known reflection specifiers; null for the rest (shown verbatim in the summary).</summary>
        public static string SpecifierTag(string specifier)
        {
            if (string.IsNullOrEmpty(specifier)) return null;
            switch (specifier)
            {
                case "BlueprintCallable": return Loc.T("蓝图可调用", "Blueprint callable");
                case "BlueprintPure": return Loc.T("蓝图纯函数", "Blueprint pure");
                case "BlueprintImplementableEvent": return Loc.T("蓝图实现事件", "Blueprint implementable event");
                case "BlueprintNativeEvent": return Loc.T("蓝图原生事件", "Blueprint native event");
                case "BlueprintReadWrite": return Loc.T("蓝图可读写", "Blueprint read/write");
                case "BlueprintReadOnly": return Loc.T("蓝图只读", "Blueprint read-only");
                case "BlueprintAssignable": return Loc.T("蓝图可绑定", "Blueprint assignable");
                case "BlueprintType": return Loc.T("蓝图类型", "Blueprint type");
                case "Blueprintable": return Loc.T("可被蓝图继承", "Blueprintable");
                case "EditAnywhere": return Loc.T("可编辑（任意处）", "Editable anywhere");
                case "EditDefaultsOnly": return Loc.T("仅默认值可编辑", "Editable on defaults only");
                case "EditInstanceOnly": return Loc.T("仅实例可编辑", "Editable on instances only");
                case "VisibleAnywhere": return Loc.T("可见（任意处）", "Visible anywhere");
                case "VisibleDefaultsOnly": return Loc.T("仅默认值可见", "Visible on defaults only");
                case "VisibleInstanceOnly": return Loc.T("仅实例可见", "Visible on instances only");
                case "Replicated": return Loc.T("网络复制", "Replicated");
                case "ReplicatedUsing": return Loc.T("网络复制（带回调）", "Replicated with notify");
                case "Server": return Loc.T("服务器 RPC", "Server RPC");
                case "Client": return Loc.T("客户端 RPC", "Client RPC");
                case "NetMulticast": return Loc.T("多播 RPC", "Multicast RPC");
                case "Reliable": return Loc.T("可靠", "Reliable");
                case "Unreliable": return Loc.T("不可靠", "Unreliable");
                case "Exec": return Loc.T("控制台命令", "Console command");
                case "Config": return Loc.T("配置项", "Config");
                case "Transient": return Loc.T("瞬态", "Transient");
                case "SaveGame": return Loc.T("存档", "SaveGame");
                case "Abstract": return Loc.T("抽象", "Abstract");
                default: return null;
            }
        }

        // ---- first takeover (M3: on by default) ----
        public static string TakeoverDefaultOnNotice => Loc.T(
            "UEToVS 现已提供完整的 C++ 感知（补全、快速信息、参数信息、语义着色、跳转），默认接管并关闭 VS 自带 C++ IntelliSense（原设置已备份）。",
            "UEToVS now provides full C++ code insight (completion, Quick Info, parameter info, semantic coloring, navigation) and takes over from Visual Studio's C++ IntelliSense by default (your settings were backed up).");
    }
}
