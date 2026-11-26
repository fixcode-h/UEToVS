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

        public async Task<FilteredCompletionModel> UpdateCompletionListAsync(IAsyncCompletionSession session, AsyncCompletionSessionDataSnapshot data, CancellationToken token)
        {
            if (!Ours(session)) return await _fallback.UpdateCompletionListAsync(session, data, token).ConfigureAwait(false);
            try
            {
                var source = await SourceItemsAsync(session, data, token).ConfigureAwait(false);
                return Update(session, data, source);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                L.Error("is.completion.update.failed", "Filtering the completion list failed", ex);
                var all = data.InitialSortedList.Select(i => new CompletionItemWithHighlight(i)).ToImmutableArray();
                return new FilteredCompletionModel(all, 0);
            }
        }

        /// <summary>
        /// The items to filter: the session's initial list, or — when the engine's first answer was cut and the word has
        /// grown since ("U" → "UMesh") — a fresh engine answer for the current word plus the other sources' items. VS
        /// does not reliably ask the source again for IsIncomplete lists, so without this the list kept filtering the
        /// first answer (the 150 shortest U* names) and never offered UMeshComponent. A fresh answer that was complete
        /// keeps serving longer words of the same start without another request.
        /// </summary>
        private static async Task<ImmutableArray<VsItem>> SourceItemsAsync(IAsyncCompletionSession session, AsyncCompletionSessionDataSnapshot data, CancellationToken token)
        {
            var initial = data.InitialSortedList;
            if (!session.Properties.TryGetProperty(CompletionNames.SessionKey, out CompletionSessionState state) || !state.Incomplete) return initial;
            if (!(state.Source is UEToVSCompletionSource source)) return initial;
            var span = CompletionWordSpan.Of(session, data.Snapshot, state);
            string word = span.GetText() ?? string.Empty;
            if (word.Length == 0 || string.Equals(word, state.RequestPrefix, StringComparison.Ordinal)) return initial;
            var cached = state.Refetch;
            if (cached == null || !Answers(cached, word))
            {
                var sw = System.Diagnostics.Stopwatch.StartNew();
                var fetched = await source.FetchAsync(span.End, '\0', false, token).ConfigureAwait(false);
                if (fetched == null) return cached?.Items.IsDefault == false ? Merge(initial, cached.Items) : initial;
                cached = new RefetchResult { Prefix = word, Items = fetched.Value.Items, Incomplete = fetched.Value.Resp.IsIncomplete };
                state.Refetch = cached;
                L.Info("is.completion.refetch", null, ("from", state.RequestPrefix), ("word", word), ("items", cached.Items.Length),
                       ("incomplete", cached.Incomplete), ("ms", sw.ElapsedMilliseconds));
            }
            return Merge(initial, cached.Items);
        }

        /// <summary>A re-fetch answers the word when it was asked with exactly it, or was complete and the word only grew.</summary>
        internal static bool Answers(RefetchResult r, string word) =>
            string.Equals(r.Prefix, word, StringComparison.Ordinal) ||
            !r.Incomplete && !(IntelliSenseContext.Settings?.CompletionTolerant ?? true) && word.StartsWith(r.Prefix, StringComparison.OrdinalIgnoreCase);

        /// <summary>Fresh UEToVS items plus the other sources' items of the initial list.</summary>
        private static ImmutableArray<VsItem> Merge(ImmutableArray<VsItem> initial, ImmutableArray<VsItem> ours)
        {
            var b = ImmutableArray.CreateBuilder<VsItem>(initial.Length + ours.Length);
            b.AddRange(ours);
            foreach (var i in initial)
                if (!i.Properties.ContainsProperty(CompletionNames.ItemKey)) b.Add(i);
            return Dedupe(b.ToImmutable());
        }

        private static FilteredCompletionModel Update(IAsyncCompletionSession session, AsyncCompletionSessionDataSnapshot data, ImmutableArray<VsItem> source)
        {
            session.Properties.TryGetProperty(CompletionNames.SessionKey, out CompletionSessionState state);
            string pattern = CompletionWordSpan.Of(session, data.Snapshot, state).GetText() ?? string.Empty;
            // Filter buttons (when a source supplies them): keep items carrying a selected filter.
            var selected = data.SelectedFilters.IsDefaultOrEmpty ? null
                : data.SelectedFilters.Where(f => f.IsSelected).Select(f => f.Filter).ToList();
            if (selected != null && selected.Count > 0 && selected.Count < data.SelectedFilters.Length)
                source = source.Where(i => !i.Filters.IsDefaultOrEmpty && i.Filters.Any(selected.Contains)).ToImmutableArray();

            var settings = IntelliSenseContext.Settings;
            var ranked = CompletionRanker.Rank(Candidates(source), pattern, IntelliSenseContext.Recent,
                                               settings?.CompletionCaseMode ?? Logic.Settings.CompletionCaseMode.Prefer, settings?.CompletionTolerant ?? true);
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
            // Nothing typed yet: soft-select so Enter / ';' do not commit by accident — except after '.' / '->' / '::'
            // (v0.10.1, like VA / Rider): the list is the members of that type, so the first one is selected and Enter takes it.
            bool members = state?.Context == UEToVS.Contracts.CompletionContextKind.Member || state?.Context == UEToVS.Contracts.CompletionContextKind.Scope;
            var hint = pattern.Length == 0 && data.Trigger.Reason == CompletionTriggerReason.Insertion && !members ? UpdateSelectionHint.SoftSelected : UpdateSelectionHint.Selected;
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
            // String contexts (tags, asset paths, UFUNCTION names, ini): the closing quote and the kind's separator.
            var inString = CompletionPolicy.CommitsInString(state.Context, typedChar);
            if (inString.HasValue) return inString.Value;
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
            var span = CompletionWordSpan.Of(session, snapshot, state);
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
            if (state?.Context == UEToVS.Contracts.CompletionContextKind.Include) CheckIncludeDependency(buffer, span.Start.Position);
            string autoInclude = !string.IsNullOrEmpty(contract.AddInclude) && IntelliSenseContext.Settings.CompletionAutoInclude ? AddInclude(buffer, contract.AddInclude) : null;
            L.Info("is.completion.commit", null, ("label", contract.Label), ("kind", contract.Kind.ToString()), ("context", state?.Context.ToString()),
                   ("typedChar", typedChar == '\0' ? null : typedChar.ToString()), ("accessorFix", fixApplied ? state.AccessorFixReplacement : null),
                   ("inherited", contract.IsInherited), ("ue", contract.Ue.ToString()), ("autoInclude", autoInclude));
            return new CommitResult(true, CommitBehavior.None);
        }

        /// <summary>
        /// v0.11.0 (2.4): the committed type / function lives in a header this file does not reach yet: add the #include where
        /// "Add Include" would (an undo step of its own) and offer the Build.cs dependency. Null when nothing was added.
        /// </summary>
        private static string AddInclude(ITextBuffer buffer, string include)
        {
            try
            {
                string text = buffer.CurrentSnapshot.GetText();
                if (UEToVS.Host.Logic.Refactoring.IncludePlanner.AlreadyIncluded(text, include)) return null;
                string path = UEToVS.Host.Navigation.VsEditor.FilePathOf(buffer);
                var point = UEToVS.Host.Logic.Refactoring.IncludePlanner.Plan(text, include);
                int applied = UEToVS.Host.Refactoring.RefactorEdits.ApplyToBuffer(buffer, new[] { UEToVS.Host.Logic.Refactoring.TextEdit.Insert(path, point.Line, point.Text) }, out _);
                if (applied == 0) return null;
                UEToVS.Host.Shell.VsStatusBar.SetText(UEToVS.Host.Logic.Localization.RefactorStrings.IncludeAdded(include));
                UEToVS.Host.Unreal.ModuleDependencyFixer.OnIncludeCommitted(HostPackage.Instance, path, include, point.Line);
                return include;
            }
            catch (Exception ex)
            {
                L.Warn("is.completion.autoInclude.failed", "Adding the #include on commit failed", ex, ("include", include));
                return null;
            }
        }

        /// <summary>
        /// §7.6: a header committed into an #include whose module the current module does not depend on → the Build.cs
        /// banner (add to Public/PrivateDependencyModuleNames) right away instead of on the next save.
        /// </summary>
        private static void CheckIncludeDependency(ITextBuffer buffer, int position)
        {
            var snapshot = buffer.CurrentSnapshot;
            if (position > snapshot.Length) return;
            string include = UEToVS.Core.Unreal.IncludeDirective.Parse(snapshot.GetLineFromPosition(position).GetText());
            if (string.IsNullOrEmpty(include) || !UEToVS.Host.Logic.Unreal.IncludeDependencyChecker.IsCppFile(include)) return;
            string path = UEToVS.Host.Navigation.VsEditor.FilePathOf(buffer);
            UEToVS.Host.Unreal.ModuleDependencyFixer.OnIncludeCommitted(HostPackage.Instance, path, include, snapshot.GetLineNumberFromPosition(position));
        }
    }

    /// <summary>
    /// The word a UEToVS session filters on and replaces: its applicable span, minus an accessor the span swallowed when the
    /// on-type fix replaced '.' with '-&gt;' right in front of it (v0.10.1, <see cref="AccessorFixes.LeadingAccessorLength"/>).
    /// Only member sessions: an include path may legitimately start with '.'.
    /// </summary>
    internal static class CompletionWordSpan
    {
        public static SnapshotSpan Of(IAsyncCompletionSession session, ITextSnapshot snapshot, CompletionSessionState state)
        {
            var span = session.ApplicableToSpan.GetSpan(snapshot);
            if (state?.Context != UEToVS.Contracts.CompletionContextKind.Member || span.Length == 0) return span;
            int skip = AccessorFixes.LeadingAccessorLength(span.GetText());
            return skip == 0 ? span : new SnapshotSpan(span.Start + skip, span.End);
        }
    }
}
