using System;
using System.Diagnostics;
using System.Linq;
using System.Threading.Tasks;
using EnvDTE;
using Microsoft.VisualStudio;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Shell.Interop;
using UEToVS.Core.Diagnostics;
using UEToVS.Host.Diagnostics;
using UEToVS.Host.Logic.Build;
using UEToVS.Host.Logic.Localization;
using UEToVS.Host.Navigation;
using UEToVS.Host.Shell;

namespace UEToVS.Host.Build
{
    /// <summary>
    /// Compile errors of the last VS build (UBT / NMake → "Build" output pane). Own IVsUpdateSolutionEvents: on begin the
    /// marks are cleared; on end the Build pane's text is read once (UI thread, DTE), parsed off the UI thread
    /// (<see cref="BuildOutputParser"/>), written to &lt;solution&gt;\.vs\UEToVS\build\last-build.json (MCP get_build_errors) and
    /// published to the squiggle tagger and the Error List. Nothing runs between builds (on demand only).
    /// </summary>
    internal sealed class BuildErrorService : IVsUpdateSolutionEvents, IDisposable
    {
        private static readonly Logger L = Log.For("Build");
        private const string BuildPaneGuid = "{1BD8A850-02D1-11D1-BEE7-00A0C913D1F8}";

        private readonly HostPackage _package;
        private IVsSolutionBuildManager _manager;
        private uint _cookie;
        private DateTime _startedUtc;
        private bool _building;
        private int _generation;
        private int _navIndex = -1;

        private BuildErrorService(HostPackage package) => _package = package;

        public static BuildErrorService Instance { get; private set; }

        /// <summary>Errors of the last build in this session (UI thread). Empty until a build ends.</summary>
        public BuildErrorSet Current { get; private set; } = BuildErrorSet.Empty;

        /// <summary>Raised on the UI thread when <see cref="Current"/> changes (build begin / end / clear).</summary>
        public event EventHandler Changed;

        public static void Initialize(HostPackage package)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            if (Instance != null) return;
            var s = new BuildErrorService(package);
            s._manager = Package.GetGlobalService(typeof(SVsSolutionBuildManager)) as IVsSolutionBuildManager;
            if (s._manager == null || ErrorHandler.Failed(s._manager.AdviseUpdateSolutionEvents(s, out s._cookie)))
            {
                L.Warn("build.errors.advise.failed", "Cannot watch solution builds; compile error marks are unavailable");
                s._manager = null;
            }
            Instance = s;
        }

        // ---------------------------------------------------------------- build events (UI thread)

        public int UpdateSolution_Begin(ref int pfCancelUpdate)
        {
            Safe.Execute(L, "build.errors.begin", () =>
            {
                _building = true;
                _startedUtc = DateTime.UtcNow;
                _generation++;
                Set(BuildErrorSet.Empty);
            });
            return VSConstants.S_OK;
        }

        public int UpdateSolution_Done(int fSucceeded, int fModified, int fCancelCommand)
        {
            Finish(fCancelCommand != 0 ? "canceled" : fSucceeded != 0 ? "succeeded" : "failed");
            return VSConstants.S_OK;
        }

        public int UpdateSolution_Cancel()
        {
            Finish("canceled");
            return VSConstants.S_OK;
        }

        public int UpdateSolution_StartUpdate(ref int pfCancelUpdate) => VSConstants.S_OK;

        public int OnActiveProjectCfgChange(IVsHierarchy pIVsHierarchy) => VSConstants.S_OK;

        private void Finish(string result)
        {
            if (!_building) return;
            _building = false;
            int gen = _generation;
            var started = _startedUtc;
            var finished = DateTime.UtcNow;
            string solutionDir = _package.SolutionDirectory;
            bool isUnreal = _package.Unreal?.IsUnreal == true;
            Safe.RunAsync(L, "build.errors.collect", async () =>
            {
                await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
                if (!isUnreal) return;
                var read = Stopwatch.StartNew();
                string text = ReadBuildPane();
                double readMs = read.Elapsed.TotalMilliseconds;
                if (text == null) return;
                var (record, parseMs, unparsed, duplicates, written) = await Task.Run(() =>
                {
                    var sw = Stopwatch.StartNew();
                    var parser = new BuildOutputParser(solutionDir);
                    parser.AddText(text);
                    var r = BuildRecord.From(parser, started, finished, result);
                    double ms = sw.Elapsed.TotalMilliseconds;
                    string error = string.IsNullOrEmpty(solutionDir) ? "no solution" : BuildRecordStore.TryWrite(BuildRecordStore.PathFor(solutionDir), r);
                    return (r, ms, parser.Unparsed, parser.Duplicates, error);
                }).ConfigureAwait(false);
                await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
                L.Info("build.errors", null,
                       ("result", result), ("errors", record.Errors), ("warnings", record.Warnings), ("notes", record.Notes),
                       ("items", record.Items.Count), ("truncated", record.Truncated), ("lines", record.LinesParsed),
                       ("unparsedCandidates", unparsed), ("duplicates", duplicates), ("unparsedSamples", record.UnparsedSamples),
                       ("buildMs", record.DurationMs), ("readMs", Math.Round(readMs, 1)), ("parseMs", Math.Round(parseMs, 1)),
                       ("chars", text.Length), ("persisted", written == null), ("persistError", written));
                if (gen != _generation) return; // a newer build started meanwhile
                Set(new BuildErrorSet(record));
                if (record.Errors + record.Warnings > 0) VsStatusBar.SetText(BuildStrings.BuildErrorsFound(record.Errors, record.Warnings));
            }, ("result", result));
        }

        /// <summary>Whole text of the Build output pane, or null when it is not there. UI thread.</summary>
        private static string ReadBuildPane()
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            if (!(Package.GetGlobalService(typeof(DTE)) is DTE dte)) return null;
            try
            {
                var output = dte.Windows.Item(EnvDTE.Constants.vsWindowKindOutput)?.Object as OutputWindow;
                if (output == null) return null;
                foreach (OutputWindowPane pane in output.OutputWindowPanes)
                {
                    if (!string.Equals(pane.Guid, BuildPaneGuid, StringComparison.OrdinalIgnoreCase)) continue;
                    var doc = pane.TextDocument;
                    return doc.StartPoint.CreateEditPoint().GetText(doc.EndPoint);
                }
                L.Debug("build.errors.noPane", "Build output pane not found");
            }
            catch (Exception ex)
            {
                L.Warn("build.errors.read.failed", "Reading the Build output pane failed", ex);
            }
            return null;
        }

        private void Set(BuildErrorSet set)
        {
            Current = set ?? BuildErrorSet.Empty;
            _navIndex = -1;
            ErrorListService.Instance?.SetBuild(Current);
            Changed?.Invoke(this, EventArgs.Empty);
        }

        // ---------------------------------------------------------------- commands

        /// <summary>Opens the next error (then warning) of the last build, wrapping around.</summary>
        public void GoToNext()
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            var order = Current.NavigationOrder();
            if (order.Count == 0)
            {
                VsStatusBar.SetText(BuildStrings.NoBuildErrors);
                return;
            }
            _navIndex = BuildErrorSet.NextIndex(order.Count, _navIndex);
            var d = order[_navIndex];
            VsEditor.OpenAt(d.File, Math.Max(0, d.Line - 1), Math.Max(0, d.Column - 1));
            VsStatusBar.SetText(BuildStrings.NextBuildError(_navIndex + 1, order.Count, (string.IsNullOrEmpty(d.Code) ? "" : d.Code + ": ") + d.Message));
            L.Info("build.errors.next", null, ("index", _navIndex), ("count", order.Count), ("code", d.Code));
        }

        public void Clear()
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            bool had = Current.Count > 0;
            Set(BuildErrorSet.Empty);
            VsStatusBar.SetText(BuildStrings.BuildErrorsCleared);
            L.Info("build.errors.cleared", null, ("had", had));
        }

        /// <summary>Solution closed: forget the marks (the JSON stays for MCP).</summary>
        public void Reset()
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            _generation++;
            _building = false;
            if (Current.Count > 0) Set(BuildErrorSet.Empty);
        }

        public bool HasErrors => Current.NavigationOrder().Any();

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
            if (Instance == this) Instance = null;
        }
    }
}
