using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using EnvDTE;
using EnvDTE80;
using EnvDTE90a;
using Microsoft.VisualStudio.Shell;
using Newtonsoft.Json.Linq;
using UEToVS.Core.Diagnostics;
using UEToVS.Host.Logic.Debugging;

namespace UEToVS.Host.Debugging
{
    /// <summary>
    /// Executes debug control ops with VS's automation model (EnvDTE Debugger / SolutionBuild / Output window), i.e. exactly
    /// what the toolbar buttons do: "start" is F5 on the startup project ("本地 Windows 调试器"), "restart" is Ctrl+Shift+F5.
    /// UEToVS adds no debugger of its own (design §1.4).
    /// <para>All DTE calls run on the UI thread; the debugger, build and output events feed <see cref="Tracker"/> so
    /// <c>wait</c> can block a pipe connection without blocking VS. Run-commands never use DTE's synchronous
    /// "WaitForBreakOrEnd" variants: they would freeze VS until the debuggee stops.</para>
    /// </summary>
    internal sealed partial class DteDebugBackend : IDebugBackend, IDisposable
    {
        private static readonly Logger L = Log.For("Debug");

        /// <summary>Breakpoints set through debug control carry this tag: "clear" removes only these by default.</summary>
        internal const string AgentTag = "uetovs-agent";

        /// <summary>Temporary breakpoint of step toLine; removed on the next break or end of debugging.</summary>
        internal const string RunToTag = "uetovs-runto";

        private readonly HostPackage _package;
        private readonly DTE2 _dte;
        private readonly string _solutionDir;
        private readonly string _projectDir;

        // DTE event objects must stay referenced, or the COM wrapper is collected and the events stop.
        private DebuggerEvents _debuggerEvents;
        private BuildEvents _buildEvents;
        private _dispDebuggerEvents_OnEnterBreakModeEventHandler _onBreak;
        private _dispDebuggerEvents_OnEnterRunModeEventHandler _onRun;
        private _dispDebuggerEvents_OnEnterDesignModeEventHandler _onDesign;
        private _dispBuildEvents_OnBuildBeginEventHandler _onBuildBegin;
        private _dispBuildEvents_OnBuildDoneEventHandler _onBuildDone;
        private DateTime _buildStartedUtc;

        private DteDebugBackend(HostPackage package, DTE2 dte, string solutionDir, string projectFile)
        {
            _package = package;
            _dte = dte;
            _solutionDir = solutionDir;
            _projectDir = string.IsNullOrEmpty(projectFile) ? null : Path.GetDirectoryName(projectFile);
        }

        public BreakTracker Tracker { get; } = new BreakTracker();

        public static async Task<DteDebugBackend> CreateAsync(HostPackage package, string solutionDir, string projectFile)
        {
            await package.JoinableTaskFactory.SwitchToMainThreadAsync(package.DisposalToken);
            var dte = await package.GetServiceAsync(typeof(DTE)) as DTE2 ?? throw new InvalidOperationException("DTE unavailable");
            var b = new DteDebugBackend(package, dte, solutionDir, projectFile);
            b.Subscribe();
            return b;
        }

        private void Subscribe()
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            _debuggerEvents = _dte.Events.DebuggerEvents;
            _buildEvents = _dte.Events.BuildEvents;
            _onBreak = (dbgEventReason reason, ref dbgExecutionAction action) =>
            {
                RemoveRunToBreakpoints();
                Tracker.OnBreak(DebugHelpers.ReasonText(reason.ToString()));
            };
            _onRun = reason =>
            {
                RestoreLaunchArgs(); // the process has its command line now
                Tracker.OnRun();
            };
            _onDesign = reason =>
            {
                RemoveRunToBreakpoints();
                RestoreLaunchArgs();
                Tracker.OnDesign(DebugHelpers.ReasonText(reason.ToString()));
            };
            _onBuildBegin = (scope, action) =>
            {
                _buildStartedUtc = DateTime.UtcNow;
                Tracker.OnBuildBegin();
            };
            _onBuildDone = (scope, action) =>
            {
                ThreadHelper.ThrowIfNotOnUIThread();
                int failed = 0;
                try { failed = _dte.Solution.SolutionBuild.LastBuildInfo; } catch (Exception) { } // no build info: count it as success, the build pane still shows errors
                if (failed != 0) RestoreLaunchArgs(); // F5 will not launch
                Tracker.OnBuildDone(failed == 0);
            };
            _debuggerEvents.OnEnterBreakMode += _onBreak;
            _debuggerEvents.OnEnterRunMode += _onRun;
            _debuggerEvents.OnEnterDesignMode += _onDesign;
            _buildEvents.OnBuildBegin += _onBuildBegin;
            _buildEvents.OnBuildDone += _onBuildDone;
            Tracker.Reset(ToMode(_dte.Debugger.CurrentMode));
        }

        public void Dispose()
        {
            ThreadHelper.JoinableTaskFactory.Run(async () =>
            {
                await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
                try
                {
                    RestoreLaunchArgs();
                    if (_debuggerEvents != null)
                    {
                        _debuggerEvents.OnEnterBreakMode -= _onBreak;
                        _debuggerEvents.OnEnterRunMode -= _onRun;
                        _debuggerEvents.OnEnterDesignMode -= _onDesign;
                    }
                    if (_buildEvents != null)
                    {
                        _buildEvents.OnBuildBegin -= _onBuildBegin;
                        _buildEvents.OnBuildDone -= _onBuildDone;
                    }
                }
                catch (Exception) { /* VS shutting down */ }
            });
        }

        // ================================================================== dispatch

        public async Task<JObject> ExecuteAsync(string op, DebugArgs a, CancellationToken ct)
        {
            L.Info("debug.op", null, ("op", op));
            if (op == DebugProtocol.Wait) return await WaitAsync(a.Int("timeoutMs", 30000, 0, DebugProtocol.MaxWaitMs), ct);

            await _package.JoinableTaskFactory.SwitchToMainThreadAsync(ct);
            JObject result;
            int waitMs;
            switch (op)
            {
                case DebugProtocol.Status: return Snapshot(a.Int("frames", 5, 0, 20));
                case DebugProtocol.Start: result = Start(a); waitMs = 0; break;
                case DebugProtocol.Restart: result = Restart(); waitMs = 0; break;
                case DebugProtocol.Stop: return Stop(a.Bool("detach", false));
                case DebugProtocol.Attach: result = Attach(a.IntOrNull("pid"), a.Str("name")); waitMs = 0; break;
                case DebugProtocol.Build: result = Build(a); waitMs = a.Int("waitMs", 0, 0, DebugProtocol.MaxWaitMs); break;
                case DebugProtocol.Pause: result = Pause(); waitMs = a.Int("waitMs", 5000, 0, DebugProtocol.MaxWaitMs); break;
                case DebugProtocol.Continue: result = Continue(); waitMs = a.Int("waitMs", 0, 0, DebugProtocol.MaxWaitMs); break;
                case DebugProtocol.Step: result = Step(a); waitMs = a.Int("waitMs", 10000, 0, DebugProtocol.MaxWaitMs); break;
                case DebugProtocol.BpList: return ListBreakpoints(a);
                case DebugProtocol.BpAdd: return AddBreakpoint(a);
                case DebugProtocol.BpRemove: return RemoveBreakpoints(a);
                case DebugProtocol.BpEnable: return EnableBreakpoints(a);
                case DebugProtocol.BpClear: return ClearBreakpoints(a);
                case DebugProtocol.Stack: return StackTrace(a);
                case DebugProtocol.Threads: return Threads(a);
                case DebugProtocol.Locals: return Locals(a);
                case DebugProtocol.Eval: return Eval(a);
                case DebugProtocol.Output: return Output(a);
                case DebugProtocol.Dump: return Dump(a);
                default: throw new DebugControlException("不认识的操作 " + op);
            }
            if (waitMs <= 0) return result;
            // Run-command + wait in one call (step defaults to it): answer with where it stopped.
            var waited = await WaitAsync(waitMs, ct);
            waited["action"] = result;
            return waited;
        }

        // ================================================================== state

        private static DebugMode ToMode(dbgDebugMode m) =>
            m == dbgDebugMode.dbgBreakMode ? DebugMode.Break : m == dbgDebugMode.dbgRunMode ? DebugMode.Run : DebugMode.Design;

        private static string ModeText(DebugMode m) => m == DebugMode.Break ? "break" : m == DebugMode.Run ? "run" : "design";

        private DebugMode CurrentMode
        {
            get
            {
                ThreadHelper.ThrowIfNotOnUIThread();
                return ToMode(_dte.Debugger.CurrentMode);
            }
        }

        private void RequireBreak()
        {
            var m = CurrentMode;
            if (m == DebugMode.Break) return;
            throw new DebugControlException(m == DebugMode.Run
                ? "UE 正在运行，没有停下：先下断点再 wait，或者 pause 暂停"
                : "没有在调试：先 start 启动或 attach 附加到 UE");
        }

        /// <summary>status: mode, startup project, configuration, debugged processes, where it stopped (with top frames).</summary>
        private JObject Snapshot(int frames)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            var mode = CurrentMode;
            var o = new JObject
            {
                ["mode"] = ModeText(mode),
                ["building"] = Tracker.Building,
            };
            if (Tracker.LaunchPending) o["launchPending"] = true;
            if (Tracker.LastBuildSucceeded == false) o["lastBuildFailed"] = true;
            if (Tracker.LastReason != null) o["lastReason"] = Tracker.LastReason;
            try
            {
                var sb = _dte.Solution.SolutionBuild;
                if (sb.StartupProjects is object[] sp && sp.Length > 0) o["startupProject"] = Path.GetFileNameWithoutExtension(sp[0] as string ?? "");
                if (sb.ActiveConfiguration is SolutionConfiguration2 c) o["configuration"] = c.Name + "|" + c.PlatformName;
            }
            catch (Exception) { /* no solution build manager yet */ }

            if (mode != DebugMode.Design)
            {
                var procs = new JArray();
                foreach (EnvDTE.Process p in _dte.Debugger.DebuggedProcesses)
                    procs.Add(new JObject { ["pid"] = p.ProcessID, ["name"] = Path.GetFileName(p.Name ?? "") });
                o["processes"] = procs;
            }
            if (mode == DebugMode.Break)
            {
                o["breakCount"] = Tracker.BreakCount;
                var t = _dte.Debugger.CurrentThread;
                if (t != null) o["thread"] = new JObject { ["id"] = t.ID, ["name"] = t.Name ?? "" };
                if (_dte.Debugger.CurrentStackFrame is StackFrame2 f) o["location"] = FrameJson(f, -1);
                if (Tracker.LastReason == "breakpoint")
                {
                    try
                    {
                        var bp = _dte.Debugger.BreakpointLastHit;
                        if (bp != null) o["breakpoint"] = BreakpointLabel(bp);
                    }
                    catch (Exception) { } // last-hit breakpoint unavailable (e.g. deleted since): status still useful without it
                }
                if (frames > 0) o["stack"] = Frames(0, frames, out _, out _);
            }
            try { o["breakpoints"] = _dte.Debugger.Breakpoints.Count; } catch (Exception) { } // count is optional in status
            return o;
        }

        private async Task<JObject> WaitAsync(int timeoutMs, CancellationToken ct)
        {
            DateTime since = _buildStartedUtc;
            string outcome = await Tracker.WaitAsync(timeoutMs, ct).ConfigureAwait(false);
            JArray errors = null;
            if (outcome == WaitOutcome.BuildFailed || outcome == WaitOutcome.BuildDone)
                errors = await BuildErrorsAsync(since, ct).ConfigureAwait(false);
            await _package.JoinableTaskFactory.SwitchToMainThreadAsync(ct);
            var o = Snapshot(outcome == WaitOutcome.Break ? 8 : 0);
            o["event"] = outcome;
            if (errors != null && errors.Count > 0) o["buildErrors"] = errors;
            if (outcome == WaitOutcome.Timeout)
                o["hint"] = Tracker.Building ? "还在编译，再 wait" : "这段时间没停下：再 wait，或者 pause 看它在做什么";
            return o;
        }

        /// <summary>
        /// The first errors of the build that just ended, from the host's own build error parser (the record it writes for
        /// get_build_errors). It parses after the build event, so allow it a moment to catch up.
        /// </summary>
        private static async Task<JArray> BuildErrorsAsync(DateTime buildStartedUtc, CancellationToken ct)
        {
            for (int i = 0; i < 30; i++)
            {
                var rec = UEToVS.Host.Build.BuildErrorService.Instance?.Current?.Record;
                if (rec != null && rec.FinishedUtc >= buildStartedUtc)
                {
                    var arr = new JArray();
                    foreach (var d in rec.Items.Where(d => d.Severity == UEToVS.Host.Logic.Build.BuildSeverity.Error).Take(10))
                        arr.Add(new JObject { ["file"] = d.File ?? "", ["line"] = d.Line, ["code"] = d.Code ?? "", ["message"] = d.Message ?? "" });
                    return arr;
                }
                await Task.Delay(100, ct).ConfigureAwait(false);
            }
            return null;
        }

        private static JObject FrameJson(StackFrame2 f, int index)
        {
            var o = new JObject();
            if (index >= 0) o["i"] = index;
            o["function"] = f.FunctionName ?? "";
            string file = null;
            uint line = 0;
            try
            {
                file = f.FileName;
                line = f.LineNumber;
            }
            catch (Exception) { /* no symbols for this frame */ }
            if (!string.IsNullOrEmpty(file))
            {
                o["file"] = file;
                o["line"] = (int)line;
            }
            string module = f.Module;
            if (!string.IsNullOrEmpty(module)) o["module"] = Path.GetFileName(module);
            return o;
        }

        private JArray Frames(int offset, int limit, out int total, out int current)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            var arr = new JArray();
            total = 0;
            current = -1;
            var thread = _dte.Debugger.CurrentThread;
            if (thread == null) return arr;
            var cur = _dte.Debugger.CurrentStackFrame;
            int i = 0;
            foreach (StackFrame f in thread.StackFrames)
            {
                if (ReferenceEquals(f, cur) || (cur != null && current < 0 && SameFrame(f, cur))) current = i;
                if (i >= offset && arr.Count < limit && f is StackFrame2 f2) arr.Add(FrameJson(f2, i));
                i++;
            }
            total = i;
            return arr;
        }

        private static bool SameFrame(StackFrame a, StackFrame b)
        {
            try { return a is StackFrame2 a2 && b is StackFrame2 b2 && a2.Depth == b2.Depth; }
            catch (Exception) { return false; }
        }
    }
}
