using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.VisualStudio.Shell;
using UEToVS.Contracts;
using UEToVS.Core.Diagnostics;
using UEToVS.Core.Input;
using UEToVS.Core.Query;
using UEToVS.Host.Bookmarks;
using UEToVS.Host.Diagnostics;
using UEToVS.Host.Input;
using UEToVS.Host.Link;
using UEToVS.Host.Logic.Actions;
using UEToVS.Host.Logic.Localization;
using UEToVS.Host.Logic.Settings;
using UEToVS.Host.Navigation;
using UEToVS.Host.Search;
using UEToVS.Host.Shell;
using UEToVS.Host.Unreal;
using Task = System.Threading.Tasks.Task;

namespace UEToVS.Host
{
    /// <summary>VS command ids of the M1 features (range 0x0300–0x03FF). Must match UEToVSHostPackage.vsct.</summary>
    internal static class M1CommandIds
    {
        public const int SearchEverywhere = 0x0300;
        public const int GoToType = 0x0301;
        public const int GoToSymbol = 0x0302;
        public const int GoToFile = 0x0303;
        public const int GoToAsset = 0x0304;

        public const int NavigateBack = 0x0310;
        public const int NavigateForward = 0x0311;
        public const int RecentFiles = 0x0312;
        public const int RecentLocations = 0x0313;
        public const int LastEditLocation = 0x0314;
        public const int NextEditLocation = 0x0315;
        public const int FileStructure = 0x0316;
        public const int NavigateTo = 0x0317;

        public const int ToggleHeaderSource = 0x0320;
        public const int GoToRelated = 0x0321;
        public const int OpenBuildCs = 0x0322;
        public const int AddModuleDependency = 0x0323;

        public const int ToggleBookmark = 0x0330;
        public const int ShowBookmarks = 0x0331;
        public const int SetBookmark0 = 0x0340; // ..0x0349
        public const int GoToBookmark0 = 0x0350; // ..0x0359

        public const int LiveCoding = 0x0360;
        public const int GenerateProjectFiles = 0x0361;
        public const int InstallEditorPlugin = 0x0362;
    }

    /// <summary>Stable action ids of the M1 features (Find Action, Search Everywhere's Actions tab, menus).</summary>
    internal static class M1ActionIds
    {
        public const string SearchEverywhere = "uetovs.search.everywhere";
        public const string GoToType = "uetovs.search.type";
        public const string GoToSymbol = "uetovs.search.symbol";
        public const string GoToFile = "uetovs.search.file";
        public const string GoToAsset = "uetovs.search.asset";

        public const string NavigateBack = "uetovs.navigation.back";
        public const string NavigateForward = "uetovs.navigation.forward";
        public const string RecentFiles = "uetovs.navigation.recentFiles";
        public const string RecentLocations = "uetovs.navigation.recentLocations";
        public const string LastEditLocation = "uetovs.navigation.lastEdit";
        public const string NextEditLocation = "uetovs.navigation.nextEdit";
        public const string FileStructure = "uetovs.navigation.fileStructure";
        public const string NavigateTo = "uetovs.navigation.navigateTo";

        public const string ToggleHeaderSource = "uetovs.related.toggleHeaderSource";
        public const string GoToRelated = "uetovs.related.goTo";
        public const string OpenBuildCs = "uetovs.unreal.openBuildCs";
        public const string AddModuleDependency = "uetovs.unreal.addModuleDependency";

        public const string ToggleBookmark = "uetovs.bookmarks.toggle";
        public const string ShowBookmarks = "uetovs.bookmarks.list";
        public static string SetBookmark(int d) => "uetovs.bookmarks.set" + d;
        public static string GoToBookmark(int d) => "uetovs.bookmarks.goTo" + d;

        public const string LiveCoding = "uetovs.unreal.liveCoding";
        public const string GenerateProjectFiles = "uetovs.unreal.generateProjectFiles";
        public const string InstallEditorPlugin = "uetovs.unreal.installEditorPlugin";

        public static string ApplyKeymap(string presetId) => "uetovs.keymap.apply." + presetId;
        public const string RestoreKeymap = "uetovs.keymap.restore";
    }

    /// <summary>
    /// M1 "prototype parity" features on the new host: Search Everywhere (double Shift), navigation history
    /// (Alt+← / Alt+→, mouse side buttons), recent files/locations, edit locations, file structure, Navigate To,
    /// header/source toggle and related items, bookmarks, Build.cs dependency fix, UE editor link (assets, Live Coding),
    /// generate project files. Wired from <see cref="HostPackage"/> with three calls (initialize / solution opened / closed).
    /// </summary>
    internal static class M1Feature
    {
        private static readonly Logger L = Log.For("M1");
        private static readonly DoubleShiftDetector DoubleShift = new DoubleShiftDetector();
        private static InputMonitor _input;
        private static HostPackage _package;

        /// <summary>Package initialization (UI thread): services, actions, commands, input hooks.</summary>
        public static void Initialize(HostPackage package, ActionRegistry registry, CommandRouter router, OleMenuCommandService mcs)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            _package = package;
            SearchEverywhere.Initialize(package);
            EditorLinkService.Initialize(package);
            RegisterActions(package, registry);
            if (mcs != null && router != null) router.RegisterMap(mcs, Commands);

            DoubleShift.Triggered += (s, e) => Safe.Execute(L, "input.doubleShift", () =>
            {
                L.Debug("input.doubleShift", null);
                SearchEverywhere.Instance?.Show(SearchTab.All, null, "doubleShift");
            });
            Safe.Execute(L, "input.install", () =>
            {
                _input = new InputMonitor(DoubleShift);
                _input.MouseNavigate = back =>
                {
                    if (_package?.Settings?.Effective?.MouseButtonsNavigate != true) return false;
                    Safe.Execute(L, "input.mouseNavigate", () =>
                    {
                        if (back) NavigationService.Instance.Back();
                        else NavigationService.Instance.Forward();
                    }, ("back", back));
                    return true;
                };
            });
            ApplySettings(package.Settings?.Effective);
            package.Settings.Changed += (s, e) => Safe.Execute(L, "m1.settings", () => ApplySettings(e.New));

            NavigationService.Instance.FileSaved += path => OnFileSaved(package, path);
            L.Info("m1.initialized", null, ("actions", registry.Count), ("commands", Commands.Count), ("hooks", _input != null));
        }

        private static void ApplySettings(HostSettings s)
        {
            if (s == null) return;
            DoubleShift.Enabled = s.DoubleShiftEnabled;
            DoubleShift.IntervalMs = Math.Max(150, s.DoubleShiftIntervalMs);
            DoubleShift.LeftShiftOnly = s.DoubleShiftLeftOnly;
            NavigationService.Instance.Configure(s);
        }

        public static void OnUnrealSolutionOpened(string solutionDir)
        {
            NavigationService.Instance.Open(solutionDir);
            BookmarkService.Open(solutionDir);
        }

        public static void OnSolutionClosing()
        {
            NavigationService.Instance.Close();
            BookmarkService.Close();
            SearchEverywhere.Instance?.Dismiss();
        }

        public static void Dispose()
        {
            try
            {
                _input?.Dispose();
                _input = null;
            }
            catch (Exception)
            {
                // VS shutting down
            }
        }

        private static void OnFileSaved(HostPackage package, string path)
        {
            var engine = package.Engine;
            if (engine?.IsConnected == true)
                _ = Safe.RunInBackgroundAsync(L, "engine.notifySaved", () => engine.TryInvokeAsync("notifyFilesChanged", async (svc, ct) =>
                {
                    await svc.NotifyFilesChangedAsync(new List<FileChange> { new FileChange { FilePath = path, Kind = FileChangeKind.Changed } }, ct);
                    return true;
                }, CancellationToken.None), ("file", path));
            ModuleDependencyFixer.OnSaved(package, path);
        }

        // ------------------------------------------------------------------ commands

        public static readonly IReadOnlyList<(int CommandId, string ActionId, Func<string> Text)> Commands = BuildCommands();

        private static IReadOnlyList<(int, string, Func<string>)> BuildCommands()
        {
            var list = new List<(int, string, Func<string>)>
            {
                (M1CommandIds.SearchEverywhere, M1ActionIds.SearchEverywhere, () => FeatureStrings.CmdSearchEverywhere),
                (M1CommandIds.GoToType, M1ActionIds.GoToType, () => FeatureStrings.CmdGoToType),
                (M1CommandIds.GoToSymbol, M1ActionIds.GoToSymbol, () => FeatureStrings.CmdGoToSymbol),
                (M1CommandIds.GoToFile, M1ActionIds.GoToFile, () => FeatureStrings.CmdGoToFile),
                (M1CommandIds.GoToAsset, M1ActionIds.GoToAsset, () => FeatureStrings.CmdGoToAsset),
                (M1CommandIds.NavigateBack, M1ActionIds.NavigateBack, () => FeatureStrings.CmdNavigateBack),
                (M1CommandIds.NavigateForward, M1ActionIds.NavigateForward, () => FeatureStrings.CmdNavigateForward),
                (M1CommandIds.RecentFiles, M1ActionIds.RecentFiles, () => FeatureStrings.CmdRecentFiles),
                (M1CommandIds.RecentLocations, M1ActionIds.RecentLocations, () => FeatureStrings.CmdRecentLocations),
                (M1CommandIds.LastEditLocation, M1ActionIds.LastEditLocation, () => FeatureStrings.CmdLastEditLocation),
                (M1CommandIds.NextEditLocation, M1ActionIds.NextEditLocation, () => FeatureStrings.CmdNextEditLocation),
                (M1CommandIds.FileStructure, M1ActionIds.FileStructure, () => FeatureStrings.CmdFileStructure),
                (M1CommandIds.NavigateTo, M1ActionIds.NavigateTo, () => FeatureStrings.CmdNavigateTo),
                (M1CommandIds.ToggleHeaderSource, M1ActionIds.ToggleHeaderSource, () => FeatureStrings.CmdToggleHeaderSource),
                (M1CommandIds.GoToRelated, M1ActionIds.GoToRelated, () => FeatureStrings.CmdGoToRelated),
                (M1CommandIds.OpenBuildCs, M1ActionIds.OpenBuildCs, () => FeatureStrings.CmdOpenBuildCs),
                (M1CommandIds.AddModuleDependency, M1ActionIds.AddModuleDependency, () => FeatureStrings.CmdAddModuleDependency),
                (M1CommandIds.ToggleBookmark, M1ActionIds.ToggleBookmark, () => FeatureStrings.CmdToggleBookmark),
                (M1CommandIds.ShowBookmarks, M1ActionIds.ShowBookmarks, () => FeatureStrings.CmdShowBookmarks),
                (M1CommandIds.LiveCoding, M1ActionIds.LiveCoding, () => FeatureStrings.CmdLiveCoding),
                (M1CommandIds.GenerateProjectFiles, M1ActionIds.GenerateProjectFiles, () => FeatureStrings.CmdGenerateProjectFiles),
                (M1CommandIds.InstallEditorPlugin, M1ActionIds.InstallEditorPlugin, () => FeatureStrings.CmdInstallEditorPlugin),
            };
            for (int d = 0; d <= 9; d++)
            {
                int digit = d;
                list.Add((M1CommandIds.SetBookmark0 + d, M1ActionIds.SetBookmark(d), () => FeatureStrings.CmdSetBookmark(digit)));
                list.Add((M1CommandIds.GoToBookmark0 + d, M1ActionIds.GoToBookmark(d), () => FeatureStrings.CmdGoToBookmark(digit)));
            }
            return list;
        }

        // ------------------------------------------------------------------ actions

        private static Func<ActionContext, Task> Ui(Action a) => async ctx =>
        {
            await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
            a();
        };

        private static void RegisterActions(HostPackage p, ActionRegistry r)
        {
            bool Unreal(ActionContext c) => c.IsUnrealSolution;
            bool Engine(ActionContext c) => c.IsUnrealSolution && c.EngineAvailable;

            void Add(string id, string title, ActionCategory cat, Func<ActionContext, Task> run, Func<ActionContext, bool> available, string[] kw) =>
                r.Register(new DelegateAction(id, title, cat, run, available, null, kw));

            Add(M1ActionIds.SearchEverywhere, FeatureStrings.CmdSearchEverywhere, ActionCategory.Navigation, Ui(() => SearchEverywhere.Instance.Show(SearchTab.All)), null, FeatureStrings.KwSearch);
            Add(M1ActionIds.GoToType, FeatureStrings.CmdGoToType, ActionCategory.Navigation, Ui(() => SearchEverywhere.Instance.Show(SearchTab.Types)), null, FeatureStrings.KwSearch);
            Add(M1ActionIds.GoToSymbol, FeatureStrings.CmdGoToSymbol, ActionCategory.Navigation, Ui(() => SearchEverywhere.Instance.Show(SearchTab.Symbols)), null, FeatureStrings.KwSearch);
            Add(M1ActionIds.GoToFile, FeatureStrings.CmdGoToFile, ActionCategory.Navigation, Ui(() => SearchEverywhere.Instance.Show(SearchTab.Files)), null, FeatureStrings.KwSearch);
            Add(M1ActionIds.GoToAsset, FeatureStrings.CmdGoToAsset, ActionCategory.Unreal, Ui(() => SearchEverywhere.Instance.Show(SearchTab.Assets)), null, FeatureStrings.KwUnreal);

            Add(M1ActionIds.NavigateBack, FeatureStrings.CmdNavigateBack, ActionCategory.Navigation, Ui(NavigationService.Instance.Back), null, FeatureStrings.KwNavigation);
            Add(M1ActionIds.NavigateForward, FeatureStrings.CmdNavigateForward, ActionCategory.Navigation, Ui(NavigationService.Instance.Forward), null, FeatureStrings.KwNavigation);
            Add(M1ActionIds.RecentFiles, FeatureStrings.CmdRecentFiles, ActionCategory.Navigation, ctx => NavigationPopups.ShowRecentFilesAsync(p), null, FeatureStrings.KwRecent);
            Add(M1ActionIds.RecentLocations, FeatureStrings.CmdRecentLocations, ActionCategory.Navigation, ctx => NavigationPopups.ShowRecentLocationsAsync(p), null, FeatureStrings.KwRecent);
            Add(M1ActionIds.LastEditLocation, FeatureStrings.CmdLastEditLocation, ActionCategory.Navigation, Ui(NavigationService.Instance.PreviousEdit), null, FeatureStrings.KwRecent);
            Add(M1ActionIds.NextEditLocation, FeatureStrings.CmdNextEditLocation, ActionCategory.Navigation, Ui(NavigationService.Instance.NextEdit), null, FeatureStrings.KwRecent);
            Add(M1ActionIds.FileStructure, FeatureStrings.CmdFileStructure, ActionCategory.Navigation, ctx => NavigationPopups.ShowFileStructureAsync(p), Engine, FeatureStrings.KwNavigation);
            Add(M1ActionIds.NavigateTo, FeatureStrings.CmdNavigateTo, ActionCategory.Navigation, ctx => RelatedNavigator.ShowNavigateToAsync(p), null, FeatureStrings.KwNavigation);

            Add(M1ActionIds.ToggleHeaderSource, FeatureStrings.CmdToggleHeaderSource, ActionCategory.Navigation, ctx => RelatedNavigator.ToggleHeaderSourceAsync(p), null, FeatureStrings.KwRelated);
            Add(M1ActionIds.GoToRelated, FeatureStrings.CmdGoToRelated, ActionCategory.Navigation, ctx => RelatedNavigator.GoToRelatedAsync(p), null, FeatureStrings.KwRelated);
            Add(M1ActionIds.OpenBuildCs, FeatureStrings.CmdOpenBuildCs, ActionCategory.Unreal, ctx => RelatedNavigator.OpenBuildCsAsync(p), Unreal, FeatureStrings.KwBuildCs);
            Add(M1ActionIds.AddModuleDependency, FeatureStrings.CmdAddModuleDependency, ActionCategory.Fix, ctx => ModuleDependencyFixer.FixAtCaretAsync(p), Engine, FeatureStrings.KwBuildCs);

            Add(M1ActionIds.ToggleBookmark, FeatureStrings.CmdToggleBookmark, ActionCategory.Navigation, Ui(BookmarkService.Toggle), null, FeatureStrings.KwBookmark);
            Add(M1ActionIds.ShowBookmarks, FeatureStrings.CmdShowBookmarks, ActionCategory.Navigation, ctx => BookmarkService.ShowListAsync(), null, FeatureStrings.KwBookmark);
            for (int d = 0; d <= 9; d++)
            {
                int digit = d;
                Add(M1ActionIds.SetBookmark(d), FeatureStrings.CmdSetBookmark(d), ActionCategory.Navigation, Ui(() => BookmarkService.SetDigit(digit)), null, FeatureStrings.KwBookmark);
                Add(M1ActionIds.GoToBookmark(d), FeatureStrings.CmdGoToBookmark(d), ActionCategory.Navigation, Ui(() => BookmarkService.GoToDigit(digit)), null, FeatureStrings.KwBookmark);
            }

            Add(M1ActionIds.LiveCoding, FeatureStrings.CmdLiveCoding, ActionCategory.Unreal, ctx => EditorLinkService.Instance.LiveCodingAsync(), Unreal, FeatureStrings.KwUnreal);
            Add(M1ActionIds.GenerateProjectFiles, FeatureStrings.CmdGenerateProjectFiles, ActionCategory.Unreal, ctx => UnrealTools.GenerateProjectFilesAsync(p),
                c => c.IsUnrealSolution && !UnrealTools.IsGeneratingProjectFiles, FeatureStrings.KwUnreal); // v0.4.5: disabled while UBT runs
            Add(M1ActionIds.InstallEditorPlugin, FeatureStrings.CmdInstallEditorPlugin, ActionCategory.Unreal, ctx => UnrealTools.InstallEditorPluginAsync(p), Unreal, FeatureStrings.KwUnreal);

            // Optional full Rider layouts through DTE bindings (backed up; restorable).
            foreach (var preset in Core.Keymap.KeymapPresets.All)
            {
                var pr = preset;
                Add(M1ActionIds.ApplyKeymap(pr.Id), FeatureStrings.CmdApplyKeymap(FeatureStrings.PresetName(pr.Id)), ActionCategory.Settings, Ui(() =>
                {
                    int n = Keymap.KeymapService.Apply(pr);
                    VsStatusBar.SetText(FeatureStrings.KeymapApplied(FeatureStrings.PresetName(pr.Id), n));
                }), null, FeatureStrings.KwKeymap);
            }
            Add(M1ActionIds.RestoreKeymap, FeatureStrings.CmdRestoreKeymap, ActionCategory.Settings, Ui(() =>
            {
                int n = Keymap.KeymapService.Restore();
                VsStatusBar.SetText(n > 0 ? FeatureStrings.KeymapRestored(n) : FeatureStrings.NoKeymapBackup);
            }), c => Keymap.KeymapService.HasBackup, FeatureStrings.KwKeymap);
        }
    }
}
