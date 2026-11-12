using System.Collections.Generic;
using System.Linq;
using UEToVS.Core.Localization;
using UEToVS.Host.Logic.Commands;
using UEToVS.Host.Logic.Localization;

namespace UEToVS.Host.Logic.IntelliSense
{
    public enum TakeoverFeatureState
    {
        /// <summary>Does not work once VS's C++ IntelliSense / browse database are off; use the UEToVS command instead.</summary>
        Unavailable = 0,
        /// <summary>The same key / gesture keeps working, served by UEToVS.</summary>
        Replaced = 1,
        /// <summary>Works as before (does not depend on VS's C++ IntelliSense).</summary>
        Available = 2,
        /// <summary>Not verified yet: to be confirmed in VS.</summary>
        Unknown = 3,
    }

    /// <summary>One VS feature and what the IntelliSense takeover does to it.</summary>
    public sealed class TakeoverFeature
    {
        public string FeatureZh { get; set; }
        public string FeatureEn { get; set; }

        /// <summary>VS command (DTE name) and its default key, for the log and the tooltip.</summary>
        public string VsCommand { get; set; }
        public string VsKeys { get; set; }

        public TakeoverFeatureState State { get; set; }

        /// <summary>UEToVS commands that replace it (CommandCatalog ids), in order.</summary>
        public int[] Replacements { get; set; } = new int[0];

        /// <summary>Replacement without a command of its own (hover, typing…), or a note.</summary>
        public string NoteZh { get; set; }
        public string NoteEn { get; set; }

        public string Feature(bool english) => english ? FeatureEn : FeatureZh;
        public string Note(bool english) => english ? NoteEn : NoteZh;
    }

    /// <summary>
    /// v0.4.5: which VS features stop working after the takeover (VS's C++ IntelliSense and browse database off) and the
    /// UEToVS command to use instead. Shown as a table in the settings window under "C++ IntelliSense"; pure data.
    /// Rows marked <see cref="TakeoverFeatureState.Unknown"/> have not been verified in VS yet.
    /// </summary>
    public static class TakeoverFeatures
    {
        /// <summary>Info id of the settings row that shows the table (SettingsWindow renders it, no InfoText).</summary>
        public const string SettingsInfoId = "settings.intelliSense.featureMatrix";

        private static TakeoverFeature F(string zh, string en, string vsCommand, string vsKeys, TakeoverFeatureState state, int[] replacements,
                                         string noteZh = null, string noteEn = null) =>
            new TakeoverFeature
            {
                FeatureZh = zh, FeatureEn = en, VsCommand = vsCommand, VsKeys = vsKeys, State = state, Replacements = replacements ?? new int[0],
                NoteZh = noteZh, NoteEn = noteEn,
            };

        private const TakeoverFeatureState Off = TakeoverFeatureState.Unavailable, Ours = TakeoverFeatureState.Replaced;

        public static IReadOnlyList<TakeoverFeature> All { get; } = new[]
        {
            F("调用层次", "Call Hierarchy", "View.CallHierarchy", "Ctrl+K, Ctrl+T", Off, new[] { CommandCatalog.CallHierarchy }),
            F("速览定义", "Peek Definition", "Edit.PeekDefinition", "Alt+F12", Ours, new int[0],
                "Alt+F12 照常使用，显示 UEToVS 找到的定义", "Alt+F12 keeps working and shows the definitions UEToVS finds"),
            F("转到定义", "Go To Definition", "Edit.GoToDefinition", "F12", Ours, new[] { CommandCatalog.GoToDefinition },
                "F12 照常使用（由 UEToVS 处理）", "F12 keeps working (handled by UEToVS)"),
            F("转到实现", "Go To Implementation", "Edit.GoToImplementation", "Ctrl+F12", Off, new[] { CommandCatalog.GoToImplementation }),
            F("查找所有引用", "Find All References", "Edit.FindAllReferences", "Shift+F12", Off, new[] { CommandCatalog.FindReferences }),
            F("快速信息", "Quick Info", "Edit.QuickInfo", "Ctrl+K, Ctrl+I", Ours, new int[0],
                "悬停提示由 UEToVS 提供（含 UE 反射、宏展开、GENERATED_BODY 生成代码）", "Hover tooltips come from UEToVS (UE reflection, macro expansion, GENERATED_BODY code)"),
            F("参数信息", "Parameter Info", "Edit.ParameterInfo", "Ctrl+Shift+Space", Ours, new int[0],
                "输入 ( 或 , 时由 UEToVS 显示", "Shown by UEToVS when typing ( or ,"),
            F("成员列表 / 自动补全", "List Members / Completion", "Edit.ListMembers", "Ctrl+J", Ours, new int[0],
                "补全列表由 UEToVS 提供（UE 感知）", "The completion list comes from UEToVS (UE-aware)"),
            F("类视图", "Class View", "View.ClassView", "Ctrl+Shift+C", Off, new[] { CommandCatalog.TypeHierarchy, CommandCatalog.FileStructure },
                "依赖 VS 的 C++ 浏览数据库", "Needs VS's C++ browse database"),
            F("对象浏览器", "Object Browser", "View.ObjectBrowser", "Ctrl+Alt+J", Off, new[] { CommandCatalog.GoToSymbol, CommandCatalog.ListTypeMembers },
                "C++ 部分依赖 VS 的浏览数据库", "Its C++ part needs VS's browse database"),
            F("导航栏（类型 / 成员下拉框）", "Navigation bar (type / member drop-downs)", null, null, Ours, new int[0],
                "由 UEToVS 的导航栏代替", "Replaced by the UEToVS navigation bar"),
            F("重命名（C++ 重构）", "Rename (C++ refactoring)", "Refactor.Rename", "Ctrl+R, Ctrl+R", Off, new[] { CommandCatalog.Rename }),
            F("提取函数（C++ 重构）", "Extract Function (C++ refactoring)", "Refactor.ExtractFunction", "Ctrl+R, Ctrl+M", Off, new int[0],
                "暂无替代", "No replacement yet"),
            F("创建声明 / 定义（快速操作）", "Create Declaration / Definition (quick actions)", "View.QuickActions", "Ctrl+.", Off, new[] { CommandCatalog.QuickActions },
                "Alt+Enter：生成实现 / 声明、重写虚函数、添加 #include", "Alt+Enter: generate implementation / declaration, override, add #include"),
            F("代码段（插入代码段）", "Code Snippets (Insert Snippet)", "Edit.InsertSnippet", "Ctrl+K, Ctrl+X", TakeoverFeatureState.Unknown, new int[0],
                "UEToVS 另有 UE 代码模板和 Postfix 模板", "UEToVS also has UE live templates and postfix templates"),
            F("切换头文件 / 源文件", "Toggle Header / Code File", "EditorContextMenus.CodeWindow.ToggleHeaderCodeFile", "Ctrl+K, Ctrl+O", TakeoverFeatureState.Unknown,
                new[] { CommandCatalog.ToggleHeaderSource }),
        };

        public static string StateText(TakeoverFeatureState s)
        {
            switch (s)
            {
                case TakeoverFeatureState.Unavailable: return NavGapStrings.TakeoverUnavailable;
                case TakeoverFeatureState.Replaced: return NavGapStrings.TakeoverReplaced;
                case TakeoverFeatureState.Available: return NavGapStrings.TakeoverAvailable;
                default: return NavGapStrings.TakeoverConfirm;
            }
        }

        /// <summary>"查找引用（Shift+Alt+F）；…" plus the note; "暂无" when there is nothing.</summary>
        public static string ReplacementText(TakeoverFeature f, KeymapPresetId preset = KeymapPresetId.VisualAssist)
        {
            bool en = Loc.IsEnglish;
            var parts = new List<string>();
            foreach (var id in f.Replacements)
            {
                var c = CommandCatalog.ById(id);
                if (c == null) continue;
                string keys = c.KeysFor(preset);
                parts.Add(string.IsNullOrEmpty(keys) ? c.Title(en) : c.Title(en) + (en ? " (" + keys + ")" : "（" + keys + "）"));
            }
            string text = string.Join(en ? "; " : "；", parts);
            string note = f.Note(en);
            if (!string.IsNullOrEmpty(note)) text = text.Length == 0 ? note : text + (en ? " — " : " — ") + note;
            return text.Length == 0 ? NavGapStrings.TakeoverNone : text;
        }

        /// <summary>"调用层次（Ctrl+K, Ctrl+T）" — the VS side of a row.</summary>
        public static string FeatureText(TakeoverFeature f)
        {
            bool en = Loc.IsEnglish;
            string name = f.Feature(en);
            return string.IsNullOrEmpty(f.VsKeys) ? name : name + (en ? " (" + f.VsKeys + ")" : "（" + f.VsKeys + "）");
        }

        /// <summary>Rows that tell the user to switch to a UEToVS command.</summary>
        public static IEnumerable<TakeoverFeature> Unavailable => All.Where(f => f.State == TakeoverFeatureState.Unavailable);
    }
}
