using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.ComponentModel.Composition;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.VisualStudio.Language.Intellisense.AsyncCompletion;
using Microsoft.VisualStudio.Language.Intellisense.AsyncCompletion.Data;
using Microsoft.VisualStudio.Text;
using Microsoft.VisualStudio.Text.Editor;
using Microsoft.VisualStudio.Utilities;
using UEToVS.Core.Diagnostics;
using UEToVS.Host.Diagnostics;
using UEToVS.Host.Editor;
using UEToVS.Host.Logic.IntelliSense;
using ContractItem = UEToVS.Contracts.CompletionItem;
using VsItem = Microsoft.VisualStudio.Language.Intellisense.AsyncCompletion.Data.CompletionItem;

namespace UEToVS.Host.IntelliSense
{
    /// <summary>
    /// Sorting and filtering of C++ completion sessions in which UEToVS took part: local re-ranking on every keystroke by
    /// fuzzy match (exact &gt; prefix &gt; camel humps &gt; substring, optional UE prefix) + own / inherited + recently used
    /// + engine score (<see cref="CompletionRanker"/>), matched characters in bold. Items of other sources (VS's own C++
    /// list before the takeover) are ranked too, after ours, and dropped when they repeat one of ours. Sessions without
    /// UEToVS items are handed to VS's default item manager unchanged.
    /// </summary>
    [Export(typeof(IAsyncCompletionItemManagerProvider))]
    [Name(CompletionNames.ItemManager)]
    [ContentType(EditorContentTypes.Cpp)]
    [Order(Before = PredefinedCompletionNames.DefaultCompletionItemManager)]
    internal sealed class UEToVSCompletionItemManagerProvider : IAsyncCompletionItemManagerProvider
    {
        private static readonly Logger L = Log.For("IntelliSense");
        private IAsyncCompletionItemManagerProvider _default;
        private bool _defaultResolved;

        [ImportMany]
        internal IEnumerable<Lazy<IAsyncCompletionItemManagerProvider, IDictionary<string, object>>> Providers { get; set; }

        public IAsyncCompletionItemManager GetOrCreate(ITextView textView)
        {
            if (!_defaultResolved)
            {
                _defaultResolved = true;
                try
                {
                    _default = Providers?.FirstOrDefault(p => p.Metadata != null && p.Metadata.TryGetValue("Name", out var n) &&
                                                             PredefinedCompletionNames.DefaultCompletionItemManager.Equals(n as string ?? (n as string[])?.FirstOrDefault()))?.Value;
                }
                catch (Exception ex)
                {
                    L.Warn("is.completion.defaultManager.failed", "VS's default completion item manager could not be resolved", ex);
                }
                L.Info("is.completion.defaultManager", null, ("found", _default != null));
            }
            return textView.Properties.GetOrCreateSingletonProperty(typeof(UEToVSCompletionItemManager),
                () => new UEToVSCompletionItemManager(_default?.GetOrCreate(textView)));
        }
    }

    internal sealed class UEToVSCompletionItemManager : IAsyncCompletionItemManager
    {
        private static readonly Logger L = Log.For("IntelliSense");
        private readonly IAsyncCompletionItemManager _fallback;

        public UEToVSCompletionItemManager(IAsyncCompletionItemManager fallback) => _fallback = fallback;

        private bool Ours(IAsyncCompletionSession session) =>
            _fallback == null || session.Properties.ContainsProperty(CompletionNames.SessionKey);

        public Task<ImmutableArray<VsItem>> SortCompletionListAsync(IAsyncCompletionSession session, AsyncCompletionSessionInitialDataSnapshot data, CancellationToken token)
        {
            if (!Ours(session)) return _fallback.SortCompletionListAsync(session, data, token);
            try
            {
                // Initial order: engine / origin / recency (the per-keystroke ranking happens in Update).
                var items = Dedupe(data.InitialList);
                var ranked = CompletionRanker.Rank(Candidates(items), string.Empty, IntelliSenseContext.Recent);
                return Task.FromResult(ranked.Select(r => items[r.Index]).ToImmutableArray());
            }
            catch (Exception ex)
            {
                L.Error("is.completion.sort.failed", "Sorting the completion list failed", ex);
                return Task.FromResult(data.InitialList);
            }
        }

        public Task<FilteredCompletionModel> UpdateCompletionListAsync(IAsyncCompletionSession session, AsyncCompletionSessionDataSnapshot data, CancellationToken token)
        {
            if (!Ours(session)) return _fallback.UpdateCompletionListAsync(session, data, token);
            try
            {
                return Task.FromResult(Update(session, data));
            }
            catch (Exception ex)
            {
                L.Error("is.completion.update.failed", "Filtering the completion list failed", ex);
                var all = data.InitialSortedList.Select(i => new CompletionItemWithHighlight(i)).ToImmutableArray();
                return Task.FromResult(new FilteredCompletionModel(all, 0));
            }
        }

        private static FilteredCompletionModel Update(IAsyncCompletionSession session, AsyncCompletionSessionDataSnapshot data)
        {
            string pattern = session.ApplicableToSpan.GetText(data.Snapshot) ?? string.Empty;
            var source = data.InitialSortedList;
            // Filter buttons (when a source supplies them): keep items carrying a selected filter.
            var selected = data.SelectedFilters.IsDefaultOrEmpty ? null
                : data.SelectedFilters.Where(f => f.IsSelected).Select(f => f.Filter).ToList();
            if (selected != null && selected.Count > 0 && selected.Count < data.SelectedFilters.Length)
                source = source.Where(i => !i.Filters.IsDefaultOrEmpty && i.Filters.Any(selected.Contains)).ToImmutableArray();

            var ranked = CompletionRanker.Rank(Candidates(source), pattern, IntelliSenseContext.Recent);
            var builder = ImmutableArray.CreateBuilder<CompletionItemWithHighlight>(ranked.Count);
            foreach (var r in ranked)
            {
                var item = source[r.Index];
                if (r.Ranges.Count == 0 || item.DisplayText != item.FilterText)
                {
                    builder.Add(new CompletionItemWithHighlight(item));
                    continue;
                }
                var spans = ImmutableArray.CreateBuilder<Span>(r.Ranges.Count);
                foreach (var range in r.Ranges)
                    if (range.Start >= 0 && range.End <= item.DisplayText.Length) spans.Add(new Span(range.Start, range.Length));
                builder.Add(new CompletionItemWithHighlight(item, spans.ToImmutable()));
            }
            var list = builder.ToImmutable();
            var filters = data.SelectedFilters.IsDefault ? ImmutableArray<CompletionFilterWithState>.Empty : data.SelectedFilters;
            if (list.Length == 0)
                return new FilteredCompletionModel(list, 0, filters, UpdateSelectionHint.SoftSelected, true, null);
            VsItem unique = list.Length == 1 && data.Trigger.Reason == CompletionTriggerReason.InvokeAndCommitIfUnique ? list[0].CompletionItem : null;
            // Nothing typed yet after '.' / '::': soft-select so Enter / ';' do not commit by accident.
            var hint = pattern.Length == 0 && data.Trigger.Reason == CompletionTriggerReason.Insertion ? UpdateSelectionHint.SoftSelected : UpdateSelectionHint.Selected;
            return new FilteredCompletionModel(list, 0, filters, hint, true, unique);
        }

        /// <summary>Drops other sources' items that repeat one of ours (same display text).</summary>
        private static ImmutableArray<VsItem> Dedupe(ImmutableArray<VsItem> items)
        {
            var ours = new HashSet<string>(StringComparer.Ordinal);
            foreach (var i in items)
                if (i.Properties.ContainsProperty(CompletionNames.ItemKey)) ours.Add(i.DisplayText);
            if (ours.Count == 0) return items;
            return items.Where(i => i.Properties.ContainsProperty(CompletionNames.ItemKey) || !ours.Contains(i.DisplayText)).ToImmutableArray();
        }

        private static List<RankCandidate> Candidates(ImmutableArray<VsItem> items)
        {
            var list = new List<RankCandidate>(items.Length);
            foreach (var i in items)
            {
                if (i.Properties.TryGetProperty(CompletionNames.ItemKey, out ContractItem c))
                    list.Add(new RankCandidate { FilterText = i.FilterText, EngineScore = c.Score, Inherited = c.IsInherited, Keyword = c.IsKeyword });
                else
                    list.Add(new RankCandidate { FilterText = i.FilterText, Foreign = true });
            }
            return list;
        }
    }

    /// <summary>
    /// Commit of UEToVS items: replaces the typed word with the item's insert text and, in the same undoable edit, applies
    /// the VA accessor fix ('.' → '-&gt;' for pointers, the reverse for values; setting "CompletionAccessorFix"), after
    /// re-checking the tracked accessor still reads '.' / '-&gt;'. Records the item as recently used. Items of other
    /// sources are left to their own commit managers.
    /// </summary>
    [Export(typeof(IAsyncCompletionCommitManagerProvider))]
    [Name(CompletionNames.CommitManager)]
    [ContentType(EditorContentTypes.Cpp)]
    internal sealed class UEToVSCompletionCommitManagerProvider : IAsyncCompletionCommitManagerProvider
    {
        public IAsyncCompletionCommitManager GetOrCreate(ITextView textView) =>
            textView.Properties.GetOrCreateSingletonProperty(typeof(UEToVSCompletionCommitManager), () => new UEToVSCompletionCommitManager());
    }

    internal sealed class UEToVSCompletionCommitManager : IAsyncCompletionCommitManager
    {
        private static readonly Logger L = Log.For("IntelliSense");

        /// <summary>C++ punctuation that commits the selected item and is then typed (no space: VA does not commit on space).</summary>
        private static readonly ImmutableArray<char> Chars = "();,.:<>[]{}=+-*/&|!?~^%\"".ToImmutableArray();

        public IEnumerable<char> PotentialCommitCharacters => Chars;

        public bool ShouldCommitCompletion(IAsyncCompletionSession session, SnapshotPoint location, char typedChar, CancellationToken token)
        {
            if (!session.Properties.TryGetProperty(CompletionNames.SessionKey, out CompletionSessionState state)) return false;
            // Include paths: '/' continues the path, '.' is part of "Foo.h".
            if (state.Context == UEToVS.Contracts.CompletionContextKind.Include) return typedChar == '"' || typedChar == '>';
            // Specifier lists: '=' starts a value (Category=…), ',' / ')' end the specifier.
            return Chars.Contains(typedChar);
        }

        public CommitResult TryCommit(IAsyncCompletionSession session, ITextBuffer buffer, VsItem item, char typedChar, CancellationToken token)
        {
            if (!item.Properties.TryGetProperty(CompletionNames.ItemKey, out ContractItem contract)) return CommitResult.Unhandled;
            session.Properties.TryGetProperty(CompletionNames.SessionKey, out CompletionSessionState state);
            return Safe.Execute(L, "is.completion.commit", () => Commit(session, buffer, item, contract, state, typedChar), CommitResult.Unhandled);
        }

        private static CommitResult Commit(IAsyncCompletionSession session, ITextBuffer buffer, VsItem item, ContractItem contract,
                                           CompletionSessionState state, char typedChar)
        {
            var snapshot = buffer.CurrentSnapshot;
            var span = session.ApplicableToSpan.GetSpan(snapshot);
            string insert = item.InsertText ?? item.DisplayText;
            // A typed '(' follows the commit: do not double the parentheses the insert text brings ("BeginPlay()").
            if (typedChar == '(' && insert.EndsWith("()", StringComparison.Ordinal)) insert = insert.Substring(0, insert.Length - 2);
            bool fixApplied = false;
            using (var edit = buffer.CreateEdit())
            {
                if (span.GetText() != insert) edit.Replace(span, insert);
                if (state?.AccessorFixSpan != null && IntelliSenseContext.Settings.CompletionAccessorFix)
                {
                    var fix = state.AccessorFixSpan.GetSpan(snapshot);
                    string current = fix.GetText();
                    if (AccessorFixes.IsAccessor(current) && AccessorFixes.IsAccessor(state.AccessorFixReplacement) && current != state.AccessorFixReplacement &&
                        fix.End.Position <= span.Start.Position)
                    {
                        edit.Replace(fix, state.AccessorFixReplacement);
                        fixApplied = true;
                    }
                }
                if (edit.HasEffectiveChanges) edit.Apply();
            }
            IntelliSenseContext.Recent.Record(item.FilterText);
            L.Info("is.completion.commit", null, ("label", contract.Label), ("kind", contract.Kind.ToString()), ("context", state?.Context.ToString()),
                   ("typedChar", typedChar == '\0' ? null : typedChar.ToString()), ("accessorFix", fixApplied ? state.AccessorFixReplacement : null),
                   ("inherited", contract.IsInherited), ("ue", contract.Ue.ToString()));
            return new CommitResult(true, CommitBehavior.None);
        }
    }
}
