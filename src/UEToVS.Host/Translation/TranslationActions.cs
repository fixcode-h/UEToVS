using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading.Tasks;
using Microsoft.VisualStudio.ComponentModelHost;
using Microsoft.VisualStudio.Language.StandardClassification;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Text;
using Microsoft.VisualStudio.Text.Adornments;
using Microsoft.VisualStudio.Text.Editor;
using UEToVS.Core.Diagnostics;
using UEToVS.Host.Editor;
using UEToVS.Host.Logic.Actions;
using UEToVS.Host.Logic.IntelliSense;
using UEToVS.Host.Logic.Localization;
using UEToVS.Host.Shell;
using Task = System.Threading.Tasks.Task;

namespace UEToVS.Host.Translation
{
    /// <summary>
    /// "翻译选中内容" (editor context menu, Ctrl+Shift+Alt+U, T) and "清空翻译缓存". The translation is shown in VS's own
    /// tooltip presenter (the one Quick Info uses), anchored at the selection, with the same bilingual / translation-only
    /// rendering as Quick Info. With no selection the comment around the caret is translated. Works in every text editor.
    /// </summary>
    internal static class TranslationActions
    {
        private static readonly Logger L = Log.For("Translation");
        private static readonly object PresenterKey = typeof(TranslationActions);

        /// <summary>Lines of comment collected around the caret when nothing is selected.</summary>
        private const int MaxCommentLines = 80;

        public static void Register(ActionRegistry registry)
        {
            registry.Register(new DelegateAction(ActionIds.TranslateSelection, TranslationStrings.CmdTranslateSelection, ActionCategory.Navigation,
                ctx => TranslateSelectionAsync(), ctx => ctx.ActiveDocumentPath != null, null, TranslationStrings.KwTranslate));
            registry.Register(new DelegateAction(ActionIds.ClearTranslationCache, TranslationStrings.CmdClearCache, ActionCategory.Settings,
                ctx => ClearCacheAsync(), null, null, TranslationStrings.KwTranslate));
        }

        private static Task ClearCacheAsync()
        {
            var svc = TranslationContext.Service;
            int n = svc.Cache.Count;
            svc.Cache.Clear();
            svc.ResetCooldown();
            VsStatusBar.SetText(TranslationStrings.CacheCleared(n));
            return Task.CompletedTask;
        }

        private static async Task TranslateSelectionAsync()
        {
            await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
            var view = EditorServices.ActiveView();
            if (view == null) return;
            var sw = Stopwatch.StartNew();
            var snap = view.TextSnapshot;
            var span = view.Selection.IsEmpty ? CommentAround(view.Caret.Position.BufferPosition) : view.Selection.StreamSelectionSpan.SnapshotSpan;
            if (span == null || span.Value.IsEmpty || string.IsNullOrWhiteSpace(span.Value.GetText()))
            {
                VsStatusBar.SetText(TranslationStrings.NoSelection);
                return;
            }
            var s = span.Value;
            string raw = s.Length > Logic.Translation.TranslationText.MaxTotalChars ? snap.GetText(s.Start, Logic.Translation.TranslationText.MaxTotalChars) : s.GetText();
            string text = QuickInfoModel.CleanDocumentation(raw) ?? raw.Trim();
            var service = TranslationContext.Service;
            string header = TranslationStrings.SelectionHeader(service.Options.Provider);
            object content = TranslatedTextElement.Create(service, text, showOriginal: true, header: header);
            if (content == null)
                content = new ContainerElement(ContainerElementStyle.Stacked, new object[]
                {
                    new ClassifiedTextElement(new ClassifiedTextRun(PredefinedClassificationTypeNames.Comment, header)),
                    new ClassifiedTextElement(new ClassifiedTextRun(PredefinedClassificationTypeNames.NaturalLanguage, TranslationStrings.NothingToTranslate)),
                });
            Show(view, s, content);
            L.Info("translate.selection", null, ("chars", text.Length), ("fromSelection", !view.Selection.IsEmpty), ("provider", service.Options.Provider.ToString()),
                ("ms", sw.ElapsedMilliseconds));
        }

        /// <summary>Opens (or replaces) the translation popup of <paramref name="view"/>; it closes on caret move, edit, focus loss.</summary>
        private static void Show(IWpfTextView view, SnapshotSpan span, object content)
        {
            if (view.Properties.TryGetProperty(PresenterKey, out IToolTipPresenter old)) old.Dismiss();
            var cm = Package.GetGlobalService(typeof(SComponentModel)) as IComponentModel;
            var tooltips = cm?.GetService<IToolTipService>();
            if (tooltips == null) return;
            var presenter = tooltips.CreatePresenter(view, new ToolTipParameters(trackMouse: false, ignoreBufferChange: false));
            view.Properties[PresenterKey] = presenter;

            void Close(object sender, EventArgs e) => presenter.Dismiss();
            view.Caret.PositionChanged += Close;
            view.LostAggregateFocus += Close;
            view.Closed += Close;
            presenter.Dismissed += (sender, e) =>
            {
                view.Caret.PositionChanged -= Close;
                view.LostAggregateFocus -= Close;
                view.Closed -= Close;
                if (view.Properties.TryGetProperty(PresenterKey, out IToolTipPresenter current) && ReferenceEquals(current, presenter))
                    view.Properties.RemoveProperty(PresenterKey);
            };
            presenter.StartOrUpdate(span.Snapshot.CreateTrackingSpan(span.Span, SpanTrackingMode.EdgeInclusive), new[] { content });
        }

        /// <summary>The comment block (// /// /* * lines) containing <paramref name="caret"/>, or its line when it is not a comment.</summary>
        private static SnapshotSpan? CommentAround(SnapshotPoint caret)
        {
            var snap = caret.Snapshot;
            var line = caret.GetContainingLine();
            if (!IsCommentLine(line.GetText())) return line.Extent;
            int first = line.LineNumber, last = line.LineNumber;
            while (first > 0 && line.LineNumber - first < MaxCommentLines && IsCommentLine(snap.GetLineFromLineNumber(first - 1).GetText())) first--;
            while (last < snap.LineCount - 1 && last - line.LineNumber < MaxCommentLines && IsCommentLine(snap.GetLineFromLineNumber(last + 1).GetText())) last++;
            var start = snap.GetLineFromLineNumber(first).Start;
            var end = snap.GetLineFromLineNumber(last).End;
            return new SnapshotSpan(start, end);
        }

        internal static bool IsCommentLine(string text)
        {
            string t = (text ?? string.Empty).TrimStart();
            return t.StartsWith("//", StringComparison.Ordinal) || t.StartsWith("/*", StringComparison.Ordinal) ||
                   t.StartsWith("*", StringComparison.Ordinal) || t.StartsWith("--", StringComparison.Ordinal);
        }
    }
}
