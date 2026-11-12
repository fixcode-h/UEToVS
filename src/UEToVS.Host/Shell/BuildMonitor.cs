using System;
using Microsoft.VisualStudio;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Shell.Interop;
using UEToVS.Core.Diagnostics;
using UEToVS.Host.Diagnostics;
using Task = System.Threading.Tasks.Task;

namespace UEToVS.Host.Shell
{
    /// <summary>
    /// Watches VS solution builds (IVsUpdateSolutionEvents) and reports begin/end so the engine can pause background
    /// indexing while UBT runs (§9.4). Callbacks arrive on the UI thread and only record state + post work.
    /// </summary>
    internal sealed class BuildMonitor : IVsUpdateSolutionEvents, IDisposable
    {
        private static readonly Logger L = Log.For("Build");
        private readonly Action<bool> _onChanged;
        private IVsSolutionBuildManager _manager;
        private uint _cookie;
        private DateTime _startedUtc;

        public BuildMonitor(Action<bool> onChanged)
        {
            _onChanged = onChanged;
        }

        public bool IsBuilding { get; private set; }

        public async Task AdviseAsync(AsyncPackage package)
        {
            await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
            _manager = await package.GetServiceAsync(typeof(SVsSolutionBuildManager)) as IVsSolutionBuildManager;
            if (_manager == null || ErrorHandler.Failed(_manager.AdviseUpdateSolutionEvents(this, out _cookie)))
            {
                L.Warn("build.advise.failed", "Cannot watch solution builds; background indexing will not pause during builds");
                _manager = null;
            }
        }

        private void Set(bool building, string reason)
        {
            Safe.Execute(L, "build.event", () =>
            {
                if (IsBuilding == building) return;
                IsBuilding = building;
                if (building) _startedUtc = DateTime.UtcNow;
                L.Info(building ? "build.begin" : "build.end", null, ("reason", reason),
                       ("sec", building ? (object)null : Math.Round((DateTime.UtcNow - _startedUtc).TotalSeconds, 1)));
                _onChanged?.Invoke(building);
            });
        }

        public int UpdateSolution_Begin(ref int pfCancelUpdate)
        {
            Set(true, "begin");
            return VSConstants.S_OK;
        }

        public int UpdateSolution_Done(int fSucceeded, int fModified, int fCancelCommand)
        {
            Set(false, fSucceeded != 0 ? "succeeded" : "failed");
            return VSConstants.S_OK;
        }

        public int UpdateSolution_StartUpdate(ref int pfCancelUpdate) => VSConstants.S_OK;

        public int UpdateSolution_Cancel()
        {
            Set(false, "canceled");
            return VSConstants.S_OK;
        }

        public int OnActiveProjectCfgChange(IVsHierarchy pIVsHierarchy) => VSConstants.S_OK;

        public void Dispose()
        {
            try
            {
                if (_manager != null && _cookie != 0 && ThreadHelper.CheckAccess()) _manager.UnadviseUpdateSolutionEvents(_cookie);
            }
            catch (Exception)
            {
                // shutting down
            }
            _manager = null;
        }
    }
}
