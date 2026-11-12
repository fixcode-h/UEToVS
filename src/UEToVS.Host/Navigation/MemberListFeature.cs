using System.Collections.Generic;
using System.Threading.Tasks;
using Microsoft.VisualStudio.Shell;
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
    /// Alt+M: declarations of the current document (fuzzy filter, glyph by kind, UE badges), and Alt+Shift+M: members of
    /// the type at the caret including inherited ones ("from AActor", grayed). Both are ChooserPopups at the caret; picking
    /// one jumps there.
    /// </summary>
    internal static class MemberListFeature
    {
        private static readonly Logger L = Log.For("Navigation");
        private static readonly LatestRequestGate Gate = new LatestRequestGate();
        private static ChooserPopup<SymbolInfo> _fileChooser;
        private static ChooserPopup<SymbolInfo> _typeChooser;
        private static SymbolInfo _typeOwner;

        public static async Task ListMembersInFileAsync(string source)
        {
            await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
            var view = ActiveCppView();
            if (view == null) return;
            var anchor = EditorServices.AnchorAt(view);
            string file = SymbolPresentation.SafeFileName(EditorServices.FilePath(view.TextBuffer));
            var (gen, ct) = Gate.Begin(NavigationContext.Disposal);
            var caret = await EditorServices.CaretRequestAsync(view);
            var engine = NavigationContext.Engine;
            var r = engine == null || caret == null
                ? EngineCallResult<CaretContextResponse>.Fail(EngineCallStatus.Unavailable, 0)
                : await engine.GetCaretContextAsync(caret, ct);
            await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
            if (!Gate.IsCurrent(gen) || !Report(r.Status)) return;
            var items = ScopeModel.FileMemberList(r.Value.FileSymbols);
            L.Info("nav.members.file", null, ("source", source), ("file", file), ("count", items.Count));
            if (items.Count == 0)
            {
                VsStatusBar.SetText(NavStrings.NoMembers);
                return;
            }
            if (_fileChooser == null) _fileChooser = new ChooserPopup<SymbolInfo>("uetovs.members.file", NavigationPresenters.Member(null), new System.Windows.Size(560, 420));
            await ChooseAsync(_fileChooser, items, anchor, NavStrings.MembersInFileTitle(file));
        }

        public static async Task ListTypeMembersAsync(string source)
        {
            await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
            var view = ActiveCppView();
            if (view == null) return;
            var anchor = EditorServices.AnchorAt(view);
            var (gen, ct) = Gate.Begin(NavigationContext.Disposal);
            var caret = await EditorServices.CaretRequestAsync(view);
            var engine = NavigationContext.Engine;
            if (engine == null || caret == null)
            {
                Report(EngineCallStatus.Unavailable);
                return;
            }

            // Which type: the enclosing type / out-of-line member's class; otherwise the type named at the caret.
            string typeName = null, container = null;
            var ctx = await engine.GetCaretContextAsync(caret, ct);
            if (!Gate.IsCurrent(gen)) return;
            if (ctx.Ok)
            {
                var t = ScopeModel.CurrentType(ctx.Value);
                if (t != null) { typeName = t.Name; container = t.Container; }
                else
                {
                    string key = ScopeModel.CurrentTypeKey(ctx.Value);
                    if (!string.IsNullOrEmpty(key)) SplitKey(key, out typeName, out container);
                }
            }
            if (typeName == null)
            {
                var nav = await engine.NavigateAsync(new NavigationRequest { Caret = caret, Kind = NavigationKind.Declaration }, ct);
                if (!Gate.IsCurrent(gen)) return;
                if (nav.Ok && nav.Value.Resolved != null && SymbolPresentation.IsType(nav.Value.Resolved.Kind))
                {
                    typeName = nav.Value.Resolved.Name;
                    container = nav.Value.Resolved.Container;
                }
            }
            if (typeName == null)
            {
                await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
                VsStatusBar.SetText(NavStrings.NoTypeAtCaret);
                return;
            }
            var r = await engine.GetMembersAsync(new MembersRequest { TypeName = typeName, Container = container, IncludeInherited = true }, ct);
            await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
            if (!Gate.IsCurrent(gen) || !Report(r.Status)) return;
            var items = ScopeModel.TypeMemberList(r.Value);
            L.Info("nav.members.type", null, ("source", source), ("type", typeName), ("container", container), ("count", items.Count), ("truncated", r.Value.Truncated));
            if (items.Count == 0)
            {
                VsStatusBar.SetText(NavStrings.NoMembers);
                return;
            }
            var owner = r.Value.Type ?? new SymbolInfo { Name = typeName, Container = container };
            _typeOwner = owner; // read by the presenter: inherited members are grayed with "from X"
            if (_typeChooser == null)
                _typeChooser = new ChooserPopup<SymbolInfo>("uetovs.members.type", NavigationPresenters.Member(() => _typeOwner), new System.Windows.Size(620, 460));
            string title = NavStrings.TypeMembersTitle(SymbolPresentation.QualifiedName(owner)) + (r.Value.Truncated ? " " + NavStrings.Truncated : string.Empty);
            await ChooseAsync(_typeChooser, items, anchor, title);
        }

        private static IWpfTextView ActiveCppView()
        {
            var view = EditorServices.ActiveView();
            if (view != null && !view.IsClosed && EditorServices.IsCpp(view.TextBuffer)) return view;
            VsStatusBar.SetText(NavStrings.NoActiveCppEditor);
            return null;
        }

        private static async Task ChooseAsync(ChooserPopup<SymbolInfo> chooser, List<SymbolInfo> items, PopupAnchor anchor, string title)
        {
            chooser.OwnerHandle = VsMainWindow.Handle;
            var picked = await chooser.ShowAsync(items, anchor, title, ScopeModel.FilterText);
            if (!picked.Chosen) return;
            var loc = NavigationPresenters.ToLocation(picked.Item.Location);
            if (loc != null) await NavigationService.Instance.JumpToAsync(loc, "members");
        }

        private static bool Report(EngineCallStatus status)
        {
            if (status == EngineCallStatus.Ok) return true;
            string msg = EngineCallText.ForStatus(status);
            if (msg != null) VsStatusBar.SetText(msg);
            return false;
        }

        private static void SplitKey(string key, out string name, out string container)
        {
            int sep = key.LastIndexOf("::", System.StringComparison.Ordinal);
            name = sep < 0 ? key : key.Substring(sep + 2);
            container = sep < 0 ? null : key.Substring(0, sep);
        }
    }
}
