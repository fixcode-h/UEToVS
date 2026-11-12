using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Threading;
using EnvDTE;
using Microsoft.VisualStudio;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Shell.Interop;
using Microsoft.VisualStudio.Threading;
using UEToVS.Contracts;
using UEToVS.Core.Diagnostics;
using UEToVS.Core.Localization;
using UEToVS.Core.Storage;
using UEToVS.Host.Actions;
using UEToVS.Host.Diagnostics;
using UEToVS.Host.Engine;
using UEToVS.Host.Integration;
using UEToVS.Host.IntelliSense;
using UEToVS.Host.Logic.Actions;
using UEToVS.Host.Logic.Engine;
using UEToVS.Host.Logic.Localization;
using UEToVS.Host.Logic.Settings;
using UEToVS.Host.Logic.Unreal;
using UEToVS.Host.Options;
using UEToVS.Host.Performance;
using UEToVS.Host.Settings;
using UEToVS.Host.Shell;
using UEToVS.UI.Shells;
using SolutionEvents = Microsoft.VisualStudio.Shell.Events.SolutionEvents;
using Task = System.Threading.Tasks.Task;
using TaskScheduler = System.Threading.Tasks.TaskScheduler;

namespace UEToVS.Host
{
    /// <summary>
    /// UEToVS VS package (L4 host services). Loading (§7.1):
    /// - autoloads in the background only through the rule-based UIContext <see cref="PackageGuids.CppSolutionContextString"/>
    ///   (fully loaded solution containing a VisualC project); never for NoSolution / non-C++ solutions;
    /// - after load it decides with <see cref="UnrealSolutionDetector"/> whether the solution is an Unreal Engine solution;
    ///   only then: logs move to &lt;solution&gt;/.vs/UEToVS/Logs, the engine starts, IntelliSense is taken over, and the
    ///   UIContext <see cref="PackageGuids.UnrealSolutionContextString"/> is activated. For other solutions nothing starts.
    /// Commands are declared in the VSCT and also load the package on first use.
    /// </summary>
    [PackageRegistration(UseManagedResourcesOnly = true, AllowsBackgroundLoading = true)]
    [InstalledProductRegistration("UEToVS", "Unreal Engine aware C++ assistant", "0.1.0")]
    [ProvideMenuResource("Menus.ctmenu", 1)]
    // v0.3.0: all settings live in the UEToVS settings window; Tools > Options keeps one stub page that opens it.
    [ProvideOptionPage(typeof(GeneralOptionsPage), "UEToVS", "General", 0, 0, true, Sort = 0)]
    [ProvideToolWindow(typeof(Search.SearchResultsWindow), Style = VsDockStyle.Tabbed, Window = ToolWindowGuids.Outputwindow, MultiInstances = false)]
    [ProvideUIContextRule(PackageGuids.CppSolutionContextString,
        name: "UEToVS C++ solution",
        expression: "SolutionReady & VisualCpp",
        termNames: new[] { "SolutionReady", "VisualCpp" },
        termValues: new[] { VSConstants.UICONTEXT.SolutionExistsAndFullyLoaded_string, "SolutionHasProjectCapability:VisualC" })]
    [ProvideAutoLoad(PackageGuids.CppSolutionContextString, PackageAutoLoadFlags.BackgroundLoad)]
    [Guid(PackageGuids.PackageString)]
    public sealed class HostPackage : AsyncPackage
    {
        private static readonly Logger L = Log.For("Package");

        private SettingsService _settings;
        private IntelliSenseTakeoverService _intelliSense;
        private EngineClient _engine;
        private DiagnosticsReporter _reporter;
        private CommandRouter _commands;
        private BuildMonitor _build;
        private SettingsWindowService _settingsWindow;
        private PerformanceWindow _performanceWindow;
        private UnrealSolutionInfo _unreal = UnrealSolutionInfo.NotUnreal;
        private string _solutionFile;
        private string _solutionDir;
        private int _vsLcid;
        private string _vsVersion;
        private string _vsEdition;
        private string _vsRelease;

        internal static HostPackage Instance { get; private set; }

        internal ActionRegistry Actions { get; } = new ActionRegistry();

        internal PerformanceMonitor Performance { get; private set; }

        internal SettingsService Settings => _settings;

        /// <summary>The UEToVS settings window (Extensions &gt; UEToVS &gt; 设置…, toolbar, Find Action, Options stub).</summary>
        internal SettingsWindowService SettingsWindow => _settingsWindow;

        internal IntelliSenseTakeoverService IntelliSense => _intelliSense;

        internal EngineClient Engine => _engine;

        internal UnrealSolutionInfo Unreal => _unreal;

        internal string SolutionDirectory => _solutionDir;

        internal string VsVersion => _vsVersion;

        internal static string ExtensionDirectory => Path.GetDirectoryName(typeof(HostPackage).Assembly.Location);

        internal static string HostVersion =>
            typeof(HostPackage).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
            ?? typeof(HostPackage).Assembly.GetName().Version.ToString();

        // ================================================================== initialization

        protected override async Task InitializeAsync(CancellationToken cancellationToken, IProgress<ServiceProgressData> progress)
        {
            Instance = this;
            // Baseline before we allocate anything (for the "since load" memory delta).
            Performance = new PerformanceMonitor(() => _engine);

            await JoinableTaskFactory.SwitchToMainThreadAsync(cancellationToken);
            using (UiThread.Measure(L, "package.initialize"))
            {
                // 1. UI language first: every string created after this point is localized.
                await InitializeLanguageAsync();

                // 2. Logging: Output pane (Info+) + fallback file until a UE solution is known.
                var pane = new OutputPaneSink(JoinableTaskFactory);
                pane.EnsurePane();
                HostLog.AttachOutputPane(pane);

                var general = (GeneralOptionsPage)GetDialogPage(typeof(GeneralOptionsPage));
                var logging = (LoggingOptionsPage)GetDialogPage(typeof(LoggingOptionsPage)); // migration source only
                _settings = new SettingsService(general, logging);
                _settingsWindow = new SettingsWindowService(this);
                var s = _settings.Effective;
                Performance.Interval = TimeSpan.FromSeconds(Math.Max(1, s.PerformanceSampleSeconds));
                HostLog.Use(StoragePaths.Fallback(), s.LogLevel, s.LogRetentionDays, s.LogMaxTotalMB);
                L.Info("package.loaded", "UEToVS package loaded", ("version", HostVersion), ("uiLanguage", Loc.Language.ToString()), ("vsLcid", _vsLcid),
                       ("extensionDir", ExtensionDirectory));

                // 3. UI component library: theme from stored appearance settings, then the integration slots.
                InitializeIntegrations();

                // 4. Services, actions, commands.
                _intelliSense = new IntelliSenseTakeoverService(this, _settings);
                _settings.Changed += (sender, e) => Safe.RunAsync(L, "settings.changed", () => OnSettingsChangedAsync(e.Old, e.New));
                HostActions.Register(this, Actions);
                _commands = new CommandRouter(this, Actions);
                if (await GetServiceAsync(typeof(System.ComponentModel.Design.IMenuCommandService)) is OleMenuCommandService mcs) _commands.Register(mcs);
                else L.Error("package.commands.unavailable", "IMenuCommandService unavailable; menu commands not registered", null);
                // M1 prototype features (Search Everywhere, navigation history, bookmarks, related, Build.cs, UE link).
                Safe.Execute(L, "m1.initialize", () => M1Feature.Initialize(this, Actions, _commands,
                    GetService(typeof(System.ComponentModel.Design.IMenuCommandService)) as OleMenuCommandService));

                // 4b. Toolbar: shown on the first run (double Shift → Search Everywhere is M1's InputMonitor).
                var dte = await GetServiceAsync(typeof(DTE)) as DTE;
                Safe.Execute(L, "toolbar.firstRun", () => ToolbarController.ApplyOnFirstRun(dte, s.ShowToolbar));

                // 5. VS build begin/end → engine pauses background indexing.
                _build = new BuildMonitor(building =>
                {
                    var engine = _engine;
                    if (engine != null) _ = Safe.RunInBackgroundAsync(L, "engine.build", () => engine.SetBuildInProgressAsync(building), ("building", building));
                });
                await _build.AdviseAsync(this);

                // 6. Solution lifecycle.
                SolutionEvents.OnAfterOpenSolution += (sender, e) => Safe.RunAsync(L, "solution.opened", OnSolutionOpenedAsync);
                SolutionEvents.OnBeforeCloseSolution += (sender, e) => Safe.Execute(L, "solution.closing", OnSolutionClosing);
                SolutionEvents.OnAfterCloseSolution += (sender, e) => Safe.RunAsync(L, "solution.closed", OnSolutionClosedAsync);
            }

            if (await IsSolutionOpenAsync()) _ = Safe.RunAsync(L, "solution.opened", OnSolutionOpenedAsync);
        }

        private async Task InitializeLanguageAsync()
        {
            await JoinableTaskFactory.SwitchToMainThreadAsync();
            int lcid = 0;
            try
            {
                if (await GetServiceAsync(typeof(SUIHostLocale)) is IUIHostLocale locale &&
                    ErrorHandler.Succeeded(locale.GetUILocale(out uint uiLcid)))
                    lcid = (int)uiLcid;
            }
            catch (Exception) { }
            try
            {
                if (await GetServiceAsync(typeof(DTE)) is DTE dte)
                {
                    if (lcid == 0) lcid = dte.LocaleID;
                    _vsVersion = dte.Version;
                    _vsEdition = dte.Edition;
                }
                if (await GetServiceAsync(typeof(SVsShell)) is IVsShell shell &&
                    ErrorHandler.Succeeded(shell.GetProperty((int)__VSSPROPID5.VSSPROPID_ReleaseVersion, out object rel)))
                    _vsRelease = rel as string;
            }
            catch (Exception) { /* logged in the session header as missing */ }
            _vsLcid = lcid;
            if (lcid != 0) Loc.InitializeFromLcid(lcid);
        }

        /// <summary>
        /// Wires the UEToVS.UI component library into the host slots (UI thread, after <see cref="Loc"/> is initialized so
        /// every UI string is created in the right language). Each step is isolated: a failure is logged and leaves that
        /// slot on its placeholder; the package keeps loading.
        /// </summary>
        private static void InitializeIntegrations()
        {
            // Theme first: stored appearance settings → ThemeManager (creating it also hooks VS theme changes).
            Safe.Execute(L, "integration.theme", UiAppearancePage.ApplyStored);
            Safe.Execute(L, "integration.popupStore", () => PopupShell.DefaultStateStore = new VsPopupStateStore());
            Safe.Execute(L, "integration.windowStore", () => UtilityWindow.DefaultPlacementStore = new VsWindowPlacementStore());
            Safe.Execute(L, "integration.slots", () =>
            {
                if (HostIntegration.Appearance == null) HostIntegration.Appearance = new UiAppearancePage();
                if (HostIntegration.CommandPalette == null) HostIntegration.CommandPalette = new ActionPaletteProvider();
                if (HostIntegration.ComponentGallery == null) HostIntegration.ComponentGallery = new UiComponentGalleryProvider();
                if (HostIntegration.PerformancePanel == null) HostIntegration.PerformancePanel = new UiPerformancePanelProvider();
            });
            // VS-level notifications stay on the VS InfoBar (UEToVS.UI's Banner is used inside UEToVS windows).
            if (HostIntegration.Banner == null) HostIntegration.Banner = new InfoBarPresenter();
            L.Info("integration.slots", null,
                   ("appearance", HostIntegration.Appearance?.GetType().FullName ?? "placeholder"),
                   ("commandPalette", HostIntegration.CommandPalette?.GetType().FullName ?? "none"),
                   ("componentGallery", HostIntegration.ComponentGallery?.GetType().FullName ?? "none"),
                   ("performancePanel", HostIntegration.PerformancePanel?.GetType().FullName ?? "placeholder"),
                   ("banner", HostIntegration.Banner.GetType().FullName),
                   ("popupStateStore", PopupShell.DefaultStateStore?.GetType().FullName));
        }

        private async System.Threading.Tasks.Task<bool> IsSolutionOpenAsync()
        {
            await JoinableTaskFactory.SwitchToMainThreadAsync();
            if (!(await GetServiceAsync(typeof(SVsSolution)) is IVsSolution sol)) return false;
            sol.GetProperty((int)__VSPROPID.VSPROPID_IsSolutionOpen, out object open);
            return open is bool b && b;
        }

        // ================================================================== solution lifecycle

        private async Task OnSolutionOpenedAsync()
        {
            await JoinableTaskFactory.SwitchToMainThreadAsync(DisposalToken);
            string file = null, dir = null;
            using (UiThread.Measure(L, "solution.info"))
            {
                if (await GetServiceAsync(typeof(SVsSolution)) is IVsSolution sol)
                    sol.GetSolutionInfo(out dir, out file, out _);
            }
            await TaskScheduler.Default;
            if (_unreal.IsUnreal && string.Equals(file, _solutionFile, StringComparison.OrdinalIgnoreCase))
            {
                L.Debug("solution.opened.duplicate", null, ("solution", file)); // event + initial check raced
                return;
            }

            var info = UnrealSolutionDetector.Detect(file, dir);
            L.Info("solution.detected", null, ("solution", file), ("dir", dir), ("kind", info.Kind.ToString()), ("reason", info.Reason),
                   ("project", info.ProjectFile), ("engineRoot", info.EngineRoot));
            _solutionFile = file;
            _solutionDir = dir;
            _unreal = info;
            if (!info.IsUnreal)
            {
                // §7.1: non-UE solution → nothing starts (no engine, no takeover, no solution log folder).
                L.Info("solution.notUnreal", "Not an Unreal Engine solution; UEToVS stays idle");
                await SetUnrealContextAsync(false);
                return;
            }

            _settings.SetSolution(dir);
            var s = _settings.Effective;
            HostLog.Use(StoragePaths.ForSolution(dir), s.LogLevel, s.LogRetentionDays, s.LogMaxTotalMB);
            WriteSessionHeader();
            if (_reporter == null)
            {
                _reporter = new DiagnosticsReporter(TimeSpan.FromSeconds(60), TimeSpan.FromMinutes(5))
                {
                    // devenv's private bytes include all of VS; the host's own increment (budget 80 MB, §9.2) cannot be
                    // isolated, so no budget warning here: the sample carries the delta since load and the managed heap.
                    PrivateBytesBudget = long.MaxValue,
                    ResourceExtras = () => new (string, object)[]
                    {
                        ("privateDeltaSinceLoadMB", Math.Round((CurrentPrivateBytes() - Performance.PrivateBytesAtLoad) / 1048576.0, 1)),
                        ("engineState", _engine?.State.ToString() ?? "None"),
                        ("enginePid", _engine?.ProcessId),
                        ("actions", Actions.Count),
                    },
                };
            }
            await SetUnrealContextAsync(true);
            await JoinableTaskFactory.SwitchToMainThreadAsync(DisposalToken);
            Safe.Execute(L, "m1.solutionOpened", () => M1Feature.OnUnrealSolutionOpened(dir));
            await TaskScheduler.Default;

            await _intelliSense.OnUnrealSolutionOpenedAsync();
            await StartEngineAsync();
            _ = Safe.RunInBackgroundAsync(L, "mcp.register", () => Mcp.McpRegistration.SyncAsync(dir, _settings.Effective)); // .vs\mcp.json for AI agents
        }

        private void OnSolutionClosing()
        {
            Safe.Execute(L, "m1.solutionClosing", M1Feature.OnSolutionClosing);
            if (!_unreal.IsUnreal) return;
            L.Info("solution.closing", null, ("solution", _solutionFile));
            var engine = _engine;
            _engine = null;
            if (engine != null) _ = Safe.RunInBackgroundAsync(L, "engine.stop", async () => { await engine.StopAsync(); engine.Dispose(); });
        }

        private async Task OnSolutionClosedAsync()
        {
            bool wasUnreal = _unreal.IsUnreal;
            L.Info("solution.closed", null, ("solution", _solutionFile), ("wasUnreal", wasUnreal));
            _unreal = UnrealSolutionInfo.NotUnreal;
            _solutionFile = null;
            _solutionDir = null;
            await SetUnrealContextAsync(false);
            VsStatusBar.SetProgress(null, 0, 0);
            if (wasUnreal)
            {
                await _intelliSense.OnSolutionClosedAsync(true);
                _settings.SetSolution(null);
                var s = _settings.Effective;
                HostLog.Use(StoragePaths.Fallback(), s.LogLevel, s.LogRetentionDays, s.LogMaxTotalMB);
            }
        }

        private async Task SetUnrealContextAsync(bool active)
        {
            await JoinableTaskFactory.SwitchToMainThreadAsync();
            var ctx = UIContext.FromUIContextGuid(PackageGuids.UnrealSolutionContext);
            if (ctx.IsActive != active)
            {
                ctx.IsActive = active;
                L.Info("uicontext.unrealSolution", null, ("active", active));
            }
        }

        private void WriteSessionHeader()
        {
            var s = _settings.Effective;
            string enginePath = EngineLaunch.ResolveExecutable(ExtensionDirectory, s.EnginePathOverride, out string engineSource);
            DiagnosticsReporter.WriteSessionHeader("UEToVS.Host", HostVersion, new (string, object)[]
            {
                ("protocol", ProtocolInfo.Version),
                ("vsVersion", _vsVersion),
                ("vsRelease", _vsRelease),
                ("vsEdition", _vsEdition),
                ("vsLcid", _vsLcid),
                ("uiLanguage", Loc.Language.ToString()),
                ("solution", _solutionFile),
                ("solutionDir", _solutionDir),
                ("ueKind", _unreal.Kind.ToString()),
                ("ueProject", _unreal.ProjectFile),
                ("ueEngineRoot", _unreal.EngineRoot ?? (string.IsNullOrEmpty(s.UnrealEngineRootOverride) ? null : s.UnrealEngineRootOverride)),
                ("ueDetectedBy", _unreal.Reason),
                ("extensionDir", ExtensionDirectory),
                ("enginePath", enginePath),
                ("engineSource", engineSource),
                ("engineInstalled", File.Exists(enginePath)),
                ("settings", SettingsService.Describe(s)),
                ("solutionSettings", _settings.Current.SolutionFile),
                ("solutionOverrides", _settings.Current.SolutionOverrides.Keys.ToList()),
                ("featureFlags", new Dictionary<string, object>
                {
                    ["engine"] = File.Exists(enginePath),
                    ["intelliSenseTakeover"] = s.IntelliSenseTakeover,
                    ["appearancePanel"] = HostIntegration.Appearance != null,
                    ["commandPalette"] = HostIntegration.CommandPalette != null,
                    ["performancePanel"] = HostIntegration.PerformancePanel != null,
                }),
                ("intelliSense", new Dictionary<string, object>
                {
                    ["enabled"] = s.IntelliSenseTakeover,
                    ["mode"] = s.IntelliSenseTakeoverMode.ToString(),
                    ["disableDatabase"] = s.DisableDatabase,
                    ["backupExists"] = _intelliSense.BackupExists,
                    ["lastState"] = _intelliSense.LastState.ToString(),
                    ["backupPath"] = _intelliSense.BackupPath,
                    ["disableBackgroundCodeAnalysis"] = s.DisableBackgroundCodeAnalysis,
                    ["codeAnalysisBackupExists"] = _intelliSense.CodeAnalysisBackupExists,
                }),
                ("devenvPrivateMBAtLoad", Math.Round(Performance.PrivateBytesAtLoad / 1048576.0, 1)),
                ("logFile", HostLog.CurrentFile),
            });
        }

        // ================================================================== engine

        /// <summary>Performance panel pause button.</summary>
        internal void SetEngineBackgroundPaused(bool paused)
        {
            var engine = _engine;
            if (engine == null) return;
            _ = Safe.RunInBackgroundAsync(L, "engine.pause", () => engine.SetBackgroundPausedAsync(paused), ("paused", paused));
        }

        private async Task StartEngineAsync()
        {
            var s = _settings.Effective;
            var old = _engine;
            if (old != null) { await old.StopAsync(); old.Dispose(); }
            var engine = new EngineClient(new EngineClientOptions
            {
                ExtensionDirectory = ExtensionDirectory,
                SolutionDirectory = _solutionDir,
                ProjectFile = _unreal.ProjectFile,
                EnginePathOverride = s.EnginePathOverride,
                UnrealEngineRootOverride = s.UnrealEngineRootOverride,
                LogLevel = s.LogLevel,
                HostVersion = HostVersion,
                VsVersion = _vsRelease ?? _vsVersion,
                RequestTimeout = TimeSpan.FromSeconds(Math.Max(1, s.EngineRequestTimeoutSeconds)),
            });
            engine.StateChanged += (sender, e) =>
            {
                OnEngineStateChanged(engine);
                // Indexing settings (threads, build policy, engine sources) go to every (re)connected engine.
                if (engine.State == EngineState.Connected) PushIndexingSettings(engine, "connected");
                // A (re)connected engine must learn about a build that is already running.
                if (engine.State == EngineState.Connected && _build?.IsBuilding == true)
                    _ = Safe.RunInBackgroundAsync(L, "engine.build.resync", () => engine.SetBuildInProgressAsync(true));
            };
            engine.StatusChanged += (sender, status) =>
            {
                var d = EngineStatusText.For(status);
                VsStatusBar.SetProgress(d.Text, d.Done, d.Total);
            };
            engine.GaveUp += (sender, message) => Banners.Show("engine.unavailable", message, BannerKind.Error,
                (Strings.RestartEngine, () => ExecuteAction(ActionIds.RestartEngine)),
                (Strings.OpenLog, () => ExecuteAction(ActionIds.OpenCurrentLog)));
            _engine = engine;
            VsStatusBar.SetText(Strings.StatusEngineStarting);
            await engine.StartAsync();
        }

        private void PushIndexingSettings(EngineClient engine, string reason)
        {
            if (engine == null) return;
            var indexing = HostSettingsSchema.Codec.ToIndexing(_settings.Effective);
            _ = Safe.RunInBackgroundAsync(L, "engine.indexing.push", () => engine.SetIndexingSettingsAsync(indexing),
                ("reason", reason), ("cppThreads", indexing.CppThreads), ("assetThreads", indexing.AssetThreads),
                ("duringBuild", indexing.DuringBuild), ("includeEngine", indexing.IncludeEngine));
        }

        private static void OnEngineStateChanged(EngineClient engine)
        {
            switch (engine.State)
            {
                case EngineState.Connected:
                    Banners.Close("engine.unavailable");
                    break;
                case EngineState.Restarting:
                    VsStatusBar.SetText(Strings.StatusEngineRestarting);
                    break;
                case EngineState.NotInstalled:
                    VsStatusBar.SetText(Strings.StatusEngineNotInstalled);
                    break;
                case EngineState.Unavailable:
                case EngineState.Incompatible:
                    VsStatusBar.SetText(Strings.StatusEngineUnavailable);
                    break;
            }
        }

        /// <summary>Restart command: restarts the engine (resets the crash budget) or starts it if it is not running.</summary>
        internal async Task RestartEngineAsync()
        {
            if (!_unreal.IsUnreal)
            {
                Banners.Show("engine.unavailable", Strings.NoUnrealSolution, BannerKind.Info);
                return;
            }
            if (_engine == null) await StartEngineAsync();
            else await _engine.RestartAsync();
        }

        // ================================================================== settings

        private async Task OnSettingsChangedAsync(HostSettings old, HostSettings now)
        {
            if (old.LogLevel != now.LogLevel)
            {
                HostLog.SetLevel(now.LogLevel);
                L.Info("log.level.changed", null, ("from", old.LogLevel.ToString()), ("to", now.LogLevel.ToString()));
                if (_engine != null) await _engine.SetLogLevelAsync(now.LogLevel);
            }
            if (old.LogRetentionDays != now.LogRetentionDays || old.LogMaxTotalMB != now.LogMaxTotalMB)
                await Task.Run(() => HostLog.ApplyRetention(now.LogRetentionDays, now.LogMaxTotalMB));
            if (!string.Equals(old.EnginePathOverride, now.EnginePathOverride, StringComparison.OrdinalIgnoreCase) ||
                !string.Equals(old.UnrealEngineRootOverride, now.UnrealEngineRootOverride, StringComparison.OrdinalIgnoreCase))
            {
                L.Info("engine.settings.changed", "Engine path/root changed; restarting the engine", ("enginePath", now.EnginePathOverride), ("ueRoot", now.UnrealEngineRootOverride));
                if (_unreal.IsUnreal) await StartEngineAsync();
            }
            if (old.ShareEngineIndex != now.ShareEngineIndex || !string.Equals(old.EngineIndexPath, now.EngineIndexPath, StringComparison.OrdinalIgnoreCase) ||
                old.CppIndexThreads != now.CppIndexThreads || old.BlueprintIndexThreads != now.BlueprintIndexThreads ||
                old.IndexDuringBuild != now.IndexDuringBuild || old.IndexEngineSource != now.IndexEngineSource)
                PushIndexingSettings(_engine, "settingsChanged");
            if (old.PerformanceSampleSeconds != now.PerformanceSampleSeconds)
                Performance.Interval = TimeSpan.FromSeconds(Math.Max(1, now.PerformanceSampleSeconds));
            if (old.HiddenToolbarButtons != now.HiddenToolbarButtons || old.ShowToolbar != now.ShowToolbar)
            {
                await JoinableTaskFactory.SwitchToMainThreadAsync(DisposalToken);
                if (old.ShowToolbar != now.ShowToolbar)
                    ToolbarController.Apply(await GetServiceAsync(typeof(DTE)) as DTE, now.ShowToolbar, "settingsChanged");
                Safe.Execute(L, "toolbar.refresh", RefreshCommandUi);
            }
            await _intelliSense.OnSettingsChangedAsync(old, now, _unreal.IsUnreal);
            if (old.EnableMcpServer != now.EnableMcpServer && _unreal.IsUnreal)
                await Mcp.McpRegistration.SyncAsync(_solutionDir, now);
        }

        /// <summary>Makes VS re-query command status (toolbar button visibility) right away.</summary>
        private void RefreshCommandUi()
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            if (GetService(typeof(SVsUIShell)) is IVsUIShell shell) shell.UpdateCommandUI(0);
        }

        // ================================================================== actions

        internal ActionContext CreateActionContext(ActionSource source) => new ActionContext
        {
            Source = source,
            SolutionDirectory = _solutionDir,
            IsUnrealSolution = _unreal.IsUnreal,
            EngineAvailable = _engine?.IsConnected == true,
            Services = this,
            CancellationToken = DisposalToken,
            // TODO(editor adapters): ActiveDocumentPath / caret from the active text view (L3).
        };

        /// <summary>Runs an action by id from any UI surface (banner links, tool window buttons, options pages).</summary>
        internal void ExecuteAction(string id, ActionSource source = ActionSource.Programmatic)
        {
            Safe.RunAsync(L, "action.dispatch", async () =>
            {
                bool ok = await Actions.ExecuteAsync(id, CreateActionContext(source));
                if (!ok) VsStatusBar.SetText(Strings.ActionFailed(Actions.Get(id)?.Title ?? id));
            }, ("id", id));
        }

        /// <summary>Opens (or activates) the floating "UEToVS 性能" window; position and size are remembered.</summary>
        internal async Task ShowPerformanceWindowAsync()
        {
            await JoinableTaskFactory.SwitchToMainThreadAsync(DisposalToken);
            if (_performanceWindow == null)
            {
                var window = new PerformanceWindow(Performance);
                window.Closed += (s, e) => { if (ReferenceEquals(_performanceWindow, window)) _performanceWindow = null; };
                _performanceWindow = window;
            }
            using (UiThread.Measure(L, "perf.window.show"))
                _performanceWindow.ShowOwned(VsMainWindow.Handle);
        }

        /// <summary>Opens the UEToVS settings window, optionally on a category (<see cref="HostSettingsSchema"/> ids).</summary>
        internal async Task ShowSettingsWindowAsync(string category = null)
        {
            await JoinableTaskFactory.SwitchToMainThreadAsync(DisposalToken);
            _settingsWindow?.Show(category);
        }

        private static long CurrentPrivateBytes()
        {
            try
            {
                using (var p = System.Diagnostics.Process.GetCurrentProcess()) return p.PrivateMemorySize64;
            }
            catch (Exception)
            {
                return 0;
            }
        }

        // ================================================================== shutdown

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                try
                {
                    L.Info("package.disposing", null);
                    _engine?.Dispose();
                    _engine = null;
                    _build?.Dispose();
                    M1Feature.Dispose();
                    Performance?.Dispose();
                    _reporter?.Dispose();
                    _reporter = null;
                    HostLog.Shutdown();
                }
                catch (Exception)
                {
                    // VS is shutting down; nothing left to report to.
                }
                if (Instance == this) Instance = null;
            }
            base.Dispose(disposing);
        }
    }
}
