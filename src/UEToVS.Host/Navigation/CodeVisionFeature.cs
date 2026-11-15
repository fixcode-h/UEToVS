using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using Microsoft.VisualStudio.Shell;
using UEToVS.Contracts;
using UEToVS.Core.Diagnostics;
using UEToVS.Host.Logic.Localization;
using UEToVS.Host.Logic.Navigation;
using UEToVS.Host.Search;
using UEToVS.Host.Shell;
using UEToVS.UI.Logic;
using UEToVS.UI.Shells;

namespace UEToVS.Host.Navigation
{
    /// <summary>
    /// v0.7.6 Code Vision click (Rider): clicking "N 个蓝图" / "N 个派生类" above a declaration opens a list right below
    /// it. Blueprint rows do what Search Everywhere's asset rows do (copy the object path, select the .uasset in Explorer;
    /// UEToVS does not talk to the UE editor); derived type rows jump to the declaration. The list comes from the engine
    /// on click (nothing is kept per hint), with the same queries as the counts.
    /// </summary>
    internal static class CodeVisionFeature
    {
        private const string PopupId = "uetovs.codeVision";
        private const int FilterFrom = 8;

        /// <summary>The list is as wide as its widest row (whole object paths, Rider) but no wider than this (DIPs).</summary>
        private const double MaxWidth = 960;

        private static readonly Logger L = Log.For("Navigation");
        private static readonly LatestRequestGate Gate = new LatestRequestGate();

        public static async Task ShowAsync(InlayHint hint, CodeVisionSegment segment, FrameworkElement anchorElement)
        {
            await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
            if (hint == null || string.IsNullOrEmpty(hint.SymbolName)) return;
            // where to open: taken now, the row may be re-laid out while the engine answers
            var anchor = PopupAnchor.Element(anchorElement, AnchorMode.Below);
            var engine = NavigationContext.Engine;
            if (engine == null)
            {
                VsStatusBar.SetText(FeatureStrings.EngineUnavailable);
                return;
            }
            var (gen, ct) = Gate.Begin(NavigationContext.Disposal);
            var r = await engine.GetCodeVisionItemsAsync(new CodeVisionItemsRequest { SymbolName = hint.SymbolName }, ct);
            await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
            if (!Gate.IsCurrent(gen)) return;
            if (r.Status != EngineCallStatus.Ok)
            {
                string msg = EngineCallText.ForStatus(r.Status);
                if (msg != null) VsStatusBar.SetText(msg);
                L.Info("editor.codeVision.click", null, ("symbol", hint.SymbolName), ("segment", segment.ToString()), ("status", r.Status.ToString()));
                return;
            }
            var rows = CodeVisionModel.Rows(segment, r.Value);
            int count = rows.Count(x => x.IsSelectable);
            L.Info("editor.codeVision.click", null, ("symbol", hint.SymbolName), ("segment", segment.ToString()), ("status", "Ok"),
                   ("shownCount", segment == CodeVisionSegment.Blueprints ? hint.Blueprints : hint.DerivedTypes), ("items", count),
                   ("truncated", r.Value.Truncated), ("bpIndexReady", r.Value.BlueprintIndexReady), ("engineMs", r.Value.ElapsedMs), ("roundTripMs", r.ElapsedMs));

            // one derived C++ type: go there (go-to-implementation behaviour); Blueprints always list (the pick only reveals)
            if (segment == CodeVisionSegment.DerivedTypes && count == 1)
            {
                var only = rows.First(x => x.IsSelectable);
                NavigationService.Instance.JumpTo(only.FilePath, only.Line, only.Column, JumpReasons.CodeVision);
                return;
            }
            var pick = await RowPopups.ShowAtAsync(PopupId, CodeVisionModel.Title(segment, hint.SymbolName, count), rows, anchor,
                                                   count > FilterFrom ? CodeVisionModel.FilterText : null, new Size(560, 360), MaxWidth);
            if (!pick.Chosen || pick.Item == null) return;
            if (pick.Item.Asset != null) AssetReveal.Reveal(pick.Item.Asset);
            else if (!string.IsNullOrEmpty(pick.Item.FilePath))
                NavigationService.Instance.JumpTo(pick.Item.FilePath, pick.Item.Line, pick.Item.Column, JumpReasons.CodeVision, pick.Kind == InvokeKind.OpenInNewTab);
        }
    }
}
