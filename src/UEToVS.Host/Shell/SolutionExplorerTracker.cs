using System;
using System.Diagnostics;
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
