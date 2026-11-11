using System;
using System.Collections.Generic;
using System.ComponentModel.Design;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Shell.Interop;
using UEToVS.Bookmarks;
using UEToVS.Commands;
using UEToVS.Core.Input;
using UEToVS.Core.Keymap;
using UEToVS.Core.Protocol;
using UEToVS.Core.Query;
using UEToVS.Indexing;
using UEToVS.Input;
using UEToVS.Keymap;
using UEToVS.Link;
using UEToVS.Navigation;
using UEToVS.Options;
using UEToVS.Search;
using UEToVS.UI;
using UEToVS.Unreal;
using SolutionEvents = Microsoft.VisualStudio.Shell.Events.SolutionEvents;
using Microsoft.VisualStudio.Threading;
using Task = System.Threading.Tasks.Task;
using TaskScheduler = System.Threading.Tasks.TaskScheduler;

namespace UEToVS
{
    [PackageRegistration(UseManagedResourcesOnly = true, AllowsBackgroundLoading = true)]
    [InstalledProductRegistration("UEToVS", "Rider-style navigation and search for Unreal Engine C++", "0.1.0")]
    [ProvideMenuResource("Menus.ctmenu", 1)]
    [ProvideOptionPage(typeof(GeneralOptions), "UEToVS", "General", 0, 0, true)]
    [ProvideAutoLoad(UIContextGuids80.SolutionExists, PackageAutoLoadFlags.BackgroundLoad)]
    [ProvideAutoLoad(UIContextGuids80.NoSolution, PackageAutoLoadFlags.BackgroundLoad)]
    [ProvideToolWindow(typeof(ResultsToolWindow), Style = VsDockStyle.Tabbed, Window = ToolWindowGuids.Outputwindow, MultiInstances = false)]
    [Guid(PackageGuidString)]
    public sealed class UEToVSPackage : AsyncPackage, ISearchHost
    {
        public const string PackageGuidString = "64ba7109-f59c-4087-8be2-6f76697024b7";

        private GeneralOptions _options;
        private IndexerClient _indexer;
        private SearchController _controller;
        private SearchWindow _searchWindow;
        private KeyboardMonitor _keyboard;
        private readonly DoubleShiftDetector _doubleShift = new DoubleShiftDetector();
        private string _solutionDir;
        private string _stateDir;

        internal static UEToVSPackage Instance { get; private set; }

        internal UeEditorLink EditorLink { get; private set; }

        internal string ProjectFile => _indexer?.ProjectFile;

        internal string ProjectRoot => _indexer?.ProjectRoot ?? _solutionDir;

        internal string EngineRoot => _indexer?.EngineRoot ?? _indexer?.LastStatus?.EngineRoot;

        // ---- ISearchHost ----

        GeneralOptions ISearchHost.Options => _options;

        internal GeneralOptions Options => _options;

        internal ActionCatalog Actions { get; } = new ActionCatalog();

        ActionCatalog ISearchHost.Actions => Actions;

        public string IndexStatusText
        {
            get
            {
                if (_indexer == null || (_indexer.ProjectFile == null && !_indexer.IsRunning)) return null;
                var s = _indexer.LastStatus;
                if (s == null) return "正在启动索引…";
                if (!s.FilesReady) return "正在扫描文件…";
                if (!s.SymbolsReady) return $"正在建立符号索引 {s.FilesParsed}/{s.FilesToParse}…";
                return null;
            }
        }

        public IReadOnlyList<string> RecentFilePaths =>
            NavigationService.Instance.Recent.Items.Select(e => e.Path).ToList();

        public async System.Threading.Tasks.Task<IIndexService> GetIndexAsync()
        {
            if (_indexer == null || _solutionDir == null) return null;
            try
            {
                return await _indexer.GetServiceAsync(_solutionDir, _options.EngineRootOverride, _options.IndexerExtraArgs).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                OutputLog.Write("Indexer unavailable: " + ex.Message);
                return null;
            }
        }

        void ISearchHost.NavigateTo(string path, int line, int column, OpenMode mode)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            NavigationService.Instance.NavigateTo(path, line, column, mode == OpenMode.SplitGroup);
        }

        void ISearchHost.OpenAsset(AssetDto asset) => UnrealTools.OpenAsset(this, asset);

        public void SaveOptions()
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            _options.SaveSettingsToStorage();
        }

        // ---- lifetime ----

        protected override async Task InitializeAsync(CancellationToken cancellationToken, IProgress<ServiceProgressData> progress)
        {
            await JoinableTaskFactory.SwitchToMainThreadAsync(cancellationToken);
            Instance = this;
            _options = (GeneralOptions)GetDialogPage(typeof(GeneralOptions));
            _indexer = new IndexerClient(Path.GetDirectoryName(typeof(UEToVSPackage).Assembly.Location));
            _indexer.StatusChanged += (s, e) => { };
            EditorLink = new UeEditorLink(() => _indexer?.ProjectRoot, () => _options.EditorLinkPort);
            _controller = new SearchController(this);

            ApplyOptions();
            _options.Applied += (s, e) => ApplyOptions();
            RegisterCommands(await GetServiceAsync(typeof(IMenuCommandService)) as OleMenuCommandService);
            RegisterActions();

            _doubleShift.Triggered += (s, e) => OnDoubleShift();
            _keyboard = new KeyboardMonitor(_doubleShift)
            {
                MouseNavigate = back =>
                {
                    if (!_options.MouseButtonsNavigate) return false;
                    if (back) NavigationService.Instance.Back();
                    else NavigationService.Instance.Forward();
                    return true;
                },
            };

            NavigationService.Instance.FileSaved += OnFileSaved;
            SolutionEvents.OnAfterOpenSolution += (s, e) => OnSolutionOpened();
            SolutionEvents.OnBeforeCloseSolution += (s, e) => OnSolutionClosing();

            if (await IsSolutionOpenAsync()) OnSolutionOpened();
            OutputLog.Write("UEToVS loaded.");
            PromptKeymapOnce();
        }

        private async System.Threading.Tasks.Task<bool> IsSolutionOpenAsync()
        {
            await JoinableTaskFactory.SwitchToMainThreadAsync();
            var solution = await GetServiceAsync(typeof(SVsSolution)) as IVsSolution;
            if (solution == null) return false;
            solution.GetProperty((int)__VSPROPID.VSPROPID_IsSolutionOpen, out object open);
            return open is bool b && b;
        }

        private void ApplyOptions()
        {
            _doubleShift.Enabled = _options.DoubleShiftEnabled;
            _doubleShift.IntervalMs = Math.Max(150, _options.DoubleShiftIntervalMs);
            _doubleShift.LeftShiftOnly = _options.LeftShiftOnly;
            NavigationService.Instance.Configure(_options.NavigationCapacity, _options.NavigationLineThreshold);
        }

        private void OnSolutionOpened()
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            _solutionDir = VsHelpers.SolutionDirectory();
            _stateDir = VsHelpers.SolutionStateDirectory();
            NavigationService.Instance.Load(_stateDir);
            BookmarkService.Load(_stateDir);
            if (_solutionDir == null) return;
            string dir = _solutionDir;
            _ = JoinableTaskFactory.RunAsync(async () =>
            {
                await TaskScheduler.Default;
                try
                {
                    await _indexer.StartAsync(dir, _options.EngineRootOverride, _options.IndexerExtraArgs);
                }
                catch (Exception ex)
                {
                    OutputLog.Write("Indexer start failed: " + ex);
                }
            });
        }

        private void OnSolutionClosing()
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            NavigationService.Instance.Save(_stateDir);
            BookmarkService.Save(_stateDir);
            NavigationService.Instance.Clear();
            BookmarkService.Load(null);
            _indexer?.Stop();
            _solutionDir = null;
            _stateDir = null;
        }

        private void OnFileSaved(string path)
        {
            _ = JoinableTaskFactory.RunAsync(async () =>
            {
                try
                {
                    var index = await GetIndexAsync();
                    if (index != null) await index.NotifyFileChangedAsync(path, CancellationToken.None);
                }
                catch (Exception)
                {
                }
            });
            ModuleDependencyFixer.OnSaved(this, path);
            // Persist navigation state now and then so a VS crash does not lose it.
            NavigationService.Instance.Save(_stateDir);
            BookmarkService.Save(_stateDir);
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                try
                {
                    _keyboard?.Dispose();
                    _indexer?.Dispose();
                }
                catch (Exception)
                {
                }
            }
            base.Dispose(disposing);
        }

        // ---- search ----

        private SearchWindow Window
        {
            get
            {
                ThreadHelper.ThrowIfNotOnUIThread();
                return _searchWindow ?? (_searchWindow = new SearchWindow(this, _controller, OpenResultsInToolWindow));
            }
        }

        private void OnDoubleShift()
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            if (_searchWindow != null && _searchWindow.IsVisible && _searchWindow.IsActive)
                _searchWindow.OnDoubleShift();
            else
                ShowSearch(SearchTab.All);
        }

        internal void ShowSearch(SearchTab tab, string text = null)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            if (text == null)
            {
                // Rider seeds the popup with the editor selection.
                var view = VsHelpers.ActiveTextView();
                if (view != null && !view.Selection.IsEmpty)
                {
                    string sel = view.Selection.StreamSelectionSpan.GetText();
                    if (sel.Length > 0 && sel.Length < 200 && sel.IndexOf('\n') < 0) text = sel.Trim();
                }
            }
            if (_searchWindow != null && _searchWindow.IsVisible && _searchWindow.CurrentTab == tab && text == null)
            {
                // Same shortcut again: Rider cycles to "include non-project items".
                _searchWindow.OnDoubleShift();
                return;
            }
            Window.ShowOn(tab, text);
        }

        private void OpenResultsInToolWindow(List<SearchItem> items, string query)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            var window = FindToolWindow(typeof(ResultsToolWindow), 0, true) as ResultsToolWindow;
            if (window?.Frame is IVsWindowFrame frame)
            {
                window.SetResults(items, query);
                frame.Show();
            }
        }

        // ---- commands ----

        private void RegisterCommands(OleMenuCommandService mcs)
        {
            if (mcs == null) return;
            void Add(int id, Action run)
            {
                var cmd = new OleMenuCommand((s, e) =>
                {
                    ThreadHelper.ThrowIfNotOnUIThread();
                    try
                    {
                        run();
                    }
                    catch (Exception ex)
                    {
                        OutputLog.Write("Command failed: " + ex);
                        OutputLog.Status("UEToVS: 命令出错，详见输出窗口");
                    }
                }, new CommandID(CommandIds.CommandSet, id));
                mcs.AddCommand(cmd);
            }

            Add(CommandIds.SearchEverywhere, () => ShowSearch(SearchTab.All));
            Add(CommandIds.GoToEverything, () => ShowSearch(SearchTab.All));
            Add(CommandIds.GoToType, () => ShowSearch(SearchTab.Types));
            Add(CommandIds.GoToFile, () => ShowSearch(SearchTab.Files));
            Add(CommandIds.GoToSymbol, () => ShowSearch(SearchTab.Symbols));
            Add(CommandIds.FindAction, () => ShowSearch(SearchTab.Actions));
            Add(CommandIds.FindInFiles, () => ShowSearch(SearchTab.Text));
            Add(CommandIds.GoToAsset, () => ShowSearch(SearchTab.Assets));

            Add(CommandIds.NavigateBack, () => NavigationService.Instance.Back());
            Add(CommandIds.NavigateForward, () => NavigationService.Instance.Forward());
            Add(CommandIds.RecentFiles, () => NavigationPopups.ShowRecentFiles(this));
            Add(CommandIds.RecentLocations, () => NavigationPopups.ShowRecentLocations(this));
            Add(CommandIds.LastEditLocation, () => NavigationService.Instance.LastEdit());
            Add(CommandIds.NextEditLocation, () => NavigationService.Instance.NextEdit());

            Add(CommandIds.FileStructure, () => NavigationPopups.ShowFileStructure(this));
            Add(CommandIds.NavigateTo, () => RelatedNavigator.ShowNavigateTo(this));
            Add(CommandIds.GoToRelated, () => RelatedNavigator.GoToRelated(this));
            Add(CommandIds.AddModuleDependency, () => ModuleDependencyFixer.FixAtCaret(this));

            Add(CommandIds.ToggleBookmark, BookmarkService.Toggle);
            Add(CommandIds.ShowBookmarks, () => BookmarkService.ShowList(this));
            for (int d = 0; d <= 9; d++)
            {
                int digit = d;
                Add(CommandIds.SetBookmark0 + d, () => BookmarkService.SetDigit(digit));
                Add(CommandIds.GoToBookmark0 + d, () => BookmarkService.GoToDigit(digit));
            }

            Add(CommandIds.LiveCoding, () => UnrealTools.LiveCoding(this));
            Add(CommandIds.GenerateProjectFiles, () => UnrealTools.GenerateProjectFiles(this));
            Add(CommandIds.ApplyKeymap, () => ApplyKeymap(_options.KeymapPreset));
            Add(CommandIds.RestoreKeymap, RestoreKeymap);
            Add(CommandIds.ReindexProject, Reindex);
        }

        private void RegisterActions()
        {
            Actions.AddToggle("UEToVS: 双击 Shift 打开 Search Everywhere", () => _options.DoubleShiftEnabled, v => { _options.DoubleShiftEnabled = v; ApplyOptions(); SaveOptions(); });
            Actions.AddToggle("UEToVS: 搜索结果预览", () => _options.ShowPreview, v => { _options.ShowPreview = v; SaveOptions(); });
            Actions.AddToggle("UEToVS: 鼠标侧键后退/前进", () => _options.MouseButtonsNavigate, v => { _options.MouseButtonsNavigate = v; SaveOptions(); });
            Actions.AddToggle("UEToVS: 保存时检查 Build.cs 模块依赖", () => _options.CheckIncludesOnSave, v => { _options.CheckIncludesOnSave = v; SaveOptions(); });
            foreach (var preset in KeymapPresets.All)
            {
                var p = preset;
                Actions.AddAction("UEToVS: 应用键位方案 — " + p.DisplayName, () => ApplyKeymap(p.Id));
            }
            Actions.AddAction("UEToVS: 恢复 Visual Studio 原来的键位", RestoreKeymap);
            Actions.AddAction("UEToVS: 重建索引", Reindex);
            Actions.AddAction("UEToVS: 生成工程文件 (Generate Project Files)", () => UnrealTools.GenerateProjectFiles(this));
            Actions.AddAction("UEToVS: Live Coding 编译", () => UnrealTools.LiveCoding(this), "Ctrl+Alt+F11");
            Actions.AddAction("UEToVS: 打开设置", () => ShowOptionPage(typeof(GeneralOptions)));
        }

        private void ApplyKeymap(string presetId)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            var preset = KeymapPresets.Find(presetId);
            int n = KeymapManager.Apply(preset);
            _options.KeymapPreset = preset.Id;
            _options.KeymapPrompted = true;
            SaveOptions();
            Actions.Invalidate();
            OutputLog.Status($"UEToVS: 已应用“{preset.DisplayName}”（{n} 个快捷键）。可用“恢复 Visual Studio 原来的键位”撤销");
        }

        private void RestoreKeymap()
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            int n = KeymapManager.Restore();
            Actions.Invalidate();
            OutputLog.Status(n > 0 ? $"UEToVS: 已恢复 {n} 个命令的原始快捷键" : "UEToVS: 没有可恢复的键位备份");
        }

        private void PromptKeymapOnce()
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            if (_options.KeymapPrompted) return;
            InfoBar.Show("UEToVS：要使用 Rider 的快捷键吗？会先备份当前键位，随时可以恢复。",
                ("应用 Rider 默认键位", () => ApplyKeymap(KeymapPresets.RiderDefaultId)),
                ("应用 Rider（VS 键位）", () => ApplyKeymap(KeymapPresets.RiderVisualStudioId)),
                ("不用了", () => { _options.KeymapPrompted = true; SaveOptions(); }));
        }

        private void Reindex()
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            if (_solutionDir == null) return;
            string dir = _solutionDir;
            OutputLog.Status("UEToVS: 正在重建索引…");
            _ = JoinableTaskFactory.RunAsync(async () =>
            {
                await TaskScheduler.Default;
                string extra = (_options.IndexerExtraArgs + " --rebuild").Trim();
                await _indexer.StartAsync(dir, _options.EngineRootOverride, extra);
            });
        }
    }
}
