using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.VisualStudio.Shell;
using UEToVS.Contracts;
using UEToVS.Core.Diagnostics;
using UEToVS.Core.Query;
using UEToVS.Core.Unreal;
using UEToVS.Host.Logic.Localization;
using UEToVS.Host.Logic.Navigation;
using UEToVS.Host.Logic.Search;
using UEToVS.Host.Search;
using UEToVS.Host.Shell;
using UiGlyph = UEToVS.UI.Logic.SymbolKind;

namespace UEToVS.Host.Navigation
{
    /// <summary>
    /// Related items for UE (Rider Go to Related): header ↔ source (Public/Private aware), RPC / BlueprintNativeEvent
    /// trios (Foo / Foo_Implementation / Foo_Validate), the .generated.h; the module's Build.cs; the Navigate To menu.
    /// </summary>
    internal static class RelatedNavigator
    {
        private static readonly Logger L = Log.For("Related");

        /// <summary>Alt+O: switch to the counterpart header/source; several candidates → chooser.</summary>
        public static async Task ToggleHeaderSourceAsync(HostPackage package)
        {
            await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
            var cur = VsEditor.CurrentLocation();
            if (cur == null)
            {
                VsStatusBar.SetText(FeatureStrings.NoActiveDocument);
                return;
            }
            var files = await CounterpartFilesAsync(package, cur.FilePath, package.DisposalToken);
            await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
            L.Info("related.toggle", null, ("file", cur.FilePath), ("candidates", files.Count));
            if (files.Count == 0)
            {
                // Fall back to VS's own toggle (handles odd layouts it knows from the project system).
                if (!VsEditor.TryExecute("EditorContextMenus.CodeWindow.ToggleHeaderCodeFile")) VsStatusBar.SetText(FeatureStrings.NoCounterpart);
                return;
            }
            if (files.Count == 1)
            {
                NavigationService.Instance.JumpTo(files[0], -1, 0, JumpReasons.Related);
                return;
            }
            var rows = files.Select(f => SearchEverywhereModel.RecentFileRow(f, ProjectRoot(package), false)).ToList();
            await PickAndJumpAsync("uetovs.related.toggle", FeatureStrings.CmdToggleHeaderSource, rows);
        }

        /// <summary>Counterpart files: same-folder / Public↔Private candidates on disk, else the index by file name.</summary>
        public static async Task<List<string>> CounterpartFilesAsync(HostPackage package, string file, CancellationToken ct)
        {
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { file };
            var result = new List<string>();
            foreach (var candidate in RelatedNames.CounterpartPathCandidates(file))
                if (File.Exists(candidate) && seen.Add(candidate)) result.Add(candidate);
            if (result.Count > 0) return result;
            string module = ModuleLocator.ForFileSystem().FindBuildCs(file);
            string moduleDir = module == null ? null : Path.GetDirectoryName(module);
            foreach (var name in RelatedNames.CounterpartFileNames(file))
            {
                var found = await EngineQueries.FindFilesByNameAsync(package.Engine, name, ct).ConfigureAwait(false);
                // Same module first: "Foo.h" exists in many modules.
                foreach (var f in found.OrderByDescending(p => moduleDir != null && p.StartsWith(moduleDir, StringComparison.OrdinalIgnoreCase)))
                    if (seen.Add(f)) result.Add(f);
            }
            if (moduleDir != null && result.Any(p => p.StartsWith(moduleDir, StringComparison.OrdinalIgnoreCase)))
                result = result.Where(p => p.StartsWith(moduleDir, StringComparison.OrdinalIgnoreCase)).ToList();
            return result;
        }

        /// <summary>Go to Related: function trio + counterpart files + generated header; one result jumps directly.</summary>
        public static async Task GoToRelatedAsync(HostPackage package)
        {
            await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
            var view = VsEditor.ActiveTextView();
            var cur = VsEditor.LocationOf(view);
            if (cur == null)
            {
                VsStatusBar.SetText(FeatureStrings.NoActiveDocument);
                return;
            }
            string word = VsEditor.WordAtCaret(view);
            List<SearchRow> rows;
            using (L.Time("related.collect", 300, ("file", Path.GetFileName(cur.FilePath))))
                rows = await CollectAsync(package, cur.FilePath, word, package.DisposalToken);
            await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
            L.Info("related.items", null, ("word", word), ("count", rows.Count));
            if (rows.Count == 0)
            {
                if (!VsEditor.TryExecute("EditorContextMenus.CodeWindow.ToggleHeaderCodeFile")) VsStatusBar.SetText(FeatureStrings.NoRelated);
                return;
            }
            if (rows.Count == 1)
            {
                Jump(rows[0], false);
                return;
            }
            await PickAndJumpAsync("uetovs.related", FeatureStrings.RelatedTitle, rows);
        }

        private static async Task<List<SearchRow>> CollectAsync(HostPackage package, string file, string word, CancellationToken ct)
        {
            var rows = new List<SearchRow>();
            var engine = package.Engine;

            // 1. UE function trios (Foo / Foo_Implementation / Foo_Validate) in the same class.
            if (engine?.IsConnected == true && !string.IsNullOrEmpty(word))
            {
                var fileSymbols = await EngineQueries.GetFileSymbolsAsync(engine, file, ct).ConfigureAwait(false);
                string container = fileSymbols?.FirstOrDefault(s => s.Name == word && !string.IsNullOrEmpty(s.Container))?.Container;
                var found = new List<SymbolInfo>();
                foreach (var name in RelatedNames.RelatedFunctionNames(word).Concat(new[] { word }).Distinct())
                    found.AddRange(await EngineQueries.FindSymbolsAsync(engine, name, container, ct).ConfigureAwait(false));
                bool IsFunc(SymbolInfo s) => s.Kind == Contracts.SymbolKind.Method || s.Kind == Contracts.SymbolKind.Function;
                bool hasTrio = found.Any(x => x.Name != word && IsFunc(x));
                foreach (var s in found.Where(IsFunc).Take(30))
                {
                    if (s.Name == word && !hasTrio) continue;
                    if (s.Name == word && string.Equals(s.Location?.FilePath, file, StringComparison.OrdinalIgnoreCase)) continue;
                    var row = SearchEverywhereModel.FromSymbol(new SearchHit { Symbol = s, Category = ResultCategory.Symbol }, null, false);
                    row.Title = (string.IsNullOrEmpty(s.Container) ? "" : s.Container + "::") + s.Name;
                    row.Detail = (s.IsDefinition ? FeatureStrings.DefinitionLabel : FeatureStrings.DeclarationLabel) + "  " + Path.GetFileName(s.Location?.FilePath ?? "");
                    rows.Add(row);
                }
            }

            // 2. Counterpart files.
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { file };
            foreach (var f in await CounterpartFilesAsync(package, file, ct).ConfigureAwait(false))
                if (seen.Add(f)) rows.Add(SearchEverywhereModel.RecentFileRow(f, ProjectRoot(package), false));

            // 3. The generated header (useful when chasing GENERATED_BODY errors).
            if (RelatedNames.IsHeader(file) && engine?.IsConnected == true)
            {
                string gen = Path.GetFileNameWithoutExtension(file) + ".generated.h";
                foreach (var f in await EngineQueries.FindFilesByNameAsync(engine, gen, ct).ConfigureAwait(false))
                    if (seen.Add(f))
                    {
                        var row = SearchEverywhereModel.RecentFileRow(f, ProjectRoot(package), false);
                        row.Right = FeatureStrings.GeneratedHeader;
                        rows.Add(row);
                    }
            }
            return rows;
        }

        public static async Task OpenBuildCsAsync(HostPackage package)
        {
            await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
            var cur = VsEditor.CurrentLocation();
            if (cur == null)
            {
                VsStatusBar.SetText(FeatureStrings.NoActiveDocument);
                return;
            }
            string buildCs = ModuleLocator.ForFileSystem().FindBuildCs(cur.FilePath);
            if (buildCs == null)
            {
                VsStatusBar.SetText(FeatureStrings.NoBuildCs(Path.GetFileName(cur.FilePath)));
                return;
            }
            NavigationService.Instance.JumpTo(buildCs, -1, 0, JumpReasons.BuildCs);
        }

        /// <summary>Navigate To (Rider Ctrl+Shift+G): everything reachable from the caret, as a menu.</summary>
        public static async Task ShowNavigateToAsync(HostPackage package)
        {
            await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
            var view = VsEditor.ActiveTextView();
            var cur = VsEditor.LocationOf(view);
            string word = VsEditor.WordAtCaret(view);
            var rows = new List<SearchRow>();
            var run = new Dictionary<SearchRow, Action>();

            void Vs(string title, string command)
            {
                var r = new SearchRow { Kind = SearchRowKind.Action, Title = title, Right = command, Glyph = UiGlyph.Action };
                rows.Add(r);
                run[r] = () =>
                {
                    if (!VsEditor.TryExecute(command)) VsStatusBar.SetText(FeatureStrings.CommandUnavailable(command));
                };
            }

            void Own(string title, string actionId)
            {
                var r = new SearchRow { Kind = SearchRowKind.Action, Title = title, Glyph = UiGlyph.Action, ActionId = actionId };
                rows.Add(r);
                run[r] = () => package.ExecuteAction(actionId, Logic.Actions.ActionSource.ContextMenu);
            }

            Vs(FeatureStrings.NavDeclaration, "Edit.GoToDeclaration");
            Vs(FeatureStrings.NavImplementation, "Edit.GoToImplementation");
            Vs(FeatureStrings.NavDefinition, "Edit.GoToDefinition");
            Vs(FeatureStrings.NavBase, "Edit.GoToBase");
            Vs(FeatureStrings.NavUsages, "Edit.FindAllReferences");
            // ---- v0.4.5 navigation gaps: VS's View Call Hierarchy needs VS's C++ IntelliSense (off after the takeover)
            Own(FeatureStrings.NavCallHierarchy, Logic.Commands.CommandCatalog.ActionIds.CallHierarchy);
            Own(FeatureStrings.NavRelated, M1ActionIds.GoToRelated);
            Own(FeatureStrings.CmdToggleHeaderSource, M1ActionIds.ToggleHeaderSource);
            Own(FeatureStrings.CmdFileStructure, M1ActionIds.FileStructure);
            if (cur != null)
            {
                Own(FeatureStrings.CmdOpenBuildCs, M1ActionIds.OpenBuildCs);
                Vs(FeatureStrings.NavSyncSolutionExplorer, "SolutionExplorer.SyncWithActiveDocument");
                Vs(FeatureStrings.NavOpenFolder, "File.OpenContainingFolder");
            }
            if (!string.IsNullOrEmpty(word))
            {
                var r = new SearchRow { Kind = SearchRowKind.Action, Title = FeatureStrings.NavSearchWord(word), Glyph = UiGlyph.Action };
                rows.Add(r);
                run[r] = () => SearchEverywhere.Instance?.Show(SearchTab.All, word, "navigateTo");
            }
            var pick = await RowPopups.ShowAsync("uetovs.navigateTo", FeatureStrings.NavigateToTitle(word), rows, true, new System.Windows.Size(480, 420));
            if (!pick.Chosen || pick.Item == null || !run.TryGetValue(pick.Item, out var action)) return;
            L.Info("navigateTo.invoke", null, ("item", pick.Item.Right ?? pick.Item.ActionId ?? pick.Item.Title));
            action();
        }

        internal static string ProjectRoot(HostPackage package)
        {
            string p = package.Unreal?.ProjectFile;
            return string.IsNullOrEmpty(p) ? package.SolutionDirectory : Path.GetDirectoryName(p);
        }

        internal static async Task PickAndJumpAsync(string id, string title, IReadOnlyList<SearchRow> rows)
        {
            var pick = await RowPopups.ShowAsync(id, title, rows);
            if (pick.Chosen && pick.Item != null) Jump(pick.Item, pick.Kind == UI.Logic.InvokeKind.OpenInNewTab);
        }

        private static void Jump(SearchRow row, bool newTab)
        {
            if (row?.FilePath == null) return;
            NavigationService.Instance.JumpTo(row.FilePath, row.Line, row.Column, JumpReasons.Related, newTab);
        }
    }
}
