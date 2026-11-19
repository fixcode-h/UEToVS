using UEToVS.Core.Localization;

namespace UEToVS.Host.Logic.Localization
{
    /// <summary>Action ids of the v0.4.5 编辑体验 features (commands 0x0180–0x019F dispatch to them; see CommandCatalog).</summary>
    public static class EditorFeatureIds
    {
        public const string SurroundWith = "uetovs.edit.surroundWith";
        public const string ToggleInlayHints = "uetovs.edit.toggleInlayHints";
        public const string ShowHashtags = "uetovs.edit.hashtags";
        public const string PasteFromHistory = "uetovs.edit.pasteFromHistory";
        public const string ToggleEngineReadOnly = "uetovs.edit.toggleEngineReadOnly";

        /// <summary>Alt+Enter fixes (context actions; argument = the replacement / word).</summary>
        public const string ReplaceDeprecated = "uetovs.edit.replaceDeprecated";
        public const string FixNaming = "uetovs.edit.fixNaming";
    }

    /// <summary>Texts of the v0.4.5 编辑体验 features (Surround With, inlay hints, deprecated API, naming, step filter, inline rename, clipboard history, #tags).</summary>
    public static class EditorFeatureStrings
    {
        // ---- commands
        public static string CmdSurroundWith => Loc.T("用代码块包围…", "Surround With...");
        public static string CmdToggleInlayHints => Loc.T("显示 / 隐藏内嵌提示", "Show / Hide Inlay Hints");
        public static string CmdShowHashtags => Loc.T("#tag 注释列表…", "#tag Comments...");
        public static string CmdPasteFromHistory => Loc.T("从剪贴板历史粘贴…", "Paste from Clipboard History...");

        public static string CmdToggleEngineReadOnly => Loc.T("引擎文件：启用编辑 / 恢复只读", "Engine File: Enable Editing / Make Read-Only");

        public static readonly string[] KwEngineReadOnly = { "engine", "read-only", "readonly", "lock", "unlock", "引擎", "只读", "锁定", "解锁", "启用编辑", "修改" };
        public static readonly string[] KwSurround = { "surround", "wrap", "包裹", "包围", "if", "#if", "region", "IsValid", "SCOPE_CYCLE_COUNTER" };
        public static readonly string[] KwInlay = { "inlay", "hints", "内嵌提示", "参数名", "parameter names", "auto" };
        public static readonly string[] KwHashtags = { "hashtag", "tag", "#tag", "书签", "todo", "bookmark" };
        public static readonly string[] KwClipboard = { "clipboard", "paste", "history", "剪贴板", "粘贴", "历史", "paste multiple" };

        // ---- v0.9.4 引擎代码只读（编辑器顶部提示条 + 状态栏）
        public static string EngineReadOnlyMessage => Loc.T("此文件属于引擎代码，已设为只读", "This file belongs to the engine and is read-only");
        public static string EngineReadOnlyDetail => Loc.T("防止误改引擎源码", "to protect the engine source from accidental edits");
        public static string EngineReadOnlyEnable => Loc.T("启用编辑", "Enable Editing");
        public static string EngineReadOnlyEnableTip => Loc.T("本次 VS 会话内允许修改这个文件", "Allow editing this file for the rest of the VS session");
        public static string EngineReadOnlySettings => Loc.T("设置", "Settings");
        public static string EngineReadOnlySettingsTip => Loc.T("设置 > 编辑 > 引擎代码", "Settings > Editing > Engine code");
        public static string EngineReadOnlyTyped => Loc.T("引擎代码是只读的：点编辑器顶部的“启用编辑”（Ctrl+Shift+Alt+U, E）后再改", "Engine code is read-only: click \"Enable Editing\" at the top of the editor (Ctrl+Shift+Alt+U, E) first");
        public static string EngineReadOnlyUnlocked => Loc.T("已允许修改这个引擎文件（本次 VS 会话内有效）", "Editing enabled for this engine file (for this VS session)");
        public static string EngineReadOnlyLocked => Loc.T("这个引擎文件已恢复只读", "This engine file is read-only again");
        public static string EngineReadOnlyNotEngine => Loc.T("当前文件不是引擎代码，不需要解锁", "The current file is not engine code; nothing to unlock");
        public static string EngineReadOnlyOff => Loc.T("引擎代码只读已在设置中关闭（设置 > 编辑 > 引擎代码）", "Read-only engine code is turned off in the settings (Settings > Editing > Engine code)");

        // ---- Surround With
        public static string SurroundDisabled => Loc.T("Surround With 已在设置中关闭", "Surround With is turned off in the settings");
        public static string SurroundReadOnly => Loc.T("文件是只读的", "The file is read-only");
        public static string SurroundDone(string title, int lines) => Loc.F("已包裹 {1} 行：{0}", "Wrapped {1} line(s): {0}", title, lines);

        // ---- inlay hints
        public static string InlayOn => Loc.T("内嵌提示：已显示", "Inlay hints: shown");
        public static string InlayOff => Loc.T("内嵌提示：已隐藏（再按一次显示）", "Inlay hints: hidden (press again to show)");
        public static string InlayAllOff => Loc.T("内嵌提示在设置中都已关闭（设置 > 补全与提示 > 内嵌提示）", "Inlay hints are off in the settings (Settings > Completion & Hints > Inlay hints)");

        // ---- deprecated API (UE0011)
        public static string Deprecated(string symbol, string version, string message)
        {
            string v = string.IsNullOrEmpty(version) ? string.Empty : Loc.F("（{0} 起）", " (since {0})", version);
            string m = string.IsNullOrEmpty(message) ? string.Empty : Loc.T("：", ": ") + message;
            return Loc.F("“{0}”已弃用{1}{2}", "'{0}' is deprecated{1}{2}", symbol, v, m);
        }

        public static string DeprecatedQuickInfo(string version, string message)
        {
            string head = string.IsNullOrEmpty(version) ? Loc.T("已弃用", "Deprecated") : Loc.F("已弃用（{0}）", "Deprecated ({0})", version);
            return string.IsNullOrEmpty(message) ? head : head + Loc.T("：", ": ") + message;
        }

        public static string ReplaceDeprecated(string oldName, string newName) => Loc.F("替换为 {1}（{0} 已弃用）", "Replace with {1} ({0} is deprecated)", oldName, newName);
        public static string ReplacedDeprecated(string oldName, string newName) => Loc.F("已把 {0} 替换为 {1}", "Replaced {0} with {1}", oldName, newName);
        public static string GroupFix => Loc.T("修复", "Fix");

        // ---- naming (UE0012)
        public static string NamingRule(string rule)
        {
            switch (rule)
            {
                case "A": return Loc.T("Actor 派生类以 A 开头", "Actor subclasses start with A");
                case "U": return Loc.T("UObject 派生类以 U 开头", "UObject subclasses start with U");
                case "F": return Loc.T("结构体和普通类以 F 开头", "Structs and plain classes start with F");
                case "E": return Loc.T("枚举以 E 开头", "Enums start with E");
                case "T": return Loc.T("模板以 T 开头", "Templates start with T");
                case "I": return Loc.T("接口以 I 开头", "Interfaces start with I");
                case "b": return Loc.T("bool 成员以 b 开头", "bool members start with b");
                default: return rule ?? string.Empty;
            }
        }

        public static string Naming(string name, string expected, string rule) =>
            Loc.F("UE 命名规范：{2}，建议改为 {1}", "UE naming convention: {2}; consider {1}", name, expected, NamingRule(rule));

        public static string FixNaming(string name, string expected) => Loc.F("重命名为 {1}（UE 命名规范）", "Rename to {1} (UE naming convention)", name, expected);

        // ---- step filter
        public static string StepFilterDeployed(string path) => Loc.F("已写入调试 Step Filter：{0}", "Debug step filter written: {0}", path);

        // ---- inline rename
        public static string InlineRenameHint => Loc.T("就地重命名：输入新名称，Enter 预览全工程修改，Esc 取消", "Inline rename: type the new name, Enter previews the project-wide change, Esc cancels");
        public static string InlineRenameNoOccurrences => Loc.T("当前文件里没有找到这个符号的其他出现处，打开预览窗口", "No occurrences found in this file; opening the preview dialog");
        public static string InlineRenameInvalid(string name) => Loc.F("“{0}”不是合法的标识符", "'{0}' is not a valid identifier", name);

        // ---- clipboard history
        public static string ClipboardEmpty => Loc.T("剪贴板历史是空的（在编辑器里复制或剪切后会记录在这里）", "The clipboard history is empty (text copied or cut in editors shows up here)");
        public static string ClipboardDisabled => Loc.T("多重剪贴板已在设置中关闭", "Clipboard history is turned off in the settings");
        public static string ClipboardEntry(int lines, int chars) => Loc.F("{0} 行 · {1} 字符", "{0} line(s) · {1} chars", lines, chars);

        // ---- #tags
        public static string HashtagsEmpty => Loc.T("已打开的文件的注释里没有 #tag（例如 // #todo 修复）", "No #tags in the comments of the open files (e.g. // #todo fix)");
        public static string HashtagsDisabled => Loc.T("注释 #tag 书签已在设置中关闭", "Comment #tag bookmarks are turned off in the settings");
        public static string HashtagGroup(string tag, int count) => Loc.F("#{0}（{1}）", "#{0} ({1})", tag, count);
    }
}
