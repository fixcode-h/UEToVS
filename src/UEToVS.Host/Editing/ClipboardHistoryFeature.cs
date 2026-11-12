using System;
using System.Linq;
using System.Threading.Tasks;
using EnvDTE;
using Microsoft.VisualStudio;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Text;
using Microsoft.VisualStudio.Text.Editor;
using UEToVS.Core.Diagnostics;
using UEToVS.Host.Diagnostics;
using UEToVS.Host.Editor;
using UEToVS.Host.Integration;
using UEToVS.Host.IntelliSense;
using UEToVS.Host.Logic.Editing;
using UEToVS.Host.Logic.Localization;
using UEToVS.Host.Shell;
using UEToVS.UI.Patterns;

namespace UEToVS.Host.Editing
{
    /// <summary>
    /// Multi clipboard (v0.4.5): after VS's own Edit.Copy / Edit.Cut the clipboard text is recorded in a
    /// <see cref="ClipboardRing"/> (event driven, no polling); Ctrl+Shift+V shows the last N in a popup and inserts the
    /// chosen one at the caret (replacing the selection, one undo step). Off with editor.clipboardHistory.
    /// </summary>
    internal static class ClipboardHistoryFeature
    {
        private static readonly Logger L = Log.For("Editing");
        private static readonly ClipboardRing Ring = new ClipboardRing();
        private static CommandEvents _copy, _cut; // keep the COM event sources alive
        private static ContextMenuPopup _menu;

        public static void Initialize()
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            if (!(Package.GetGlobalService(typeof(DTE)) is DTE dte)) return;
            string std97 = typeof(VSConstants.VSStd97CmdID).GUID.ToString("B");
            _copy = dte.Events.CommandEvents[std97, (int)VSConstants.VSStd97CmdID.Copy];
            _cut = dte.Events.CommandEvents[std97, (int)VSConstants.VSStd97CmdID.Cut];
            _copy.AfterExecute += OnCopied;
            _cut.AfterExecute += OnCopied;
            L.Debug("editor.clipboard.ready", null);
        }

        private static void OnCopied(string guid, int id, object customIn, object customOut)
        {
            Safe.Execute(L, "editor.clipboard.capture", () =>
            {
                ThreadHelper.ThrowIfNotOnUIThread();
                var settings = IntelliSenseContext.Settings;
                if (settings?.ClipboardHistoryEnabled == false) return;
                if (settings != null) Ring.Capacity = settings.ClipboardHistorySize;
                if (!System.Windows.Clipboard.ContainsText()) return;
                string text = System.Windows.Clipboard.GetText();
                bool added = Ring.Add(text);
                L.Debug("editor.clipboard.capture", null, ("chars", text?.Length ?? 0), ("added", added), ("count", Ring.Count));
            });
        }

        public static async Task PasteAsync()
        {
            await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
            if (IntelliSenseContext.Settings?.ClipboardHistoryEnabled == false)
            {
                VsStatusBar.SetText(EditorFeatureStrings.ClipboardDisabled);
                return;
            }
            var view = EditorServices.ActiveView();
            if (view == null) return;
            var items = Ring.Items;
            if (items.Count == 0)
            {
                VsStatusBar.SetText(EditorFeatureStrings.ClipboardEmpty);
                return;
            }
            var entries = items.Select((t, i) => new MenuEntry("clip" + i, () => ClipboardRing.Preview(t))
            {
                Description = () => EditorFeatureStrings.ClipboardEntry(ClipboardRing.LineCount(t), t.Length),
                Tag = t,
            }).ToList();
            if (_menu == null) _menu = new ContextMenuPopup("uetovs.edit.pasteFromHistory");
            _menu.OwnerHandle = VsMainWindow.Handle;
            var caret = view.Caret.Position.BufferPosition;
            var chosen = await _menu.ShowAsync(entries, EditorServices.AnchorAt(view, caret));
            await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
            if (!(chosen?.Tag is string text) || view.IsClosed) return;
            Safe.Execute(L, "editor.clipboard.paste", () => Insert(view, text, items.IndexOf(text)));
        }

        private static void Insert(IWpfTextView view, string text, int index)
        {
            if (view.Options.GetOptionValue(DefaultTextViewOptions.ViewProhibitUserInputId))
            {
                VsStatusBar.SetText(EditorFeatureStrings.SurroundReadOnly);
                return;
            }
            var sel = view.Selection.IsEmpty
                ? new SnapshotSpan(view.Caret.Position.BufferPosition, 0)
                : view.Selection.StreamSelectionSpan.SnapshotSpan;
            using (var edit = view.TextBuffer.CreateEdit())
            {
                edit.Replace(sel.Span, text);
                edit.Apply();
            }
            view.Selection.Clear();
            Ring.Add(text); // most recently used on top
            L.Info("editor.clipboard.paste", null, ("index", index), ("chars", text.Length), ("count", Ring.Count));
        }
    }
}
