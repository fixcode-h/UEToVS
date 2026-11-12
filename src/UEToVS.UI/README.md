# UEToVS.UI：组件库

UEToVS 的全部界面都由本库提供（架构设计 §8）。它是一个 net48 的 WPF 类库，完全用 C# 构建：没有 `.xaml`，样式和模板都通过 `FrameworkElementFactory` 生成。默认外观对齐 Rider（New UI），所有视觉参数都可以在 “外观设置” 页微调（§8.2.1）。

功能代码**只组合组件**，不自己写控件模板、颜色或字号。

## 目录结构

| 目录 | 内容 |
|---|---|
| `Logic/` | **不依赖 WPF** 的纯逻辑：颜色运算 `Rgba`、令牌推导 `TokenDerivation`、`AppearanceSettings`、高亮区间、`ListNavigator`、`TreeFlattener`、`PopupPlacement`、`SearchHistory`、`QueryGate`、`ShortcutText`、`PerfMath`/`SampleBuffer`。这些代码由 `tests/UEToVS.UI.Logic.Tests`（net8.0）直接编译测试。 |
| `Theming/` | `Tokens`（资源键）、`ThemeManager`（主题与设置）、调色板来源、`Ui` 辅助方法（绑定、几何图形、动画、`Safe` 异常日志）。 |
| `Atoms/` | `UiText`、`HighlightedText`、`UiIcon`、`TypeGlyph`、`Badge`、`Kbd`、`Divider`、`Spinner`。 |
| `Controls/` | `UiButton`、`IconButton`、`IconToggle`、`UiToggle`、`UiCheckBox`、`Chip`、`SegmentedControl`、`TextInput`、`SearchInput`、`Dropdown`、`ColorField`、`NumberStepper`、`LinkText`、`Tip`（工具提示）、`UiScrollViewer`/`ScrollRail`。 |
| `Molecules/` | 列表行 `ListRow`、虚拟化列表 `VirtualList<T>`、树 `TreeList<T>`、`TabStrip`、`CodePreview`、`EmptyState`、`KeyHint`、`StatusFooter`、`Banner`、`Toast`。 |
| `Shells/` | `ChromeWindow`（圆角与阴影弹窗窗口）、`PopupShell`（可记忆尺寸的弹出层）、`DialogShell`、`ToolWindowShell`、`PopupAnchor`。 |
| `Patterns/` | `CommandPalette<T>`、`ChooserPopup<T>`、`ContextMenuPopup`、`ResultsPanel<T>`。 |
| `Perf/` | `StatTile`、`ProgressLine`/`ProgressRow`、`LineChart`（含迷你图模式）、`StackedBar`、`DataTable<T>`、`PerformancePanel`。 |
| `Settings/` | `AppearanceSettingsPanel`，即外观设置页，带实时预览。 |
| `Gallery/` | `ComponentGallery`（开发用组件展示窗口）和示例数据。 |
| `Strings.cs` | **所有**用户可见文本（中文和英文）。 |

## 设计令牌

令牌分为两层：

- **基础层**：由 `ThemePalette` 提供，即 VS 主题色。读取顺序为 `EnvironmentColors`，读不到时退回内置的 Dark、Light、Blue 或高对比度调色板。
- **语义层**：由 `TokenDerivation.Derive(palette, settings)` 推导。组件只引用语义令牌。

推导时会保证对比度：正文至少 4.5:1，徽章至少 3:1，并有单元测试覆盖。

**颜色**
- Surface：`Surface.Base/Popup/Raised/Sunken/Hover/Pressed`
- Text：`Text.Primary/Secondary/Disabled/Match/OnAccent/Link/LinkHover`
- Border：`Border.Subtle/Strong/Focus`
- Accent：`Accent`、`Accent.Hover/Pressed/Subtle`
- Selection：`Selection.Background/Foreground/Secondary/InactiveBackground`
- Match：`Match.Background`
- Input：`Input.Background/Foreground/Border/Placeholder`
- Button：`Button.Secondary.*`、`Button.Ghost.*`
- 其他控件：`Toggle.Track.Off`、`Toggle.Thumb`、`ScrollBar.Thumb/ThumbHover`、`Shadow`、`Kbd.*`、`Tooltip.*`、`Code.LineHighlight/LineNumber`
- 按种类生成：`Severity.{Info|Success|Warning|Error}[.Background]`、`Badge.{Kind}.Foreground/Background/Solid`、`Glyph.{SymbolKind}[.Background]`

**尺寸**
- 字号：`Font.Size.Body/Small/Caption/Title/Input/Code`
- 行高与图标：`Row.Height`、`Row.HeaderHeight`、`Icon.Size`
- 圆角：`Radius.Popup/Control/Badge`
- 内边距与间距：`Pad.Row.X`、`Pad.Control.X/Y`、`Pad.Popup`、`Gap.Inline`、`Space.{n}`
- 阴影：`Shadow.Blur/Depth/Margin`
- 动画：`Animation.Ms`
- 匹配高亮：`Match.Bold`、`Match.Underline`

**WPF 类型键**
- 圆角：`Tokens.CornerRadiusPopup/Control/Badge/Pill`
- 内边距：`Tokens.ThicknessControlPadding/RowPadding/PopupPadding`
- 字体：`Tokens.FontFamilyUi/Code`
- 匹配样式：`Tokens.MatchFontWeight/MatchDecorations`

**用法**
- 资源键都是 `ComponentResourceKey`，例如 `Tokens.SurfacePopup` 或 `Tokens.Key("Text.Primary")`。
- 组件内用 `el.Bind(prop, Tokens.X)` 绑定（即 `SetResourceReference`），样式里用 `Ui.Dyn(prop, Tokens.X)`。
- 主题切换或设置变化时，`ThemeManager` 会**原地更新**每个根元素的令牌字典，所有绑定自动刷新，不会重建可视树。

## 宿主集成（VS 包侧）

1. **主题与设置**：每个根元素调用一次 `ThemeManager.Instance.Attach(root)`。弹窗外壳和 `ComponentGallery` 已经自动调用。
   - 启动时 `ThemeManager.Instance.Apply(AppearanceSettings.Parse(stored, out var problems))`。
   - 设置以 `ToStorageString()` 的 key=value 文本保存。
   - VS 主题变化（`VSColorTheme.ThemeChanged`）和高对比度切换会自动跟随。
2. **设置页**：在 `UIElementDialogPage.Child` 中放一个 `new AppearanceSettingsPanel()`。
   - `OnActivate` 调用 `Load(ThemeManager.Instance.Settings)`。
   - `OnApply` 调用 `Commit()`，然后把 `Settings.ToStorageString()` 写入设置存储。
   - `OnClosed` 和取消时调用 `Revert()`。
   - 打开 “实时预览” 时，修改通过 `ThemeManager.Preview` 立即作用于整个扩展 UI。
3. **语言**：文本来自 `UEToVS.Core.Localization.Loc`。宿主根据 VS 界面语言设置 `Loc.Language`。组件通过 `Strings.Track(owner, apply)` 在 `Loc.LanguageChanged` 时重新读取文本，并在 Loaded/Unloaded 时自动订阅和退订。新增文本只能加在 `Strings.cs` 中，不要在别处硬编码。
4. **弹窗尺寸记忆**：实现 `IPopupStateStore` 并赋给 `PopupShell.DefaultStateStore`。它按弹窗 id 保存宽、高、预览分割比例和预览开关；默认实现是 `InMemoryPopupStateStore`。
5. **代码预览**：实现 `ICodePreviewHost` 并赋给 `CodePreview` 实例的 `Host` 属性（例如 `palette.Preview.Host`、`resultsPanel.Preview.Host`），即可用真正的 VS 编辑器（`IWpfTextViewHost`，带语法高亮）。没有实现时，内置的轻量预览会异步读取文件（最大 8 MB）并虚拟化显示，带行号和目标行高亮。
6. **弹窗父窗口**：给弹窗设置 `OwnerHandle`（VS 主窗口句柄），并用 `PopupAnchor.Caret(...)`、`CenterOf(hwnd)`、`Element(...)` 或 `Mouse()` 定位。定位使用设备像素和显示器工作区，支持每显示器 DPI。
7. **日志**：本库的日志器是 `Log.For("UI")`。所有事件处理都包在 `Ui.Safe("ui.xxx", ...)` 中，异常会被记录，不会被静默吞掉。

## 用法示例：组合一个弹出功能

以 “转到符号” 为例，功能只需提供**数据源**和**呈现器**：

```csharp
sealed class SymbolPresenter : IItemPresenter<Symbol>
{
    public void Present(Symbol s, RowData row)
    {
        row.Glyph = s.Kind;                       // 或 row.Moniker = KnownMonikers.Class
        row.Primary = s.Name; row.PrimaryRanges = s.MatchRanges;
        row.Secondary = s.Container;
        row.Right = s.FileName + ":" + s.Line;
        if (s.IsUClass) row.AddBadge("UCLASS", BadgeKind.UClass);
    }
}

sealed class SymbolSource : ICommandPaletteSource<Symbol>
{
    public IReadOnlyList<PaletteTab> Tabs { get; } = new[]
    {
        new PaletteTab("all", () => FeatureStrings.TabAll),
        new PaletteTab("types", () => FeatureStrings.TabTypes),
    };
    public IReadOnlyList<PaletteFilter> Filters { get; } = new[] { new PaletteFilter("nonProject", () => FeatureStrings.NonProject) };
    public IItemPresenter<Symbol> Presenter { get; } = new SymbolPresenter();

    public async Task<PaletteResult<Symbol>> QueryAsync(PaletteQuery q, IProgress<PaletteResult<Symbol>> partial, CancellationToken ct)
    {
        // 可先 partial.Report(前 200 条) 流式返回；q.Generation 已由面板去重，过期结果会被丢弃
        var items = await _index.SearchAsync(q.Text, q.TabId, q.IsFilterOn("nonProject"), ct);
        return new PaletteResult<Symbol> { Items = items, IsComplete = true };
    }
    public CodePreviewRequest GetPreview(Symbol s) => new CodePreviewRequest { FilePath = s.File, Line = s.Line };
    public bool Invoke(Symbol s, InvokeKind kind, PaletteQuery q, IReadOnlyList<Symbol> all)
    {
        if (kind == InvokeKind.OpenInResults) { /* 打开 ResultsPanel */ }
        _nav.Open(s, newTab: kind == InvokeKind.OpenInNewTab);
        return true; // true 表示关闭面板
    }
}

var palette = new CommandPalette<Symbol>("goto.symbol", new SymbolSource()) { OwnerHandle = vsMainHwnd };
palette.Prewarm();                                  // 空闲时预热，首次打开无延迟
palette.Show(PopupAnchor.CenterOf(vsMainHwnd), initialText: selectedWord);
```

`CommandPalette` 已内置以下行为：
- 输入防抖。
- 取消过期查询。
- 流式部分结果。
- 标签页（Tab 和 Shift+Tab 切换，显示计数）。
- 筛选标签。
- 搜索历史（Alt+↑ 和 Alt+↓）。
- 预览分栏（Ctrl+P 开关，记住分割比例）。
- 键盘操作：↑↓、PgUp/PgDn、Ctrl+Home/End；Enter 打开，Shift+Enter 在新标签打开，Ctrl+Enter 在结果面板打开。
- 空状态、加载中和错误状态，以及底栏快捷键提示。

其他模式：
- **`ChooserPopup<T>`**：`await chooser.ShowAsync(items, anchor, title, s => s.Name)` 返回 `ChooserResult<T>`，适合 “选择实现” 和 “选择重载” 这类场景。
- **`ContextMenuPopup`**：`await menu.ShowAsync(List<MenuEntry>, anchor)` 返回选中项或 null，适合 Alt+Enter 快速操作。它支持分组、快捷键、子菜单、输入即筛选和禁用项。
- **`ResultsPanel<T>`**：结果工具窗口（“查找引用” 等）。`SetResults(roots, summary)` 设置结果；`PreviewProvider` 提供预览；`ItemInvoked` 处理打开。`Root` 是一个 `ToolWindowShell`，可以直接作为 `ToolWindowPane.Content`。
- **`DialogShell`**：用于重命名、新建类等对话框。设置 `Form`、可选的 `Preview` 和 `Validate`，然后调用 `ShowModal(owner)`。

列表全部使用自研虚拟化（`VirtualListCore`）：只创建可见行数加 1 个行对象并循环复用，10 万条数据时滚动和键盘操作也是 O(1)。

## 性能面板

`PerformancePanel` 的 `Root` 是一个 `ToolWindowShell`，可以直接放进性能工具窗口。它包含：
- 指标卡：Engine 内存、VS 内扩展内存、CPU、索引状态和进度。
- 最近 10 分钟的 CPU 和内存曲线。
- “正在进行” 列表。
- 阶段表。
- 按部件的内存构成（托管、映射、磁盘）。
- 操作统计表，超出预算的行会高亮。
- “打开日志”、“生成诊断包”、“暂停后台索引” 三个按钮。

```csharp
var panel = new PerformancePanel();
panel.OpenLogRequested += (s, e) => diagnostics.OpenLog();
panel.DiagnosticsRequested += (s, e) => diagnostics.CreateBundle();
panel.PauseToggled += (s, paused) => indexer.SetPaused(paused);

// UI 线程上约每秒一次：
panel.Update(new PerformanceViewData
{
    EngineMemoryBytes = ..., EngineMemoryBudgetBytes = ...,
    HostMemoryBytes = ..., CpuPercent = ...,
    IndexState = ..., IndexSeverity = Severity.Info, IndexProgress = 0.42,
    CpuHistory = cpuSamples, MemoryHistory = memSamples, SampleIntervalSeconds = 1,
    Active = ..., Phases = ..., MemoryParts = ..., Operations = ...,   // OperationView.IsOverBudget 即 p95 > 预算
});
```

`Update` 只更新文本和数值，不重建可视树：表格和列表复用虚拟化行，曲线只重绘一次 `StreamGeometry`。`PerformanceViewData` 是 UI 本地的数据结构，由宿主把 Core 的诊断快照映射过来。

## 无障碍

- 所有交互控件都可以用键盘操作，焦点框使用 `Border.Focus` 令牌。
- 列表和行提供 UI Automation peer（List/ListItem，名称取 `RowData.AutomationName` 或主文本）。
- 图标按钮设置了 `AutomationProperties.Name`。
- 高对比度模式会切换到系统颜色调色板，并加粗边框。
- 字号随 “字体缩放” 设置变化，行高会随之增大，保证文字不被截断。
- 关闭 “动画” 后，所有过渡效果都会停用。

## 组件展示窗口

`new ComponentGallery().Show()`（仅用于开发命令）。工具栏可以：
- 切换主题（跟随 VS、深色、浅色、蓝色、高对比度）、风格预设、语言（中文或 English）和数据量（空、10、10 万）。
- 打开命令面板、选择器、快捷操作菜单、对话框和 Toast。

左侧导航各页分别展示原子、控件、虚拟化列表、树形结果、代码预览、反馈组件、性能面板（每秒用假数据刷新）和外观设置页。

## 构建与测试

- `dotnet build src/UEToVS.UI`：可以在 Linux 上编译，但 WPF 只能在 Windows 上运行。
- `dotnet test tests/UEToVS.UI.Logic.Tests`：测试纯逻辑层。
