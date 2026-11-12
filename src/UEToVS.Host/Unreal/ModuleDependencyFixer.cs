using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.VisualStudio.Shell;
using UEToVS.Core.Diagnostics;
using UEToVS.Core.Unreal;
using UEToVS.Host.Diagnostics;
using UEToVS.Host.Engine;
using UEToVS.Host.Integration;
using UEToVS.Host.Logic.Localization;
using UEToVS.Host.Logic.Search;
using UEToVS.Host.Logic.Unreal;
using UEToVS.Host.Navigation;
using UEToVS.Host.Search;
using UEToVS.Host.Shell;
using UiGlyph = UEToVS.UI.Logic.SymbolKind;

namespace UEToVS.Host.Unreal
{
    /// <summary>
    /// Build.cs dependency fix (prototype feature): an #include whose header belongs to a module the including module
    /// does not depend on → offer to add it to Public/PrivateDependencyModuleNames. Runs on save (banner) and on demand
    /// (command on the caret line / type name / whole file).
    /// </summary>
    internal static class ModuleDependencyFixer
    {
        private static readonly Logger L = Log.For("BuildCs");
        private static readonly HashSet<string> Dismissed = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        private static int _onSaveRunning;

        private sealed class IncludeEnv : IIncludeEnvironment
        {
            private readonly EngineClient _engine;
            private readonly ModuleLocator _locator = ModuleLocator.ForFileSystem();
            private readonly Dictionary<string, string> _open;

            public IncludeEnv(EngineClient engine, Dictionary<string, string> openTexts)
            {
                _engine = engine;
                _open = openTexts ?? new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            }

            public Task<IReadOnlyList<string>> FindFilesByNameAsync(string fileName, CancellationToken ct) => EngineQueries.FindFilesByNameAsync(_engine, fileName, ct);

            public string FindBuildCsForFile(string filePath) => _locator.FindBuildCs(filePath);

            public async Task<string> FindBuildCsForModuleAsync(string module, CancellationToken ct)
            {
                var files = await EngineQueries.FindFilesByNameAsync(_engine, module + ".Build.cs", ct).ConfigureAwait(false);
                // Prefer the project's own copy when the same module name exists twice.
                return files.OrderByDescending(p => p.IndexOf(Path.DirectorySeparatorChar + "Engine" + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) < 0).FirstOrDefault();
            }

            public string ReadText(string path)
            {
                if (_open.TryGetValue(path, out var t)) return t;
                try
                {
                    return File.Exists(path) ? File.ReadAllText(path) : null;
                }
                catch (IOException)
                {
                    return null;
                }
            }

            public bool FileExists(string path) => File.Exists(path);
        }

        internal static IIncludeEnvironment CreateEnvironment(HostPackage package)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            // Open (possibly unsaved) Build.cs buffers win over disk.
            var open = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            try
            {
                foreach (EnvDTE.Document d in VsEditor.Dte.Documents)
                {
                    try
                    {
                        if (d.FullName.EndsWith(".Build.cs", StringComparison.OrdinalIgnoreCase)) open[d.FullName] = VsEditor.ReadDocument(d);
                    }
                    catch (Exception)
                    {
                        // document closing
                    }
                }
            }
            catch (Exception ex)
            {
                L.Warn("buildcs.openDocs.failed", "Reading open Build.cs documents failed", ex);
            }
            return new IncludeEnv(package.Engine, open);
        }

        /// <summary>On save of a .h/.cpp (setting CheckIncludesOnSave): check and offer a fix in a banner.</summary>
        public static void OnSaved(HostPackage package, string path)
        {
            if (package.Settings?.Effective?.CheckIncludesOnSave != true || !IncludeDependencyChecker.IsCppFile(path)) return;
            if (package.Engine?.IsConnected != true || !package.Unreal.IsUnreal) return;
            if (Interlocked.Exchange(ref _onSaveRunning, 1) == 1) return; // one check at a time (Save All saves many files)
            Safe.RunAsync(L, "buildcs.onSave", async () =>
            {
                try
                {
                    await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
                    var env = CreateEnvironment(package);
                    string text = env.ReadText(path);
                    using (var cts = new CancellationTokenSource(TimeSpan.FromSeconds(20)))
                    {
                        var missing = await Task.Run(() => IncludeDependencyChecker.CheckAsync(path, IncludeDependencyChecker.ParseIncludes(text), env, cts.Token)).ConfigureAwait(true);
                        missing = missing.Where(m => !Dismissed.Contains(m.Key)).ToList();
                        if (missing.Count == 0) return;
                        ShowBanner(path, missing);
                    }
                }
                finally
                {
                    Interlocked.Exchange(ref _onSaveRunning, 0);
                }
            }, ("file", path));
        }

        private static void ShowBanner(string path, List<MissingDependency> missing)
        {
            var first = missing[0];
            string modules = string.Join(", ", missing.Select(m => m.Module).Distinct(StringComparer.OrdinalIgnoreCase));
            string includes = string.Join(", ", missing.Select(m => Path.GetFileName(m.Include)).Distinct().Take(3));
            bool header = RelatedNames.IsHeader(path);
            string preferred = header ? "PublicDependencyModuleNames" : "PrivateDependencyModuleNames";
            string other = header ? "PrivateDependencyModuleNames" : "PublicDependencyModuleNames";
            L.Info("buildcs.banner", null, ("file", path), ("modules", modules), ("owner", first.OwnerModule));
            Banners.Show("buildcs.missing", FeatureStrings.MissingDependencyBanner(Path.GetFileName(path), includes, modules, first.OwnerModule), BannerKind.Warning,
                (FeatureStrings.AddToShort(preferred), () => Apply(missing, header)),
                (FeatureStrings.AddToShort(other), () => Apply(missing, !header)),
                (FeatureStrings.Ignore, () =>
                {
                    foreach (var m in missing) Dismissed.Add(m.Key);
                    L.Info("buildcs.dismissed", null, ("modules", modules));
                }));
        }

        /// <summary>Command: the include on the caret line, the header declaring the type at the caret, or the whole file.</summary>
        public static async Task FixAtCaretAsync(HostPackage package)
        {
            await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
            var view = VsEditor.ActiveTextView();
            var cur = VsEditor.LocationOf(view);
            if (cur == null)
            {
                VsStatusBar.SetText(FeatureStrings.NoActiveDocument);
                return;
            }
            if (package.Engine?.IsConnected != true)
            {
                VsStatusBar.SetText(FeatureStrings.EngineUnavailable);
                return;
            }
            string include = IncludeDirective.Parse(VsEditor.CaretLineText(view));
            string word = VsEditor.WordAtCaret(view);
            string fileText = view.TextBuffer.CurrentSnapshot.GetText();
            var env = CreateEnvironment(package);
            List<IncludeRef> refs;
            if (include != null) refs = new List<IncludeRef> { new IncludeRef(include, cur.Line) };
            else if (!string.IsNullOrEmpty(word))
            {
                // Caret on a type name: the header that declares it.
                var symbols = await EngineQueries.FindSymbolsAsync(package.Engine, word, null, package.DisposalToken);
                var decl = symbols.FirstOrDefault(s => SearchEverywhereModel.IsTypeKind(s.Kind) && s.Location?.FilePath != null && RelatedNames.IsHeader(s.Location.FilePath));
                refs = decl == null ? IncludeDependencyChecker.ParseIncludes(fileText) : new List<IncludeRef> { new IncludeRef(Path.GetFileName(decl.Location.FilePath), cur.Line) };
            }
            else refs = IncludeDependencyChecker.ParseIncludes(fileText);

            List<MissingDependency> missing;
            using (var cts = new CancellationTokenSource(TimeSpan.FromSeconds(20)))
                missing = await Task.Run(() => IncludeDependencyChecker.CheckAsync(cur.FilePath, refs, env, cts.Token));
            await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
            if (missing.Count == 0)
            {
                VsStatusBar.SetText(FeatureStrings.DependenciesComplete);
                return;
            }
            bool header = RelatedNames.IsHeader(cur.FilePath);
            string modules = string.Join(", ", missing.Select(m => m.Module).Distinct(StringComparer.OrdinalIgnoreCase));
            var rows = new List<SearchRow>();
            foreach (bool isPublic in new[] { header, !header })
            {
                rows.Add(new SearchRow
                {
                    Kind = SearchRowKind.Action,
                    Title = FeatureStrings.AddTo(modules, isPublic ? "PublicDependencyModuleNames" : "PrivateDependencyModuleNames"),
                    Detail = missing[0].OwnerModule + ".Build.cs",
                    Glyph = UiGlyph.Module,
                    ActionId = isPublic ? "public" : "private",
                });
            }
            var pick = await RowPopups.ShowAsync("uetovs.buildcs.fix", FeatureStrings.AddDependencyTitle, rows, false, new System.Windows.Size(640, 200));
            if (pick.Chosen && pick.Item != null) Apply(missing, pick.Item.ActionId == "public");
        }

        /// <summary>Adds the modules to the owner Build.cs (open document edited in place and saved, else the file).</summary>
        public static void Apply(List<MissingDependency> missing, bool isPublic)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            foreach (var group in missing.GroupBy(m => m.OwnerBuildCsPath, StringComparer.OrdinalIgnoreCase))
            {
                string buildCs = group.Key;
                try
                {
                    var doc = VsEditor.FindOpenDocument(buildCs);
                    string text = doc != null ? VsEditor.ReadDocument(doc) : File.ReadAllText(buildCs);
                    var (newText, added) = IncludeDependencyChecker.ApplyToText(buildCs, text, group.Select(m => m.Module), isPublic);
                    if (added.Count == 0) continue;
                    if (doc != null)
                    {
                        VsEditor.WriteDocument(doc, newText);
                        doc.Save();
                    }
                    else File.WriteAllText(buildCs, newText);
                    L.Info("buildcs.applied", null, ("buildCs", buildCs), ("added", added.ToArray()), ("public", isPublic));
                    VsStatusBar.SetText(FeatureStrings.DependencyAdded(string.Join(", ", added), Path.GetFileName(buildCs)));
                }
                catch (Exception ex)
                {
                    L.Error("buildcs.apply.failed", "Editing Build.cs failed", ex, ("buildCs", buildCs));
                    VsStatusBar.SetText(FeatureStrings.DependencyEditFailed);
                }
            }
            Banners.Close("buildcs.missing");
        }
    }
}
