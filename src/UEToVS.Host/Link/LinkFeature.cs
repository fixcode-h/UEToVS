using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.VisualStudio.Shell;
using UEToVS.Core.Diagnostics;
using UEToVS.Host.Diagnostics;
using UEToVS.Host.Logic.Actions;
using UEToVS.Host.Logic.Commands;
using UEToVS.Host.Logic.Link;
using UEToVS.Host.Logic.Localization;
using UEToVS.Host.Logic.Navigation;
using UEToVS.Host.Logic.Search;
using UEToVS.Host.Logic.Settings;
using UEToVS.Host.Navigation;
using UEToVS.Host.Search;
using UEToVS.Host.Integration;
using UEToVS.Host.Shell;

namespace UEToVS.Host.Link
{
    /// <summary>
    /// v0.4.5 features on top of the UEToVSLink protocol 2: the UE log window, the automation test runner and Live Coding
    /// error collection. Owns the one streaming connection (<see cref="EditorLinkSubscription"/>) shared by all of them;
    /// a topic is subscribed only while something needs it (window open, Live Coding requested), so nothing connects or
    /// polls while the features are unused. Live Coding output goes to <see cref="LiveCoding"/>, whose
    /// <see cref="IBuildOutputSink"/> list is the hand-over point for the diagnostics integration.
    /// </summary>
    internal static class LinkFeature
    {
        private static readonly Logger L = Log.For("Link");
        private static HostPackage _package;
        private static UeLogWindow _logWindow;
        private static AutomationTestsWindow _testsWindow;
        private static bool _liveCodingWanted;
        private static bool _logWanted;

        public static EditorLinkSubscription Subscription { get; private set; }

        /// <summary>Live Coding output of the current / last compile; add an <see cref="IBuildOutputSink"/> to receive it.</summary>
        public static LiveCodingOutputCollector LiveCoding { get; private set; }

        /// <summary>Log lines received from the editor (kept while the window is closed when the setting says so).</summary>
        public static UeLogStore LogStore { get; private set; }

        public static readonly IReadOnlyList<(int CommandId, string ActionId, Func<string> Text)> Commands = new List<(int, string, Func<string>)>
        {
            (CommandCatalog.UeLogWindow, CommandCatalog.ActionIds.UeLogWindow, () => LinkStrings.CmdLogWindow),
            (CommandCatalog.UeTestRunner, CommandCatalog.ActionIds.UeTestRunner, () => LinkStrings.CmdTestRunner),
        };

        /// <summary>Package initialization (UI thread), after <see cref="EditorLinkService.Initialize"/>.</summary>
        public static void Initialize(HostPackage package, ActionRegistry registry, CommandRouter router, Microsoft.VisualStudio.Shell.OleMenuCommandService mcs)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            _package = package;
            Subscription = new EditorLinkSubscription(() => EditorLinkService.Instance?.Client.ResolvePort() ?? 0);
            Subscription.EventReceived += OnEvent;
            Subscription.StateChanged += OnStateChanged;
            LogStore = new UeLogStore(package.Settings?.Effective?.LinkLogMaxLines ?? UeLogBuffer.DefaultCapacity);
            LiveCoding = new LiveCodingOutputCollector(() => EditorLinkService.Instance?.ProjectRoot);
            LiveCoding.BuildCompleted += OnLiveCodingCompleted;
            LiveCoding.AddSink(Build.BuildErrorService.LiveCodingSink); // compile errors → squiggles / Error List / last-build.json

            bool Unreal(ActionContext c) => c.IsUnrealSolution;
            registry.Register(new DelegateAction(CommandCatalog.ActionIds.UeLogWindow, LinkStrings.CmdLogWindow, ActionCategory.Unreal,
                Ui(ShowLogWindow), Unreal, null, LinkStrings.KwLog));
            registry.Register(new DelegateAction(CommandCatalog.ActionIds.UeTestRunner, LinkStrings.CmdTestRunner, ActionCategory.Unreal,
                Ui(ShowTestsWindow), Unreal, null, LinkStrings.KwTests));
            if (mcs != null && router != null) router.RegisterMap(mcs, Commands);

            package.Settings.Changed += (s, e) => Safe.Execute(L, "link.settings", () => ApplySettings(e.New));
            L.Info("link.initialized", null, ("commands", Commands.Count), ("protocol", LinkProtocol.Version));
        }

        private static Func<ActionContext, Task> Ui(Action a) => async ctx =>
        {
            await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
            a();
        };

        private static void ApplySettings(HostSettings s)
        {
            if (s == null) return;
            LogStore?.SetCapacity(s.LinkLogMaxLines);
            if (!s.LinkLiveCodingErrors && _liveCodingWanted)
            {
                _liveCodingWanted = false;
                UpdateTopics();
            }
            if (!s.LinkLogKeepReceiving && _logWindow == null && _logWanted)
            {
                _logWanted = false;
                UpdateTopics();
            }
        }

        // ------------------------------------------------------------------ topics

        /// <summary>Live Coding was requested from VS: collect its output from now on (when the setting is on).</summary>
        public static void OnLiveCodingRequested()
        {
            if (Subscription == null || _package?.Settings?.Effective?.LinkLiveCodingErrors != true) return;
            if (_liveCodingWanted) return;
            _liveCodingWanted = true;
            UpdateTopics();
        }

        internal static void UpdateTopics()
        {
            if (Subscription == null) return;
            bool liveCoding = _package?.Settings?.Effective?.LinkLiveCodingErrors == true && (_liveCodingWanted || _logWindow != null || _testsWindow != null);
            Subscription.SetTopic(LinkProtocol.TopicLog, _logWanted || _logWindow != null);
            Subscription.SetTopic(LinkProtocol.TopicTests, _testsWindow != null);
            Subscription.SetTopic(LinkProtocol.TopicLiveCoding, liveCoding);
        }

        public static void OnSolutionClosing()
        {
            _liveCodingWanted = false;
            _logWanted = _logWindow != null;
            UpdateTopics();
            LogStore?.Clear();
        }

        public static void Dispose()
        {
            try
            {
                Subscription?.Dispose();
            }
            catch (Exception ex)
            {
                L.Warn("link.dispose.failed", null, ex);
            }
        }

        // ------------------------------------------------------------------ windows

        private static void ShowLogWindow()
        {
            if (_logWindow == null)
            {
                _logWindow = new UeLogWindow(LogStore, Subscription);
                _logWindow.Closed += (s, e) =>
                {
                    _logWindow = null;
                    _logWanted = _package?.Settings?.Effective?.LinkLogKeepReceiving == true;
                    UpdateTopics();
                };
                _logWanted = true;
                UpdateTopics();
                _logWindow.ShowOwned(VsMainWindow.Handle);
            }
            else
            {
                _logWindow.Activate();
            }
        }

        private static void ShowTestsWindow()
        {
            if (_testsWindow == null)
            {
                _testsWindow = new AutomationTestsWindow(_package, Subscription);
                _testsWindow.Closed += (s, e) =>
                {
                    _testsWindow = null;
                    UpdateTopics();
                };
                UpdateTopics();
                _testsWindow.ShowOwned(VsMainWindow.Handle);
            }
            else
            {
                _testsWindow.Activate();
            }
        }

        // ------------------------------------------------------------------ events (background thread)

        private static void OnEvent(LinkEvent e)
        {
            switch (e)
            {
                case LogBatchEvent log:
                    LogStore?.Add(log);
                    break;
                case LiveCodingEvent lc:
                    if (_package?.Settings?.Effective?.LinkLiveCodingErrors != true) return;
                    if (lc.Phase == LiveCodingPhase.Started)
                    {
                        L.Info("link.liveCoding.started", null);
                        VsStatusBar.SetText(LinkStrings.LiveCodingCompiling);
                        Banners.Close("link.liveCoding.result");
                    }
                    LiveCoding?.OnEvent(lc);
                    break;
            }
        }

        private static void OnStateChanged(LinkSubscriptionState state)
        {
            L.Info("link.subscription.state", null, ("state", state.ToString()));
            if (state != LinkSubscriptionState.Connected && LiveCoding?.IsBuilding == true) LiveCoding.OnDisconnected();
        }

        private static void OnLiveCodingCompleted(LiveCodingBuild build)
        {
            if (build == null) return;
            L.Info("link.liveCoding.finished", null, ("result", build.Result), ("succeeded", build.Succeeded), ("errors", build.Errors.Count),
                   ("warnings", build.Warnings.Count), ("lines", build.Lines), ("durationMs", Math.Round(build.DurationMs)));
            if (build.Errors.Count > 0)
            {
                var first = build.Errors[0];
                VsStatusBar.SetText(LinkStrings.LiveCodingFailedErrors(build.Errors.Count));
                Banners.Show("link.liveCoding.result", LinkStrings.LiveCodingFailedErrors(build.Errors.Count) + " — " + first, Integration.BannerKind.Error,
                    (LinkStrings.LiveCodingFirstError, () => JumpToDiagnostic(first)),
                    (LinkStrings.LiveCodingShowOutput, () => Safe.RunAsync(L, "link.liveCoding.list", () => ShowDiagnosticsAsync(build))));
                return;
            }
            switch (build.Result)
            {
                case "success": VsStatusBar.SetText(LinkStrings.LiveCodingSucceeded); break;
                case "noChanges": VsStatusBar.SetText(LinkStrings.LiveCodingNoChanges); break;
                case "failed":
                    VsStatusBar.SetText(LinkStrings.LiveCodingFailed);
                    break;
            }
        }

        private static void JumpToDiagnostic(MsvcErrorLine d)
        {
            if (d == null || string.IsNullOrEmpty(d.FilePath)) return;
            Safe.Execute(L, "link.liveCoding.jump", () =>
            {
                L.Info("link.liveCoding.jump", null, ("file", Path.GetFileName(d.FilePath)), ("line", d.Line), ("code", d.Code));
                NavigationService.Instance.JumpTo(d.FilePath, Math.Max(0, d.Line - 1), Math.Max(0, d.Column - 1), JumpReasons.Other);
            });
        }

        private static async Task ShowDiagnosticsAsync(LiveCodingBuild build)
        {
            await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
            var rows = build.Errors.Concat(build.Warnings).Where(d => !string.IsNullOrEmpty(d.FilePath)).Take(500).Select(d => new SearchRow
            {
                Kind = SearchRowKind.File,
                Title = (d.Code ?? "") + "  " + d.Message,
                Detail = Path.GetFileName(d.FilePath) + "(" + d.Line + ")",
                Right = d.Severity == MsvcSeverity.Error ? LinkStrings.LevelError : LinkStrings.LevelWarning,
                Glyph = UEToVS.UI.Logic.SymbolKind.File,
                FilePath = d.FilePath,
                Line = Math.Max(0, d.Line - 1),
                Column = Math.Max(0, d.Column - 1),
            }).ToList();
            if (rows.Count == 0) return;
            await RelatedNavigator.PickAndJumpAsync("uetovs.liveCoding.errors", LinkStrings.LiveCodingFailedErrors(build.Errors.Count), rows);
        }
    }

    /// <summary>
    /// Thread-safe holder of the received log lines: the subscription thread appends, the log window's UI timer reads
    /// through a <see cref="UeLogView"/> under the same lock. Memory is bounded by the ring buffer capacity.
    /// </summary>
    internal sealed class UeLogStore
    {
        private long _dropped;

        public UeLogStore(int capacity)
        {
            Buffer = new UeLogBuffer(UeLogBuffer.ClampCapacity(capacity));
        }

        public object Gate { get; } = new object();

        /// <summary>Read / modify only while holding <see cref="Gate"/>.</summary>
        public UeLogBuffer Buffer { get; }

        /// <summary>Lines the editor dropped (rate limit / slow reader) since the last clear.</summary>
        public long Dropped => System.Threading.Interlocked.Read(ref _dropped);

        public void Add(LogBatchEvent batch)
        {
            if (batch == null) return;
            if (batch.Dropped > 0) System.Threading.Interlocked.Add(ref _dropped, batch.Dropped);
            if (batch.Lines.Count == 0) return;
            var now = DateTime.Now;
            lock (Gate)
                foreach (var r in batch.Lines) Buffer.Add(r, now);
        }

        public void SetCapacity(int capacity)
        {
            lock (Gate) Buffer.SetCapacity(UeLogBuffer.ClampCapacity(capacity));
        }

        public void Clear()
        {
            lock (Gate) Buffer.Clear();
            System.Threading.Interlocked.Exchange(ref _dropped, 0);
        }
    }
}
