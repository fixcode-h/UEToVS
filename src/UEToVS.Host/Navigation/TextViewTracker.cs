using System;
using System.ComponentModel.Composition;
using Microsoft.VisualStudio.Text;
using Microsoft.VisualStudio.Text.Editor;
using Microsoft.VisualStudio.Utilities;
using UEToVS.Core.Diagnostics;
using UEToVS.Core.Navigation;

namespace UEToVS.Host.Navigation
{
    /// <summary>
    /// Feeds caret moves, edits and saves of every document view into <see cref="NavigationService"/> (Back/Forward,
    /// recent files/locations, edit locations, Build.cs check on save). Handlers are cheap (no I/O) and never throw.
    /// </summary>
    [Export(typeof(IWpfTextViewCreationListener))]
    [ContentType("text")]
    [TextViewRole(PredefinedTextViewRoles.Document)]
    internal sealed class TextViewTracker : IWpfTextViewCreationListener
    {
        private static readonly Logger L = Log.For("Navigation");

        public void TextViewCreated(IWpfTextView view)
        {
            if (!view.TextBuffer.Properties.TryGetProperty(typeof(ITextDocument), out ITextDocument doc)) return;
            var nav = NavigationService.Instance;

            void Caret(object s, CaretPositionChangedEventArgs e)
            {
                if (!view.HasAggregateFocus) return;
                Guard("nav.track.caret", () => nav.OnCaret(VsEditor.LocationOf(view)));
            }

            void Focus(object s, EventArgs e) => Guard("nav.track.focus", () => nav.OnCaret(VsEditor.LocationOf(view)));

            void Changed(object s, TextContentChangedEventArgs e)
            {
                if (!view.HasAggregateFocus || e.Changes.Count == 0) return;
                Guard("nav.track.edit", () =>
                {
                    int pos = Math.Min(e.Changes[0].NewPosition, e.After.Length);
                    var line = e.After.GetLineFromPosition(pos);
                    nav.OnEdit(new CodeLocation(doc.FilePath, line.LineNumber, pos - line.Start.Position), line.GetText());
                });
            }

            void FileAction(object s, TextDocumentFileActionEventArgs e)
            {
                if ((e.FileActionType & FileActionTypes.ContentSavedToDisk) != 0) Guard("nav.track.saved", () => nav.OnSaved(e.FilePath));
            }

            view.Caret.PositionChanged += Caret;
            view.GotAggregateFocus += Focus;
            view.TextBuffer.Changed += Changed;
            doc.FileActionOccurred += FileAction;
            view.Closed += (s, e) =>
            {
                view.Caret.PositionChanged -= Caret;
                view.GotAggregateFocus -= Focus;
                view.TextBuffer.Changed -= Changed;
                doc.FileActionOccurred -= FileAction;
            };
        }

        private static void Guard(string evt, Action a)
        {
            try
            {
                a();
            }
            catch (Exception ex)
            {
                L.Error(evt + ".failed", "Navigation tracking handler failed", ex);
            }
        }
    }
}
