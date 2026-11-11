using System.ComponentModel;
using Microsoft.VisualStudio.Shell;
using UEToVS.Core.Keymap;

namespace UEToVS.Options
{
    /// <summary>Tools > Options > UEToVS > General.</summary>
    public sealed class GeneralOptions : DialogPage
    {
        [Category("Search Everywhere")]
        [DisplayName("启用双击 Shift")]
        [Description("在 VS 任意位置连按两次 Shift 打开 Search Everywhere。")]
        public bool DoubleShiftEnabled { get; set; } = true;

        [Category("Search Everywhere")]
        [DisplayName("双击间隔 (ms)")]
        [Description("两次按下 Shift 的最大间隔。")]
        public int DoubleShiftIntervalMs { get; set; } = 300;

        [Category("Search Everywhere")]
        [DisplayName("仅左 Shift")]
        public bool LeftShiftOnly { get; set; }

        [Category("Search Everywhere")]
        [DisplayName("每类最多结果数")]
        public int MaxResultsPerCategory { get; set; } = 60;

        [Category("Search Everywhere")]
        [DisplayName("显示预览")]
        public bool ShowPreview { get; set; } = true;

        [Category("导航")]
        [DisplayName("鼠标侧键后退/前进")]
        [Description("鼠标 XButton1/XButton2 使用 UEToVS 的导航历史。")]
        public bool MouseButtonsNavigate { get; set; } = true;

        [Category("导航")]
        [DisplayName("记录跳转的行数阈值")]
        [Description("同一文件内光标一次移动超过此行数才记入后退历史。")]
        public int NavigationLineThreshold { get; set; } = 15;

        [Category("导航")]
        [DisplayName("历史容量")]
        public int NavigationCapacity { get; set; } = 100;

        [Category("快捷键")]
        [DisplayName("快捷键预设")]
        [Description("rider-default（Rider 默认键位）或 rider-vs（Rider 的 Visual Studio 键位）。修改后执行 UEToVS > Apply Rider Keymap。")]
        public string KeymapPreset { get; set; } = KeymapPresets.RiderDefaultId;

        [Category("快捷键")]
        [DisplayName("已提示过应用键位")]
        [Browsable(false)]
        public bool KeymapPrompted { get; set; }

        [Category("Unreal")]
        [DisplayName("引擎根目录（可选）")]
        [Description("留空时根据 .uproject 的 EngineAssociation 自动查找（源码版注册表 / 启动器安装）。")]
        public string EngineRootOverride { get; set; } = string.Empty;

        [Category("Unreal")]
        [DisplayName("保存时检查模块依赖")]
        [Description("保存 .h/.cpp 时检查 #include 的头文件所在模块是否已加入 Build.cs，缺失时提示一键添加。")]
        public bool CheckIncludesOnSave { get; set; } = true;

        [Category("Unreal")]
        [DisplayName("编辑器插件端口（0 = 自动）")]
        [Description("UEToVSLink 编辑器插件的 TCP 端口。0 时从 <Project>/Saved/UEToVSLink/port.txt 读取。")]
        public int EditorLinkPort { get; set; }

        [Category("高级")]
        [DisplayName("索引进程额外参数")]
        public string IndexerExtraArgs { get; set; } = string.Empty;

        [Category("高级")]
        [DisplayName("最近搜索")]
        [Browsable(false)]
        public string RecentQueries { get; set; } = string.Empty;

        [Category("高级")]
        [DisplayName("弹窗尺寸")]
        [Browsable(false)]
        public string PopupBounds { get; set; } = string.Empty;
    }
}
