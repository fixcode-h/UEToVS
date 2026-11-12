using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Threading;
using Microsoft.VisualStudio.Imaging;
using UEToVS.Core.Localization;
using UEToVS.UI.Atoms;
using UEToVS.UI.Controls;
using UEToVS.UI.Logic;
using UEToVS.UI.Molecules;
using UEToVS.UI.Patterns;
using UEToVS.UI.Perf;
using UEToVS.UI.Settings;
using UEToVS.UI.Shells;
using UEToVS.UI.Theming;

namespace UEToVS.UI.Gallery
{
    /// <summary>
    /// Development window showing every component and pattern with sample data (§8.7). Toggles: theme
    /// (VS / Dark / Light / Blue / High contrast), style preset, UI language, data size (empty / 10 / 100k).
    /// Host: <c>new ComponentGallery().Show()</c> from a dev-only command; also usable for screenshot tests.
    /// </summary>
    public class ComponentGallery : Window
    {
        private readonly ContentControl _page = new ContentControl { Focusable = false };
        private readonly VirtualList<string> _nav;
        private readonly List<(Func<string> Title, Func<UIElement> Build)> _pages = new List<(Func<string>, Func<UIElement>)>();
        private readonly Dictionary<int, UIElement> _built = new Dictionary<int, UIElement>();
        private readonly Dropdown _theme = new Dropdown();
        private readonly Dropdown _preset = new Dropdown();
        private readonly SegmentedControl _lang = new SegmentedControl("中文", "English");
        private readonly Dropdown _dataSize = new Dropdown();
        private List<SampleSymbol> _data = SampleData.Symbols(10);
        private CommandPalette<SampleSymbol> _palette;
        private ChooserPopup<SampleSymbol> _chooser;
        private ContextMenuPopup _menu;
        private VirtualList<SampleSymbol> _bigList;
        private DispatcherTimer _perfTimer;

        public ComponentGallery()
        {
            Width = 1180;
            Height = 780;
            ThemeManager.Instance.Attach(this);
            this.Bind(BackgroundProperty, Tokens.SurfaceBase);
            Strings.Track(this, () => Title = Strings.GalleryTitle);

            _nav = new VirtualList<string>(new DelegatePresenter<string>((s, r) => r.Primary = s)) { Width = 200 };
            DefinePages();

            var toolbar = BuildToolbar();
            var body = new Grid();
            body.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            body.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1) });
            body.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            var navHost = new Border { Child = _nav, Padding = new Thickness(0, 6, 0, 6) };
            navHost.Bind(Border.BackgroundProperty, Tokens.SurfaceRaised);
            var div = new Divider(true);
            Grid.SetColumn(div, 1);
            Grid.SetColumn(_page, 2);
            body.Children.Add(navHost);
            body.Children.Add(div);
            body.Children.Add(_page);

            var root = new DockPanel { LastChildFill = true };
            DockPanel.SetDock(toolbar, Dock.Top);
            root.Children.Add(toolbar);
            root.Children.Add(body);
            Content = root;

            _nav.SelectionChanged += (s, e) => Ui.Safe("ui.gallery.nav", () => ShowPage(_nav.SelectedIndex));
            Strings.Track(root, RelabelNav);
            RelabelNav();
            ShowPage(0);
            Closed += (s, e) => Ui.Safe("ui.gallery.closed", () =>
            {
                _perfTimer?.Stop();
                _palette?.Shell.CloseForReal();
                _chooser?.Shell.CloseForReal();
                _menu?.Shell.CloseForReal();
                ThemeManager.Instance.PaletteSource = new VsThemePaletteSource();
            });
        }

        private IntPtr Hwnd => new WindowInteropHelper(this).Handle;

        private void RelabelNav()
        {
            var titles = new List<string>();
            foreach (var p in _pages) titles.Add(p.Title());
            int sel = _nav.SelectedIndex;
            _nav.SetItems(titles, keepPosition: true);
            if (sel >= 0) _nav.SelectedIndex = sel;
            _theme.Relabel(new[] { Loc.T("跟随 VS", "Follow VS"), Loc.T("深色", "Dark"), Loc.T("浅色", "Light"), Loc.T("蓝色", "Blue"), Loc.T("高对比度", "High contrast") });
            _preset.Relabel(new[] { Strings.Preset(StylePreset.Vs2026), Strings.Preset(StylePreset.Compact), Strings.Preset(StylePreset.Comfortable) });
            _dataSize.Relabel(new[] { Loc.T("空", "Empty"), "10", "100,000" });
        }

        private FrameworkElement BuildToolbar()
        {
            _theme.Items = new[] { "VS", "Dark", "Light", "Blue", "HC" };
            _theme.SelectedIndex = 0;
            _theme.SelectionChanged += (s, e) => Ui.Safe("ui.gallery.theme", () =>
            {
                switch (_theme.SelectedIndex)
                {
                    case 1: ThemeManager.Instance.PaletteSource = new FixedPaletteSource(ThemePalette.FallbackDark()); break;
                    case 2: ThemeManager.Instance.PaletteSource = new FixedPaletteSource(ThemePalette.FallbackLight()); break;
                    case 3: ThemeManager.Instance.PaletteSource = new FixedPaletteSource(ThemePalette.FallbackBlue()); break;
                    case 4: ThemeManager.Instance.PaletteSource = new FixedPaletteSource(ThemePalette.FallbackHighContrast()); break;
                    default: ThemeManager.Instance.PaletteSource = new VsThemePaletteSource(); break;
                }
            });
            _preset.Items = new[] { "VS 2026", "Compact", "Comfortable" };
            _preset.SelectedIndex = (int)ThemeManager.Instance.Settings.Preset;
            _preset.SelectionChanged += (s, e) => Ui.Safe("ui.gallery.preset", () =>
            {
                var st = ThemeManager.Instance.Settings;
                st.ApplyPreset((StylePreset)_preset.SelectedIndex);
                ThemeManager.Instance.Apply(st);
            });
            _lang.SelectedIndex = Loc.IsEnglish ? 1 : 0;
            _lang.SelectionChanged += (s, e) => Ui.Safe("ui.gallery.lang", () => Loc.Language = _lang.SelectedIndex == 1 ? UiLanguage.English : UiLanguage.Chinese);
            _dataSize.Items = new[] { "0", "10", "100000" };
            _dataSize.SelectedIndex = 1;
            _dataSize.SelectionChanged += (s, e) => Ui.Safe("ui.gallery.size", () =>
            {
                int n = _dataSize.SelectedIndex == 0 ? 0 : _dataSize.SelectedIndex == 1 ? 10 : 100_000;
                using (Ui.Logger.Time("ui.gallery.generate", 500, ("n", n))) _data = SampleData.Symbols(n);
                if (_bigList != null) _bigList.Items = SampleData.Grouped(_data);
            });

            var palette = new UiButton(() => Loc.T("命令面板", "Command palette"), ButtonKind.Primary);
            palette.Click += (s, e) => Ui.Safe("ui.gallery.palette", OpenPalette);
            var chooser = new UiButton(() => Loc.T("选择器", "Chooser"));
            chooser.Click += (s, e) => Ui.Safe("ui.gallery.chooser", OpenChooser);
            var menu = new UiButton(() => Loc.T("快捷操作 (Alt+Enter)", "Quick actions (Alt+Enter)"));
            menu.Click += (s, e) => Ui.Safe("ui.gallery.menu", () => OpenMenu(menu));
            var dialog = new UiButton(() => Loc.T("对话框", "Dialog"));
            dialog.Click += (s, e) => Ui.Safe("ui.gallery.dialog", OpenDialog);
            var toast = new UiButton(() => Loc.T("提示", "Toast"), ButtonKind.Ghost);
            toast.Click += (s, e) => Ui.Safe("ui.gallery.toast", () => Toast.Show(_page, Loc.T("已复制到剪贴板", "Copied to clipboard"), Severity.Success));

            var bar = new WrapPanel { Margin = new Thickness(8, 6, 8, 6) };
            void Add(Func<string> label, UIElement el)
            {
                var sp = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 2, 14, 2) };
                sp.Children.Add(new UiText(label, TextVariant.Small) { Margin = new Thickness(0, 0, 6, 0) });
                sp.Children.Add(el);
                bar.Children.Add(sp);
            }
            Add(() => Loc.T("主题", "Theme"), _theme);
            Add(() => Loc.T("风格", "Style"), _preset);
            Add(() => Loc.T("语言", "Language"), _lang);
            Add(() => Loc.T("数据量", "Data"), _dataSize);
            foreach (var b in new UIElement[] { palette, chooser, menu, dialog, toast })
            {
                ((FrameworkElement)b).Margin = new Thickness(0, 2, 6, 2);
                bar.Children.Add(b);
            }
            var host = new Border { Child = bar, BorderThickness = new Thickness(0, 0, 0, 1) };
            host.Bind(Border.BorderBrushProperty, Tokens.BorderSubtle);
            host.Bind(Border.BackgroundProperty, Tokens.SurfaceRaised);
            return host;
        }

        private void ShowPage(int index)
        {
            if (index < 0 || index >= _pages.Count) return;
            if (!_built.TryGetValue(index, out var el))
            {
                using (Ui.Logger.Time("ui.gallery.build_page", 100, ("page", index)))
                    el = _pages[index].Build();
                _built[index] = el;
            }
            _page.Content = el;
        }

        private static StackPanel Stack() => new StackPanel { Margin = new Thickness(20, 14, 20, 20) };

        private static UIElement Scroll(UIElement content) => new UiScrollViewer { Content = content };

        private static UiText Heading(Func<string> t) => new UiText(t, TextVariant.Title) { Margin = new Thickness(0, 14, 0, 8) };

        private static WrapPanel Row(params UIElement[] items)
        {
            var w = new WrapPanel { Margin = new Thickness(0, 2, 0, 6) };
            foreach (var i in items)
            {
                if (i is FrameworkElement fe) fe.Margin = new Thickness(0, 2, 10, 2);
                w.Children.Add(i);
            }
            return w;
        }

        private void DefinePages()
        {
            _pages.Add((() => Loc.T("原子", "Atoms"), BuildAtoms));
            _pages.Add((() => Loc.T("控件", "Controls"), BuildControls));
            _pages.Add((() => Loc.T("列表（虚拟化）", "List (virtualized)"), BuildList));
            _pages.Add((() => Loc.T("树形结果", "Tree results"), BuildTree));
            _pages.Add((() => Loc.T("代码预览", "Code preview"), BuildPreview));
            _pages.Add((() => Loc.T("反馈", "Feedback"), BuildFeedback));
            _pages.Add((() => Loc.T("性能面板", "Performance"), BuildPerf));
            _pages.Add((() => Loc.T("外观设置", "Appearance settings"), () => new AppearanceSettingsPanel()));
        }

        private UIElement BuildAtoms()
        {
            var sp = Stack();
            sp.Children.Add(Heading(() => Loc.T("文本", "Text")));
            foreach (TextVariant v in Enum.GetValues(typeof(TextVariant)))
                sp.Children.Add(new UiText(v + " — " + Loc.T("快速的棕色狐狸", "The quick brown fox"), v));
            sp.Children.Add(Heading(() => Loc.T("匹配高亮", "Match highlight")));
            var ht = new HighlightedText();
            ht.Set("UAbilitySystemComponent", HighlightRanges.FromPairs(new[] { 0, 1, 1, 7, 14, 3 }));
            sp.Children.Add(ht);
            sp.Children.Add(Heading(() => Loc.T("图标与类型字形", "Icons and glyphs")));
            var icons = new WrapPanel();
            try
            {
                foreach (var m in new[] { KnownMonikers.Class, KnownMonikers.Method, KnownMonikers.Property, KnownMonikers.Field, KnownMonikers.Enumeration, KnownMonikers.Structure, KnownMonikers.Document })
                    icons.Children.Add(new UiIcon(m) { Margin = new Thickness(0, 0, 8, 0) });
            }
            catch (Exception ex)
            {
                Ui.Logger.Warn("ui.gallery.monikers", "KnownMonikers unavailable outside VS", ex);
            }
            foreach (SymbolKind k in TokenNames.AllSymbolKinds) icons.Children.Add(new TypeGlyph(k) { Margin = new Thickness(0, 0, 6, 0) });
            sp.Children.Add(icons);
            sp.Children.Add(Heading(() => Loc.T("徽章", "Badges")));
            var badges = new WrapPanel();
            foreach (BadgeKind k in TokenNames.AllBadgeKinds) badges.Children.Add(new Badge(Strings.Badge(k), k) { Margin = new Thickness(0, 0, 6, 6) });
            foreach (BadgeKind k in TokenNames.AllBadgeKinds) badges.Children.Add(new Badge(Strings.Badge(k), k, true) { Margin = new Thickness(0, 0, 6, 6) });
            sp.Children.Add(badges);
            sp.Children.Add(Heading(() => Loc.T("快捷键 / 分隔线 / 加载", "Keys / divider / spinner")));
            sp.Children.Add(Row(new Kbd("Ctrl+Shift+F"), new Kbd("Ctrl+K, Ctrl+C"), new Kbd("Alt+Enter"), new Spinner(), new Spinner(20)));
            sp.Children.Add(new Divider(false, 8));
            return Scroll(sp);
        }

        private UIElement BuildControls()
        {
            var sp = Stack();
            sp.Children.Add(Heading(() => Loc.T("按钮", "Buttons")));
            sp.Children.Add(Row(
                new UiButton(() => Loc.T("主要", "Primary"), ButtonKind.Primary),
                new UiButton(() => Loc.T("次要", "Secondary")),
                new UiButton(() => Loc.T("幽灵", "Ghost"), ButtonKind.Ghost),
                new UiButton(() => Loc.T("禁用", "Disabled")) { IsEnabled = false },
                new IconButton(Ui.CrossMark, () => Strings.CommonClose, "Esc", 8),
                new IconToggle(Ui.FrozenGeometry("M 0,0 L 12,0 L 12,10 L 0,10 Z M 0,6 L 12,6"), () => Strings.PaletteTogglePreview, "Ctrl+P")));
            sp.Children.Add(Heading(() => Loc.T("开关 / 复选 / 筛选标签", "Toggle / check / chips")));
            var chip = new Chip(() => "Blueprint", true);
            chip.Count = 12;
            sp.Children.Add(Row(new UiToggle(() => Loc.T("实时预览", "Live preview"), true), new UiCheckBox(() => Loc.T("包含非项目项", "Include non-project items")),
                chip, new Chip(() => "C++"), new SegmentedControl("VS 2026", "Compact", "Comfortable")));
            sp.Children.Add(Heading(() => Loc.T("输入", "Inputs")));
            var input = new TextInput { Width = 260 };
            Strings.Track(input, () => input.Placeholder = Loc.T("类名", "Class name"));
            var search = new SearchInput(false) { Width = 340, HistoryOnPlainArrows = true, Prefix = "Types:" };
            search.History.Load(new[] { "ACharacter", "UGameplayAbility", "FHitResult" });
            var dd = new Dropdown { Items = new[] { "Development Editor", "DebugGame Editor", "Shipping" }, SelectedIndex = 0 };
            sp.Children.Add(Row(input, search, dd, new NumberStepper(0, 100, 5, 40), new ColorField("Accent")));
            sp.Children.Add(Heading(() => Loc.T("链接与工具提示", "Link and tooltip")));
            var link = new LinkText(() => Loc.T("打开 Build.cs", "Open Build.cs"));
            Tip.Set(link, () => Loc.T("跳转到模块的 Build.cs", "Go to the module's Build.cs"), "F12");
            link.Click += (s, e) => Toast.Show(_page, "Build.cs");
            sp.Children.Add(Row(link));
            return Scroll(sp);
        }

        private UIElement BuildList()
        {
            _bigList = new VirtualList<SampleSymbol>(new SamplePresenter());
            _bigList.Items = SampleData.Grouped(_data);
            var footer = new StatusFooter();
            footer.SetHints(new KeyHint("Enter", () => Strings.PaletteHintOpen), new KeyHint("Shift+Enter", () => Strings.PaletteHintNewTab));
            void Count() => footer.Count = Strings.ListPosition(_bigList.SelectedIndex + 1, PerfMath.FormatCount(_bigList.Count));
            _bigList.SelectionChanged += (s, e) => Count();
            _bigList.ItemInvoked += (s, e) => Toast.Show(_page, e.Kind + ": " + e.Item.Name);
            var dock = new DockPanel { LastChildFill = true };
            DockPanel.SetDock(footer, Dock.Bottom);
            dock.Children.Add(footer);
            dock.Children.Add(_bigList);
            Count();
            return dock;
        }

        private UIElement BuildTree()
        {
            var panel = new ResultsPanel<SampleSymbol>(new SamplePresenter());
            var roots = new List<TreeNode<SampleSymbol>>();
            var syms = SampleData.Symbols(400);
            foreach (var g in System.Linq.Enumerable.GroupBy(syms, s => s.File))
            {
                var children = new List<TreeNode<SampleSymbol>>();
                foreach (var s in g) children.Add(new TreeNode<SampleSymbol>(s));
                roots.Add(new TreeNode<SampleSymbol>(new SampleSymbol { Name = g.Key, Kind = SymbolKind.File, Container = "Source/MyGame", File = g.Key, Line = 1 }, children, roots.Count < 3));
            }
            panel.PreviewProvider = s => new CodePreviewRequest { FilePath = s.File, Line = s.Line, Column = 6, Length = s.Name.Length, Text = SampleData.FakeSource(s, Math.Max(200, s.Line + 20)) };
            panel.ItemInvoked += (s, e) => Toast.Show(panel.Root, e.Item.Name);
            panel.SetResults(roots, Loc.T("“Character” 的引用", "References of “Character”"));
            panel.Root.ShowBanner(new Banner(Severity.Warning, Loc.T("索引尚未完成，结果可能不完整。", "Indexing is not finished; results may be incomplete.")));
            return panel.Root;
        }

        private UIElement BuildPreview()
        {
            var p = new CodePreview();
            var s = SampleData.Symbols(5)[2];
            p.Show(new CodePreviewRequest { FilePath = s.File, Line = s.Line, Column = 6, Length = s.Name.Length, Text = SampleData.FakeSource(s) });
            return p;
        }

        private UIElement BuildFeedback()
        {
            var sp = Stack();
            sp.Children.Add(Heading(() => "Banner"));
            foreach (Severity sev in Enum.GetValues(typeof(Severity)))
            {
                var b = new Banner(sev, sev + ": " + Loc.T("检测到 VS 自带的 IntelliSense 仍在运行。", "Visual Studio IntelliSense is still running.")) { Margin = new Thickness(0, 0, 0, 6) };
                b.AddAction(() => Loc.T("关闭它", "Turn it off"), () => Toast.Show(_page, "OK"));
                sp.Children.Add(b);
            }
            sp.Children.Add(Heading(() => "EmptyState"));
            var empty = new EmptyState();
            empty.Set(Loc.T("没有找到匹配“Foo”的结果", "Nothing found for “Foo”"), Loc.T("试试包含非项目项。", "Try including non-project items."), Loc.T("包含非项目项", "Include non-project items"), () => Toast.Show(_page, "…"));
            sp.Children.Add(new Border { Child = empty, Height = 180, BorderThickness = new Thickness(1) }.Bind(Border.BorderBrushProperty, Tokens.BorderSubtle));
            sp.Children.Add(Heading(() => "StatusFooter"));
            var f = new StatusFooter { IsBusy = true, Status = Loc.T("正在搜索…", "Searching…"), Count = "3 / 120" };
            f.SetHints(new KeyHint("Enter", () => Strings.PaletteHintOpen), new KeyHint("Ctrl+Enter", () => Strings.PaletteHintResults));
            sp.Children.Add(f);
            return Scroll(sp);
        }

        private UIElement BuildPerf()
        {
            var panel = new PerformancePanel();
            var rnd = new Random(7);
            var cpu = new List<double>();
            var mem = new List<double>();
            int tick = 0;
            for (; tick < 300; tick++) SampleData.Perf(tick, rnd, cpu, mem); // pre-fill 5 minutes of history
            bool paused = false;
            panel.PauseToggled += (s, pause) => paused = pause;
            panel.OpenLogRequested += (s, e) => Toast.Show(panel.Root, Strings.PerfOpenLog);
            panel.DiagnosticsRequested += (s, e) => Toast.Show(panel.Root, Strings.PerfDiagnostics);
            _perfTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
            _perfTimer.Tick += (s, e) => Ui.Safe("ui.gallery.perf", () =>
            {
                var d = SampleData.Perf(tick++, rnd, cpu, mem);
                d.BackgroundPaused = paused;
                panel.Update(d);
            });
            panel.Update(SampleData.Perf(tick++, rnd, cpu, mem));
            _perfTimer.Start();
            return panel.Root;
        }

        private void OpenPalette()
        {
            if (_palette == null)
            {
                var source = new SamplePaletteSource(() => _data);
                source.Invoked = (item, kind) => Toast.Show(_page, kind + ": " + item.Name);
                _palette = new CommandPalette<SampleSymbol>("gallery.palette", source) { OwnerHandle = Hwnd };
            }
            _palette.Show(PopupAnchor.CenterOf(Hwnd));
        }

        private async void OpenChooser()
        {
            try
            {
                if (_chooser == null) _chooser = new ChooserPopup<SampleSymbol>("gallery.chooser", new SamplePresenter()) { OwnerHandle = Hwnd };
                var items = SampleData.Symbols(9);
                var r = await _chooser.ShowAsync(items, PopupAnchor.Mouse(), Loc.F("选择实现（{0}）", "Choose implementation ({0})", items.Count), s => s.Name);
                if (r.Chosen) Toast.Show(_page, r.Item.Name);
            }
            catch (Exception ex)
            {
                Ui.Logger.Error("ui.gallery.chooser_failed", "Chooser demo failed", ex);
            }
        }

        private async void OpenMenu(FrameworkElement anchor)
        {
            try
            {
                if (_menu == null) _menu = new ContextMenuPopup("gallery.menu") { OwnerHandle = Hwnd };
                Func<string> fix = () => Loc.T("快速修复", "Quick fixes");
                Func<string> refactor = () => Loc.T("重构", "Refactor");
                var entries = new List<MenuEntry>
                {
                    new MenuEntry("include", () => Loc.T("添加 #include \"GameFramework/Character.h\"", "Add #include \"GameFramework/Character.h\"")) { Group = fix, Glyph = SymbolKind.File },
                    new MenuEntry("dep", () => Loc.T("在 Build.cs 中添加模块依赖", "Add module dependency to Build.cs")) { Group = fix, Glyph = SymbolKind.Module },
                    new MenuEntry("rename", () => Loc.T("重命名…", "Rename…")) { Group = refactor, Shortcut = "Ctrl+R, Ctrl+R" },
                    new MenuEntry("impl", () => Loc.T("生成实现", "Generate implementation")) { Group = refactor, Shortcut = "Alt+Insert" },
                    new MenuEntry("more", () => Loc.T("转换为…", "Convert to…"))
                    {
                        Group = refactor,
                        Children = new[]
                        {
                            new MenuEntry("tobj", () => "TObjectPtr<>"),
                            new MenuEntry("weak", () => "TWeakObjectPtr<>"),
                            new MenuEntry("soft", () => "TSoftObjectPtr<>"),
                        },
                    },
                    new MenuEntry("disabled", () => Loc.T("提取函数（不可用）", "Extract function (unavailable)")) { Group = refactor, IsEnabled = false },
                };
                var chosen = await _menu.ShowAsync(entries, PopupAnchor.Element(anchor));
                if (chosen != null) Toast.Show(_page, chosen.Id);
            }
            catch (Exception ex)
            {
                Ui.Logger.Error("ui.gallery.menu_failed", "Menu demo failed", ex);
            }
        }

        private void OpenDialog()
        {
            var dlg = new DialogShell(() => Loc.T("重命名符号", "Rename symbol"), () => Loc.T("重命名", "Rename"));
            dlg.Description = Loc.T("将同时更新 12 个文件中的 48 处引用。", "48 references in 12 files will be updated.");
            var form = new StackPanel();
            var name = new TextInput { Text = "AMyCharacter" };
            form.Children.Add(new UiText(() => Loc.T("新名称", "New name"), TextVariant.Small) { Margin = new Thickness(0, 0, 0, 4) });
            form.Children.Add(name);
            form.Children.Add(new UiCheckBox(() => Loc.T("同时重命名文件", "Rename files too"), true) { Margin = new Thickness(0, 10, 0, 0) });
            form.Children.Add(new UiCheckBox(() => Loc.T("更新 CoreRedirects", "Update CoreRedirects"), true) { Margin = new Thickness(0, 6, 0, 0) });
            dlg.Form = form;
            var preview = new CodePreview();
            var s = SampleData.Symbols(3)[1];
            preview.Show(new CodePreviewRequest { FilePath = s.File, Line = s.Line, Column = 6, Length = s.Name.Length, Text = SampleData.FakeSource(s) });
            dlg.Preview = preview;
            dlg.Validate = () => !string.IsNullOrWhiteSpace(name.Text);
            bool ok = dlg.ShowModal(Hwnd);
            Toast.Show(_page, ok ? "OK: " + name.Text : Strings.CommonCancel);
        }
    }
}
