using System;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Documents;
using System.Windows.Input;
using Microsoft.VisualStudio;
using Microsoft.VisualStudio.OLE.Interop;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Shell.Interop;
using UEToVS.Core.Diagnostics;
using UEToVS.Host.Logic.Shell;
using UEToVS.UI.Shells;

namespace UEToVS.Host.Integration
{
    /// <summary>
    /// Priority command target that keeps Edit.Copy / Cut / Paste / Undo / Redo / SelectAll / Delete inside UEToVS's own
    /// windows (settings window, Search Everywhere and other <see cref="ChromeWindow"/> popups). Those are modeless WPF
    /// windows, so VS translates Ctrl+V etc. into its commands before WPF sees the key and runs them on the last active
    /// code editor: pasting an API key into the settings window pasted it into the code (v0.4.0). While one of our
    /// windows is active, these commands run on the focused WPF control instead (or do nothing), never in the editor.
    /// UI thread only.
    /// </summary>
    internal sealed class ModelessEditCommandRouter : IOleCommandTarget, IDisposable
    {
        private static readonly Logger L = Log.For("Shell");
        private readonly IVsRegisterPriorityCommandTarget _registrar;
        private uint _cookie;

        private ModelessEditCommandRouter(IVsRegisterPriorityCommandTarget registrar) => _registrar = registrar;

        public static ModelessEditCommandRouter Register(System.IServiceProvider services)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            if (!(services?.GetService(typeof(SVsRegisterPriorityCommandTarget)) is IVsRegisterPriorityCommandTarget registrar))
            {
                L.Warn("shell.editRouter.unavailable", "SVsRegisterPriorityCommandTarget not available; Ctrl+V in UEToVS windows may reach the editor");
                return null;
            }
            var router = new ModelessEditCommandRouter(registrar);
            int hr = registrar.RegisterPriorityCommandTarget(0, router, out router._cookie);
            if (ErrorHandler.Failed(hr))
            {
                L.Warn("shell.editRouter.failed", "Registering the edit command router failed", ("hr", hr));
                return null;
            }
            L.Debug("shell.editRouter.registered", null);
            return router;
        }

        public void Dispose()
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            if (_cookie != 0) _registrar.UnregisterPriorityCommandTarget(_cookie);
            _cookie = 0;
        }

        private static WindowEditCommand Map(Guid group, uint id)
        {
            var c = WindowEditCommands.Map(group, id);
            if (c != WindowEditCommand.None || group != VSConstants.VSStd2K) return c;
            switch ((VSConstants.VSStd2KCmdID)id)
            {
                case VSConstants.VSStd2KCmdID.SELECTALL: return WindowEditCommand.SelectAll;
                case VSConstants.VSStd2KCmdID.UNDO: return WindowEditCommand.Undo;
                case VSConstants.VSStd2KCmdID.REDO: return WindowEditCommand.Redo;
                case VSConstants.VSStd2KCmdID.DELETE: return WindowEditCommand.Delete;
                default: return WindowEditCommand.None;
            }
        }

        /// <summary>The focused element when it sits in an active UEToVS window, else null (VS handles the command as usual).</summary>
        private static IInputElement OurFocus(out Window window)
        {
            window = null;
            var focused = Keyboard.FocusedElement as DependencyObject;
            if (focused == null) return null;
            window = Window.GetWindow(focused);
            return window is ChromeWindow && window.IsActive ? (IInputElement)focused : null;
        }

        private static RoutedUICommand Wpf(WindowEditCommand c)
        {
            switch (c)
            {
                case WindowEditCommand.Copy: return ApplicationCommands.Copy;
                case WindowEditCommand.Cut: return ApplicationCommands.Cut;
                case WindowEditCommand.Paste: return ApplicationCommands.Paste;
                case WindowEditCommand.Undo: return ApplicationCommands.Undo;
                case WindowEditCommand.Redo: return ApplicationCommands.Redo;
                case WindowEditCommand.SelectAll: return ApplicationCommands.SelectAll;
                case WindowEditCommand.Delete: return EditingCommands.Delete;
                default: return null;
            }
        }

        public int QueryStatus(ref Guid pguidCmdGroup, uint cCmds, OLECMD[] prgCmds, IntPtr pCmdText)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            if (prgCmds == null || cCmds == 0) return (int)Microsoft.VisualStudio.OLE.Interop.Constants.OLECMDERR_E_NOTSUPPORTED;
            var c = Map(pguidCmdGroup, prgCmds[0].cmdID);
            if (c == WindowEditCommand.None) return (int)Microsoft.VisualStudio.OLE.Interop.Constants.OLECMDERR_E_NOTSUPPORTED;
            var target = OurFocus(out _);
            if (target == null) return (int)Microsoft.VisualStudio.OLE.Interop.Constants.OLECMDERR_E_NOTSUPPORTED;
            // Supported while our window is active (so VS never routes it to the editor); enabled when the control can do it.
            bool can = false;
            try { can = Wpf(c)?.CanExecute(null, target) == true; }
            catch (Exception ex) { L.Debug("shell.editRouter.canExecute.failed", null, ("command", c.ToString()), ("error", ex.Message)); }
            prgCmds[0].cmdf = (uint)OLECMDF.OLECMDF_SUPPORTED | (can ? (uint)OLECMDF.OLECMDF_ENABLED : 0);
            return VSConstants.S_OK;
        }

        public int Exec(ref Guid pguidCmdGroup, uint nCmdID, uint nCmdexecopt, IntPtr pvaIn, IntPtr pvaOut)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            var c = Map(pguidCmdGroup, nCmdID);
            if (c == WindowEditCommand.None) return (int)Microsoft.VisualStudio.OLE.Interop.Constants.OLECMDERR_E_NOTSUPPORTED;
            var target = OurFocus(out var window);
            if (target == null) return (int)Microsoft.VisualStudio.OLE.Interop.Constants.OLECMDERR_E_NOTSUPPORTED;
            bool ran = false;
            try
            {
                var cmd = Wpf(c);
                if (cmd != null && cmd.CanExecute(null, target))
                {
                    cmd.Execute(null, target);
                    ran = true;
                }
            }
            catch (Exception ex) when (ex is COMException || ex is InvalidOperationException || ex is ExternalException)
            {
                L.Warn("shell.editRouter.exec.failed", "Edit command in a UEToVS window failed", ex, ("command", c.ToString()));
            }
            L.Debug("shell.editRouter.exec", null, ("command", c.ToString()), ("ran", ran), ("window", window?.GetType().Name),
                    ("target", target.GetType().Name));
            return VSConstants.S_OK; // handled either way: never fall through to the code editor
        }
    }
}
