using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using Microsoft.VisualStudio.Shell;
using UEToVS.Contracts;
using UEToVS.Core.Diagnostics;
using UEToVS.Core.Localization;
using UEToVS.Host.Integration;
using UEToVS.Host.Logic.Actions;
using UEToVS.Host.Logic.Localization;
using UEToVS.Host.Logic.Navigation;
using UEToVS.Host.Logic.Refactoring;
using UEToVS.Host.Shell;
using UEToVS.UI.Logic;

namespace UEToVS.Host.Refactoring.Batch2
{
    /// <summary>
    /// Change Signature (v0.4.5): edit the parameters in a dialog; the declaration(s), the definition (parameter renames
    /// follow into the body), UFUNCTION _Implementation / _Validate, optionally the whole override chain, and every call
    /// change in one undo step. Same flow as Rename: Find References (engine) → preview by file (uncheckable; engine /
    /// third-party / generated files and places that cannot be planned are listed only) → apply. Defaults stay in the
    /// declarations; a call that relied on a moved / removed default gets the value spelled out.
    /// </summary>
    internal static class ChangeSignatureFeature
    {
        private static readonly Logger L = Log.For("Refactor");

        public static async Task ExecuteAsync(ActionContext ctx)
        {
            var sw = Stopwatch.StartNew();
            var (view, rc) = await RefactoringBatch2.ResolveAsync(ctx, r => r.IncludeSignatureFamily = true);
            if (rc == null) return;
            await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
            if (!RefactorProviders2.CanChangeSignature(rc, out string problem))
            {
                Report(problem, rc, sw, ctx);
                return;
            }
            var f = rc.Function;
            var bases = rc.Engine.BaseDeclarations ?? new List<SymbolInfo>();
            var overrides = rc.Engine.Overrides ?? new List<SymbolInfo>();
            if (bases.Any(b => !RenamePlanner.IsProjectOrigin(b.Origin))) problem = "engine-base";
            else if ((f.Modifiers & FunctionModifiers.Override) != 0 && bases.Count == 0) problem = "no-base";
            if (problem != null)
            {
                Report(problem, rc, sw, ctx);
                return;
            }
            var original = ChangeSignaturePlanner.Parse(f.Parameters ?? string.Empty);
            string prefix = (string.IsNullOrEmpty(f.ReturnType) ? string.Empty : f.ReturnType.Trim() + " ") + f.Name;
            var editor = new ChangeSignatureDialog(f.Name, prefix, original, bases.Count + overrides.Count);
            var edited = editor.ShowModal(VsMainWindow.Handle);
            if (edited == null)
            {
                L.Info("refactor.changeSignature", null, ("name", f.Name), ("canceled", true), ("stage", "parameters"), ("ms", sw.ElapsedMilliseconds));
                return;
            }
            bool family = editor.IncludeFamily;
            var change = new ChangeSignaturePlanner.Change { Original = original, Edited = edited };
            var primary = f.IsDefinition ? rc.Engine.Declarations?.FirstOrDefault()?.Location : f.Location;
            if (primary != null)
            {
                change.PrimaryDeclarationFile = primary.FilePath;
                change.PrimaryDeclarationLine = primary.Line;
            }

            // ---- references of the function, its UE counterparts and (optionally) the override chain
            var symbols = new List<SymbolInfo> { rc.Symbol != null && rc.Symbol.Name == f.Name ? rc.Symbol : ToSymbol(f) };
            symbols.AddRange((rc.Engine.UeCounterpartDefinitions ?? new List<SymbolInfo>()).Where(s => s != null));
            if (family)
            {
                symbols.AddRange(bases);
                symbols.AddRange(overrides);
            }
            var engine = HostPackage.Instance?.Engine;
            if (engine == null || !engine.IsConnected)
            {
                VsStatusBar.SetText(NavStrings.EngineUnavailable);
                return;
            }
            VsStatusBar.SetText(Refactor2Strings.Searching(f.Name));
            var locations = new List<(RenameLocation Location, string Name)>();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var searched = new HashSet<string>(StringComparer.Ordinal);
            bool truncated = false;
            foreach (var s in symbols)
            {
                string key = s.Name + "|" + s.Location?.FilePath + "|" + s.Location?.Line;
                if (!searched.Add(key)) continue;
                var r = await engine.FindReferencesAsync(new ReferencesRequest { Symbol = s, IncludeDeclarations = true, IncludeNonProject = true, MaxResults = 5000 },
                    ctx.CancellationToken);
                await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
                if (r.Status != EngineCallStatus.Ok)
                {
                    VsStatusBar.SetText(EngineCallText.ForStatus(r.Status) ?? NavStrings.ReferencesFailed);
                    L.Info("refactor.changeSignature", null, ("name", f.Name), ("result", "references-failed"), ("symbol", s.Name), ("status", r.Status.ToString()),
                        ("ms", sw.ElapsedMilliseconds));
                    return;
                }
                truncated |= r.Value.Truncated;
                foreach (var l in RenamePlanner.FromHits(r.Value.Hits, s.Name))
                    if (seen.Add(l.FilePath + "|" + l.Line + "|" + l.Column)) locations.Add((l, s.Name));
            }
            long searchMs = sw.ElapsedMilliseconds;

            // ---- plan every place (text read once per file)
            var texts = new Dictionary<string, (string Text, List<ScanToken> Tokens)>(StringComparer.OrdinalIgnoreCase);
            var sites = new List<SignatureSite>();
            int unchanged = 0;
            foreach (var (l, name) in locations)
            {
                if (!texts.TryGetValue(l.FilePath, out var file))
                {
                    string text = RefactorEdits.ReadText(l.FilePath);
                    file = (text, text == null ? null : CppTextScan.Tokenize(text));
                    texts[l.FilePath] = file;
                }
                if (file.Text == null) continue;
                bool editable = l.IsEditable && RefactoringBatch2.IsEditablePath(l.FilePath);
                var site = ChangeSignaturePlanner.PlanSite(l.FilePath, file.Text, file.Tokens, l.Line, l.Column, name, l.Kind, editable, change);
                if (site.Problem == null && site.Edits.Count == 0)
                {
                    unchanged++;
                    continue;
                }
                if (l.Confidence == ResolutionConfidence.NameOnly) site.Checked = false;
                sites.Add(site);
            }
            // a declaration or definition that cannot be planned would leave the code half changed: refuse
            var broken = sites.FirstOrDefault(s => s.IsEditable && s.Problem != null && (s.Kind == SignatureSiteKind.Declaration || s.Kind == SignatureSiteKind.Definition));
            bool anySignature = sites.Any(s => s.IsEditable && s.Problem == null && (s.Kind == SignatureSiteKind.Declaration || s.Kind == SignatureSiteKind.Definition));
            if (broken != null || !anySignature)
            {
                VsStatusBar.SetText(Refactor2Strings.Problem("unparsable") + (broken == null ? string.Empty : "  " + Path.GetFileName(broken.FilePath) + ":" + (broken.Line + 1)));
                L.Info("refactor.changeSignature", null, ("name", f.Name), ("result", broken != null ? "declaration-unparsable" : "no-declaration"),
                    ("problem", broken?.Problem), ("sites", sites.Count), ("ms", sw.ElapsedMilliseconds));
                return;
            }

            // ---- preview
            int project = sites.Count(s => s.IsEditable && s.Problem == null), engineSites = sites.Count(s => !s.IsEditable), problems = sites.Count(s => s.IsEditable && s.Problem != null);
            var dialog = new CheckListDialog<SignatureSite>(() => Refactor2Strings.PreviewTitle(f.Name), () => Refactor2Strings.ChangeSignatureButton,
                Present, s => s.FilePath + " " + s.OldLineText, new Size(900, 620), "dialog.changeSignaturePreview")
            {
                Description = Refactor2Strings.PreviewDescription(project, engineSites, problems) + (truncated ? " " + RefactorStrings.ReferencesTruncated : string.Empty),
            };
            var rows = new List<CheckRow<SignatureSite>>();
            foreach (var g in sites.OrderBy(s => s.IsEditable ? 0 : 1).ThenBy(s => s.FilePath, StringComparer.OrdinalIgnoreCase).ThenBy(s => s.Line)
                                   .GroupBy(s => s.FilePath, StringComparer.OrdinalIgnoreCase))
            {
                bool editable = g.First().IsEditable;
                rows.Add(new CheckRow<SignatureSite> { Header = Path.GetFileName(g.Key) + (editable ? string.Empty : "  · " + RefactorStrings.EngineReadOnly), HeaderCount = g.Count() });
                rows.AddRange(g.Select(s => new CheckRow<SignatureSite> { Item = s, Checked = s.Checked && s.Problem == null, ReadOnly = !s.IsEditable || s.Problem != null }));
            }
            var picked = dialog.ShowModal(rows, VsMainWindow.Handle);
            if (picked == null)
            {
                L.Info("refactor.changeSignature", null, ("name", f.Name), ("canceled", true), ("stage", "preview"), ("project", project), ("ms", sw.ElapsedMilliseconds));
                return;
            }
            var pickedSet = new HashSet<SignatureSite>(picked);
            foreach (var s in sites) s.Checked = pickedSet.Contains(s);

            // ---- apply (one undo step)
            var edits = new List<TextEdit>();
            int collided = 0;
            foreach (var g in sites.Where(s => s.Checked).GroupBy(s => s.FilePath, StringComparer.OrdinalIgnoreCase))
            {
                edits.AddRange(ChangeSignaturePlanner.Edits(g.Key, texts[g.Key].Text, g, out var dropped));
                collided += dropped.Count;
            }
            var result = RefactorEdits.Apply("UEToVS: " + Refactor2Strings.ChangeSignatureTitle(f.Name), edits);
            L.Info("refactor.changeSignature", null, ("name", f.Name), ("kind", f.Kind.ToString()), ("before", original.Count), ("after", edited.Count),
                ("added", edited.Count(p => p.IsNew)), ("removed", original.Count - edited.Count(p => !p.IsNew)), ("family", family), ("bases", bases.Count),
                ("overrides", overrides.Count), ("references", locations.Count), ("project", project), ("engine", engineSites), ("problems", problems),
                ("unchanged", unchanged), ("picked", picked.Count), ("collided", collided), ("edits", result.Applied), ("skipped", result.Skipped),
                ("filesChanged", result.Files), ("truncated", truncated), ("ok", result.Ok), ("searchMs", searchMs), ("ms", sw.ElapsedMilliseconds),
                ("source", ctx.Source.ToString()));
            if (!result.Ok)
            {
                VsStatusBar.SetText(RefactorStrings.EditFailed);
                return;
            }
            string message = Refactor2Strings.SignatureChanged(f.Name, result.Applied, result.Files);
            if (result.Skipped + collided > 0) message += "  " + Refactor2Strings.Skipped(result.Skipped + collided);
            VsStatusBar.SetText(message);
        }

        private static void Report(string problem, RefactorContext rc, Stopwatch sw, ActionContext ctx)
        {
            VsStatusBar.SetText(Refactor2Strings.Problem(problem));
            L.Info("refactor.changeSignature", null, ("name", rc.Function?.Name ?? rc.Word), ("problem", problem), ("ms", sw.ElapsedMilliseconds), ("source", ctx.Source.ToString()));
        }

        private static SymbolInfo ToSymbol(FunctionSignature f) => new SymbolInfo
        {
            Name = f.Name, Container = f.Container, Kind = f.Kind, Ue = f.Ue, Specifiers = f.Specifiers, Location = f.Location, Module = f.Module,
            Origin = f.Origin, IsDefinition = f.IsDefinition,
        };

        private static void Present(SignatureSite s, UEToVS.UI.Molecules.RowData d)
        {
            d.Primary = (s.Problem == null ? s.NewLineText : s.OldLineText)?.Trim() ?? string.Empty;
            d.Secondary = s.Problem == null && s.NewLineText != s.OldLineText ? s.OldLineText?.Trim() : null;
            d.Right = (s.Line + 1).ToString();
            d.ToolTip = s.FilePath + ":" + (s.Line + 1);
            switch (s.Kind)
            {
                case SignatureSiteKind.Declaration: d.AddBadge(Loc.T("声明", "decl"), BadgeKind.Neutral); break;
                case SignatureSiteKind.Definition: d.AddBadge(Loc.T("定义", "def"), BadgeKind.Neutral); break;
                case SignatureSiteKind.Call: d.AddBadge(Loc.T("调用", "call"), BadgeKind.Neutral); break;
            }
            if (s.Problem != null) d.AddBadge(Loc.T("需手动修改", "manual"), BadgeKind.Neutral);
        }
    }
}
