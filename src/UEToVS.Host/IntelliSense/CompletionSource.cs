using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.ComponentModel.Composition;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.VisualStudio.Imaging;
using Microsoft.VisualStudio.Language.Intellisense.AsyncCompletion;
using Microsoft.VisualStudio.Language.Intellisense.AsyncCompletion.Data;
using Microsoft.VisualStudio.Language.StandardClassification;
using Microsoft.VisualStudio.Text;
using Microsoft.VisualStudio.Text.Adornments;
using Microsoft.VisualStudio.Text.Editor;
using Microsoft.VisualStudio.Utilities;
using UEToVS.Contracts;
using UEToVS.Core.Diagnostics;
using UEToVS.Host.Diagnostics;
using UEToVS.Host.Editor;
using UEToVS.Host.Logic.IntelliSense;
using UEToVS.Host.Logic.Localization;
using UEToVS.Host.Logic.Navigation;
using UEToVS.Host.Shell;
using ContractItem = UEToVS.Contracts.CompletionItem;
using VsItem = Microsoft.VisualStudio.Language.Intellisense.AsyncCompletion.Data.CompletionItem;

namespace UEToVS.Host.IntelliSense
{
    /// <summary>Names of the completion MEF parts (for ordering and logs).</summary>
    internal static class CompletionNames
    {
        public const string Source = "UEToVS Completion Source";
        public const string ItemManager = "UEToVS Completion Item Manager";
        public const string CommitManager = "UEToVS Completion Commit Manager";

        /// <summary>Session property: <see cref="CompletionSessionState"/> (set when UEToVS took part in the session).</summary>
        public static readonly object SessionKey = typeof(CompletionSessionState);

        /// <summary>Item property: the contract item behind a VS completion item.</summary>
        public static readonly object ItemKey = typeof(ContractItem);
    }

    /// <summary>What the item / commit managers need to know about UEToVS's part of a completion session.</summary>
    internal sealed class CompletionSessionState
    {
        public CompletionContextKind Context { get; set; }

        /// <summary>Accessor to fix on commit ('.' → '-&gt;' …), tracked so typing after the trigger does not move it.</summary>
        public ITrackingSpan AccessorFixSpan { get; set; }

        public string AccessorFixReplacement { get; set; }

        public IAsyncCompletionSource Source { get; set; }

        public bool ProvidesAll { get; set; }

        public int Items { get; set; }

        /// <summary>Word the engine was asked with when the session started ("U" when typing "UMesh" opened it).</summary>
        public string RequestPrefix { get; set; } = string.Empty;

        /// <summary>The engine's answer was cut (more matches than returned): a longer word must be asked again.</summary>
        public bool Incomplete { get; set; }

        /// <summary>
        /// v0.9.10: items re-fetched by the item manager for a longer word (VS does not reliably ask the source again on
        /// IsIncomplete, so "U" → "UMesh" kept filtering the 150 shortest U* names and never found UMeshComponent).
        /// </summary>
        public RefetchResult Refetch { get; set; }
    }

    internal sealed class RefetchResult
    {
        public string Prefix;
        public ImmutableArray<VsItem> Items;
        public bool Incomplete;
    }

    [Export(typeof(IAsyncCompletionSourceProvider))]
    [Name(CompletionNames.Source)]
    [ContentType(EditorContentTypes.Cpp)]
    internal sealed class UEToVSCompletionSourceProvider : IAsyncCompletionSourceProvider
    {
        public IAsyncCompletionSource GetOrCreate(ITextView textView) =>
            textView.Properties.GetOrCreateSingletonProperty(typeof(UEToVSCompletionSource), () => new UEToVSCompletionSource(textView));
    }

    /// <summary>
    /// UE-aware completion (§7.6) on VS's async completion UI. Starts on identifier typing, '.', '-&gt;', '::',
    /// #include "/&lt;, inside reflection specifier lists and on Ctrl+Space (<see cref="CompletionTriggers"/>); asks the
    /// engine with the triggering snapshot; maps items to KnownMonikers icons with a UE attribute icon, inherited marker
    /// and the detail on the right. Before VS's own C++ IntelliSense is taken over only UE enhancements are offered
    /// (<see cref="CompletionPolicy"/>), so the two lists do not repeat each other. IsIncomplete answers make VS ask again
    /// as the prefix grows. Never throws into the editor: failures end in an empty list and a log entry.
    /// </summary>
    internal sealed class UEToVSCompletionSource : IAsyncCompletionSource
    {
        private static readonly Logger L = Log.For("IntelliSense");
        private readonly ITextView _view;

        public UEToVSCompletionSource(ITextView view) => _view = view;

        public CompletionStartData InitializeCompletion(CompletionTrigger trigger, SnapshotPoint triggerLocation, CancellationToken token)
        {
            return Safe.Execute(L, "is.completion.init", () =>
            {
                if (!IntelliSenseContext.CompletionActive) return CompletionStartData.DoesNotParticipateInCompletion;
                var r = Evaluate(trigger, triggerLocation);
                if (!r.Start || !CompletionPolicy.Participates(r.Hint, IntelliSenseContext.ProvidesAllCompletions))
                    return CompletionStartData.DoesNotParticipateInCompletion;
                var line = triggerLocation.GetContainingLine();
                var span = new SnapshotSpan(triggerLocation.Snapshot, line.Start.Position + r.WordStart, r.WordLength);
                return new CompletionStartData(CompletionParticipation.ProvidesItems, span);
            }, CompletionStartData.DoesNotParticipateInCompletion);
        }

        private static TriggerResult Evaluate(CompletionTrigger trigger, SnapshotPoint location)
        {
            var line = location.GetContainingLine();
            if (line.Length > 4000) return TriggerResult.None;
            bool isExplicit = trigger.Reason == CompletionTriggerReason.Invoke || trigger.Reason == CompletionTriggerReason.InvokeAndCommitIfUnique;
            char typed = trigger.Reason == CompletionTriggerReason.Insertion ? trigger.Character : '\0';
            if (!isExplicit && trigger.Reason != CompletionTriggerReason.Insertion) return TriggerResult.None;
            return CompletionTriggers.Evaluate(line.GetText(), location.Position - line.Start.Position, typed, isExplicit,
                                               IntelliSenseContext.Settings.CompletionOnIdentifier);
        }

        /// <summary>Asks the engine at <paramref name="location"/>; null when it is unavailable or the call failed.</summary>
        internal async Task<(CompletionResponse Resp, ImmutableArray<VsItem> Items)?> FetchAsync(SnapshotPoint location, char triggerChar, bool isExplicit, CancellationToken token)
        {
            var engine = IntelliSenseContext.Engine;
            if (engine == null) return null;
            var capture = IntelliSenseEditor.CaptureAt(_view.TextBuffer, location);
            if (capture == null) return null;
            var settings = IntelliSenseContext.Settings;
            var caret = await Task.Run(capture, token).ConfigureAwait(false);
            var request = new CompletionRequest
            {
                Caret = caret,
                TriggerChar = triggerChar,
                Explicit = isExplicit,
                MaxResults = Math.Max(50, settings.CompletionMaxResults),
                Tolerant = settings.CompletionTolerant,
            };
            var r = await engine.GetCompletionsAsync(request, token).ConfigureAwait(false);
            if (!r.Ok)
            {
                if (isExplicit && r.Status == EngineCallStatus.Unavailable) VsStatusBar.SetText(IntelliSenseStrings.CompletionEngineUnavailable);
                return null;
            }
            var resp = r.Value;
            bool all = IntelliSenseContext.ProvidesAllCompletions;
            var items = ImmutableArray.CreateBuilder<VsItem>(resp.Items?.Count ?? 0);
            int dropped = 0;
            if (resp.Items != null)
                foreach (var it in resp.Items)
                {
                    if (!CompletionPolicy.Keep(it, resp.Context, all))
                    {
                        dropped++;
                        continue;
                    }
                    items.Add(Map(it, resp.Context));
                }
            if (dropped > 0)
                L.Debug("is.completion.filtered", null, ("context", resp.Context.ToString()), ("kept", items.Count), ("dropped", dropped), ("providesAll", all));
            return (resp, items.ToImmutable());
        }

        public async Task<CompletionContext> GetCompletionContextAsync(IAsyncCompletionSession session, CompletionTrigger trigger, SnapshotPoint triggerLocation,
                                                                       SnapshotSpan applicableToSpan, CancellationToken token)
        {
            try
            {
                var t = Evaluate(trigger, triggerLocation);
                bool isExplicit = trigger.Reason == CompletionTriggerReason.Invoke || trigger.Reason == CompletionTriggerReason.InvokeAndCommitIfUnique;
                var settings = IntelliSenseContext.Settings;
                var fetched = await FetchAsync(triggerLocation, t.TriggerChar, isExplicit, token).ConfigureAwait(false);
                if (fetched == null) return CompletionContext.Empty;
                var (resp, items) = fetched.Value;
                bool all = IntelliSenseContext.ProvidesAllCompletions;
                var state = new CompletionSessionState
                {
                    Context = resp.Context, Source = this, ProvidesAll = all, Items = items.Length,
                    RequestPrefix = applicableToSpan.GetText() ?? string.Empty, Incomplete = resp.IsIncomplete,
                };
                if (resp.AccessorFix != null && settings.CompletionAccessorFix) TrackAccessorFix(state, resp.AccessorFix, triggerLocation.Snapshot);
                // v0.9.12 输入即纠正：有把握时打出 '.' / '->' 就改，不等提交；列表照常弹（项目本来就是纠正后的类型的成员）
                var triggerLine = triggerLocation.GetContainingLine();
                if (state.AccessorFixSpan != null && AccessorFixes.ShouldApplyOnType(resp.AccessorFix, t.TriggerChar, triggerLine.LineNumber,
                        triggerLocation.Position - triggerLine.Start.Position, settings.CompletionAccessorFix && settings.CompletionAccessorFixOnType))
                    await ApplyAccessorFixNowAsync(state, token);
                session.Properties[CompletionNames.SessionKey] = state;
                return new CompletionContext(items, null, InitialSelectionHint.RegularSelection,
                                             ImmutableArray<CompletionFilterWithState>.Empty, resp.IsIncomplete);
            }
            catch (OperationCanceledException)
            {
                return CompletionContext.Empty;
            }
            catch (Exception ex)
            {
                L.Error("is.completion.failed", "Completion failed", ex);
                return CompletionContext.Empty;
            }
        }

        /// <summary>
        /// v0.9.12: replaces the tracked accessor right away, as its own edit (one Ctrl+Z brings the typed character back).
        /// The completion session stays open: its applicable span (empty, after the accessor) and the caret move behind the
        /// new accessor. The commit-time fix is dropped so it never runs twice.
        /// </summary>
        private async Task ApplyAccessorFixNowAsync(CompletionSessionState state, CancellationToken token)
        {
            var sw = System.Diagnostics.Stopwatch.StartNew();
            await Microsoft.VisualStudio.Shell.ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync(token);
            string replacement = state.AccessorFixReplacement;
            string problem = null;
            var buffer = _view.TextBuffer;
            var snapshot = buffer.CurrentSnapshot;
            var span = state.AccessorFixSpan.GetSpan(snapshot);
            string current = span.GetText();
            if (_view.IsClosed) problem = "closed";
            else if (!AccessorFixes.IsAccessor(current) || current == replacement) problem = "changed";
            else if (buffer.IsReadOnly(span.Span)) problem = "readonly";
            else
            {
                bool caretAtEnd = _view.Caret.Position.BufferPosition.Position == span.End.Position;
                using (var edit = buffer.CreateEdit())
                {
                    edit.Replace(span, replacement);
                    if (edit.HasEffectiveChanges) snapshot = edit.Apply();
                }
                int end = span.Start.Position + replacement.Length;
                if (caretAtEnd && end <= snapshot.Length && _view.Caret.Position.BufferPosition.Position != end)
                    _view.Caret.MoveTo(new SnapshotPoint(snapshot, end));
                state.AccessorFixSpan = null; // 已经改过，提交时不再改
                // v0.10.1：记下纠正出来的 "->"，紧跟着退格时一次删掉（用户只打了一个 '.'）
                if (replacement == "->" && span.Start.Position + 2 <= snapshot.Length)
                    _view.Properties[FixedArrow.Key] = snapshot.CreateTrackingSpan(span.Start.Position, 2, SpanTrackingMode.EdgeExclusive);
            }
            L.Info("is.completion.accessorFix.onType", null, ("from", current), ("to", replacement), ("applied", problem == null), ("problem", problem),
                   ("ms", Math.Round(sw.Elapsed.TotalMilliseconds, 2)));
        }

        /// <summary>Validates the engine's accessor fix against the trigger snapshot and tracks it for the commit.</summary>
        private static void TrackAccessorFix(CompletionSessionState state, AccessorFix fix, ITextSnapshot snapshot)
        {
            if (fix.Line < 0 || fix.Line >= snapshot.LineCount) return;
            var line = snapshot.GetLineFromLineNumber(fix.Line);
            if (!AccessorFixes.IsValid(fix, line.GetText()))
            {
                L.Info("is.completion.accessorFix.rejected", null, ("line", fix.Line), ("col", fix.Column), ("len", fix.Length), ("replacement", fix.Replacement));
                return;
            }
            state.AccessorFixSpan = snapshot.CreateTrackingSpan(line.Start.Position + fix.Column, fix.Length, SpanTrackingMode.EdgeExclusive);
            state.AccessorFixReplacement = fix.Replacement;
        }

        private VsItem Map(ContractItem it, CompletionContextKind context)
        {
            var icon = IntelliSenseEditor.Image(IntelliSenseEditor.MonikerFor(it, context));
            var attributes = ImmutableArray<ImageElement>.Empty;
            var badge = SymbolPresentation.UeBadge(it.Ue);
            if (badge != null) attributes = attributes.Add(IntelliSenseEditor.Image(KnownMonikers.Attribute, IntelliSenseStrings.UeBadgeTooltip(badge.Value.Text)));
            if (it.IsInherited && !string.IsNullOrEmpty(it.Container))
                attributes = attributes.Add(IntelliSenseEditor.Image(KnownMonikers.Inheritance, IntelliSenseStrings.InheritedFrom(it.Container)));
            string suffix = it.Detail ?? string.Empty;
            if (it.IsInherited && !string.IsNullOrEmpty(it.Container)) suffix = suffix.Length == 0 ? it.Container : suffix + "  · " + it.Container;
            string insert = string.IsNullOrEmpty(it.InsertText) ? it.Label : it.InsertText;
            var item = new VsItem(it.Label, this, icon, ImmutableArray<CompletionFilter>.Empty, suffix, insert, it.Label, it.Label, attributes);
            item.Properties[CompletionNames.ItemKey] = it;
            return item;
        }

        public Task<object> GetDescriptionAsync(IAsyncCompletionSession session, VsItem item, CancellationToken token)
        {
            if (!item.Properties.TryGetProperty(CompletionNames.ItemKey, out ContractItem it)) return Task.FromResult<object>(null);
            return Task.FromResult<object>(Description(it));
        }

        /// <summary>Tooltip next to the list: coloured signature / detail, container, UE reflection, inherited marker.</summary>
        private static object Description(ContractItem it)
        {
            var parts = new List<object>();
            string decl = string.IsNullOrEmpty(it.Detail) ? it.Label : it.Detail.Contains(it.Label) ? it.Detail : it.Detail + " " + it.Label;
            var sym = new SymbolInfo { Name = it.Label, Kind = it.Kind, Ue = it.Ue, Container = it.Container };
            var runs = new List<ClassifiedTextRun>();
            foreach (var t in DeclarationTokenizer.Tokenize(decl, sym)) runs.Add(new ClassifiedTextRun(IntelliSenseEditor.ClassificationFor(t), t.Text));
            parts.Add(new ContainerElement(ContainerElementStyle.Wrapped,
                IntelliSenseEditor.Image(IntelliSenseEditor.MonikerFor(it.Kind, it.Ue)), new ClassifiedTextElement(runs)));
            var badge = SymbolPresentation.UeBadge(it.Ue);
            var info = new List<ClassifiedTextRun>();
            if (badge != null) info.Add(new ClassifiedTextRun(SemanticClassificationNames.UeMacro, badge.Value.Text + "  "));
            if (!string.IsNullOrEmpty(it.Container))
                info.Add(new ClassifiedTextRun(PredefinedClassificationTypeNames.Comment, it.IsInherited ? IntelliSenseStrings.InheritedFrom(it.Container) : it.Container));
            if (it.Location?.FilePath != null)
                info.Add(new ClassifiedTextRun(PredefinedClassificationTypeNames.Comment,
                    (info.Count > 0 ? "  · " : string.Empty) + QuickInfoModel.LocationText(new SymbolInfo { Location = it.Location })));
            if (info.Count > 0) parts.Add(new ClassifiedTextElement(info));
            return new ContainerElement(ContainerElementStyle.Stacked, parts);
        }
    }
}
