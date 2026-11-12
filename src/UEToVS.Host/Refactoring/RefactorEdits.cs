using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using EnvDTE;
using Microsoft.VisualStudio;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Shell.Interop;
using Microsoft.VisualStudio.Text;
using Microsoft.VisualStudio.TextManager.Interop;
using UEToVS.Core.Diagnostics;
using UEToVS.Host.Editor;
using UEToVS.Host.Logic.Refactoring;
using UEToVS.Host.Navigation;

namespace UEToVS.Host.Refactoring
{
    /// <summary>Outcome of <see cref="RefactorEdits.Apply"/>.</summary>
    internal sealed class EditResult
    {
        public int Applied { get; set; }
        public int Skipped { get; set; }
        public int Files { get; set; }
        public List<string> FailedFiles { get; } = new List<string>();
        public bool Ok => FailedFiles.Count == 0;
    }

    /// <summary>
    /// Applies planned <see cref="TextEdit"/>s through the VS editor so every refactoring is ONE undo step (§6.6):
    /// all buffers are edited inside a DTE UndoContext (linked undo across documents); each buffer gets a single
    /// ITextEdit. Files that are not open are opened as documents without showing a window (their edits then take part in
    /// the linked undo), and saved when they were clean before, so the engine and the disk see the change. Edits whose
    /// old text no longer matches are skipped and counted. UI thread only.
    /// </summary>
    internal static class RefactorEdits
    {
        private static readonly Logger L = Log.For("Refactor");

        public static EditResult Apply(string undoName, IEnumerable<TextEdit> edits)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            var result = new EditResult();
            var byFile = edits.Where(e => e != null && !string.IsNullOrEmpty(e.FilePath))
                              .GroupBy(e => e.FilePath, StringComparer.OrdinalIgnoreCase).ToList();
            if (byFile.Count == 0) return result;
            var dte = VsEditor.Dte;
            UndoContext undo = null;
            bool opened = false;
            try
            {
                undo = dte?.UndoContext;
                if (undo != null && !undo.IsOpen)
                {
                    undo.Open(undoName, false);
                    opened = true;
                }
            }
            catch (Exception ex)
            {
                L.Warn("refactor.undo.open.failed", "UndoContext.Open failed; edits are undone per file", ex);
                opened = false;
            }
            try
            {
                foreach (var group in byFile)
                {
                    try
                    {
                        var (buffer, wasDirty) = GetBuffer(group.Key);
                        if (buffer == null)
                        {
                            result.FailedFiles.Add(group.Key);
                            L.Warn("refactor.buffer.unavailable", "No editor buffer for the file", ("file", group.Key));
                            continue;
                        }
                        int applied = ApplyToBuffer(buffer, group.ToList(), out int skipped);
                        result.Applied += applied;
                        result.Skipped += skipped;
                        if (applied > 0) result.Files++;
                        if (applied > 0 && !wasDirty) EditorServices.Document(buffer)?.Save();
                    }
                    catch (Exception ex)
                    {
                        result.FailedFiles.Add(group.Key);
                        L.Error("refactor.edit.failed", "Editing a file failed", ex, ("file", group.Key));
                    }
                }
            }
            finally
            {
                try
                {
                    if (opened && undo.IsOpen) undo.Close();
                }
                catch (Exception ex)
                {
                    L.Warn("refactor.undo.close.failed", "UndoContext.Close failed", ex);
                }
            }
            L.Info("refactor.edits.applied", null, ("undo", undoName), ("applied", result.Applied), ("skipped", result.Skipped), ("files", result.Files),
                ("failed", result.FailedFiles.Count), ("linkedUndo", opened));
            return result;
        }

        /// <summary>Applies the edits to one buffer as a single ITextEdit (positions from the current snapshot; old text verified).</summary>
        internal static int ApplyToBuffer(ITextBuffer buffer, IReadOnlyList<TextEdit> edits, out int skipped)
        {
            skipped = 0;
            var snapshot = buffer.CurrentSnapshot;
            var spans = new List<(Span Span, string Text)>();
            foreach (var e in edits)
            {
                Span span;
                if (e.Line >= snapshot.LineCount)
                {
                    // append at the end (start a new line first when the text does not end with one)
                    string t = e.NewText ?? string.Empty;
                    if (snapshot.Length > 0 && !snapshot.GetText(snapshot.Length - 1, 1).EndsWith("\n", StringComparison.Ordinal))
                        t = (snapshot.GetText().Contains("\r\n") ? "\r\n" : "\n") + t.TrimEnd('\r', '\n');
                    spans.Add((new Span(snapshot.Length, 0), t));
                    continue;
                }
                if (e.Line < 0) { skipped++; continue; }
                var line = snapshot.GetLineFromLineNumber(e.Line);
                int start = line.Start.Position + e.Column;
                if (e.Column < 0 || start + e.Length > snapshot.Length || e.Column > line.LengthIncludingLineBreak) { skipped++; continue; }
                span = new Span(start, e.Length);
                if (e.OldText != null && snapshot.GetText(span) != e.OldText) { skipped++; continue; }
                spans.Add((span, e.NewText ?? string.Empty));
            }
            if (spans.Count == 0) return 0;
            using (var edit = buffer.CreateEdit())
            {
                foreach (var (span, text) in spans.OrderByDescending(s => s.Span.Start)) edit.Replace(span, text);
                edit.Apply();
            }
            return spans.Count;
        }

        /// <summary>The editor buffer of a file: the open document's, else the file opened as a document without a visible window.</summary>
        internal static (ITextBuffer Buffer, bool WasDirty) GetBuffer(string path)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            if (!File.Exists(path)) return (null, false);
            var adapters = EditorServices.Adapters;
            if (adapters == null) return (null, false);
            if (Package.GetGlobalService(typeof(SVsRunningDocumentTable)) is IVsRunningDocumentTable rdt &&
                ErrorHandler.Succeeded(rdt.FindAndLockDocument((uint)_VSRDTFLAGS.RDT_NoLock, path, out _, out _, out IntPtr docData, out _)) && docData != IntPtr.Zero)
            {
                try
                {
                    var obj = System.Runtime.InteropServices.Marshal.GetObjectForIUnknown(docData);
                    var vsBuffer = obj as IVsTextBuffer ?? (obj as IVsTextBufferProvider)?.GetBufferOrNull();
                    var buffer = vsBuffer == null ? null : adapters.GetDocumentBuffer(vsBuffer);
                    if (buffer != null) return (buffer, EditorServices.Document(buffer)?.IsDirty == true);
                }
                finally
                {
                    System.Runtime.InteropServices.Marshal.Release(docData);
                }
            }
            // not open: open it as a document (the window frame is not shown)
            VsShellUtilities.OpenDocument(ServiceProvider.GlobalProvider, path, VSConstants.LOGVIEWID.TextView_guid, out _, out _, out IVsWindowFrame frame);
            if (frame == null) return (null, false);
            ErrorHandler.ThrowOnFailure(frame.GetProperty((int)__VSFPROPID.VSFPROPID_DocData, out object data));
            var lines = data as IVsTextBuffer ?? (data as IVsTextBufferProvider)?.GetBufferOrNull();
            var b = lines == null ? null : adapters.GetDocumentBuffer(lines);
            L.Debug("refactor.buffer.opened", null, ("file", path), ("ok", b != null));
            return (b, false);
        }

        private static IVsTextBuffer GetBufferOrNull(this IVsTextBufferProvider provider) =>
            provider != null && ErrorHandler.Succeeded(provider.GetTextBuffer(out IVsTextLines lines)) ? lines : null;

        /// <summary>Current text of a file: the open buffer's (possibly unsaved), else the disk. Null when unreadable.</summary>
        public static string ReadText(string path)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            try
            {
                if (Package.GetGlobalService(typeof(SVsRunningDocumentTable)) is IVsRunningDocumentTable rdt &&
                    ErrorHandler.Succeeded(rdt.FindAndLockDocument((uint)_VSRDTFLAGS.RDT_NoLock, path, out _, out _, out IntPtr docData, out _)) && docData != IntPtr.Zero)
                {
                    try
                    {
                        var obj = System.Runtime.InteropServices.Marshal.GetObjectForIUnknown(docData);
                        var vsBuffer = obj as IVsTextBuffer ?? (obj as IVsTextBufferProvider)?.GetBufferOrNull();
                        var buffer = vsBuffer == null ? null : EditorServices.Adapters?.GetDocumentBuffer(vsBuffer);
                        if (buffer != null) return buffer.CurrentSnapshot.GetText();
                    }
                    finally
                    {
                        System.Runtime.InteropServices.Marshal.Release(docData);
                    }
                }
                return File.Exists(path) ? File.ReadAllText(path) : null;
            }
            catch (Exception ex)
            {
                L.Warn("refactor.read.failed", "Reading a file failed", ex, ("file", path));
                return null;
            }
        }
    }
}
