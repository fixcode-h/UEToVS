using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
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
    /// The same applies to typing (v0.4.2 report: typing in Search Everywhere sent characters into the code): when keyboard
    /// focus sits on a VS editor surface inside our window (the code preview), VS turns keys into text editor commands
    /// (Edit.TypeChar, Backspace, arrows …) and routes them to the last code editor. While our window is active every
    /// text editor command (VSStd2K) and every UEToVS editor-scope command stops here: typing commands are replayed on the
    /// window's own input box, the rest do nothing.
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
            if (focused != null)
            {
                window = Window.GetWindow(focused);
                if (window is ChromeWindow && window.IsActive) return (IInputElement)focused;
                if (window != null) return null; // focus is in another (VS) window
            }
            // No WPF focus (e.g. it was just cleared) while one of our windows is the active one: still ours, never the editor.
            var app = Application.Current;
            if (app == null) return null;
            foreach (Window w in app.Windows)
            {
                if (w is ChromeWindow && w.IsActive)
                {
                    window = w;
                    return w;
                }
            }
            return null;
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
            if (c == WindowEditCommand.None)
            {
                if (!IsEditorOnly(pguidCmdGroup, prgCmds[0].cmdID) || OurFocus(out _) == null)
                    return (int)Microsoft.VisualStudio.OLE.Interop.Constants.OLECMDERR_E_NOTSUPPORTED;
                prgCmds[0].cmdf = (uint)(OLECMDF.OLECMDF_SUPPORTED | OLECMDF.OLECMDF_ENABLED);
                return VSConstants.S_OK;
            }
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
            if (c == WindowEditCommand.None)
            {
                if (!IsEditorOnly(pguidCmdGroup, nCmdID)) return (int)Microsoft.VisualStudio.OLE.Interop.Constants.OLECMDERR_E_NOTSUPPORTED;
                var focus = OurFocus(out var w);
                if (focus == null) return (int)Microsoft.VisualStudio.OLE.Interop.Constants.OLECMDERR_E_NOTSUPPORTED;
                string replayed = "none";
                try
                {
                    if (pguidCmdGroup == VSConstants.VSStd2K) replayed = Replay((VSConstants.VSStd2KCmdID)nCmdID, pvaIn, focus, w);
                }
                catch (Exception ex) when (ex is COMException || ex is InvalidOperationException || ex is ArgumentException || ex is ExternalException)
                {
                    L.Warn("shell.editRouter.replay.failed", "Replaying a key in a UEToVS window failed", ex, ("command", nCmdID));
                }
                LogKeystroke(pguidCmdGroup, nCmdID, replayed, w, focus);
                return VSConstants.S_OK; // never reaches the code editor
            }
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
            // ---------------------------------------------------------------- keystrokes routed as editor commands

        /// <summary>Text editor commands (VSStd2K) and UEToVS's own editor-scope commands: meaningless in our windows.</summary>
        private static bool IsEditorOnly(Guid group, uint id)
        {
            if (group == VSConstants.VSStd2K) return true;
            if (group != Shell.PackageGuids.CommandSet) return false;
            var info = Logic.Commands.CommandCatalog.ById((int)id);
            return info != null && info.Scope == Logic.Commands.CommandScope.TextEditor;
        }

        /// <summary>Replays a typing command on the window's input box. Returns what was done (for the log).</summary>
        private static string Replay(VSConstants.VSStd2KCmdID cmd, IntPtr pvaIn, IInputElement focus, Window window)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            var box = focus as TextBoxBase ?? PrimaryInput(window);
            switch (cmd)
            {
                case VSConstants.VSStd2KCmdID.TYPECHAR:
                {
                    if (box == null || pvaIn == IntPtr.Zero) return "noInput";
                    object v = Marshal.GetObjectForNativeVariant(pvaIn);
                    string text = v is char ch ? ch.ToString() : v is ushort u ? ((char)u).ToString() : v is short sh ? ((char)sh).ToString() : v as string;
                    if (string.IsNullOrEmpty(text)) return "noChar";
                    if (!ReferenceEquals(box, focus)) box.Focus();
                    TextCompositionManager.StartComposition(new TextComposition(InputManager.Current, box, text));
                    return ReferenceEquals(box, focus) ? "typed" : "typedIntoInput";
                }
                case VSConstants.VSStd2KCmdID.BACKSPACE: return Run(EditingCommands.Backspace, box);
                case VSConstants.VSStd2KCmdID.LEFT: return Run(EditingCommands.MoveLeftByCharacter, box);
                case VSConstants.VSStd2KCmdID.LEFT_EXT: return Run(EditingCommands.SelectLeftByCharacter, box);
                case VSConstants.VSStd2KCmdID.RIGHT: return Run(EditingCommands.MoveRightByCharacter, box);
                case VSConstants.VSStd2KCmdID.RIGHT_EXT: return Run(EditingCommands.SelectRightByCharacter, box);
                case VSConstants.VSStd2KCmdID.HOME:
                case VSConstants.VSStd2KCmdID.BOL: return Run(EditingCommands.MoveToLineStart, box);
                case VSConstants.VSStd2KCmdID.HOME_EXT:
                case VSConstants.VSStd2KCmdID.BOL_EXT: return Run(EditingCommands.SelectToLineStart, box);
                case VSConstants.VSStd2KCmdID.END:
                case VSConstants.VSStd2KCmdID.EOL: return Run(EditingCommands.MoveToLineEnd, box);
                case VSConstants.VSStd2KCmdID.END_EXT:
                case VSConstants.VSStd2KCmdID.EOL_EXT: return Run(EditingCommands.SelectToLineEnd, box);
                case VSConstants.VSStd2KCmdID.RETURN: return RaiseKey(focus, Key.Enter);
                case VSConstants.VSStd2KCmdID.TAB:
                case VSConstants.VSStd2KCmdID.BACKTAB: return RaiseKey(focus, Key.Tab);
                case VSConstants.VSStd2KCmdID.UP: return RaiseKey(focus, Key.Up);
                case VSConstants.VSStd2KCmdID.DOWN: return RaiseKey(focus, Key.Down);
                case VSConstants.VSStd2KCmdID.PAGEUP: return RaiseKey(focus, Key.PageUp);
                case VSConstants.VSStd2KCmdID.PAGEDN: return RaiseKey(focus, Key.PageDown);
                case VSConstants.VSStd2KCmdID.CANCEL: return RaiseKey(focus, Key.Escape);
                default: return "ignored";
            }
        }

        private static string Run(RoutedUICommand cmd, IInputElement box)
        {
            if (box == null || !cmd.CanExecute(null, box)) return "noInput";
            cmd.Execute(null, box);
            return cmd.Name;
        }

        /// <summary>Raises PreviewKeyDown + KeyDown for a key on the focused element (list navigation, Enter, Esc, Tab).</summary>
        private static string RaiseKey(IInputElement focus, Key key)
        {
            var source = focus is DependencyObject d ? PresentationSource.FromDependencyObject(d) : null;
            if (source == null) return "noSource";
            var preview = new KeyEventArgs(Keyboard.PrimaryDevice, source, Environment.TickCount, key) { RoutedEvent = Keyboard.PreviewKeyDownEvent };
            InputManager.Current.ProcessInput(preview);
            if (!preview.Handled)
                InputManager.Current.ProcessInput(new KeyEventArgs(Keyboard.PrimaryDevice, source, Environment.TickCount, key) { RoutedEvent = Keyboard.KeyDownEvent });
            return "key:" + key;
        }

        /// <summary>The window's first visible, editable text box (Search Everywhere's search field, …).</summary>
        private static TextBoxBase PrimaryInput(Window window)
        {
            if (window == null) return null;
            var queue = new Queue<DependencyObject>();
            queue.Enqueue(window);
            int visited = 0;
            while (queue.Count > 0 && visited++ < 5000)
            {
                var d = queue.Dequeue();
                if (d is TextBoxBase t && t.IsVisible && t.IsEnabled && !t.IsReadOnly && t.Focusable) return t;
                if (!(d is Visual)) continue;
                int n = VisualTreeHelper.GetChildrenCount(d);
                for (int i = 0; i < n; i++) queue.Enqueue(VisualTreeHelper.GetChild(d, i));
            }
            return null;
        }

        private static readonly HashSet<string> Logged = new HashSet<string>(StringComparer.Ordinal);

        /// <summary>Info once per (window, command, outcome) so the log shows where keys came from without one line per keystroke.</summary>
        private static void LogKeystroke(Guid group, uint id, string replayed, Window window, IInputElement focus)
        {
            string cmd = group == VSConstants.VSStd2K ? ((VSConstants.VSStd2KCmdID)id).ToString() : "UEToVS:0x" + id.ToString("X4");
            string key = window?.GetType().Name + "|" + cmd + "|" + replayed;
            if (Logged.Add(key))
                L.Info("shell.editRouter.keystroke", "Editor command while a UEToVS window is active: kept out of the code editor",
                       ("command", cmd), ("replayed", replayed), ("window", window?.GetType().Name), ("focus", focus?.GetType().FullName));
            else L.Debug("shell.editRouter.keystroke", null, ("command", cmd), ("replayed", replayed));
        }
    }
}
