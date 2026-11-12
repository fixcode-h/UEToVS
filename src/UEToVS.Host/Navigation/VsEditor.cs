using System;
using System.Collections.Generic;
using System.IO;
using EnvDTE;
using Microsoft.VisualStudio;
using Microsoft.VisualStudio.ComponentModelHost;
using Microsoft.VisualStudio.Editor;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Shell.Interop;
using Microsoft.VisualStudio.Text;
using Microsoft.VisualStudio.Text.Editor;
using Microsoft.VisualStudio.TextManager.Interop;
using UEToVS.Core.Diagnostics;
using UEToVS.Core.Navigation;

namespace UEToVS.Host.Navigation
{
    /// <summary>
    /// Editor helpers shared by the M1 features (UI thread): active text view, caret location, open-at-line, word at
    /// caret, executing VS commands by name. Ported from the prototype's VsHelpers.
    /// </summary>
    internal static class VsEditor
    {
        private static readonly Logger L = Log.For("Editor");

        public static DTE Dte
        {
            get
            {
                ThreadHelper.ThrowIfNotOnUIThread();
                return Package.GetGlobalService(typeof(DTE)) as DTE;
            }
        }

        /// <summary>The active document text view, or null.</summary>
        public static IWpfTextView ActiveTextView()
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            try
            {
                if (!(Package.GetGlobalService(typeof(SVsTextManager)) is IVsTextManager tm)) return null;
                if (tm.GetActiveView(1, null, out IVsTextView vsView) != VSConstants.S_OK || vsView == null) return null;
                var cm = Package.GetGlobalService(typeof(SComponentModel)) as IComponentModel;
                return cm?.GetService<IVsEditorAdaptersFactoryService>()?.GetWpfTextView(vsView);
            }
            catch (Exception ex)
            {
                L.Warn("editor.activeView.failed", "Cannot get the active text view", ex);
                return null;
            }
        }

        public static string FilePathOf(ITextBuffer buffer)
        {
            if (buffer != null && buffer.Properties.TryGetProperty(typeof(ITextDocument), out ITextDocument doc)) return doc.FilePath;
            return null;
        }

        public static CodeLocation LocationOf(ITextView view)
        {
            if (view == null || view.IsClosed) return null;
            string path = FilePathOf(view.TextBuffer);
            if (path == null) return null;
            var caret = view.Caret.Position.BufferPosition;
            var line = caret.GetContainingLine();
            return new CodeLocation(path, line.LineNumber, caret.Position - line.Start.Position);
        }

        public static CodeLocation CurrentLocation() => LocationOf(ActiveTextView());

        /// <summary>Text of the caret line of <paramref name="view"/>, or null.</summary>
        public static string CaretLineText(ITextView view)
        {
            if (view == null) return null;
            return view.Caret.Position.BufferPosition.GetContainingLine().GetText();
        }

        /// <summary>Identifier at the caret (letters, digits, underscore), or null.</summary>
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

        /// <summary>Single-line editor selection (seed for search popups), or null.</summary>
        public static string SelectedText(ITextView view)
        {
            if (view == null || view.Selection.IsEmpty) return null;
            string sel = view.Selection.StreamSelectionSpan.GetText();
            if (sel.Length == 0 || sel.Length >= 200 || sel.IndexOf('\n') >= 0) return null;
            return sel.Trim();
        }

        /// <summary>
        /// Opens <paramref name="path"/> and puts the caret at the 0-based line/column, centered. line &lt; 0 only opens /
        /// activates the file. Returns false when the file does not exist or VS refused to open it.
        /// </summary>
        public static bool OpenAt(string path, int line, int column, bool newTabGroup = false)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            if (string.IsNullOrEmpty(path) || !File.Exists(path)) return false;
            try
            {
                VsShellUtilities.OpenDocument(ServiceProvider.GlobalProvider, path, VSConstants.LOGVIEWID.TextView_guid,
                    out _, out _, out IVsWindowFrame frame, out IVsTextView view);
                if (newTabGroup) TryExecute("Window.NewVerticalTabGroup");
                frame?.Show();
                if (line < 0) return true;
                if (view == null && frame != null) view = VsShellUtilities.GetTextView(frame);
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
                L.Error("editor.open.failed", "Opening a document failed", ex, ("file", path), ("line", line));
                return false;
            }
        }

        /// <summary>Executes a VS command by its canonical name; false when unknown or unavailable.</summary>
        public static bool TryExecute(string command, string args = "")
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            try
            {
                var dte = Dte;
                var cmd = dte?.Commands.Item(command);
                if (cmd == null || !cmd.IsAvailable) return false;
                dte.ExecuteCommand(command, args ?? string.Empty);
                L.Debug("editor.command", null, ("command", command));
                return true;
            }
            catch (Exception ex)
            {
                L.Debug("editor.command.failed", ex.Message, ("command", command));
                return false;
            }
        }

        /// <summary>Up to <paramref name="count"/> lines starting at the 0-based <paramref name="firstLine"/> (disk). Never throws.</summary>
        public static string[] ReadLines(string path, int firstLine, int count)
        {
            var result = new List<string>();
            try
            {
                int i = 0;
                foreach (var l in File.ReadLines(path))
                {
                    if (i >= firstLine) result.Add(l);
                    if (++i >= firstLine + count) break;
                }
            }
            catch (Exception)
            {
                // preview only
            }
            return result.ToArray();
        }

        /// <summary>Open document with this path (DTE), or null.</summary>
        public static Document FindOpenDocument(string path)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            try
            {
                foreach (Document d in Dte.Documents)
                {
                    try
                    {
                        if (string.Equals(d.FullName, path, StringComparison.OrdinalIgnoreCase)) return d;
                    }
                    catch (Exception)
                    {
                        // documents being closed
                    }
                }
            }
            catch (Exception ex)
            {
                L.Warn("editor.documents.failed", "Enumerating open documents failed", ex);
            }
            return null;
        }

        public static string ReadDocument(Document doc)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            var td = (TextDocument)doc.Object("TextDocument");
            return td.StartPoint.CreateEditPoint().GetText(td.EndPoint);
        }

        public static void WriteDocument(Document doc, string text)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            var td = (TextDocument)doc.Object("TextDocument");
            td.StartPoint.CreateEditPoint().ReplaceText(td.EndPoint, text, (int)vsEPReplaceTextOptions.vsEPReplaceTextKeepMarkers);
        }
    }
}
