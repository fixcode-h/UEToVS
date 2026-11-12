using System;
using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using Microsoft.VisualStudio;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Shell.Interop;
using UEToVS.Core.Diagnostics;
using UEToVS.Host.Diagnostics;
using UEToVS.Host.Logic.Navigation;
using UEToVS.Host.Navigation;
using Task = System.Threading.Tasks.Task;

namespace UEToVS.Host.Shell
{
    /// <summary>
    /// "在解决方案资源管理器中定位 C++ 文件" (Rider's Autoscroll from Source, scoped to C++): when the active document changes
    /// to a C++ file, expands and selects it in Solution Explorer without taking focus from the editor.
    /// Kept cheap for huge UE solutions: it only listens to the active-document element (SEID_DocumentFrame, not every
    /// selection change), debounces rapid tab switches, takes the hierarchy/item id straight from the window frame
    /// (no lookup through the solution) and never creates Solution Explorer if it is not open.
    /// </summary>
    internal sealed class SolutionExplorerTracker : IVsSelectionEvents, IDisposable
    {
        private static readonly Logger L = Log.For("SolutionExplorer");
        private static Guid _solutionExplorer = VSConstants.StandardToolWindows.SolutionExplorer;

        private IVsMonitorSelection _monitor;
        private IVsUIShell _shell;
        private uint _cookie;
        private DispatcherTimer _timer;
        private IVsWindowFrame _pending;

        public async Task AdviseAsync(AsyncPackage package)
        {
            await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
            _shell = await package.GetServiceAsync(typeof(SVsUIShell)) as IVsUIShell;
            _monitor = await package.GetServiceAsync(typeof(SVsShellMonitorSelection)) as IVsMonitorSelection;
            if (_shell == null || _monitor == null || ErrorHandler.Failed(_monitor.AdviseSelectionEvents(this, out _cookie)))
            {
                L.Warn("solutionExplorer.advise.failed", "Cannot watch the active document; Solution Explorer sync is unavailable");
                _monitor = null;
                return;
            }
            _timer = new DispatcherTimer(DispatcherPriority.Background) { Interval = SolutionExplorerSync.Debounce };
            _timer.Tick += (s, e) =>
            {
                _timer.Stop();
                var frame = _pending;
                _pending = null;
                if (frame != null) Safe.Execute(L, "solutionExplorer.reveal", () => Reveal(frame));
            };
        }

        public int OnElementValueChanged(uint elementid, object varValueOld, object varValueNew)
        {
            if (elementid != (uint)VSConstants.VSSELELEMID.SEID_DocumentFrame || _timer == null) return VSConstants.S_OK;
            Safe.Execute(L, "solutionExplorer.documentChanged", () =>
            {
                // Cheap filter first: nothing is queued unless the feature is on in a UE solution.
                var s = NavigationContext.Settings;
                if (!s.SyncSolutionExplorer || !NavigationContext.IsUnrealSolution || !(varValueNew is IVsWindowFrame frame))
                {
                    _pending = null;
                    return;
                }
                _pending = frame;
                _timer.Stop();
                _timer.Start();
            });
            return VSConstants.S_OK;
        }

        private void Reveal(IVsWindowFrame frame)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            var s = NavigationContext.Settings;
            if (ErrorHandler.Failed(frame.GetProperty((int)__VSFPROPID.VSFPROPID_pszMkDocument, out object doc))) return;
            string path = doc as string;
            if (!SolutionExplorerSync.ShouldReveal(s.SyncSolutionExplorer, NavigationContext.IsUnrealSolution, path)) return;

            if (ErrorHandler.Failed(frame.GetProperty((int)__VSFPROPID.VSFPROPID_Hierarchy, out object h)) || !(h is IVsUIHierarchy hierarchy)) return;
            if (ErrorHandler.Failed(frame.GetProperty((int)__VSFPROPID.VSFPROPID_ItemID, out object id))) return;
            uint itemId = id is int i ? unchecked((uint)i) : id is uint u ? u : VSConstants.VSITEMID_NIL;
            if (itemId == VSConstants.VSITEMID_NIL || itemId == VSConstants.VSITEMID_ROOT) return;

            // Only an existing, open Solution Explorer (FTW_fFindFirst never creates it).
            if (ErrorHandler.Failed(_shell.FindToolWindow((uint)__VSFINDTOOLWIN.FTW_fFindFirst, ref _solutionExplorer, out IVsWindowFrame seFrame)) || seFrame == null) return;
            if (seFrame.IsVisible() != VSConstants.S_OK) return;
            if (ErrorHandler.Failed(seFrame.GetProperty((int)__VSFPROPID.VSFPROPID_DocView, out object view)) || !(view is IVsUIHierarchyWindow window)) return;

            var sw = Stopwatch.StartNew();
            int hr = window.ExpandItem(hierarchy, itemId, EXPANDFLAGS.EXPF_SelectItem);
            sw.Stop();
            // Files outside any project (Miscellaneous Files) are not in the tree: ExpandItem fails, nothing to do.
            L.Debug("solutionExplorer.reveal", null, ("file", path), ("hr", hr), ("ms", Math.Round(sw.Elapsed.TotalMilliseconds, 1)));
            // ExpandItem only scrolls the item just into view (often the last visible row): center it once the tree has laid out.
            if (ErrorHandler.Succeeded(hr))
            {
                var once = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(60) };
                once.Tick += (s2, e2) =>
                {
                    once.Stop();
                    Safe.Execute(L, "solutionExplorer.center", () => CenterSelected(seFrame, path));
                };
                once.Start();
            }
        }

        /// <summary>
        /// Scrolls Solution Explorer so its selected row sits in the middle. Solution Explorer is a WPF tree hosted in the
        /// tool window; we find its ScrollViewer and the selected row through the visual tree (no private APIs), and do
        /// nothing when the layout is not what we expect.
        /// </summary>
        private static void CenterSelected(IVsWindowFrame seFrame, string path)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            if (seFrame.IsVisible() != VSConstants.S_OK) return;
            var root = SolutionExplorerRoot(seFrame);
            var row = root == null ? null : FindSelectedRow(root);
            var viewer = row == null ? null : FindAncestor<ScrollViewer>(row);
            if (viewer == null)
            {
                L.Debug("solutionExplorer.center.skipped", null, ("file", path), ("root", root != null), ("row", row != null));
                return;
            }
            double top = row.TransformToAncestor(viewer).Transform(new Point(0, 0)).Y;
            double height = row.ActualHeight;
            // The row's header only (a TreeViewItem's ActualHeight includes its expanded children).
            if (row is TreeViewItem tvi && tvi.IsExpanded && tvi.HasItems && FindChild<FrameworkElement>(tvi, e => e.Name == "PART_Header") is FrameworkElement header)
                height = header.ActualHeight;
            var target = SolutionExplorerSync.CenteredOffset(viewer.VerticalOffset, top, height, viewer.ActualHeight, viewer.CanContentScroll, viewer.ScrollableHeight);
            if (target.HasValue) viewer.ScrollToVerticalOffset(target.Value);
            L.Debug("solutionExplorer.center", null, ("file", path), ("top", Math.Round(top)), ("viewport", Math.Round(viewer.ActualHeight)),
                    ("logical", viewer.CanContentScroll), ("from", viewer.VerticalOffset), ("to", target));
        }

        private static DependencyObject SolutionExplorerRoot(IVsWindowFrame seFrame)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            // The tool window's WPF content: VSFPROPID_DocView may itself be a FrameworkElement, otherwise go through its HWND.
            if (ErrorHandler.Succeeded(seFrame.GetProperty((int)__VSFPROPID.VSFPROPID_DocView, out object view)) && view is DependencyObject d) return d;
            if (view is WindowPane pane && pane.Content is DependencyObject content) return content;
            if (view is System.Windows.Interop.IWin32Window w32 && HwndSource.FromHwnd(w32.Handle) is HwndSource src) return src.RootVisual;
            // Last resort: Solution Explorer's tree control by type name, in any WPF source (docked, floating, auto-hidden).
            foreach (PresentationSource ps in PresentationSource.CurrentSources)
            {
                if (ps?.RootVisual == null) continue;
                var tree = FindChild<FrameworkElement>(ps.RootVisual, e => e.GetType().Name.IndexOf("SolutionPivotTreeView", StringComparison.Ordinal) >= 0);
                if (tree != null) return tree;
            }
            return null;
        }

        /// <summary>The selected, visible tree row (TreeViewItem or ListBoxItem: Solution Explorer's tree is a virtualized list).</summary>
        private static FrameworkElement FindSelectedRow(DependencyObject root) =>
            FindChild<FrameworkElement>(root, e => (e is TreeViewItem || e is ListBoxItem) && e.IsVisible && Selector.GetIsSelected(e)
                                                   && !(e is TreeViewItem t && t.IsExpanded && HasSelectedDescendant(t)));

        private static bool HasSelectedDescendant(TreeViewItem t) =>
            FindChild<FrameworkElement>(t, e => !ReferenceEquals(e, t) && (e is TreeViewItem || e is ListBoxItem) && Selector.GetIsSelected(e)) != null;

        private static T FindChild<T>(DependencyObject root, Func<T, bool> match) where T : DependencyObject
        {
            var queue = new System.Collections.Generic.Queue<DependencyObject>();
            queue.Enqueue(root);
            int visited = 0;
            while (queue.Count > 0 && visited++ < 20000)
            {
                var d = queue.Dequeue();
                if (d is T t && match(t)) return t;
                if (!(d is Visual) && !(d is System.Windows.Media.Media3D.Visual3D)) continue;
                int n = VisualTreeHelper.GetChildrenCount(d);
                for (int i = 0; i < n; i++) queue.Enqueue(VisualTreeHelper.GetChild(d, i));
            }
            return null;
        }

        private static T FindAncestor<T>(DependencyObject d) where T : DependencyObject
        {
            for (var p = d == null ? null : VisualTreeHelper.GetParent(d); p != null; p = VisualTreeHelper.GetParent(p))
                if (p is T t) return t;
            return null;
        }

        public int OnSelectionChanged(IVsHierarchy pHierOld, uint itemidOld, IVsMultiItemSelect pMISOld, ISelectionContainer pSCOld,
                                      IVsHierarchy pHierNew, uint itemidNew, IVsMultiItemSelect pMISNew, ISelectionContainer pSCNew) => VSConstants.S_OK;

        public int OnCmdUIContextChanged(uint dwCmdUICookie, int fActive) => VSConstants.S_OK;

        public void Dispose()
        {
            try
            {
                _timer?.Stop();
                if (_monitor != null && _cookie != 0 && ThreadHelper.CheckAccess()) _monitor.UnadviseSelectionEvents(_cookie);
            }
            catch (Exception)
            {
                // VS is shutting down.
            }
            _monitor = null;
            _cookie = 0;
        }
    }
}
