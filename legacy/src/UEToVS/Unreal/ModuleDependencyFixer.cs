using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using EnvDTE;
using Microsoft.VisualStudio.Shell;
using UEToVS.Core.Protocol;
using UEToVS.Core.Unreal;
using UEToVS.Search;
using UEToVS.UI;

namespace UEToVS.Unreal
{
    /// <summary>
    /// Detects #includes whose module is not visible to the including module (missing Build.cs dependency, the
    /// classic "cannot open include file" / LNK2019 after adding an include) and adds the dependency.
    /// </summary>
    internal static class ModuleDependencyFixer
    {
        private static readonly HashSet<string> Dismissed = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        public static IncludeRef[] ReadIncludes(string path)
        {
            var list = new List<IncludeRef>();
            try
            {
                int i = 0;
                foreach (var line in File.ReadLines(path))
                {
                    var inc = IncludeDirective.Parse(line);
                    if (inc != null) list.Add(new IncludeRef { Path = inc, Line = i });
                    i++;
                }
            }
            catch (IOException)
            {
            }
            return list.ToArray();
        }

        private static bool IsCpp(string path)
        {
            string ext = Path.GetExtension(path).ToLowerInvariant();
            return ext == ".h" || ext == ".hpp" || ext == ".cpp" || ext == ".inl" || ext == ".cc";
        }

        /// <summary>On save: check the file and offer a fix in an InfoBar.</summary>
        public static void OnSaved(UEToVSPackage pkg, string path)
        {
            if (!pkg.Options.CheckIncludesOnSave || !IsCpp(path)) return;
            _ = pkg.JoinableTaskFactory.RunAsync(async () =>
            {
                var missing = await CheckAsync(pkg, path, null);
                missing = missing.Where(m => !Dismissed.Contains(m.OwnerModule + "|" + m.Module)).ToList();
                if (missing.Count == 0) return;
                await pkg.JoinableTaskFactory.SwitchToMainThreadAsync();
                var first = missing[0];
                var modules = missing.Select(m => m.Module).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
                string text = $"{Path.GetFileName(path)} 包含的 {string.Join("、", missing.Select(m => Path.GetFileName(m.Include)).Distinct().Take(3))} " +
                              $"属于模块 {string.Join("、", modules)}，但 {first.OwnerModule}.Build.cs 没有依赖它（编译或链接会失败）。";
                bool header = RelatedNames.IsHeader(path);
                InfoBar.Show(text,
                    ("添加到 " + (header ? "PublicDependencyModuleNames" : "PrivateDependencyModuleNames"), () => Apply(missing, header)),
                    ("添加到 " + (header ? "PrivateDependencyModuleNames" : "PublicDependencyModuleNames"), () => Apply(missing, !header)),
                    ("忽略", () => { foreach (var m in missing) Dismissed.Add(m.OwnerModule + "|" + m.Module); }));
            });
        }

        /// <summary>Command: check the include on the caret line (or the whole file) and fix it.</summary>
        public static void FixAtCaret(UEToVSPackage pkg)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            var view = VsHelpers.ActiveTextView();
            var cur = VsHelpers.LocationOf(view);
            if (cur == null) return;
            string lineText = view.Caret.Position.BufferPosition.GetContainingLine().GetText();
            string include = IncludeDirective.Parse(lineText);
            string word = VsHelpers.WordAtCaret(view);
            _ = pkg.JoinableTaskFactory.RunAsync(async () =>
            {
                var index = await pkg.GetIndexAsync();
                if (index == null)
                {
                    await pkg.JoinableTaskFactory.SwitchToMainThreadAsync();
                    OutputLog.Status("UEToVS: 索引未就绪");
                    return;
                }
                IncludeRef[] refs;
                if (include != null) refs = new[] { new IncludeRef { Path = include, Line = cur.Line } };
                else if (!string.IsNullOrEmpty(word))
                {
                    // Caret on a type name: find the header that declares it.
                    var symbols = await index.FindSymbolsAsync(new[] { word }, null, CancellationToken.None);
                    var decl = symbols.FirstOrDefault(s => (s.Kind <= SymbolKind.Typedef || s.Kind == SymbolKind.Delegate) && RelatedNames.IsHeader(s.FilePath));
                    refs = decl == null ? ReadIncludes(cur.FilePath) : new[] { new IncludeRef { Path = Path.GetFileName(decl.FilePath), Line = cur.Line } };
                }
                else refs = ReadIncludes(cur.FilePath);

                var missing = await CheckAsync(pkg, cur.FilePath, refs);
                await pkg.JoinableTaskFactory.SwitchToMainThreadAsync();
                if (missing.Count == 0)
                {
                    OutputLog.Status("UEToVS: 模块依赖完整，无需修改 Build.cs");
                    return;
                }
                bool header = RelatedNames.IsHeader(cur.FilePath);
                var items = new List<SearchItem>();
                foreach (bool isPublic in new[] { header, !header })
                {
                    string list = isPublic ? "PublicDependencyModuleNames" : "PrivateDependencyModuleNames";
                    items.Add(new SearchItem
                    {
                        Kind = ItemKind.Action,
                        Title = "把 " + string.Join("、", missing.Select(m => m.Module).Distinct()) + " 加入 " + list,
                        Detail = missing[0].OwnerModule + ".Build.cs",
                        Activate = m => { Apply(missing, isPublic); return true; },
                    });
                }
                ListPopup.Show("添加模块依赖", f => items, "头文件里用到的模块通常放 Public，只在 .cpp 用到的放 Private", null, 620, 200);
            });
        }

        private static async System.Threading.Tasks.Task<List<MissingDependency>> CheckAsync(UEToVSPackage pkg, string path, IncludeRef[] refs)
        {
            try
            {
                var index = await pkg.GetIndexAsync();
                if (index == null) return new List<MissingDependency>();
                refs = refs ?? ReadIncludes(path);
                if (refs.Length == 0) return new List<MissingDependency>();
                return await index.CheckIncludesAsync(path, refs, CancellationToken.None) ?? new List<MissingDependency>();
            }
            catch (Exception ex)
            {
                OutputLog.Write("Include check failed: " + ex.Message);
                return new List<MissingDependency>();
            }
        }

        public static void Apply(List<MissingDependency> missing, bool isPublic)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            foreach (var group in missing.GroupBy(m => m.OwnerBuildCsPath, StringComparer.OrdinalIgnoreCase))
            {
                string buildCs = group.Key;
                try
                {
                    string module = BuildCsFile.ModuleNameFromPath(buildCs);
                    var doc = FindOpenDocument(buildCs);
                    string text = doc != null ? ReadDocument(doc) : File.ReadAllText(buildCs);
                    var file = new BuildCsFile(module, text);
                    var added = new List<string>();
                    foreach (var dep in group.Select(m => m.Module).Distinct(StringComparer.OrdinalIgnoreCase))
                        if (file.AddDependency(dep, isPublic)) added.Add(dep);
                    if (added.Count == 0) continue;
                    if (doc != null)
                    {
                        WriteDocument(doc, file.Text);
                        doc.Save();
                    }
                    else
                    {
                        File.WriteAllText(buildCs, file.Text);
                    }
                    OutputLog.Write($"{Path.GetFileName(buildCs)}: 已添加 {string.Join(", ", added)} 到 {(isPublic ? "Public" : "Private")}DependencyModuleNames");
                    OutputLog.Status($"UEToVS: 已添加 {string.Join(", ", added)}，需要重新生成工程文件或直接编译");
                }
                catch (Exception ex)
                {
                    OutputLog.Write("Editing " + buildCs + " failed: " + ex.Message);
                    OutputLog.Status("UEToVS: 修改 Build.cs 失败，详见输出窗口");
                }
            }
        }

        private static Document FindOpenDocument(string path)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            foreach (Document d in VsHelpers.Dte.Documents)
            {
                try
                {
                    if (string.Equals(d.FullName, path, StringComparison.OrdinalIgnoreCase)) return d;
                }
                catch (Exception)
                {
                }
            }
            return null;
        }

        private static string ReadDocument(Document doc)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            var td = (TextDocument)doc.Object("TextDocument");
            return td.StartPoint.CreateEditPoint().GetText(td.EndPoint);
        }

        private static void WriteDocument(Document doc, string text)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            var td = (TextDocument)doc.Object("TextDocument");
            td.StartPoint.CreateEditPoint().ReplaceText(td.EndPoint, text, (int)vsEPReplaceTextOptions.vsEPReplaceTextKeepMarkers);
        }
    }
}
