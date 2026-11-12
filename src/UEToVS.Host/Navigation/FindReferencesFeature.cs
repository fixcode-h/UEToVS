using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Microsoft.VisualStudio.Shell;
using UEToVS.Contracts;
using UEToVS.Core.Diagnostics;
using UEToVS.Host.Editor;
using UEToVS.Host.Integration;
using UEToVS.Host.Logic.Localization;
using UEToVS.Host.Logic.Navigation;
using UEToVS.Host.Shell;
using UEToVS.UI.Controls;
using UEToVS.UI.Logic;
using UEToVS.UI.Molecules;
using UEToVS.UI.Patterns;
using UEToVS.UI.Shells;
using UEToVS.UI.Theming;

namespace UEToVS.Host.Navigation
{
    /// <summary>
    /// Find References (Shift+Alt+F) in a floating popup (ResultsPopup: grouped by file, line number + line text with the
    /// match highlighted, kind badge, name-only matches grayed, code preview). Toolbar: search again, writes only, include
    /// engine code, show uncertain matches. Enter / double-click jumps (the popup stays open to walk the results).
    /// Footer: hits / files / engine time / truncated. The popup is created once and reused.
    /// </summary>
    internal static class FindReferencesFeature
    {
        private static readonly Logger L = Log.For("Navigation");
        private static readonly LatestRequestGate Gate = new LatestRequestGate();
        private static readonly ReferenceFilter Filter = new ReferenceFilter();
        private static ResultsPopup<ReferenceRow> _popup;
        private static Chip _writesOnly, _includeEngine, _showUncertain;
        private static ReferencesResponse _last;
        private static CaretRequest _lastCaret;
        private static string _lastTitle;

        public static async Task ExecuteAsync(string source)
        {
            await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
            var view = EditorServices.ActiveView();
            if (view == null || view.IsClosed || !EditorServices.IsCpp(view.TextBuffer))
            {
                VsStatusBar.SetText(NavStrings.NoActiveCppEditor);
                return;
            }
            string word = WordAtCaret(view);
            var caret = await EditorServices.CaretRequestAsync(view);
            if (caret == null) return;
            await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
            L.Info("nav.references.start", null, ("source", source), ("word", word), ("includeEngine", Filter.IncludeEngine));
            _lastCaret = caret;
            _last = null;
            _lastTitle = word;
            await SearchAsync(caret, null, word);
        }

        private static string WordAtCaret(Microsoft.VisualStudio.Text.Editor.IWpfTextView view)
        {
            var p = view.Caret.Position.BufferPosition;
            var line = p.GetContainingLine();
            return IdentifierFinder.WordAt(line.GetText(), p.Position - line.Start.Position, true);
        }

        /// <summary>Runs (or re-runs) the search: by caret first, by the resolved symbol on "search again".</summary>
        private static async Task SearchAsync(CaretRequest caret, SymbolInfo symbol, string word)
        {
            EnsurePopup();
            string display = symbol != null ? SymbolPresentation.QualifiedName(symbol) : word ?? "?";
            _popup.Title = NavStrings.ReferencesTitle(display);
            _popup.IsBusy = true;
            _popup.SetResults(Array.Empty<TreeNode<ReferenceRow>>(), NavStrings.ReferencesSearching(display));
            if (!_popup.IsVisible) _popup.ShowAt(PopupAnchor.CenterOf(VsMainWindow.Handle));

            var (gen, ct) = Gate.Begin(NavigationContext.Disposal);
            var engine = NavigationContext.Engine;
            var request = new ReferencesRequest { Caret = caret, Symbol = symbol, IncludeNonProject = Filter.IncludeEngine };
            var r = engine == null
                ? EngineCallResult<ReferencesResponse>.Fail(EngineCallStatus.Unavailable, 0)
                : await engine.FindReferencesAsync(request, ct);
            await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
            if (!Gate.IsCurrent(gen)) return;
            _popup.IsBusy = false;
            if (r.Status != EngineCallStatus.Ok)
            {
                string msg = EngineCallText.ForStatus(r.Status) ?? NavStrings.ReferencesFailed;
                _popup.SetResults(Array.Empty<TreeNode<ReferenceRow>>(), msg);
                VsStatusBar.SetText(msg);
                return;
            }
            _last = r.Value;
            if (_last.Symbol != null)
            {
                _lastTitle = SymbolPresentation.QualifiedName(_last.Symbol);
                _popup.Title = NavStrings.ReferencesTitle(_lastTitle);
            }
            Apply();
            if ((_last.Hits?.Count ?? 0) == 0) VsStatusBar.SetText(NavStrings.NoReferences(_lastTitle ?? display));
        }

        /// <summary>Re-renders the last response with the current filter (no engine call).</summary>
        private static void Apply()
        {
            if (_popup == null || _last == null) return;
            using (L.Time("nav.references.render", 16))
            {
                var view = ReferenceResults.Build(_last.Hits, Filter);
                bool expand = view.Shown <= 2000;
                var roots = new List<TreeNode<ReferenceRow>>(view.Groups.Count);
                foreach (var g in view.Groups)
                {
                    var children = new List<TreeNode<ReferenceRow>>(g.Hits.Count);
                    foreach (var h in g.Hits) children.Add(new TreeNode<ReferenceRow>(new ReferenceRow { Hit = h }));
                    roots.Add(new TreeNode<ReferenceRow>(new ReferenceRow { Group = g }, children, expand || roots.Count < 20));
                }
                _popup.SetResults(roots, ReferenceResults.Summary(_last, view));
                if (_popup.Tree.Count > 1) _popup.Tree.SelectedIndex = 1; // first hit, not the first file header
                L.Debug("nav.references.view", null, ("shown", view.Shown), ("hidden", view.Hidden), ("files", view.Groups.Count),
                        ("writesOnly", Filter.WritesOnly), ("includeEngine", Filter.IncludeEngine), ("showUncertain", Filter.ShowUncertain));
            }
        }

        private static void EnsurePopup()
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            if (_popup != null)
            {
                _popup.OwnerHandle = VsMainWindow.Handle;
                return;
            }
            _popup = new ResultsPopup<ReferenceRow>("uetovs.references", NavigationPresenters.Reference, new System.Windows.Size(900, 520))
            {
                OwnerHandle = VsMainWindow.Handle,
            };
            var rerun = new IconButton(Ui.FrozenGeometry("M 10,2 A 5,5 0 1 0 11,7 M 10,0 L 10,3 L 7,3"), () => NavStrings.ReferencesRerun, null, 11);
            _writesOnly = new Chip(() => NavStrings.ReferencesWritesOnly, Filter.WritesOnly);
            _includeEngine = new Chip(() => NavStrings.ReferencesIncludeEngine, Filter.IncludeEngine);
            _showUncertain = new Chip(() => NavStrings.ReferencesShowUncertain, Filter.ShowUncertain);
            Tip.Set(_showUncertain, () => NavStrings.ReferencesUncertainTip);
            _popup.AddToolbarItem(rerun);
            _popup.AddToolbarItem(_writesOnly);
            _popup.AddToolbarItem(_includeEngine);
            _popup.AddToolbarItem(_showUncertain);
            rerun.Click += (s, e) => Diagnostics.Safe.RunAsync(L, "nav.references.rerun", () => SearchAsync(_lastCaret, _last?.Symbol, _lastTitle));
            _writesOnly.Click += (s, e) => Diagnostics.Safe.Execute(L, "nav.references.writesOnly", () => { Filter.WritesOnly = _writesOnly.IsChecked == true; Apply(); });
            _showUncertain.Click += (s, e) => Diagnostics.Safe.Execute(L, "nav.references.uncertain", () => { Filter.ShowUncertain = _showUncertain.IsChecked == true; Apply(); });
            _includeEngine.Click += (s, e) => Diagnostics.Safe.RunAsync(L, "nav.references.includeEngine", async () =>
            {
                Filter.IncludeEngine = _includeEngine.IsChecked == true;
                // The engine filters non-project files server side: search again when turning it on.
                if (Filter.IncludeEngine && _lastCaret != null) await SearchAsync(_lastCaret, _last?.Symbol, _lastTitle);
                else Apply();
            });
            // CodePreviewRequest.Line is 1-based; contract lines are 0-based.
            _popup.Panel.PreviewProvider = r =>
            {
                if (r.Hit?.Location != null)
                    return new CodePreviewRequest { FilePath = r.Hit.Location.FilePath, Line = r.Hit.Location.Line + 1, Column = r.Hit.Location.Column, Length = r.Hit.Length };
                return r.Group == null ? null : new CodePreviewRequest { FilePath = r.Group.FilePath, Line = r.Group.Hits.Count > 0 ? r.Group.Hits[0].Location.Line + 1 : 1 };
            };
            _popup.ItemInvoked += (s, e) => Diagnostics.Safe.Execute(L, "nav.references.open", () =>
            {
                var loc = NavigationPresenters.ToLocation(e.Item.Hit?.Location);
                if (loc != null) NavigationService.Instance.JumpTo(loc, "references");
            });
        }
    }
}
