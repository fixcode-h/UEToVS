using System;
using System.ComponentModel.Composition;
using System.Runtime.InteropServices;
using System.Threading;
using Microsoft.VisualStudio;
using Microsoft.VisualStudio.Editor;
using Microsoft.VisualStudio.Language.Intellisense;
using Microsoft.VisualStudio.Language.Intellisense.AsyncCompletion;
using Microsoft.VisualStudio.Language.Intellisense.AsyncCompletion.Data;
using Microsoft.VisualStudio.OLE.Interop;
using Microsoft.VisualStudio.Text;
using Microsoft.VisualStudio.Text.Editor;
using Microsoft.VisualStudio.TextManager.Interop;
using Microsoft.VisualStudio.Utilities;
using UEToVS.Core.Diagnostics;
using UEToVS.Host.Diagnostics;
using UEToVS.Host.Editor;
using UEToVS.Host.Navigation;

namespace UEToVS.Host.IntelliSense
{
    /// <summary>
    /// Keeps the IntelliSense keys working once VS's own C++ IntelliSense is off (§7.5 "接管时 … 全部由 UEToVS 提供"):
    /// Ctrl+Space (Edit.CompleteWord) / Ctrl+J (Edit.ListMembers) open UEToVS completion, Ctrl+Shift+Space
    /// (Edit.ParameterInfo) and typed '(' / ',' drive parameter info, Ctrl+K,I (Edit.QuickInfo) and mouse hover open Quick
    /// Info — none of it goes through the C++ language service, so it works with "Disable IntelliSense". Before the
    /// takeover the commands pass through to VS untouched (VS's handlers then also ask the UEToVS sources).
    /// </summary>
    [Export(typeof(IVsTextViewCreationListener))]
    [ContentType(EditorContentTypes.Cpp)]
    [TextViewRole(PredefinedTextViewRoles.Document)]
    internal sealed class IntelliSenseCommandFilterProvider : IVsTextViewCreationListener
    {
        private static readonly Logger L = Log.For("IntelliSense");

        [Import]
        internal IVsEditorAdaptersFactoryService Adapters { get; set; }

        [Import]
        internal IAsyncCompletionBroker CompletionBroker { get; set; }

        [Import]
        internal ISignatureHelpBroker SignatureBroker { get; set; }

        [Import]
        internal IAsyncQuickInfoBroker QuickInfoBroker { get; set; }

        public void VsTextViewCreated(IVsTextView textViewAdapter) => Safe.Execute(L, "is.filter.attach", () =>
        {
            var view = Adapters?.GetWpfTextView(textViewAdapter);
            if (view == null) return;
            var filter = new IntelliSenseCommandFilter(view, CompletionBroker, SignatureHelpController.For(view, SignatureBroker), QuickInfoBroker);
            if (ErrorHandler.Succeeded(textViewAdapter.AddCommandFilter(filter, out IOleCommandTarget next))) filter.Next = next;
            else L.Warn("is.filter.attachFailed", "AddCommandFilter failed");
            view.MouseHover += filter.OnMouseHover;
            view.Closed += (s, e) => view.MouseHover -= filter.OnMouseHover;
        });
    }

    internal sealed class IntelliSenseCommandFilter : IOleCommandTarget
    {
        private static readonly Logger L = Log.For("IntelliSense");
        private static readonly Guid Std2K = VSConstants.VSStd2K;

        private readonly IWpfTextView _view;
        private readonly IAsyncCompletionBroker _completion;
        private readonly SignatureHelpController _signatures;
        private readonly IAsyncQuickInfoBroker _quickInfo;

        public IntelliSenseCommandFilter(IWpfTextView view, IAsyncCompletionBroker completion, SignatureHelpController signatures, IAsyncQuickInfoBroker quickInfo)
        {
            _view = view;
            _completion = completion;
            _signatures = signatures;
            _quickInfo = quickInfo;
        }

        public IOleCommandTarget Next { get; set; }

        private static bool Owned => IntelliSenseContext.IsUnrealSolution && IntelliSenseContext.TakenOver;

        private static bool IsOurs(ref Guid group, uint id, out VSConstants.VSStd2KCmdID cmd)
        {
            cmd = (VSConstants.VSStd2KCmdID)id;
            if (group != Std2K) return false;
            switch (cmd)
            {
                case VSConstants.VSStd2KCmdID.COMPLETEWORD:
                case VSConstants.VSStd2KCmdID.SHOWMEMBERLIST:
                case VSConstants.VSStd2KCmdID.PARAMINFO:
                case VSConstants.VSStd2KCmdID.QUICKINFO:
                    return true;
                default:
                    return false;
            }
        }

        public int QueryStatus(ref Guid pguidCmdGroup, uint cCmds, OLECMD[] prgCmds, IntPtr pCmdText)
        {
            if (cCmds == 1 && IsOurs(ref pguidCmdGroup, prgCmds[0].cmdID, out _) && Owned)
            {
                prgCmds[0].cmdf = (uint)(OLECMDF.OLECMDF_SUPPORTED | OLECMDF.OLECMDF_ENABLED);
                return VSConstants.S_OK;
            }
            return Next?.QueryStatus(ref pguidCmdGroup, cCmds, prgCmds, pCmdText) ?? (int)Constants.OLECMDERR_E_NOTSUPPORTED;
        }

        public int Exec(ref Guid pguidCmdGroup, uint nCmdID, uint nCmdexecopt, IntPtr pvaIn, IntPtr pvaOut)
        {
            if (pguidCmdGroup == Std2K && nCmdID == (uint)VSConstants.VSStd2KCmdID.COMPLETEWORD && IsAltArrowDown(out bool back))
            {
                // VS keeps "Text Editor::Alt+Right Arrow" on Edit.CompleteWord even after UEToVS removes it (DTE reads the
                // binding back unchanged), and a Text Editor binding beats our Global Alt+→. When the gesture really is
                // Alt+←/→, it means navigation history.
                L.Info("nav.altArrow.redirect", null, ("back", back), ("from", "Edit.CompleteWord"));
                Safe.Execute(L, "nav.altArrow", () =>
                {
                    if (back) NavigationService.Instance.Back();
                    else NavigationService.Instance.Forward();
                });
                return VSConstants.S_OK;
            }
            if (IsOurs(ref pguidCmdGroup, nCmdID, out var cmd) && Owned)
            {
                Safe.Execute(L, "is.command", () => Run(cmd), ("command", cmd.ToString()));
                return VSConstants.S_OK;
            }
            char typed = '\0';
            if (pguidCmdGroup == Std2K && nCmdID == (uint)VSConstants.VSStd2KCmdID.TYPECHAR && pvaIn != IntPtr.Zero)
            {
                try
                {
                    typed = (char)(ushort)Marshal.GetObjectForNativeVariant(pvaIn);
                }
                catch (Exception)
                {
                    typed = '\0';
                }
            }
            int hr = Next?.Exec(ref pguidCmdGroup, nCmdID, nCmdexecopt, pvaIn, pvaOut) ?? (int)Constants.OLECMDERR_E_NOTSUPPORTED;
            if ((typed == '(' || typed == ',') && ErrorHandler.Succeeded(hr) && _signatures != null)
                Safe.Execute(L, "is.signatureHelp.typed", () => _signatures.Trigger(typed, false));
            return hr;
        }

        /// <summary>Alt (only) + ← or → is held right now.</summary>
        private static bool IsAltArrowDown(out bool back)
        {
            back = false;
            if (System.Windows.Input.Keyboard.Modifiers != System.Windows.Input.ModifierKeys.Alt) return false;
            if (System.Windows.Input.Keyboard.IsKeyDown(System.Windows.Input.Key.Left)) { back = true; return true; }
            return System.Windows.Input.Keyboard.IsKeyDown(System.Windows.Input.Key.Right);
        }

        private void Run(VSConstants.VSStd2KCmdID cmd)
        {
            L.Info("is.command", null, ("command", cmd.ToString()));
            var caret = _view.Caret.Position.BufferPosition;
            switch (cmd)
            {
                case VSConstants.VSStd2KCmdID.COMPLETEWORD:
                case VSConstants.VSStd2KCmdID.SHOWMEMBERLIST:
                {
                    if (_completion == null || !IntelliSenseContext.CompletionActive) return;
                    var reason = cmd == VSConstants.VSStd2KCmdID.COMPLETEWORD ? CompletionTriggerReason.InvokeAndCommitIfUnique : CompletionTriggerReason.Invoke;
                    var trigger = new CompletionTrigger(reason, caret.Snapshot);
                    var existing = _completion.GetSession(_view);
                    var session = existing ?? _completion.TriggerCompletion(_view, trigger, caret, CancellationToken.None);
                    session?.OpenOrUpdate(trigger, caret, CancellationToken.None);
                    return;
                }
                case VSConstants.VSStd2KCmdID.PARAMINFO:
                    _signatures?.Trigger('\0', true);
                    return;
                case VSConstants.VSStd2KCmdID.QUICKINFO:
                    TriggerQuickInfo(caret);
                    return;
            }
        }

        private void TriggerQuickInfo(SnapshotPoint point)
        {
            if (_quickInfo == null || !IntelliSenseContext.QuickInfoActive) return;
            var tracking = point.Snapshot.CreateTrackingPoint(point.Position, PointTrackingMode.Positive);
            Safe.RunAsync(L, "is.quickInfo.trigger", () => _quickInfo.TriggerQuickInfoAsync(_view, tracking, QuickInfoSessionOptions.None, CancellationToken.None));
        }

        /// <summary>
        /// Hover fallback: with VS's "auto Quick Info" switched off by the takeover the C++ editor may not open Quick Info on
        /// hover any more, so we do it (only when no session is open already).
        /// </summary>
        internal void OnMouseHover(object sender, MouseHoverEventArgs e)
        {
            if (!Owned || _quickInfo == null || !IntelliSenseContext.QuickInfoActive) return;
            Safe.Execute(L, "is.quickInfo.hover", () =>
            {
                if (_quickInfo.IsQuickInfoActive(_view)) return;
                var point = e.TextPosition.GetPoint(_view.TextBuffer, PositionAffinity.Predecessor);
                if (point == null) return;
                var p = point.Value;
                var tracking = p.Snapshot.CreateTrackingPoint(p.Position, PointTrackingMode.Positive);
                Safe.RunAsync(L, "is.quickInfo.hoverTrigger", () => _quickInfo.TriggerQuickInfoAsync(_view, tracking, QuickInfoSessionOptions.TrackMouse, CancellationToken.None));
            });
        }
    }
}
