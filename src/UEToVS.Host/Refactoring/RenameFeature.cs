using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using Microsoft.VisualStudio.Shell;
using UEToVS.Contracts;
using UEToVS.Core.Diagnostics;
using UEToVS.Core.Unreal;
using UEToVS.Host.Integration;
using UEToVS.Host.Logic.Actions;
using UEToVS.Host.Logic.Localization;
using UEToVS.Host.Logic.Navigation;
using UEToVS.Host.Diagnostics;
using UEToVS.Core.Localization;
using UEToVS.Host.Logic.Refactoring;
using UEToVS.Host.Navigation;
using UEToVS.Host.Shell;
using UEToVS.UI.Atoms;
using UEToVS.UI.Controls;
using UEToVS.UI.Logic;
using SymbolKind = UEToVS.Contracts.SymbolKind;

namespace UEToVS.Host.Refactoring
{
    /// <summary>
    /// Rename a symbol (type, function, variable, UPROPERTY / UFUNCTION): the existing Find References (engine, on demand)
    /// gives the locations; a preview dialog lists them by file with check boxes (engine / third-party hits listed only,
    /// name-only matches unchecked); the checked project locations change in one undo step. Reflected symbols can also get a
    /// [CoreRedirects] line in the project's Config/DefaultEngine.ini; a class named after its files can rename the .h / .cpp
    /// and rewrite the #includes in the affected files.
    /// </summary>
    internal static class RenameFeature
    {
        private static readonly Logger L = Log.For("Refactor");

        public static async Task ExecuteAsync(ActionContext ctx)
        {
            var sw = Stopwatch.StartNew();
            var (view, rc) = await RefactoringFeature.ResolveAsync(ctx);
            if (rc == null) return;
            await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
            if (!RefactorProviders.CanRename(rc))
            {
                VsStatusBar.SetText(rc.Symbol != null && !RenamePlanner.IsProjectOrigin(rc.Symbol.Origin) ? RefactorStrings.NotProjectFile : RefactorStrings.RenameNothing);
                return;
            }
            var symbol = rc.Symbol;
            string oldName = symbol.Name;
            var package = HostPackage.Instance;
            var engine = package?.Engine;
            if (engine == null || !engine.IsConnected)
            {
                VsStatusBar.SetText(NavStrings.EngineUnavailable);
                return;
            }

            VsStatusBar.SetText(RefactorStrings.Searching(oldName));
            var r = await engine.FindReferencesAsync(new ReferencesRequest { Symbol = symbol, IncludeDeclarations = true, IncludeNonProject = true, MaxResults = 5000 },
                ctx.CancellationToken);
            await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
            if (r.Status != EngineCallStatus.Ok)
            {
                VsStatusBar.SetText(EngineCallText.ForStatus(r.Status) ?? NavStrings.ReferencesFailed);
                L.Info("refactor.rename", null, ("name", oldName), ("result", "references-failed"), ("status", r.Status.ToString()), ("ms", sw.ElapsedMilliseconds));
                return;
            }
            var locations = RenamePlanner.FromHits(r.Value.Hits, oldName);
            int projectCount = locations.Count(l => l.IsEditable), engineCount = locations.Count - projectCount;
            if (projectCount == 0)
            {
                VsStatusBar.SetText(RefactorStrings.RenameNothing);
                L.Info("refactor.rename", null, ("name", oldName), ("result", "nothing"), ("engineHits", engineCount), ("ms", sw.ElapsedMilliseconds));
                return;
            }
            long searchMs = sw.ElapsedMilliseconds;

            // ---- v0.4.5 编辑体验: preset name (Alt+Enter naming fix) or inline rename first (editor.renameMode = Inline)
            string preset = ctx?.Get<string>(Logic.Editing.EditorFeatureKeys.PresetName);
            if (string.IsNullOrEmpty(preset) && UEToVS.Host.IntelliSense.IntelliSenseContext.Settings?.RenameMode == Logic.Settings.RenameMode.Inline && view != null && !view.IsClosed)
            {
                var occurrences = Logic.Editing.InlineRenamePlan.Occurrences(locations, rc.FilePath, oldName);
                if (occurrences.Count == 0) VsStatusBar.SetText(EditorFeatureStrings.InlineRenameNoOccurrences);
                else
                {
                    preset = await Editing.InlineRenameSession.RunAsync(view, occurrences, oldName);
                    await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
                    if (preset == null)
                    {
                        L.Info("refactor.rename", null, ("name", oldName), ("canceled", true), ("inline", true), ("ms", sw.ElapsedMilliseconds));
                        return;
                    }
                }
            }

            // ---- dialog
            var redirectKind = RenamePlanner.RedirectFor(symbol);
            string header = symbol.Location?.FilePath;
            bool isType = symbol.Kind == SymbolKind.Class || symbol.Kind == SymbolKind.Struct || symbol.Kind == SymbolKind.Enum;
            bool canRenameFiles = isType && header != null && RenamedFileBase(header, oldName, oldName) != null && RefactoringFeature.IsProjectPath(header);

            var dialog = new CheckListDialog<RenameLocation>(() => RefactorStrings.RenameDialogTitle(oldName), () => RefactorStrings.RenameButton,
                Present, l => l.FilePath + " " + l.LineText, new Size(820, 620))
            {
                Description = RefactorStrings.RenameDialogDescription(projectCount, engineCount) + (r.Value.Truncated ? " " + RefactorStrings.ReferencesTruncated : string.Empty),
            };
            var nameBox = new TextInput { Text = string.IsNullOrEmpty(preset) ? oldName : preset, Margin = new Thickness(0, 4, 0, 0) };
            var nameError = new UiText((string)null, TextVariant.Secondary) { Margin = new Thickness(0, 2, 0, 0) };
            var redirects = new UiCheckBox(RefactorStrings.WriteCoreRedirects, true) { Margin = new Thickness(0, 8, 0, 0), Visibility = redirectKind == RedirectKind.None ? Visibility.Collapsed : Visibility.Visible };
            Tip.Set(redirects, () => RefactorStrings.WriteCoreRedirectsTip);
            var renameFiles = new UiCheckBox(RefactorStrings.RenameFiles(Path.GetFileNameWithoutExtension(header ?? string.Empty), "…"), true)
            {
                Margin = new Thickness(0, 6, 0, 0),
                Visibility = canRenameFiles ? Visibility.Visible : Visibility.Collapsed,
            };
            dialog.Form.Children.Add(new UiText(RefactorStrings.NewNameLabel, TextVariant.Secondary));
            dialog.Form.Children.Add(nameBox);
            dialog.Form.Children.Add(nameError);
            dialog.Form.Children.Add(redirects);
            dialog.Form.Children.Add(renameFiles);
            nameBox.TextChanged += (s, e) => UEToVS.UI.Theming.Ui.Safe("refactor.rename.name", () =>
            {
                string n = nameBox.Text?.Trim() ?? string.Empty;
                nameError.Text = NameError(oldName, n);
                if (canRenameFiles)
                    renameFiles.Content = RefactorStrings.RenameFiles(Path.GetFileNameWithoutExtension(header),
                        RenamePlanner.IsValidIdentifier(n) ? RenamedFileBase(header, oldName, n) ?? "…" : "…");
            });
            dialog.Validate = () => NameError(oldName, nameBox.Text?.Trim() ?? string.Empty) == null;
            nameBox.Loaded += (s, e) => UEToVS.UI.Theming.Ui.Safe("refactor.rename.focus", () =>
            {
                nameBox.Focus();
                nameBox.SelectAll();
            });

            var rows = new List<CheckRow<RenameLocation>>();
            foreach (var g in locations.GroupBy(l => l.FilePath, StringComparer.OrdinalIgnoreCase))
            {
                bool editable = g.First().IsEditable;
                rows.Add(new CheckRow<RenameLocation> { Header = Path.GetFileName(g.Key) + (editable ? string.Empty : "  · " + RefactorStrings.EngineReadOnly), HeaderCount = g.Count() });
                rows.AddRange(g.Select(l => new CheckRow<RenameLocation> { Item = l, Checked = l.Checked, ReadOnly = !l.IsEditable }));
            }
            var picked = dialog.ShowModal(rows, VsMainWindow.Handle);
            if (picked == null)
            {
                L.Info("refactor.rename", null, ("name", oldName), ("canceled", true), ("project", projectCount), ("engine", engineCount), ("ms", sw.ElapsedMilliseconds));
                return;
            }
            string newName = nameBox.Text.Trim();
            foreach (var l in locations) l.Checked = false;
            foreach (var l in picked) l.Checked = true;

            // ---- plan
            var edits = RenamePlanner.Edits(locations, oldName, newName);
            int symbolEdits = edits.Count;
            var changedFiles = new HashSet<string>(edits.Select(e => e.FilePath), StringComparer.OrdinalIgnoreCase);

            string oldBase = null, newBase = null, cpp = null;
            if (canRenameFiles && renameFiles.IsChecked == true)
            {
                oldBase = Path.GetFileNameWithoutExtension(header);
                newBase = RenamedFileBase(header, oldName, newName);
                var candidates = await RelatedNavigator.CounterpartFilesAsync(package, header, package.DisposalToken);
                await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
                cpp = candidates.FirstOrDefault(c => !RelatedNames.IsHeader(c) && string.Equals(Path.GetFileNameWithoutExtension(c), oldBase, StringComparison.OrdinalIgnoreCase));
                // #include rewrites: every project file the rename touches, the header itself and its .cpp
                var scan = new HashSet<string>(changedFiles, StringComparer.OrdinalIgnoreCase) { header };
                if (cpp != null) scan.Add(cpp);
                foreach (var f in scan)
                {
                    if (!RefactoringFeature.IsProjectPath(f)) continue;
                    string text = RefactorEdits.ReadText(f);
                    if (text == null) continue;
                    foreach (var inc in RenamePlanner.IncludeRewrites(f, text, oldBase, newBase))
                        edits.Add(new TextEdit { FilePath = f, Line = inc.Line, Column = inc.Column, Length = inc.Length, OldText = oldBase, NewText = inc.Replacement });
                }
            }

            string ini = null, iniWrittenNew = null;
            var redirectLines = new List<string>();
            if (redirectKind != RedirectKind.None && redirects.IsChecked == true)
            {
                string owner = SignatureText.ClassName(symbol.Container);
                string line = RenamePlanner.RedirectLine(redirectKind, symbol.Module, oldName, newName, owner);
                string root = engine.LastStatus?.ProjectRoot;
                if (line != null && !string.IsNullOrEmpty(root))
                {
                    redirectLines.Add(line);
                    ini = Path.Combine(root, "Config", "DefaultEngine.ini");
                    string iniText = File.Exists(ini) ? RefactorEdits.ReadText(ini) : null;
                    if (iniText != null)
                    {
                        string updated = RenamePlanner.AddRedirects(iniText, redirectLines);
                        if (!ReferenceEquals(updated, iniText) && updated != iniText)
                            edits.Add(new TextEdit { FilePath = ini, Line = 0, Column = 0, Length = iniText.Length, OldText = iniText, NewText = updated });
                    }
                    else iniWrittenNew = RenamePlanner.AddRedirects(string.Empty, redirectLines);
                }
                else L.Warn("refactor.rename.redirect.skipped", "No Core Redirect written (no module / owner / project root)", null,
                    ("name", oldName), ("module", symbol.Module), ("owner", owner), ("root", root));
            }

            // ---- apply (one undo step)
            var result = RefactorEdits.Apply("UEToVS: " + RefactorStrings.RenameDialogTitle(oldName), edits);
            if (iniWrittenNew != null)
            {
                try
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(ini));
                    File.WriteAllText(ini, iniWrittenNew);
                }
                catch (Exception ex)
                {
                    L.Error("refactor.rename.ini.failed", "Writing DefaultEngine.ini failed", ex, ("ini", ini));
                    ini = null;
                }
            }

            int renamedFiles = 0;
            if (result.Ok && newBase != null && !string.Equals(oldBase, newBase, StringComparison.Ordinal))
            {
                foreach (var f in new[] { header, cpp })
                    if (f != null && RenameFile(f, Path.Combine(Path.GetDirectoryName(f), newBase + Path.GetExtension(f)))) renamedFiles++;
            }

            L.Info("refactor.rename", null, ("name", oldName), ("newName", newName), ("kind", symbol.Kind.ToString()), ("project", projectCount), ("engine", engineCount),
                ("picked", picked.Count), ("symbolEdits", symbolEdits), ("edits", result.Applied), ("skipped", result.Skipped), ("filesChanged", result.Files),
                ("filesRenamed", renamedFiles), ("redirect", redirectLines.Count > 0 && ini != null), ("truncated", r.Value.Truncated), ("ok", result.Ok),
                ("searchMs", searchMs), ("ms", sw.ElapsedMilliseconds));
            if (!result.Ok)
            {
                VsStatusBar.SetText(RefactorStrings.EditFailed);
                return;
            }
            var messages = new List<string> { RefactorStrings.Renamed(oldName, newName, result.Applied, result.Files) };
            if (result.Skipped > 0) messages.Add(RefactorStrings.RenameSkipped(result.Skipped));
            if (redirectLines.Count > 0 && ini != null) messages.Add(RefactorStrings.RedirectsWritten(ini));
            if (renamedFiles > 0) messages.Add(RefactorStrings.FilesRenamedRegenerate);
            VsStatusBar.SetText(string.Join("  ", messages));
        }

        /// <summary>Error text for the name box; null when the name can be used.</summary>
        private static string NameError(string oldName, string newName)
        {
            if (!RenamePlanner.IsValidIdentifier(newName)) return RefactorStrings.InvalidIdentifier;
            if (string.Equals(oldName, newName, StringComparison.Ordinal)) return RefactorStrings.NameUnchanged;
            return null;
        }

        private static string RenamedFileBase(string header, string oldName, string newName) => RenamePlanner.RenamedFileBase(header, oldName, newName);

        /// <summary>Renames through the VS project (keeps the project item and an open document) and falls back to the file system.</summary>
        private static bool RenameFile(string from, string to)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            if (!File.Exists(from) || File.Exists(to)) return false;
            bool ok = false;
            try
            {
                var item = VsEditor.Dte?.Solution?.FindProjectItem(from);
                if (item != null)
                {
                    item.Name = Path.GetFileName(to);
                    ok = File.Exists(to);
                }
            }
            catch (Exception ex)
            {
                L.Warn("refactor.rename.projectItem.failed", "Renaming through the project failed; moving the file", ex, ("from", from));
            }
            if (!ok)
            {
                try
                {
                    File.Move(from, to);
                    ok = true;
                }
                catch (Exception ex)
                {
                    L.Error("refactor.rename.file.failed", "Renaming the file failed", ex, ("from", from), ("to", to));
                    return false;
                }
            }
            L.Info("refactor.rename.file", null, ("from", from), ("to", to));
            var engine = HostPackage.Instance?.Engine;
            if (engine?.IsConnected == true)
                _ = Safe.RunInBackgroundAsync(L, "refactor.rename.notify", () => engine.TryInvokeAsync("notifyFilesChanged", async (svc, ct) =>
                {
                    await svc.NotifyFilesChangedAsync(new List<FileChange>
                    {
                        new FileChange { FilePath = from, Kind = FileChangeKind.Deleted },
                        new FileChange { FilePath = to, Kind = FileChangeKind.Created },
                    }, ct);
                    return true;
                }, CancellationToken.None), ("file", to));
            return true;
        }

        private static void Present(RenameLocation l, UEToVS.UI.Molecules.RowData d)
        {
            d.Primary = l.LineText ?? string.Empty;
            d.Secondary = l.Context;
            d.Right = (l.Line + 1).ToString();
            d.ToolTip = l.FilePath + ":" + (l.Line + 1);
            if (l.Confidence == ResolutionConfidence.NameOnly) d.AddBadge(Loc.T("仅名称", "name only"), BadgeKind.Neutral);
            if ((l.Kind & ReferenceKind.Declaration) != 0) d.AddBadge(Loc.T("声明", "decl"), BadgeKind.Neutral);
            if ((l.Kind & ReferenceKind.Definition) != 0) d.AddBadge(Loc.T("定义", "def"), BadgeKind.Neutral);
        }
    }
}
