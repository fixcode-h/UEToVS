using System;
using System.Windows;
using UEToVS.Core.Localization;
using UEToVS.UI.Logic;

namespace UEToVS.UI
{
    /// <summary>
    /// Every user-visible string of the UI library (中文 / English via <see cref="Loc"/>, following the VS UI language).
    /// Components never hard-code text: they read these properties and re-read them on <see cref="Loc.LanguageChanged"/>
    /// through <see cref="Track"/>. Features pass their own (already localized) text into components.
    /// </summary>
    public static class Strings
    {
        public static string CommonClear => Loc.T("清除", "Clear");
        public static string CommonClose => Loc.T("关闭", "Close");
        public static string CommonOk => Loc.T("确定", "OK");
        public static string CommonCancel => Loc.T("取消", "Cancel");
        public static string CommonReset => Loc.T("恢复默认", "Reset to defaults");
        public static string CommonAuto => Loc.T("自动", "Auto");
        public static string CommonLoading => Loc.T("正在加载…", "Loading…");
        public static string CommonMore => Loc.T("更多", "More");
        // ---- path list / path input (settings: Lua script folders, engine paths)
        public static string PathAddFolder => Loc.T("添加文件夹…", "Add folder…");
        public static string PathBrowse => Loc.T("浏览…", "Browse…");
        public static string PathBrowseRow => Loc.T("选择其他文件夹…", "Pick another folder…");
        public static string PathRemove => Loc.T("删除", "Remove");
        public static string PathEditName => Loc.T("路径", "Path");
        public static string PathEditing => Loc.T("编辑中 · 回车确认", "Editing · Enter to confirm");
        public static string PathRelativeTag(object baseName) => Loc.F("相对 {0}", "Relative to {0}", baseName);
        public static string PathAbsoluteTag => Loc.T("绝对路径", "Absolute");
        public static string PathMissing => Loc.T("⚠ 文件夹不存在", "⚠ Folder not found");
        public static string PathPickTitle => Loc.T("选择文件夹", "Select folder");
        public static string PathListEmpty => Loc.T("还没有文件夹，点击右上角的“添加文件夹…”添加。", "No folders yet; use “Add folder…” at the top right.");
        public static string PathListHint => Loc.T("双击一行可直接编辑；旧配置里用 ; 分隔的路径会自动拆成多行。", "Double-click a row to edit it; paths separated by ';' in older settings are split into rows.");
        public static string SearchPlaceholder => Loc.T("输入以搜索", "Type to search");
        public static string SearchHistory => Loc.T("历史记录 (Alt+↑/↓)", "History (Alt+↑/↓)");
        public static string ListEmpty => Loc.T("没有结果", "No results");
        public static string ListPosition(object a0, object a1) => Loc.F("{0} / {1}", "{0} / {1}", a0, a1);
        public static string ListGroup(object a0, object a1) => Loc.F("{0}，{1} 项", "{0}, {1} items", a0, a1);
        public static string PaletteHintOpen => Loc.T("打开", "Open");
        public static string PaletteHintNewTab => Loc.T("新标签打开", "Open in new tab");
        public static string PaletteHintResults => Loc.T("在结果窗口打开", "Open in results window");
        public static string PaletteHintTabs => Loc.T("切换标签", "Switch tab");
        public static string PaletteHintPreview => Loc.T("预览", "Preview");
        public static string PaletteSearching => Loc.T("正在搜索…", "Searching…");
        public static string PaletteIncomplete => Loc.T("结果不完整", "Results incomplete");
        public static string PaletteTypeToSearch => Loc.T("输入名称开始搜索", "Type a name to search");
        public static string PaletteNoResults(object a0) => Loc.F("没有找到匹配“{0}”的结果", "Nothing found for “{0}”", a0);
        public static string PaletteError(object a0) => Loc.F("搜索失败：{0}", "Search failed: {0}", a0);
        public static string PaletteTogglePreview => Loc.T("显示/隐藏预览 (Ctrl+P)", "Show/hide preview (Ctrl+P)");
        public static string PreviewNone => Loc.T("无预览", "No preview");
        public static string PreviewLoading => Loc.T("正在加载预览…", "Loading preview…");
        public static string PreviewFailed(object a0) => Loc.F("无法加载预览：{0}", "Cannot load preview: {0}", a0);
        public static string MenuFilter => Loc.T("输入以筛选操作", "Type to filter actions");
        public static string TreeExpandAll => Loc.T("全部展开", "Expand all");
        public static string TreeCollapseAll => Loc.T("全部折叠", "Collapse all");
        public static string TreeSwitchTab => Loc.T("切换标签", "Switch tab");
        public static string ResultsCount(object a0) => Loc.F("{0} 个结果", "{0} results", a0);
        public static string DialogPreview => Loc.T("预览", "Preview");
        public static string BannerDismiss => Loc.T("关闭提示", "Dismiss");
        public static string SettingsTitle => Loc.T("外观", "Appearance");
        public static string SettingsSectionStyle => Loc.T("风格与密度", "Style and density");
        public static string SettingsPreset => Loc.T("界面风格", "Style");
        public static string SettingsPresetVs2026 => Loc.T("标准", "Standard");
        public static string SettingsPresetCompact => Loc.T("紧凑", "Compact");
        public static string SettingsPresetComfortable => Loc.T("宽松", "Comfortable");
        public static string SettingsFontScale => Loc.T("字号缩放", "Font scale");
        public static string SettingsRowHeight => Loc.T("列表行高", "Row height");
        public static string SettingsRadiusPopup => Loc.T("弹窗圆角", "Popup radius");
        public static string SettingsRadiusControl => Loc.T("按钮圆角", "Button radius");
        public static string SettingsRadiusBadge => Loc.T("徽章圆角", "Badge radius");
        public static string SettingsSpacing => Loc.T("间距缩放", "Spacing");
        public static string SettingsSectionColor => Loc.T("颜色", "Colors");
        public static string SettingsAccent => Loc.T("强调色", "Accent color");
        public static string SettingsAccentFollowVs => Loc.T("跟随 VS", "Follow VS");
        public static string SettingsAccentCustom => Loc.T("自定义", "Custom");
        public static string SettingsMatch => Loc.T("匹配高亮", "Match highlight");
        public static string SettingsMatchBold => Loc.T("加粗", "Bold");
        public static string SettingsMatchBackground => Loc.T("底色", "Background");
        public static string SettingsMatchUnderline => Loc.T("下划线", "Underline");
        public static string SettingsMatchAccent => Loc.T("强调色文字", "Accent text");
        public static string SettingsBadges => Loc.T("徽章颜色", "Badge colors");
        public static string SettingsSectionMotion => Loc.T("弹窗与动画", "Popups and animation");
        public static string SettingsShadow => Loc.T("弹窗阴影", "Popup shadow");
        public static string SettingsChrome => Loc.T("弹窗外框", "Popup frame");
        public static string SettingsChromeLayered => Loc.T("精确圆角", "Exact corners");
        public static string SettingsChromeNative => Loc.T("系统 (DWM)", "System (DWM)");
        public static string SettingsAnimations => Loc.T("动画", "Animations");
        public static string SettingsAnimationMs => Loc.T("动画时长", "Animation duration");
        public static string SettingsSectionPreview => Loc.T("预览面板", "Preview pane");
        public static string SettingsPreviewVisible => Loc.T("默认显示预览", "Show preview by default");
        public static string SettingsPreviewPosition => Loc.T("预览位置", "Preview position");
        public static string SettingsPreviewPositionBottom => Loc.T("下方", "Bottom");
        public static string SettingsPreviewPositionRight => Loc.T("右侧", "Right");
        public static string SettingsRadius => Loc.T("圆角", "Corners");
        public static string SettingsRadiusPopupShort => Loc.T("弹窗", "Popup");
        public static string SettingsRadiusControlShort => Loc.T("按钮", "Button");
        public static string SettingsRadiusBadgeShort => Loc.T("徽章", "Badge");
        public static string SettingsPresetDesc => Loc.T("控件高度和间距的整体预设", "Preset for control heights and spacing");
        public static string SettingsFontScaleDesc => Loc.T("相对 VS 环境字号", "Relative to the VS environment font size");
        public static string SettingsRowHeightDesc => Loc.T("搜索结果等列表的行高", "Height of list rows such as search results");
        public static string SettingsSpacingDesc => Loc.T("行间距、内边距统一放大或缩小", "Scales row gaps and padding together");
        public static string SettingsRadiusDesc => Loc.T("弹窗 / 按钮 / 徽章，单位 px", "Popup / button / badge, in px");
        public static string SettingsAccentDesc => Loc.T("选中项、主按钮、焦点下划线", "Selection, primary buttons, focus underline");
        public static string SettingsMatchDesc => Loc.T("搜索结果里命中的字符", "Matched characters in search results");
        public static string SettingsBadgesDesc => Loc.T("留空 = 按主题自动派生", "Empty = derived from the theme");
        public static string SettingsShadowDesc => Loc.T("弹窗下方的柔和阴影", "Soft shadow under popups");
        public static string SettingsChromeDesc => Loc.T("精确圆角需要分层窗口；系统外框由 DWM 绘制", "Exact corners use a layered window; the system frame is drawn by DWM");
        public static string SettingsPreviewVisibleDesc => Loc.T("Search Everywhere 打开时是否显示预览", "Whether Search Everywhere opens with the preview");
        public static string SampleButtonPrimary => Loc.T("主按钮", "Primary");
        public static string SampleButtonSecondary => Loc.T("次按钮", "Secondary");
        public static string SampleButtonText => Loc.T("文字按钮", "Text");
        public static string SettingsLivePreview => Loc.T("实时预览", "Live preview");
        public static string SettingsSampleTitle => Loc.T("预览", "Preview");
        public static string PerfEngineMemory => Loc.T("Engine 内存", "Engine memory");
        public static string PerfHostMemory => Loc.T("VS 内扩展内存", "In-VS memory");
        public static string PerfCpu => Loc.T("CPU", "CPU");
        public static string PerfIndex => Loc.T("索引状态", "Index");
        public static string PerfChart => Loc.T("CPU 与内存（最近 10 分钟）", "CPU and memory (last 10 min)");
        public static string PerfActive => Loc.T("正在进行", "In progress");
        public static string PerfActiveNone => Loc.T("当前没有后台任务", "No background work");
        public static string PerfPhases => Loc.T("索引阶段", "Index phases");
        public static string PerfColPhase => Loc.T("阶段", "Phase");
        public static string PerfColDuration => Loc.T("耗时", "Duration");
        public static string PerfColCpu => Loc.T("CPU 时间", "CPU time");
        public static string PerfColItems => Loc.T("条目数", "Items");
        public static string PerfColDetail => Loc.T("详情", "Details");
        public static string PerfMemory => Loc.T("索引内存构成", "Index memory");
        public static string PerfMemManaged => Loc.T("托管", "Managed");
        public static string PerfMemMapped => Loc.T("映射", "Mapped");
        public static string PerfMemDisk => Loc.T("磁盘", "Disk");
        public static string PerfOps => Loc.T("操作耗时", "Operations");
        public static string PerfColOp => Loc.T("操作", "Operation");
        public static string PerfColCount => Loc.T("次数", "Count");
        public static string PerfColErrors => Loc.T("错误", "Errors");
        public static string PerfColP50 => Loc.T("p50", "p50");
        public static string PerfColP95 => Loc.T("p95", "p95");
        public static string PerfColMax => Loc.T("max", "max");
        public static string PerfColBudget => Loc.T("预算", "Budget");
        public static string PerfOpenLog => Loc.T("打开日志", "Open log");
        public static string PerfDiagnostics => Loc.T("生成诊断包", "Create diagnostics bundle");
        public static string PerfPause => Loc.T("暂停后台索引", "Pause background indexing");
        public static string PerfResume => Loc.T("恢复后台索引", "Resume background indexing");
        public static string PerfOverBudget => Loc.T("超出预算", "Over budget");
        public static string PerfSeriesCpu => Loc.T("CPU %", "CPU %");
        public static string PerfSeriesMemory => Loc.T("内存", "Memory");
        public static string PerfSeriesHostMemory => Loc.T("VS 进程内存", "VS process memory");
        public static string PerfHostChart => Loc.T("VS 进程内存（最近 10 分钟）", "VS process memory (last 10 min)");
        public static string GalleryTitle => Loc.T("UEToVS 组件画廊", "UEToVS Component Gallery");

        public static string PerfNow => Loc.T("现在", "now");
        public static string PerfBudgetOf(object a0) => Loc.F("预算 {0}", "budget {0}", a0);

        public static string Preset(StylePreset p)
        {
            switch (p)
            {
                case StylePreset.Compact: return SettingsPresetCompact;
                case StylePreset.Comfortable: return SettingsPresetComfortable;
                default: return SettingsPresetVs2026;
            }
        }

        public static string Badge(BadgeKind k)
        {
            switch (k)
            {
                case BadgeKind.UClass: return "UCLASS";
                case BadgeKind.UFunction: return "UFUNCTION";
                case BadgeKind.UProperty: return "UPROPERTY";
                case BadgeKind.UStruct: return "USTRUCT";
                case BadgeKind.UEnum: return "UENUM";
                case BadgeKind.UInterface: return "UINTERFACE";
                case BadgeKind.Delegate: return Loc.T("委托", "Delegate");
                case BadgeKind.Macro: return Loc.T("宏", "Macro");
                case BadgeKind.File: return Loc.T("文件", "File");
                case BadgeKind.Asset: return Loc.T("资产", "Asset");
                case BadgeKind.Module: return Loc.T("模块", "Module");
                default: return Loc.T("其他", "Other");
            }
        }

        /// <summary>
        /// Calls <paramref name="apply"/> now and whenever the UI language changes while <paramref name="owner"/> is
        /// loaded (subscribed on Loaded, unsubscribed on Unloaded, so no leak through the static event).
        /// Language changes while unloaded are picked up on the next Loaded.
        /// With <paramref name="onlyWhenChanged"/> the Loaded pass is skipped when the language is still the one last
        /// applied (for expensive relabels such as the settings window, which otherwise built its page twice per open).
        /// </summary>
        public static void Track(FrameworkElement owner, Action apply, bool onlyWhenChanged = false)
        {
            if (owner == null || apply == null) return;
            UiLanguage applied = Loc.Language;
            void Run()
            {
                applied = Loc.Language;
                Theming.Ui.Safe("ui.loc.apply", apply);
            }
            void OnChanged()
            {
                var d = owner.Dispatcher;
                if (d.CheckAccess()) Run();
                else d.BeginInvoke(new Action(Run));
            }
            Run();
            bool subscribed = false;
            owner.Loaded += (s, e) =>
            {
                if (!onlyWhenChanged || applied != Loc.Language) Run();
                if (!subscribed) { Loc.LanguageChanged += OnChanged; subscribed = true; }
            };
            owner.Unloaded += (s, e) =>
            {
                if (subscribed) { Loc.LanguageChanged -= OnChanged; subscribed = false; }
            };
        }
    }
}
