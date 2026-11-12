using System;
using System.Collections.Generic;
using System.ComponentModel.Composition;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.VisualStudio.Imaging.Interop;
using Microsoft.VisualStudio.Language.Intellisense;
using Microsoft.VisualStudio.Text;
using Microsoft.VisualStudio.Text.Editor;
using Microsoft.VisualStudio.Utilities;
using UEToVS.Core.Diagnostics;
using UEToVS.Host.Editor;
using UEToVS.Host.Logic.Actions;
using UEToVS.Host.Logic.Refactoring;

namespace UEToVS.Host.Refactoring
{
    [Export(typeof(ISuggestedActionsSourceProvider))]
    [Name("UEToVS Suggested Actions")]
    [ContentType(EditorContentTypes.Cpp)]
    internal sealed class UEToVSSuggestedActionsSourceProvider : ISuggestedActionsSourceProvider
    {
        public ISuggestedActionsSource CreateSuggestedActionsSource(ITextView textView, ITextBuffer textBuffer)
        {
            if (!(textView is IWpfTextView view) || textBuffer != textView.TextBuffer) return null;
            return view.Properties.GetOrCreateSingletonProperty(typeof(UEToVSSuggestedActionsSource), () => new UEToVSSuggestedActionsSource(view));
        }
    }

    /// <summary>
    /// VS light bulb for the UEToVS context actions (same providers as Alt+Enter, <see cref="RefactoringFeature.Hub"/>).
    /// Only while UEToVS IntelliSense is in effect. One engine call per caret position (debounced 120 ms, cancelled by VS
    /// when the caret moves on); the result is cached per snapshot version + position so GetSuggestedActions (UI thread,
    /// synchronous) never waits on the engine. Cheap lexical pre-check: no call on blank lines.
    /// </summary>
    internal sealed class UEToVSSuggestedActionsSource : ISuggestedActionsSource
    {
        private static readonly Logger L = Log.For("Refactor");
        private readonly IWpfTextView _view;
        private (long Version, int Position, int SelectionEnd, RefactorContext Context, List<ContextActionItem> Items) _cache;

        public UEToVSSuggestedActionsSource(IWpfTextView view) => _view = view;

#pragma warning disable CS0067 // required by the interface; the items follow the caret, VS re-queries on its own
        public event EventHandler<EventArgs> SuggestedActionsChanged;
#pragma warning restore CS0067

        public async Task<bool> HasSuggestedActionsAsync(ISuggestedActionCategorySet requestedActionCategories, SnapshotSpan range, CancellationToken cancellationToken)
        {
            try
            {
                if (!RefactoringFeature.IsActive || _view.IsClosed) return false;
                var caret = _view.Caret.Position.BufferPosition;
                if (caret.Snapshot != range.Snapshot) return false;
                var line = caret.GetContainingLine();
                if (line.Length == 0 || line.Length > 4000 || string.IsNullOrWhiteSpace(line.GetText())) return false;
                long version = caret.Snapshot.Version.VersionNumber;
                int selectionEnd = SelectionKey(); // v0.4.5 batch 2: the items depend on the selection too
                var cached = _cache;
                if (cached.Context != null && cached.Version == version && cached.Position == caret.Position && cached.SelectionEnd == selectionEnd) return cached.Items.Count > 0;
                await Task.Delay(120, cancellationToken).ConfigureAwait(false);
                var context = await RefactoringFeature.BuildContextAsync(_view, true, cancellationToken);
                if (context == null || context.Version != version) return false;
                var items = RefactoringFeature.Hub.Collect(context);
                _cache = (version, caret.Position, selectionEnd, context, items);
                L.Debug("refactor.lightbulb", null, ("items", items.Count), ("word", context.Word), ("line", context.Line));
                return items.Count > 0;
            }
            catch (OperationCanceledException)
            {
                return false;
            }
            catch (Exception ex)
            {
                L.Error("refactor.lightbulb.failed", "Light bulb query failed", ex);
                return false;
            }
        }

        public IEnumerable<SuggestedActionSet> GetSuggestedActions(ISuggestedActionCategorySet requestedActionCategories, SnapshotSpan range, CancellationToken cancellationToken)
        {
            var cached = _cache;
            var caret = _view.Caret.Position.BufferPosition;
            if (cached.Context == null || cached.Items.Count == 0 || cached.Version != caret.Snapshot.Version.VersionNumber || cached.Position != caret.Position ||
                cached.SelectionEnd != SelectionKey())
                return Enumerable.Empty<SuggestedActionSet>();
            var actions = cached.Items.Select(i => (ISuggestedAction)new UEToVSSuggestedAction(_view, cached.Context, i)).ToList();
            return new[] { new SuggestedActionSet(PredefinedSuggestedActionCategoryNames.Refactoring, actions, null, SuggestedActionSetPriority.Medium) };
        }

        /// <summary>Selection start * 31 + length (0 without a selection): part of the cache key.</summary>
        private int SelectionKey()
        {
            var sel = _view.Selection;
            if (sel == null || sel.IsEmpty || sel.SelectedSpans.Count == 0) return 0;
            var span = sel.SelectedSpans[0];
            return unchecked(span.Start.Position * 31 + span.Length + 1);
        }

        public bool TryGetTelemetryId(out Guid telemetryId)
        {
            telemetryId = Guid.Empty;
            return false;
        }

        public void Dispose() => _cache = default;
    }

    internal sealed class UEToVSSuggestedAction : ISuggestedAction
    {
        private readonly IWpfTextView _view;
        private readonly RefactorContext _context;
        private readonly ContextActionItem _item;

        public UEToVSSuggestedAction(IWpfTextView view, RefactorContext context, ContextActionItem item)
        {
            _view = view;
            _context = context;
            _item = item;
        }

        public string DisplayText => _item.Title;
        public bool HasActionSets => false;
        public bool HasPreview => false;
        public ImageMoniker IconMoniker => default;
        public string IconAutomationText => null;
        public string InputGestureText => null;

        public Task<IEnumerable<SuggestedActionSet>> GetActionSetsAsync(CancellationToken cancellationToken) => Task.FromResult<IEnumerable<SuggestedActionSet>>(null);

        public Task<object> GetPreviewAsync(CancellationToken cancellationToken) => Task.FromResult<object>(null);

        public void Invoke(CancellationToken cancellationToken) => RefactoringFeature.Execute(_item, _view, _context, ActionSource.LightBulb);

        public bool TryGetTelemetryId(out Guid telemetryId)
        {
            telemetryId = Guid.Empty;
            return false;
        }

        public void Dispose()
        {
        }
    }
}
