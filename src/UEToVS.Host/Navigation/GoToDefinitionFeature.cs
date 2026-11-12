using System;
using System.Threading.Tasks;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Text;
using Microsoft.VisualStudio.Text.Editor;
using UEToVS.Contracts;
using UEToVS.Core.Diagnostics;
using UEToVS.Host.Editor;
using UEToVS.Host.Integration;
using UEToVS.Host.Logic.Localization;
using UEToVS.Host.Logic.Navigation;
using UEToVS.Host.Shell;
using UEToVS.UI.Patterns;
using UEToVS.UI.Shells;

namespace UEToVS.Host.Navigation
{
    /// <summary>
    /// Go to definition / declaration (Alt+G, Toggle: declaration ↔ definition), go to implementation (Alt+Shift+G),
    /// Ctrl+click and F12 (when UEToVS has taken over C++ IntelliSense). One target → jump; several → ChooserPopup at the
    /// caret; none → localized reason in the status bar. A newer invocation cancels an older one still waiting on the engine.
    /// </summary>
    internal static class GoToDefinitionFeature
    {
        private static readonly Logger L = Log.For("Navigation");
        private static readonly LatestRequestGate Gate = new LatestRequestGate();
        private static ChooserPopup<NavigationTarget> _chooser;

        /// <param name="source">Stable id for logs: "command", "ctrlClick", "f12", "findAction".</param>
        /// <param name="view">Target view (null = active view).</param>
        /// <param name="at">Point to resolve (null = caret).</param>
        public static async Task ExecuteAsync(NavigationKind kind, string source, IWpfTextView view = null, SnapshotPoint? at = null)
        {
            await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
            view = view ?? EditorServices.ActiveView();
            if (view == null || view.IsClosed || !EditorServices.IsCpp(view.TextBuffer))
            {
                VsStatusBar.SetText(NavStrings.NoActiveCppEditor);
                L.Info("nav.goto.noEditor", null, ("kind", kind.ToString()), ("source", source));
                return;
            }
            var anchor = EditorServices.AnchorAt(view, at);
            var (gen, ct) = Gate.Begin(NavigationContext.Disposal);
            var caret = await EditorServices.CaretRequestAsync(view, at);
            if (caret == null)
            {
                VsStatusBar.SetText(NavStrings.NoActiveCppEditor);
                return;
            }
            var engine = NavigationContext.Engine;
            var result = engine == null
                ? EngineCallResult<NavigationResponse>.Fail(EngineCallStatus.Unavailable, 0)
                : await engine.NavigateAsync(new NavigationRequest { Caret = caret, Kind = kind }, ct);
            if (!Gate.IsCurrent(gen))
            {
                L.Debug("nav.goto.superseded", null, ("kind", kind.ToString()), ("source", source));
                return;
            }
            var outcome = NavigationOutcome.Decide(result.Status, result.Value, kind);
            L.Info("nav.goto.decision", null, ("kind", kind.ToString()), ("source", source), ("decision", outcome.Decision.ToString()),
                   ("targets", outcome.Targets.Count), ("status", result.Status.ToString()), ("word", outcome.Word));
            await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
            string reason = kind == NavigationKind.Implementation ? "goToImplementation" : source == "ctrlClick" ? "ctrlClick" : "goToDefinition";
            switch (outcome.Decision)
            {
                case NavigationDecision.Jump:
                    if (await NavigationService.JumpToAsync(NavigationPresenters.ToLocation(outcome.Targets[0].Symbol.Location), reason) && outcome.Message != null)
                        VsStatusBar.SetText(outcome.Message);
                    break;
                case NavigationDecision.Choose:
                    await ChooseAsync(outcome, kind, anchor, reason);
                    break;
                default:
                    if (outcome.Message != null) VsStatusBar.SetText(outcome.Message);
                    break;
            }
        }

        private static async Task ChooseAsync(NavigationOutcome outcome, NavigationKind kind, PopupAnchor anchor, string reason)
        {
            if (_chooser == null) _chooser = new ChooserPopup<NavigationTarget>("uetovs.goto.chooser", NavigationPresenters.Target, new System.Windows.Size(620, 320));
            _chooser.OwnerHandle = VsMainWindow.Handle;
            string word = string.IsNullOrEmpty(outcome.Word) ? "?" : outcome.Word;
            string title = kind == NavigationKind.Implementation ? NavStrings.ChooseImplementation(word) : NavStrings.ChooseDefinition(word);
            var picked = await _chooser.ShowAsync(outcome.Targets, anchor, title, t => SymbolPresentation.QualifiedName(t.Symbol));
            L.Info("nav.goto.chooser", null, ("kind", kind.ToString()), ("count", outcome.Targets.Count), ("chosen", picked.Chosen),
                   ("index", picked.Chosen ? IndexOf(outcome, picked.Item) : -1));
            if (picked.Chosen) await NavigationService.JumpToAsync(NavigationPresenters.ToLocation(picked.Item.Symbol.Location), reason);
        }

        private static int IndexOf(NavigationOutcome o, NavigationTarget t)
        {
            for (int i = 0; i < o.Targets.Count; i++)
                if (ReferenceEquals(o.Targets[i], t)) return i;
            return -1;
        }
    }
}
