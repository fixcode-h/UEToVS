using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;
using Microsoft.VisualStudio.Shell;
using UEToVS.Core.Diagnostics;
using UEToVS.Host.Diagnostics;
using UEToVS.Host.Logic.Localization;
using UEToVS.Host.Logic.Unreal;
using UEToVS.Host.Navigation;
using UEToVS.UI.Atoms;
using UEToVS.UI.Controls;
using UEToVS.UI.Logic;
using UEToVS.UI.Molecules;
using UEToVS.UI.Shells;
using UEToVS.UI.Theming;
using Task = System.Threading.Tasks.Task;

namespace UEToVS.Host.Unreal
{
    /// <summary>
    /// “模块依赖图”窗口：依赖树、简单的分层图（WPF 画布，只画项目模块 / 当前模块的邻居）、问题列表（循环依赖、Public 头用到 Private
    /// 依赖、可能没用到的依赖）。数据在后台读（<see cref="ModuleGraphFeature"/>），include 检查边跑边刷新问题列表，关窗口即取消。
    /// </summary>
    internal sealed class ModuleGraphWindow : UtilityWindow
    {
        private static readonly Logger L = Log.For("BuildCs");
        private const double BoxWidth = 170, BoxHeight = 28, LayerGap = 90, RowGap = 16;

        private readonly HostPackage _package;
        private readonly TabStrip _tabs = new TabStrip();
        private readonly ContentControl _body = new ContentControl { Focusable = false };
        private readonly UiText _status = new UiText((string)null, TextVariant.Caption) { Margin = new Thickness(12, 6, 12, 8), TextWrapping = TextWrapping.Wrap };
        private readonly SegmentedControl _scope = new SegmentedControl();
        private readonly TreeList<TreeItem> _tree;
        private readonly VirtualList<ModuleFinding> _problems;
        private readonly Canvas _canvas = new Canvas();
        private readonly ScrollViewer _graphScroll;
        private CancellationTokenSource _cts;
        private ModuleGraphData _data;
        private string _currentFile;
        private bool _wholeProject;

        private sealed class TreeItem
        {
            public string Name;
            public bool IsPublic;
            public bool IsRoot;
            public bool Repeated;
        }

        public ModuleGraphWindow(HostPackage package)
            : base("moduleGraph", () => UnrealFileStrings.GraphTitle(UnrealFileStrings.GraphCurrentModule), new Size(920, 640))
        {
            _package = package;
            _tree = new TreeList<TreeItem>(new DelegatePresenter<TreeItem>(PresentTree)) { InvokeExpandableItems = true };
            _tree.ItemInvoked += (s, e) => Ui.Safe("ui.moduleGraph.tree.invoke", () => OpenBuildCs(e.Item?.Name));
            _problems = new VirtualList<ModuleFinding>(new DelegatePresenter<ModuleFinding>(PresentFinding));
            _problems.ItemInvoked += (s, e) => Ui.Safe("ui.moduleGraph.problem.invoke", () => OpenFinding(e.Item));
            _graphScroll = new ScrollViewer
            {
                Content = _canvas, HorizontalScrollBarVisibility = ScrollBarVisibility.Auto, VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                Focusable = false,
            };

            _scope.SetOptions(new[] { UnrealFileStrings.GraphCurrentModule, UnrealFileStrings.GraphWholeProject });
            _scope.SelectionChanged += (s, e) => Ui.Safe("ui.moduleGraph.scope", () =>
            {
                _wholeProject = _scope.SelectedIndex == 1;
                Render();
            });
            AddTitleAction(_scope);
            var refresh = new UiButton(() => UnrealFileStrings.GraphRefresh, ButtonKind.Ghost);
            refresh.Click += (s, e) => Ui.Safe("ui.moduleGraph.refresh", () => Load(_currentFile, refresh: true));
            AddTitleAction(refresh);

            _tabs.SetTabs(new[]
            {
                new TabDefinition("tree", () => UnrealFileStrings.GraphTabTree),
                new TabDefinition("graph", () => UnrealFileStrings.GraphTabGraph),
                new TabDefinition("problems", () => UnrealFileStrings.GraphTabProblems),
            });
            _tabs.SelectionChanged += (s, e) => Ui.Safe("ui.moduleGraph.tab", ShowTab);

            var root = new DockPanel { LastChildFill = true };
            DockPanel.SetDock(_tabs, Dock.Top);
            DockPanel.SetDock(_status, Dock.Bottom);
            root.Children.Add(_tabs);
            root.Children.Add(_status);
            root.Children.Add(_body);
            WindowContent = root;
            Closed += (s, e) => Safe.Execute(L, "moduleGraph.close", () => _cts?.Cancel());
            ShowTab();
        }

        private void ShowTab()
        {
            switch (_tabs.SelectedId)
            {
                case "graph": _body.Content = _graphScroll; break;
                case "problems": _body.Content = _problems; break;
                default: _body.Content = _tree; break;
            }
        }

        /// <summary>重新读数据；<paramref name="currentFile"/> 决定“当前模块”。</summary>
        public void Load(string currentFile, bool refresh)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            _currentFile = currentFile;
            _cts?.Cancel();
            var cts = _cts = new CancellationTokenSource();
            _status.Text = UnrealFileStrings.GraphLoading;
            Safe.RunAsync(L, "moduleGraph.load", async () =>
            {
                var data = await ModuleGraphFeature.LoadAsync(_package, currentFile, refresh, cts.Token).ConfigureAwait(false);
                await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync(cts.Token);
                _data = data;
                _wholeProject = data.Focus == null; // 找不到当前模块（比如没打开文件）就直接看全项目
                _scope.SelectedIndex = _wholeProject ? 1 : 0;
                Render();
                // include 检查：项目模块（加上当前模块，哪怕它是引擎的）
                var audit = data.Project.Select(m => m.Name).ToList();
                if (data.Focus != null && !audit.Contains(data.Focus, StringComparer.OrdinalIgnoreCase)) audit.Insert(0, data.Focus);
                else if (data.Focus != null)
                {
                    audit.RemoveAll(n => string.Equals(n, data.Focus, StringComparison.OrdinalIgnoreCase));
                    audit.Insert(0, data.Focus); // 先查当前模块，结果最先出来
                }
                var progress = new Progress<int>(done => Ui.Safe("ui.moduleGraph.progress", () =>
                {
                    if (cts.IsCancellationRequested || !ReferenceEquals(_data, data)) return;
                    RenderProblems();
                    _status.Text = UnrealFileStrings.GraphScanning(done, audit.Count);
                }));
                try
                {
                    await ModuleGraphFeature.AuditAsync(data, audit, progress, cts.Token);
                }
                catch (OperationCanceledException)
                {
                    return;
                }
                await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync(cts.Token);
                if (!ReferenceEquals(_data, data)) return;
                RenderProblems();
                UpdateStatus();
            });
        }

        // ------------------------------------------------------------------ 渲染

        private IEnumerable<string> Roots()
        {
            if (_data == null) return Enumerable.Empty<string>();
            if (!_wholeProject && _data.Focus != null) return new[] { _data.Focus };
            return _data.Project.Select(m => m.Name).OrderBy(n => n, StringComparer.OrdinalIgnoreCase);
        }

        private void Render()
        {
            if (_data == null) return;
            using (UiThread.Measure(L, "moduleGraph.render"))
            {
                RenderTree();
                RenderGraph();
                RenderProblems();
                UpdateStatus();
            }
        }

        private void UpdateStatus()
        {
            if (_data == null) return;
            if (_data.Project.Count == 0 && _data.Focus == null)
            {
                _status.Text = UnrealFileStrings.GraphNoModules;
                return;
            }
            int problems;
            lock (_data.Findings) problems = _data.Findings.Count;
            _status.Text = UnrealFileStrings.GraphSummary(_data.Project.Count, _data.Graph.Cycles.Count, problems) + "    " + UnrealFileStrings.GraphLegend;
        }

        private void RenderTree()
        {
            var roots = Roots().Select(r => Build(r, true, true, new HashSet<string>(StringComparer.OrdinalIgnoreCase), 0)).ToList();
            _tree.SetRoots(roots);
        }

        private TreeNode<TreeItem> Build(string name, bool isPublic, bool isRoot, HashSet<string> path, int depth)
        {
            var item = new TreeItem { Name = name, IsPublic = isPublic, IsRoot = isRoot };
            var node = _data.Graph.Get(name);
            var children = new List<TreeNode<TreeItem>>();
            // 只展开项目模块（和根），引擎模块是终点；同一条路径上再出现就是循环，不再往下
            if (node != null && (node.IsProject || isRoot) && depth < 6 && path.Add(name))
            {
                foreach (var d in node.Public.OrderBy(x => x, StringComparer.OrdinalIgnoreCase))
                    if (!string.Equals(d, name, StringComparison.OrdinalIgnoreCase)) children.Add(Build(d, true, false, path, depth + 1));
                foreach (var d in node.Private.Where(x => !node.Public.Contains(x)).OrderBy(x => x, StringComparer.OrdinalIgnoreCase))
                    if (!string.Equals(d, name, StringComparison.OrdinalIgnoreCase)) children.Add(Build(d, false, false, path, depth + 1));
                path.Remove(name);
            }
            else if (node != null && path.Contains(name)) item.Repeated = true;
            return new TreeNode<TreeItem>(item, children, isRoot);
        }

        private void PresentTree(TreeItem t, RowData row)
        {
            var node = _data?.Graph.Get(t.Name);
            row.Glyph = UEToVS.UI.Logic.SymbolKind.Module;
            row.Primary = t.Name;
            row.Secondary = t.IsRoot ? node?.Plugin : t.IsPublic ? UnrealFileStrings.GraphPublic : UnrealFileStrings.GraphPrivate;
            if (_data != null && _data.Graph.InCycle(t.Name)) row.AddBadge(UnrealFileStrings.GraphInCycle, BadgeKind.Neutral, solid: true);
            if (node != null && !node.IsProject) row.AddBadge(UnrealFileStrings.GraphEngine, BadgeKind.Module);
            if (node == null) row.AddBadge(UnrealFileStrings.GraphUnresolved, BadgeKind.Neutral);
            row.IsDimmed = node == null;
            row.ToolTip = node?.BuildCsPath;
        }

        private void RenderProblems()
        {
            if (_data == null) return;
            List<ModuleFinding> list;
            lock (_data.Findings) list = _data.Findings.ToList();
            var scope = new HashSet<string>(Roots(), StringComparer.OrdinalIgnoreCase);
            var shown = list.Where(f => _wholeProject || scope.Contains(f.Module) || f.Kind == ModuleFindingKind.Cycle && f.Dependency.Split(new[] { ", " }, StringSplitOptions.None).Any(scope.Contains))
                            .OrderBy(f => f.Kind).ThenBy(f => f.Module, StringComparer.OrdinalIgnoreCase).ThenBy(f => f.File, StringComparer.OrdinalIgnoreCase).ToList();
            _problems.SetItems(shown, true);
            _tabs.SetCount("problems", shown.Count);
        }

        private void PresentFinding(ModuleFinding f, RowData row)
        {
            bool en = UnrealFileStrings.English;
            row.Glyph = f.Kind == ModuleFindingKind.UnusedDependency ? UEToVS.UI.Logic.SymbolKind.Module : UEToVS.UI.Logic.SymbolKind.File;
            row.Primary = f.Kind == ModuleFindingKind.Cycle ? f.Dependency : f.Module + " → " + f.Dependency;
            row.Secondary = f.Message(en);
            row.Right = f.Kind == ModuleFindingKind.PublicHeaderUsesPrivateDependency ? System.IO.Path.GetFileName(f.File) + ":" + (f.Line + 1) : null;
            row.AddBadge(f.Kind == ModuleFindingKind.Cycle ? UnrealFileStrings.GraphCycleKind
                         : f.Kind == ModuleFindingKind.PublicHeaderUsesPrivateDependency ? UnrealFileStrings.GraphPrivateInPublicKind : UnrealFileStrings.GraphUnusedKind,
                         BadgeKind.Neutral, solid: f.Kind != ModuleFindingKind.UnusedDependency);
            row.IsDimmed = f.Kind == ModuleFindingKind.UnusedDependency;
            row.ToolTip = f.Message(en);
        }

        /// <summary>
        /// 分层图：当前模块视图画它、它的依赖和依赖它的项目模块；全项目视图只画项目模块（引擎模块太多）。依赖别人的在左，被依赖的在右。
        /// </summary>
        private void RenderGraph()
        {
            _canvas.Children.Clear();
            var g = _data.Graph;
            HashSet<string> visible;
            if (!_wholeProject && _data.Focus != null)
            {
                var f = g.Get(_data.Focus);
                visible = new HashSet<string>(new[] { _data.Focus }.Concat(f?.Dependencies ?? Enumerable.Empty<string>()).Concat(g.Dependents(_data.Focus)), StringComparer.OrdinalIgnoreCase);
            }
            else visible = new HashSet<string>(_data.Project.Select(m => m.Name), StringComparer.OrdinalIgnoreCase);
            if (visible.Count == 0) return;
            var layout = g.Layout(visible);
            var centers = new Dictionary<string, Point>(StringComparer.OrdinalIgnoreCase);
            double maxX = 0, maxY = 0;
            foreach (var kv in layout)
            {
                double x = 16 + kv.Value.Layer * (BoxWidth + LayerGap);
                double y = 16 + kv.Value.Row * (BoxHeight + RowGap);
                centers[kv.Key] = new Point(x, y);
                maxX = Math.Max(maxX, x + BoxWidth);
                maxY = Math.Max(maxY, y + BoxHeight);
            }
            // 先画边（在方块下面）
            foreach (var e in g.Edges)
            {
                if (!centers.TryGetValue(e.From, out var a) || !centers.TryGetValue(e.To, out var b)) continue;
                var from = new Point(a.X + BoxWidth, a.Y + BoxHeight / 2);
                var to = new Point(b.X, b.Y + BoxHeight / 2);
                if (b.X <= a.X) // 同层或往回（循环）：从下边连到下边
                {
                    from = new Point(a.X + BoxWidth / 2, a.Y + BoxHeight);
                    to = new Point(b.X + BoxWidth / 2, b.Y + BoxHeight);
                }
                var line = new Line { X1 = from.X, Y1 = from.Y, X2 = to.X, Y2 = to.Y, StrokeThickness = e.InCycle ? 1.6 : 1, IsHitTestVisible = false };
                if (!e.IsPublic) line.StrokeDashArray = new DoubleCollection { 4, 3 };
                line.SetResourceReference(Shape.StrokeProperty, e.InCycle ? Tokens.Severity(Severity.Error) : Tokens.BorderStrong);
                _canvas.Children.Add(line);
                _canvas.Children.Add(Arrow(from, to, e.InCycle));
            }
            foreach (var kv in centers)
            {
                var node = g.Get(kv.Key);
                var text = new UiText(kv.Key, TextVariant.Body)
                {
                    TextTrimming = TextTrimming.CharacterEllipsis, VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Center,
                    Margin = new Thickness(6, 0, 6, 0),
                };
                if (node == null || !node.IsProject) text.SetResourceReference(TextBlock.ForegroundProperty, Tokens.TextSecondary);
                var box = new Border
                {
                    Width = BoxWidth, Height = BoxHeight, CornerRadius = new CornerRadius(4), Child = text,
                    BorderThickness = new Thickness(string.Equals(kv.Key, _data.Focus, StringComparison.OrdinalIgnoreCase) ? 2 : 1),
                    ToolTip = node?.BuildCsPath ?? UnrealFileStrings.GraphUnresolved,
                };
                box.SetResourceReference(Border.BackgroundProperty, Tokens.SurfaceRaised);
                box.SetResourceReference(Border.BorderBrushProperty, g.InCycle(kv.Key) ? Tokens.Severity(Severity.Error)
                                                                     : string.Equals(kv.Key, _data.Focus, StringComparison.OrdinalIgnoreCase) ? Tokens.Accent : Tokens.BorderSubtle);
                string name = kv.Key;
                box.MouseLeftButtonDown += (s, e) => Ui.Safe("ui.moduleGraph.box", () =>
                {
                    if (e.ClickCount == 2) OpenBuildCs(name);
                });
                Canvas.SetLeft(box, kv.Value.X);
                Canvas.SetTop(box, kv.Value.Y);
                _canvas.Children.Add(box);
            }
            _canvas.Width = maxX + 16;
            _canvas.Height = maxY + 24;
        }

        private static Polygon Arrow(Point from, Point to, bool cycle)
        {
            var v = to - from;
            if (v.Length < 1) v = new Vector(1, 0);
            v.Normalize();
            var n = new Vector(-v.Y, v.X);
            var p = new Polygon
            {
                Points = new PointCollection { to, to - v * 7 + n * 3.5, to - v * 7 - n * 3.5 },
                IsHitTestVisible = false,
            };
            p.SetResourceReference(Shape.FillProperty, cycle ? Tokens.Severity(Severity.Error) : Tokens.BorderStrong);
            return p;
        }

        // ------------------------------------------------------------------ 跳转

        private void OpenBuildCs(string module)
        {
            var node = _data?.Graph.Get(module);
            if (node?.BuildCsPath == null) return;
            L.Info("moduleGraph.open", null, ("module", module));
            VsEditor.OpenAt(node.BuildCsPath, 0, 0);
        }

        private void OpenFinding(ModuleFinding f)
        {
            if (f?.File == null) return;
            L.Info("moduleGraph.openFinding", null, ("kind", f.Kind.ToString()), ("module", f.Module));
            if (f.Line >= 0) NavigationService.Instance.JumpTo(f.File, f.Line, 0, "moduleGraph");
            else VsEditor.OpenAt(f.File, 0, 0);
        }
    }
}
