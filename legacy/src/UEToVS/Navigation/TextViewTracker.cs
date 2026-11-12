using System;
using System.ComponentModel.Composition;
using Microsoft.VisualStudio.Text;
using Microsoft.VisualStudio.Text.Editor;
using Microsoft.VisualStudio.Utilities;
using UEToVS.Core.Navigation;
using UEToVS.UI;

namespace UEToVS.Navigation
{
    /// <summary>Feeds caret moves, edits and saves of every document view into <see cref="NavigationService"/>.</summary>
    [Export(typeof(IWpfTextViewCreationListener))]
    [ContentType("text")]
    [TextViewRole(PredefinedTextViewRoles.Document)]
    internal sealed class TextViewTracker : IWpfTextViewCreationListener
    {
        public void TextViewCreated(IWpfTextView view)
        {
            if (!view.TextBuffer.Properties.TryGetProperty(typeof(ITextDocument), out ITextDocument doc)) return;
            var nav = NavigationService.Instance;

            void Caret(object s, CaretPositionChangedEventArgs e)
            {
                if (!view.HasAggregateFocus) return;
                nav.OnCaret(VsHelpers.LocationOf(view), null);
            }

            void Focus(object s, EventArgs e) => nav.OnCaret(VsHelpers.LocationOf(view), null);

            void Changed(object s, TextContentChangedEventArgs e)
            {
                if (!view.HasAggregateFocus || e.Changes.Count == 0) return;
                try
                {
                    var pos = e.Changes[0].NewPosition;
                    var line = e.After.GetLineFromPosition(Math.Min(pos, e.After.Length));
                    nav.OnEdit(new CodeLocation(doc.FilePath, line.LineNumber, pos - line.Start.Position), line.GetText());
                }
                catch (Exception)
                {
                }
            }

            void FileAction(object s, TextDocumentFileActionEventArgs e)
            {
                if ((e.FileActionType & FileActionTypes.ContentSavedToDisk) != 0) nav.OnSaved(e.FilePath);
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
    }
}
