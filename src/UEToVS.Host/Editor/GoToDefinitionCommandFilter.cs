using System;
using System.ComponentModel.Composition;
using Microsoft.VisualStudio;
using Microsoft.VisualStudio.Editor;
using Microsoft.VisualStudio.OLE.Interop;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Text.Editor;
using Microsoft.VisualStudio.TextManager.Interop;
using Microsoft.VisualStudio.Utilities;
using UEToVS.Contracts;
using UEToVS.Core.Diagnostics;
using UEToVS.Host.Diagnostics;
using UEToVS.Host.Navigation;

namespace UEToVS.Host.Editor
{
    /// <summary>
    /// When UEToVS has taken over VS's C++ IntelliSense (a takeover backup exists) VS's own F12 has nothing to work with,
    /// so F12 (Edit.GoToDefinition, VSStd97 cmdidGotoDefn) in C++ editors of an Unreal solution runs our go to definition.
    /// Otherwise the command passes through to VS untouched.
    /// </summary>
    [Export(typeof(IVsTextViewCreationListener))]
    [ContentType(EditorContentTypes.Cpp)]
    [TextViewRole(PredefinedTextViewRoles.Document)]
    internal sealed class GoToDefinitionFilterProvider : IVsTextViewCreationListener
    {
        private static readonly Logger L = Log.For("Editor");

        [Import]
        internal IVsEditorAdaptersFactoryService Adapters { get; set; }

        public void VsTextViewCreated(IVsTextView textViewAdapter) => Safe.Execute(L, "editor.gotoFilter.attach", () =>
        {
            var view = Adapters?.GetWpfTextView(textViewAdapter);
            if (view == null) return;
            var filter = new GoToDefinitionCommandFilter(view);
            if (ErrorHandler.Succeeded(textViewAdapter.AddCommandFilter(filter, out IOleCommandTarget next))) filter.Next = next;
            else L.Warn("editor.gotoFilter.attachFailed", "AddCommandFilter failed");
        });
    }

    internal sealed class GoToDefinitionCommandFilter : IOleCommandTarget
    {
        private static readonly Logger L = Log.For("Editor");
        private static readonly Guid Std97 = VSConstants.GUID_VSStandardCommandSet97;
        private readonly IWpfTextView _view;

        public GoToDefinitionCommandFilter(IWpfTextView view) => _view = view;

        public IOleCommandTarget Next { get; set; }

        /// <summary>Intercept only when VS's C++ IntelliSense is taken over and this is a UE solution.</summary>
        private static bool Intercept
        {
            get
            {
                if (!NavigationContext.IsUnrealSolution) return false;
                // BackupExists is a File.Exists: cache it briefly (QueryStatus runs on menu / toolbar refreshes).
                int now = Environment.TickCount;
                if (unchecked(now - _checkedAt) > 2000 || _checkedAt == 0)
                {
                    _takenOver = HostPackage.Instance?.IntelliSense?.BackupExists == true;
                    _checkedAt = now == 0 ? 1 : now;
                }
                return _takenOver;
            }
        }

        private static bool _takenOver;
        private static int _checkedAt;

        private static bool IsGotoDefn(ref Guid group, uint id) => group == Std97 && id == (uint)VSConstants.VSStd97CmdID.GotoDefn;

        public int QueryStatus(ref Guid pguidCmdGroup, uint cCmds, OLECMD[] prgCmds, IntPtr pCmdText)
        {
            if (cCmds == 1 && IsGotoDefn(ref pguidCmdGroup, prgCmds[0].cmdID) && Intercept)
            {
                prgCmds[0].cmdf = (uint)(OLECMDF.OLECMDF_SUPPORTED | OLECMDF.OLECMDF_ENABLED);
                return VSConstants.S_OK;
            }
            return Next?.QueryStatus(ref pguidCmdGroup, cCmds, prgCmds, pCmdText) ?? (int)Constants.OLECMDERR_E_NOTSUPPORTED;
        }

        public int Exec(ref Guid pguidCmdGroup, uint nCmdID, uint nCmdexecopt, IntPtr pvaIn, IntPtr pvaOut)
        {
            if (IsGotoDefn(ref pguidCmdGroup, nCmdID) && Intercept)
            {
                L.Info("editor.f12.intercepted", null);
                var view = _view;
                Safe.RunAsync(L, "editor.f12", () => GoToDefinitionFeature.ExecuteAsync(NavigationKind.Definition, "f12", view));
                return VSConstants.S_OK;
            }
            return Next?.Exec(ref pguidCmdGroup, nCmdID, nCmdexecopt, pvaIn, pvaOut) ?? (int)Constants.OLECMDERR_E_NOTSUPPORTED;
        }
    }
}
