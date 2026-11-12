using System;
using System.Collections.Generic;
using System.ComponentModel.Design;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using EnvDTE;
using Microsoft.VisualStudio.Shell;
using UEToVS.Core.Diagnostics;
using UEToVS.Host.Diagnostics;
using UEToVS.Host.Editor;
using UEToVS.Host.Integration;
using UEToVS.Host.Logic.Actions;
using UEToVS.Host.Logic.Localization;
using UEToVS.Host.Logic.Unreal;
using UEToVS.Host.Navigation;
using UEToVS.Host.Shell;
using UEToVS.Host.Unreal;
using Task = System.Threading.Tasks.Task;

namespace UEToVS.Host.Build
{
    /// <summary>
    /// v0.4.5 构建结果 / 错误列表 / 新建 UE 类: services (build errors, Error List), actions (New UE Class, next / clear build
    /// errors; the catalog commands 0x0170–0x0172 are bound by CommandRouter) and the Solution Explorer context commands
    /// (0x0173 Generate Visual Studio Project Files on the solution / game project / .uproject, 0x0174 New UE Class on folders
    /// and projects), visible only where they apply. UI thread.
    /// </summary>
    internal static class BuildFeature
    {
        private static readonly Logger L = Log.For("NewClass");

        /// <summary>Called from HostActions.Register (UI thread, package initialization).</summary>
        public static void Register(HostPackage package, ActionRegistry registry)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            Safe.Execute(L, "build.errors.init", () => BuildErrorService.Initialize(package));
            Safe.Execute(L, "errorList.init", ErrorListService.Initialize);

            registry.Register(new DelegateAction(ActionIds.NewUeClass, BuildStrings.CmdNewUeClass, ActionCategory.Unreal,
                ctx => NewClassAsync(package, null), ctx => ctx.IsUnrealSolution && package.Unreal?.ProjectFile != null, null, BuildStrings.KwNewClass));
            registry.Register(new DelegateAction(ActionIds.NextBuildError, BuildStrings.CmdNextBuildError, ActionCategory.Navigation,
                async ctx =>
                {
                    await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
                    BuildErrorService.Instance?.GoToNext();
                }, ctx => BuildErrorService.Instance != null, null, BuildStrings.KwBuildErrors));
            registry.Register(new DelegateAction(ActionIds.ClearBuildErrors, BuildStrings.CmdClearBuildErrors, ActionCategory.Diagnostics,
                async ctx =>
                {
                    await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
                    BuildErrorService.Instance?.Clear();
                }, ctx => BuildErrorService.Instance?.Current.Count > 0, null, BuildStrings.KwBuildErrors));

            if (((IServiceProvider)package).GetService(typeof(IMenuCommandService)) is OleMenuCommandService mcs) RegisterContextCommands(package, mcs);
            Microsoft.VisualStudio.Shell.Events.SolutionEvents.OnAfterCloseSolution += (s, e) => Safe.Execute(L, "build.errors.reset", () => BuildErrorService.Instance?.Reset());
        }

        // ---------------------------------------------------------------- Solution Explorer

        private enum Node
        {
            None,
            Solution,
            GameProject,
            OtherProject,
            UProject,
            Folder,
            File,
        }

        private static void RegisterContextCommands(HostPackage package, OleMenuCommandService mcs)
        {
            var gen = new OleMenuCommand((s, e) => Safe.Execute(L, "ubt.context.invoke", () =>
                package.ExecuteAction(CommandCatalogIds.GenerateProjectFiles, ActionSource.Menu)), new CommandID(PackageGuids.CommandSet, CommandIds.CtxGenerateProjectFiles));
            gen.BeforeQueryStatus += (s, e) => Safe.Execute(L, "ubt.context.query", () =>
            {
                var node = Selected(package, out _);
                gen.Text = BuildStrings.CmdGenerateProjectFiles;
                gen.Visible = node == Node.Solution || node == Node.GameProject || node == Node.UProject;
                gen.Enabled = gen.Visible && !UnrealTools.IsGeneratingProjectFiles;
            });
            mcs.AddCommand(gen);

            var add = new OleMenuCommand((s, e) => Safe.Execute(L, "newClass.context.invoke", () =>
            {
                Selected(package, out var item);
                Safe.RunAsync(L, "newClass.context", () => NewClassAsync(package, item));
            }), new CommandID(PackageGuids.CommandSet, CommandIds.CtxNewUeClass));
            add.BeforeQueryStatus += (s, e) => Safe.Execute(L, "newClass.context.query", () =>
            {
                var node = Selected(package, out _);
                add.Text = BuildStrings.CmdNewUeClass;
                add.Visible = node == Node.Folder || node == Node.GameProject;
                add.Enabled = add.Visible;
            });
            mcs.AddCommand(add);
        }

        /// <summary>The single Solution Explorer selection, classified (UE solutions only; anything else → None).</summary>
        private static Node Selected(HostPackage package, out SelectedItem item)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            item = null;
            string uproject = package.Unreal?.ProjectFile;
            if (package.Unreal?.IsUnreal != true) return Node.None;
            if (!(Package.GetGlobalService(typeof(DTE)) is DTE dte)) return Node.None;
            var items = dte.SelectedItems;
            if (items == null || items.Count != 1) return Node.None;
            item = items.Item(1);
            if (item.ProjectItem != null)
            {
                var pi = item.ProjectItem;
                if (pi.Kind == EnvDTE.Constants.vsProjectItemKindVirtualFolder || pi.Kind == EnvDTE.Constants.vsProjectItemKindPhysicalFolder) return Node.Folder;
                string name = pi.Name ?? string.Empty;
                if (name.EndsWith(".uproject", StringComparison.OrdinalIgnoreCase)) return Node.UProject;
                return pi.ProjectItems != null && pi.ProjectItems.Count > 0 && Path.GetExtension(name).Length == 0 ? Node.Folder : Node.File;
            }
            if (item.Project != null)
            {
                string game = uproject == null ? null : Path.GetFileNameWithoutExtension(uproject);
                return game != null && string.Equals(item.Project.Name, game, StringComparison.OrdinalIgnoreCase) ? Node.GameProject : Node.OtherProject;
            }
            return Node.Solution;
        }

        /// <summary>Disk folder of a Solution Explorer folder (UE filters mirror the disk): through a file below it, else by name.</summary>
        private static string FolderOf(SelectedItem item, string projectRoot)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            var pi = item?.ProjectItem;
            if (pi == null) return null;
            try
            {
                string physical = pi.Properties?.Item("FullPath")?.Value as string;
                if (!string.IsNullOrEmpty(physical) && Directory.Exists(physical)) return physical.TrimEnd('\\');
            }
            catch (Exception)
            {
                // C++ filters have no FullPath
            }
            var file = FirstFile(pi.ProjectItems, 1, 0);
            if (file.Path != null) return NewClassPlanner.FolderFromDescendant(file.Path, file.Depth);
            // empty filter: its path from the project node, below the project root
            var names = new List<string>();
            for (var p = pi; p != null; p = p.Collection?.Parent as ProjectItem) names.Insert(0, p.Name);
            string guess = projectRoot == null ? null : Path.Combine(projectRoot, Path.Combine(names.ToArray()));
            return guess != null && Directory.Exists(guess) ? guess : null;
        }

        private static (string Path, int Depth) FirstFile(ProjectItems items, int depth, int visited)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            if (items == null || depth > 8) return (null, 0);
            var folders = new List<ProjectItem>();
            foreach (ProjectItem child in items)
            {
                if (++visited > 400) break;
                string f = child.FileCount > 0 ? child.FileNames[1] : null;
                if (!string.IsNullOrEmpty(f) && File.Exists(f)) return (f, depth - 1);
                folders.Add(child);
            }
            foreach (var folder in folders)
            {
                var r = FirstFile(folder.ProjectItems, depth + 1, visited);
                if (r.Path != null) return r;
            }
            return (null, 0);
        }

        // ---------------------------------------------------------------- New UE Class

        private static async Task NewClassAsync(HostPackage package, SelectedItem fromSolutionExplorer)
        {
            await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
            string uproject = package.Unreal?.ProjectFile;
            if (uproject == null)
            {
                VsStatusBar.SetText(FeatureStrings.NoUProject);
                return;
            }
            string root = Path.GetDirectoryName(uproject);
            string hint = fromSolutionExplorer != null ? FolderOf(fromSolutionExplorer, root) : Path.GetDirectoryName(EditorServices.FilePath(EditorServices.ActiveView()?.TextBuffer) ?? root);
            var scan = Stopwatch.StartNew();
            var (modules, copyright) = await Task.Run(() =>
            {
                var m = NewClassPlanner.ScanModules(root);
                string ini = Path.Combine(root, "Config", "DefaultGame.ini");
                string c = null;
                try
                {
                    if (File.Exists(ini)) c = NewClassPlanner.CopyrightFromIni(File.ReadAllText(ini));
                }
                catch (IOException)
                {
                }
                return (m, c);
            });
            await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
            if (modules.Count == 0)
            {
                L.Info("unreal.newClass.noModules", null, ("root", root));
                VsStatusBar.SetText(BuildStrings.NoModules);
                return;
            }
            var (module, isPublic, sub) = NewClassPlanner.ForFolder(modules, hint);
            if (module == null)
            {
                string game = Path.GetFileNameWithoutExtension(uproject);
                module = modules.FirstOrDefault(m => string.Equals(m.Name, game, StringComparison.OrdinalIgnoreCase) && m.Plugin == null) ?? modules[0];
                isPublic = module.CanSplit;
                sub = string.Empty;
            }
            double scanMs = scan.Elapsed.TotalMilliseconds;
            var choice = new NewClassDialog(modules, package.Engine, copyright).ShowModal(module, isPublic, sub, VsMainWindow.Handle);
            if (choice == null)
            {
                L.Info("unreal.newClass.cancelled", null, ("modules", modules.Count));
                return;
            }
            var spec = choice.Spec;
            try
            {
                string header = NewClassPlanner.Header(spec), source = NewClassPlanner.Source(spec);
                var utf8 = new UTF8Encoding(false);
                Directory.CreateDirectory(Path.GetDirectoryName(choice.Target.HeaderPath));
                Directory.CreateDirectory(Path.GetDirectoryName(choice.Target.SourcePath));
                File.WriteAllText(choice.Target.HeaderPath, header, utf8);
                File.WriteAllText(choice.Target.SourcePath, source, utf8);
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
            {
                L.Error("unreal.newClass.failed", "Writing the new class failed", ex, ("header", choice.Target.HeaderPath));
                VsStatusBar.SetText(BuildStrings.CreateFailed(ex.Message));
                return;
            }
            L.Info("unreal.newClass", null,
                   ("class", spec.ClassName), ("parent", spec.ParentName), ("kind", spec.Kind.ToString()), ("module", choice.Module.Name),
                   ("plugin", choice.Module.Plugin), ("public", choice.IsPublic), ("subdir", choice.Subdirectory), ("beginPlayTick", spec.WithBeginPlayAndTick),
                   ("fromSolutionExplorer", fromSolutionExplorer != null), ("modules", modules.Count), ("scanMs", Math.Round(scanMs, 1)),
                   ("header", choice.Target.HeaderPath), ("source", choice.Target.SourcePath));
            VsStatusBar.SetText(BuildStrings.Created(spec.ClassName));
            // Open first (they exist on disk), then regenerate the project files so they join the solution.
            VsEditor.OpenAt(choice.Target.SourcePath, -1, 0);
            VsEditor.OpenAt(choice.Target.HeaderPath, -1, 0);
            if (!UnrealTools.IsGeneratingProjectFiles) await UnrealTools.GenerateProjectFilesAsync(package);
        }

        private static class CommandCatalogIds
        {
            public const string GenerateProjectFiles = Logic.Commands.CommandCatalog.ActionIds.GenerateProjectFiles;
        }
    }
}
