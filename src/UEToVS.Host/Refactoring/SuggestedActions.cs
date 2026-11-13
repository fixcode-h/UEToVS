using System;
using System.Collections.Generic;
using System.ComponentModel.Composition;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.VisualStudio.Imaging;
using Microsoft.VisualStudio.Imaging.Interop;
using Microsoft.VisualStudio.Language.Intellisense;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Text;
using Microsoft.VisualStudio.Text.Editor;
using Microsoft.VisualStudio.Utilities;
using UEToVS.Core.Diagnostics;
using UEToVS.Host.Editor;
using UEToVS.Contracts;
using UEToVS.Host.Logic.Actions;
using UEToVS.Host.Logic.Editing;
using UEToVS.Host.Logic.Localization;
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
            // v0.5.12: 修复类单独一组排最前（UE 规则错误用 ErrorFix，VS 显示错误灯泡），生成 / 重构其次，导航和编辑放最后
            var sets = new List<SuggestedActionSet>();
            foreach (var g in cached.Items.GroupBy(KindOf).OrderBy(g => (int)g.Key))
            {
                var actions = g.Select(i => (ISuggestedAction)new UEToVSSuggestedAction(_view, cached.Context, i, g.Key)).ToList();
                switch (g.Key)
                {
                    case LightBulbKind.ErrorFix:
                        sets.Add(new SuggestedActionSet(PredefinedSuggestedActionCategoryNames.ErrorFix, actions, null, SuggestedActionSetPriority.High));
                        break;
                    case LightBulbKind.Fix:
                        sets.Add(new SuggestedActionSet(PredefinedSuggestedActionCategoryNames.CodeFix, actions, null, SuggestedActionSetPriority.High));
                        break;
                    case LightBulbKind.Other:
                        sets.Add(new SuggestedActionSet(PredefinedSuggestedActionCategoryNames.Refactoring, actions, null, SuggestedActionSetPriority.Low));
                        break;
                    default:
                        sets.Add(new SuggestedActionSet(PredefinedSuggestedActionCategoryNames.Refactoring, actions, null, SuggestedActionSetPriority.Medium));
                        break;
                }
            }
            return sets;
        }

        private static readonly HashSet<string> ErrorCodes = new HashSet<string>(StringComparer.Ordinal)
        {
            CodeDiagnosticCodes.MissingGeneratedBody, CodeDiagnosticCodes.GeneratedFunctionBody, CodeDiagnosticCodes.MissingRepNotify,
            CodeDiagnosticCodes.UnregisteredReplicatedProperty,
        };

        internal static LightBulbKind KindOf(ContextActionItem i)
        {
            if (i.Argument is UeRuleFix f && ErrorCodes.Contains(f.Code ?? string.Empty)) return LightBulbKind.ErrorFix;
            if (i.Group == RefactorStrings.GroupFix || i.Group == EditorFeatureStrings.GroupFix) return LightBulbKind.Fix;
            if (i.Group == RefactorStrings.GroupGenerate) return LightBulbKind.Generate;
            if (i.Group == LightBulbStrings.GroupNavigate || i.Group == LightBulbStrings.GroupEdit) return LightBulbKind.Other;
            return LightBulbKind.Refactor;
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

    /// <summary>灯泡里的分组（顺序即显示顺序）。</summary>
    internal enum LightBulbKind
    {
        ErrorFix,
        Fix,
        Generate,
        Refactor,
        Other,
    }

    internal sealed class UEToVSSuggestedAction : ISuggestedAction
    {
        private static readonly Logger L = Log.For("Refactor");
        private readonly IWpfTextView _view;
        private readonly RefactorContext _context;
        private readonly ContextActionItem _item;
        private readonly LightBulbKind _kind;

        public UEToVSSuggestedAction(IWpfTextView view, RefactorContext context, ContextActionItem item, LightBulbKind kind)
        {
            _view = view;
            _context = context;
            _item = item;
            _kind = kind;
        }

        public string DisplayText => _item.Title;
        public bool HasActionSets => false;
        public bool HasPreview => false;

        /// <summary>v0.5.12: 按分组给图标，和 Alt+Enter 菜单的分组标题对应。</summary>
        public ImageMoniker IconMoniker
        {
            get
            {
                switch (_kind)
                {
                    case LightBulbKind.ErrorFix: return KnownMonikers.StatusError;
                    case LightBulbKind.Fix: return KnownMonikers.StatusWarning;
                    case LightBulbKind.Generate: return KnownMonikers.MethodPublic;
                    case LightBulbKind.Other:
                        if (_item.ActionId == EditorFeatureIds.SurroundWith) return KnownMonikers.Snippet;
                        if (_item.ActionId == LightBulbActionIds.OpenGeneratedHeader) return KnownMonikers.CPPHeaderFile;
                        if (_item.ActionId == LightBulbActionIds.GoToLocation) return KnownMonikers.Method;
                        return KnownMonikers.StatusInformation;
                    default: return default;
                }
            }
        }

        public string IconAutomationText => null;

        /// <summary>v0.5.12: 有对应命令的项显示它的快捷键（和 Alt+Enter 菜单一样）。</summary>
        public string InputGestureText => RefactoringFeature.ShortcutOf(_item.ActionId);

        public Task<IEnumerable<SuggestedActionSet>> GetActionSetsAsync(CancellationToken cancellationToken) => Task.FromResult<IEnumerable<SuggestedActionSet>>(null);

        public Task<object> GetPreviewAsync(CancellationToken cancellationToken) => Task.FromResult<object>(null);

        // 等灯泡菜单关掉、焦点回到编辑器后再执行：翻译弹窗在失去焦点时会关闭，Surround 的选择菜单也要定位到编辑器
        public void Invoke(CancellationToken cancellationToken) =>
            UEToVS.Host.Diagnostics.Safe.RunAsync(L, "refactor.lightbulb.invoke", async () =>
            {
                await Task.Delay(30).ConfigureAwait(false);
                await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
                if (!_view.IsClosed) RefactoringFeature.Execute(_item, _view, _context, ActionSource.LightBulb);
            }, ("action", _item.ActionId));

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
