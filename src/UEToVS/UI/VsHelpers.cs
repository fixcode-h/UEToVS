using System;
using System.IO;
using EnvDTE;
using Microsoft.VisualStudio;
using Microsoft.VisualStudio.Editor;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Shell.Interop;
using Microsoft.VisualStudio.Text;
using Microsoft.VisualStudio.Text.Editor;
using Microsoft.VisualStudio.TextManager.Interop;
using UEToVS.Core.Navigation;

namespace UEToVS.UI
{
    internal static class VsHelpers
    {
        public static DTE Dte
        {
            get
            {
                ThreadHelper.ThrowIfNotOnUIThread();
                return (DTE)Package.GetGlobalService(typeof(DTE));
            }
        }

        public static IntPtr MainWindowHandle
        {
            get
            {
                ThreadHelper.ThrowIfNotOnUIThread();
                var shell = (IVsUIShell)Package.GetGlobalService(typeof(SVsUIShell));
                if (shell != null && shell.GetDialogOwnerHwnd(out IntPtr hwnd) == VSConstants.S_OK) return hwnd;
                return IntPtr.Zero;
            }
        }

        /// <summary>The active document text view, or null.</summary>
        public static IWpfTextView ActiveTextView()
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            var textManager = (IVsTextManager)Package.GetGlobalService(typeof(SVsTextManager));
            if (textManager == null || textManager.GetActiveView(1, null, out IVsTextView vsView) != VSConstants.S_OK || vsView == null) return null;
            var componentModel = (Microsoft.VisualStudio.ComponentModelHost.IComponentModel)Package.GetGlobalService(typeof(Microsoft.VisualStudio.ComponentModelHost.SComponentModel));
            var adapters = componentModel?.GetService<IVsEditorAdaptersFactoryService>();
            return adapters?.GetWpfTextView(vsView);
        }

        public static string FilePathOf(ITextBuffer buffer)
        {
            if (buffer != null && buffer.Properties.TryGetProperty(typeof(ITextDocument), out ITextDocument doc)) return doc.FilePath;
            return null;
        }

        public static CodeLocation LocationOf(ITextView view)
        {
            if (view == null) return null;
            string path = FilePathOf(view.TextBuffer);
            if (path == null) return null;
            var caret = view.Caret.Position.BufferPosition;
            var line = caret.GetContainingLine();
            return new CodeLocation(path, line.LineNumber, caret.Position - line.Start.Position);
        }

        public static CodeLocation CurrentLocation()
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            return LocationOf(ActiveTextView());
        }

        /// <summary>Opens a file and puts the caret at 0-based line/column, centered. line &lt; 0 only opens the file.</summary>
        public static bool OpenAt(string path, int line, int column, bool newTabGroup = false)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            if (string.IsNullOrEmpty(path) || !File.Exists(path))
            {
                OutputLog.Status("UEToVS: 文件不存在 " + path);
                return false;
            }
            try
            {
                VsShellUtilities.OpenDocument(ServiceProvider.GlobalProvider, path, VSConstants.LOGVIEWID.TextView_guid,
                    out _, out _, out IVsWindowFrame frame, out IVsTextView view);
                if (newTabGroup) TryExecute("Window.NewVerticalTabGroup");
                frame?.Show();
                if (line < 0) return true; // just open/activate, keep the caret where it was
                if (view == null) view = VsShellUtilities.GetTextView(frame);
                if (view == null) return true;
                view.GetBuffer(out IVsTextLines lines);
                int lineCount = 0;
                lines?.GetLineCount(out lineCount);
                line = Math.Max(0, Math.Min(line, Math.Max(0, lineCount - 1)));
                int len = 0;
                lines?.GetLengthOfLine(line, out len);
                column = Math.Max(0, Math.Min(column, len));
                view.SetCaretPos(line, column);
                view.CenterLines(Math.Max(0, line - 1), 3);
                view.SetSelection(line, column, line, column);
                return true;
            }
            catch (Exception ex)
            {
                OutputLog.Write("Open failed: " + path + ": " + ex.Message);
                return false;
            }
        }

        public static bool TryExecute(string command, string args = "")
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            try
            {
                var cmd = Dte.Commands.Item(command);
                if (cmd == null || !cmd.IsAvailable) return false;
                Dte.ExecuteCommand(command, args);
                return true;
            }
            catch (Exception)
            {
                return false;
            }
        }

        /// <summary>Directory of the open solution, or null.</summary>
        public static string SolutionDirectory()
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            var solution = (IVsSolution)Package.GetGlobalService(typeof(SVsSolution));
            if (solution == null) return null;
            solution.GetSolutionInfo(out string dir, out _, out _);
            return string.IsNullOrEmpty(dir) ? null : dir;
        }

        /// <summary>Per-solution storage folder: &lt;sln&gt;/.vs/UEToVS.</summary>
        public static string SolutionStateDirectory()
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            var dir = SolutionDirectory();
            if (dir == null) return null;
            string path = Path.Combine(dir, ".vs", "UEToVS");
            Directory.CreateDirectory(path);
            return path;
        }

        public static string WordAtCaret(ITextView view)
        {
            if (view == null) return null;
            var pos = view.Caret.Position.BufferPosition;
            var line = pos.GetContainingLine();
            string text = line.GetText();
            int i = pos.Position - line.Start.Position;
            int s = i, e = i;
            while (s > 0 && IsWordChar(text[s - 1])) s--;
            while (e < text.Length && IsWordChar(text[e])) e++;
            return e > s ? text.Substring(s, e - s) : null;
        }

        private static bool IsWordChar(char c) => char.IsLetterOrDigit(c) || c == '_';

        /// <summary>Reads up to <paramref name="count"/> lines around a 0-based line from an open buffer or disk.</summary>
        public static string[] ReadLines(string path, int firstLine, int count)
        {
            try
            {
                var result = new System.Collections.Generic.List<string>();
                int i = 0;
                foreach (var l in File.ReadLines(path))
                {
                    if (i >= firstLine) result.Add(l);
                    if (++i >= firstLine + count) break;
                }
                return result.ToArray();
            }
            catch (Exception)
            {
                return Array.Empty<string>();
            }
        }
    }
}
