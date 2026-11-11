using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using Microsoft.VisualStudio.Shell;
using UEToVS.Core.Protocol;
using UEToVS.Core.Unreal;
using UEToVS.Navigation;
using UEToVS.Search;
using UEToVS.UI;

namespace UEToVS.Unreal
{
    /// <summary>
    /// Go to Related (Rider Ctrl+Alt+Home) for UE: header ↔ source (Public/Private aware), RPC and
    /// BlueprintNativeEvent trios (Foo / Foo_Implementation / Foo_Validate), .generated.h.
    /// Also builds the Navigate To (Ctrl+Shift+G) menu.
    /// </summary>
    internal static class RelatedNavigator
    {
        public static void GoToRelated(UEToVSPackage pkg)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            var view = VsHelpers.ActiveTextView();
            var cur = VsHelpers.LocationOf(view);
            if (cur == null) return;
            string word = VsHelpers.WordAtCaret(view);
            _ = pkg.JoinableTaskFactory.RunAsync(async () =>
            {
                var items = await CollectAsync(pkg, cur.FilePath, word);
                await pkg.JoinableTaskFactory.SwitchToMainThreadAsync();
                if (items.Count == 0)
                {
                    // Fall back to VS's own header/code toggle.
                    if (!VsHelpers.TryExecute("EditorContextMenus.CodeWindow.ToggleHeaderCodeFile"))
                        OutputLog.Status("UEToVS: 没有找到相关的文件或函数");
                    return;
                }
                if (items.Count == 1)
                {
                    items[0].Activate(OpenMode.Normal);
                    return;
                }
                ListPopup.Show("相关项", f => ListPopup.Filter(items, f), null, null, 640, 320);
            });
        }

        private static async System.Threading.Tasks.Task<List<SearchItem>> CollectAsync(UEToVSPackage pkg, string file, string word)
        {
            var items = new List<SearchItem>();
            var index = await pkg.GetIndexAsync();

            // 1. UE function trios.
            if (index != null && !string.IsNullOrEmpty(word))
            {
                try
                {
                    var related = RelatedNames.RelatedFunctionNames(word);
                    var fileSymbols = await index.GetFileSymbolsAsync(file, CancellationToken.None);
                    string container = fileSymbols?.FirstOrDefault(s => s.Name == word && !string.IsNullOrEmpty(s.Container))?.Container;
                    var found = await index.FindSymbolsAsync(related.Concat(new[] { word }).ToArray(), container, CancellationToken.None);
                    found = found ?? new List<SymbolDto>();
                    bool hasTrio = found.Any(x => x.Name != word && (x.Kind == SymbolKind.Method || x.Kind == SymbolKind.Function));
                    foreach (var s in found.Take(30))
                    {
                        if (s.Name == word && !hasTrio) continue;
                        if (s.Kind != SymbolKind.Method && s.Kind != SymbolKind.Function) continue;
                        if (s.Name == word && string.Equals(s.FilePath, file, StringComparison.OrdinalIgnoreCase)) continue;
                        var item = NavigationPopups.SymbolItem(s);
                        item.Title = (string.IsNullOrEmpty(s.Container) ? "" : s.Container + "::") + s.Name;
                        item.Detail = (s.IsDefinition ? "定义  " : "声明  ") + Path.GetFileName(s.FilePath);
                        items.Add(item);
                    }
                }
                catch (Exception ex)
                {
                    OutputLog.Write("Related symbols failed: " + ex.Message);
                }
            }

            // 2. Counterpart files.
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { file };
            foreach (var candidate in RelatedNames.CounterpartPathCandidates(file))
            {
                if (File.Exists(candidate) && seen.Add(candidate)) items.Add(NavigationPopups.FileItem(pkg, candidate));
            }
            if (index != null && items.All(i => i.Kind != ItemKind.File))
            {
                try
                {
                    var files = await index.FindFilesByNameAsync(RelatedNames.CounterpartFileNames(file).ToArray(), CancellationToken.None);
                    foreach (var f in files ?? new List<FileDto>())
                        if (seen.Add(f.Path)) items.Add(NavigationPopups.FileItem(pkg, f.Path));
                }
                catch (Exception ex)
                {
                    OutputLog.Write("Counterpart lookup failed: " + ex.Message);
                }
            }

            // 3. Generated header (useful when chasing GENERATED_BODY errors).
            if (RelatedNames.IsHeader(file) && index != null)
            {
                try
                {
                    string gen = Path.GetFileNameWithoutExtension(file) + ".generated.h";
                    var files = await index.FindFilesByNameAsync(new[] { gen }, CancellationToken.None);
                    foreach (var f in files ?? new List<FileDto>())
                        if (seen.Add(f.Path)) items.Add(NavigationPopups.FileItem(pkg, f.Path));
                }
                catch (Exception)
                {
                }
            }
            return items;
        }

        /// <summary>Navigate To (Rider Ctrl+Shift+G): a menu of everything reachable from the caret.</summary>
        public static void ShowNavigateTo(UEToVSPackage pkg)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            var view = VsHelpers.ActiveTextView();
            var cur = VsHelpers.LocationOf(view);
            string word = VsHelpers.WordAtCaret(view);
            var items = new List<SearchItem>();

            void Cmd(string title, string command, string keys = null)
            {
                items.Add(new SearchItem
                {
                    Kind = ItemKind.Action,
                    Title = title,
                    Right = keys,
                    Activate = m =>
                    {
                        ThreadHelper.ThrowIfNotOnUIThread();
                        if (!VsHelpers.TryExecute(command)) OutputLog.Status("UEToVS: 命令当前不可用 " + command);
                        return true;
                    },
                });
            }

            void Own(string title, Action run, string keys = null)
            {
                items.Add(new SearchItem { Kind = ItemKind.Action, Title = title, Right = keys, Activate = m => { run(); return true; } });
            }

            Cmd("声明 (Declaration)", "Edit.GoToDeclaration");
            Cmd("实现 (Implementation)", "Edit.GoToImplementation");
            Cmd("定义 (Definition)", "Edit.GoToDefinition");
            Cmd("基类符号 (Base Symbols)", "Edit.GoToBase");
            Cmd("用法 (Usages)", "Edit.FindAllReferences");
            Cmd("调用层次 (Call Hierarchy)", "EditorContextMenus.CodeWindow.ViewCallHierarchy");
            Cmd("类型层次 (Type Hierarchy)", "View.ClassView");
            Own("相关项：头文件/源文件、_Implementation、_Validate", () => GoToRelated(pkg));
            Own("文件结构", () => NavigationPopups.ShowFileStructure(pkg));
            if (cur != null)
            {
                Own("所属模块的 Build.cs", () => OpenBuildCs(pkg, cur.FilePath));
                Cmd("在解决方案资源管理器中定位", "SolutionExplorer.SyncWithActiveDocument");
                Cmd("在文件资源管理器中打开所在文件夹", "File.OpenContainingFolder");
            }
            if (!string.IsNullOrEmpty(word))
                Own("在 Search Everywhere 中搜索 “" + word + "”", () => pkg.ShowSearch(Core.Query.SearchTab.All, word));

            ListPopup.Show("跳转到" + (string.IsNullOrEmpty(word) ? "" : " — " + word), f => ListPopup.Filter(items, f), null, null, 460, 400);
        }

        public static void OpenBuildCs(UEToVSPackage pkg, string file)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            string buildCs = ModuleLocator.ForFileSystem().FindBuildCs(file);
            if (buildCs == null)
            {
                OutputLog.Status("UEToVS: 没有找到 " + Path.GetFileName(file) + " 所属模块的 Build.cs");
                return;
            }
            NavigationService.Instance.NavigateTo(buildCs, -1, 0);
        }
    }
}
