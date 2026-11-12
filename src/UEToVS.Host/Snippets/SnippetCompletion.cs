using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.ComponentModel.Composition;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.VisualStudio;
using Microsoft.VisualStudio.Editor;
using Microsoft.VisualStudio.Imaging;
using Microsoft.VisualStudio.Language.Intellisense.AsyncCompletion;
using Microsoft.VisualStudio.Language.Intellisense.AsyncCompletion.Data;
using Microsoft.VisualStudio.Language.StandardClassification;
using Microsoft.VisualStudio.OLE.Interop;
using Microsoft.VisualStudio.Text;
using Microsoft.VisualStudio.Text.Adornments;
using Microsoft.VisualStudio.Text.Editor;
using Microsoft.VisualStudio.TextManager.Interop;
using Microsoft.VisualStudio.Utilities;
using UEToVS.Core.Diagnostics;
using UEToVS.Host.Diagnostics;
using UEToVS.Host.Editor;
using UEToVS.Host.IntelliSense;
using UEToVS.Host.Logic.Snippets;
using VsItem = Microsoft.VisualStudio.Language.Intellisense.AsyncCompletion.Data.CompletionItem;

namespace UEToVS.Host.Snippets
{
    internal static class SnippetNames
    {
        public const string Source = "UEToVS Snippet Completion Source";
        public const string CommitManager = "UEToVS Snippet Commit Manager";

        /// <summary>Item property: <see cref="SnippetTemplate"/> or <see cref="PostfixTemplate"/>.</summary>
        public static readonly object ItemKey = typeof(SnippetNames);

        public static bool SnippetsActive => IntelliSenseContext.IsUnrealSolution && IntelliSenseContext.Settings.SnippetsEnabled;

        public static bool PostfixActive => IntelliSenseContext.IsUnrealSolution && IntelliSenseContext.Settings.PostfixTemplatesEnabled;

        /// <summary>Expands a live template whose abbreviation spans <paramref name="word"/>.</summary>
        public static void ExpandSnippet(ITextView view, SnapshotSpan word, SnippetTemplate t, string trigger)
        {
            var line = word.Start.GetContainingLine();
            var (unit, nl) = SnippetSession.Format(view, line);
            string indent = SnippetExpander.LeadingWhitespace(line.GetText());
            var e = SnippetExpander.Expand(t, indent, unit, nl, EditorServices.FilePath(view.TextBuffer));
            SnippetSession.Insert(view, word, e, "snippet:" + trigger, t.Shortcut);
        }

        /// <summary>Replaces <c>expr.key</c> (from the expression start to <paramref name="end"/>) with the postfix template.</summary>
        public static bool ExpandPostfix(ITextView view, SnapshotPoint end, PostfixTemplate t)
        {
            var line = end.GetContainingLine();
            string text = line.GetText();
            var site = PostfixTemplates.Analyze(text, end.Position - line.Start.Position);
            if (site == null) return false;
            var (unit, nl) = SnippetSession.Format(view, line);
            var e = SnippetExpander.Expand(PostfixTemplates.Body(t, site.Expression), SnippetExpander.LeadingWhitespace(text), unit, nl, null);
            var span = new SnapshotSpan(line.Start + site.ExpressionStart, end);
            SnippetSession.Insert(view, span, e, "postfix", t.Key);
            return true;
        }
    }

    [Export(typeof(IAsyncCompletionSourceProvider))]
    [Name(SnippetNames.Source)]
    [ContentType(EditorContentTypes.Cpp)]
    internal sealed class SnippetCompletionSourceProvider : IAsyncCompletionSourceProvider
    {
        public IAsyncCompletionSource GetOrCreate(ITextView textView) =>
            textView.Properties.GetOrCreateSingletonProperty(typeof(SnippetCompletionSource), () => new SnippetCompletionSource());
    }

    /// <summary>
    /// UE live templates at line start and postfix templates after <c>expr.</c>, merged into the completion list (Rider style).
    /// Purely line-local: no engine request, nothing computed when the line does not match.
    /// </summary>
    internal sealed class SnippetCompletionSource : IAsyncCompletionSource
    {
        private static readonly Logger L = Log.For("Snippets");

        public CompletionStartData InitializeCompletion(CompletionTrigger trigger, SnapshotPoint triggerLocation, CancellationToken token)
        {
            return Safe.Execute(L, "snippets.completion.init", () =>
            {
                if (trigger.Reason != CompletionTriggerReason.Insertion && trigger.Reason != CompletionTriggerReason.Invoke &&
                    trigger.Reason != CompletionTriggerReason.InvokeAndCommitIfUnique) return CompletionStartData.DoesNotParticipateInCompletion;
                var line = triggerLocation.GetContainingLine();
                if (line.Length > 2000) return CompletionStartData.DoesNotParticipateInCompletion;
                string text = line.GetText();
                int caret = triggerLocation.Position - line.Start.Position;
                if (SnippetNames.PostfixActive)
                {
                    var site = PostfixTemplates.Analyze(text, caret);
                    if (site != null && Any(PostfixTemplates.For(site)))
                        return new CompletionStartData(CompletionParticipation.ProvidesItems, new SnapshotSpan(line.Start + site.Dot + 1, triggerLocation));
                }
                if (SnippetNames.SnippetsActive && trigger.Character != '.' && trigger.Character != ':' && trigger.Character != '>')
                {
                    int s = SnippetCatalog.ShortcutAtLineStart(text, caret, out string word);
                    if (s >= 0 && !PostfixTemplates.InCommentOrString(text, s) && Any(SnippetCatalog.Matching(word)))
                        return new CompletionStartData(CompletionParticipation.ProvidesItems, new SnapshotSpan(line.Start + s, triggerLocation));
                }
                return CompletionStartData.DoesNotParticipateInCompletion;
            }, CompletionStartData.DoesNotParticipateInCompletion);
        }

        private static bool Any<T>(IEnumerable<T> items)
        {
            foreach (var _ in items) return true;
            return false;
        }

        public Task<CompletionContext> GetCompletionContextAsync(IAsyncCompletionSession session, CompletionTrigger trigger, SnapshotPoint triggerLocation,
                                                                 SnapshotSpan applicableToSpan, CancellationToken token)
        {
            return Task.FromResult(Safe.Execute(L, "snippets.completion.items", () =>
            {
                var items = ImmutableArray.CreateBuilder<VsItem>();
                var line = triggerLocation.GetContainingLine();
                string text = line.GetText();
                int caret = triggerLocation.Position - line.Start.Position;
                var site = SnippetNames.PostfixActive ? PostfixTemplates.Analyze(text, caret) : null;
                if (site != null)
                {
                    foreach (var t in PostfixTemplates.All)
                        if (site.AtLineStart || !t.Statement) items.Add(Item(t.Key, t.Title, t));
                }
                else if (SnippetNames.SnippetsActive && SnippetCatalog.ShortcutAtLineStart(text, caret, out _) >= 0)
                {
                    foreach (var t in SnippetCatalog.BuiltIn) items.Add(Item(t.Shortcut, t.Title, t));
                }
                return items.Count == 0 ? CompletionContext.Empty : new CompletionContext(items.ToImmutable());
            }, CompletionContext.Empty));
        }

        private VsItem Item(string label, string title, object template)
        {
            var item = new VsItem(label, this, IntelliSenseEditor.Image(KnownMonikers.Snippet), ImmutableArray<CompletionFilter>.Empty, title);
            item.Properties[SnippetNames.ItemKey] = template;
            return item;
        }

        public Task<object> GetDescriptionAsync(IAsyncCompletionSession session, VsItem item, CancellationToken token)
        {
            if (!item.Properties.TryGetProperty(SnippetNames.ItemKey, out object t)) return Task.FromResult<object>(null);
            string preview = t is SnippetTemplate s ? s.Preview : t is PostfixTemplate p ? p.Title : null;
            string title = t is SnippetTemplate s2 ? s2.Title : null;
            var runs = new List<object>();
            if (title != null) runs.Add(new ClassifiedTextElement(new ClassifiedTextRun(PredefinedClassificationTypeNames.Comment, title)));
            if (preview != null) runs.Add(new ClassifiedTextElement(new ClassifiedTextRun(PredefinedClassificationTypeNames.Text, preview)));
            return Task.FromResult<object>(new ContainerElement(ContainerElementStyle.Stacked, runs));
        }
    }

    [Export(typeof(IAsyncCompletionCommitManagerProvider))]
    [Name(SnippetNames.CommitManager)]
    [ContentType(EditorContentTypes.Cpp)]
    internal sealed class SnippetCommitManagerProvider : IAsyncCompletionCommitManagerProvider
    {
        public IAsyncCompletionCommitManager GetOrCreate(ITextView textView) =>
            textView.Properties.GetOrCreateSingletonProperty(typeof(SnippetCommitManager), () => new SnippetCommitManager(textView));
    }

    /// <summary>Commits template items by expanding them (other items are left to the other commit managers).</summary>
    internal sealed class SnippetCommitManager : IAsyncCompletionCommitManager
    {
        private static readonly Logger L = Log.For("Snippets");
        private readonly ITextView _view;

        public SnippetCommitManager(ITextView view) => _view = view;

        public IEnumerable<char> PotentialCommitCharacters => Array.Empty<char>();

        public bool ShouldCommitCompletion(IAsyncCompletionSession session, SnapshotPoint location, char typedChar, CancellationToken token) => false;

        public CommitResult TryCommit(IAsyncCompletionSession session, ITextBuffer buffer, VsItem item, char typedChar, CancellationToken token)
        {
            if (!item.Properties.TryGetProperty(SnippetNames.ItemKey, out object t)) return CommitResult.Unhandled;
            return Safe.Execute(L, "snippets.commit", () =>
            {
                var span = session.ApplicableToSpan.GetSpan(buffer.CurrentSnapshot);
                if (t is SnippetTemplate s)
                {
                    SnippetNames.ExpandSnippet(_view, span, s, "list");
                    return new CommitResult(true, CommitBehavior.CancelCommit);
                }
                if (t is PostfixTemplate p && SnippetNames.ExpandPostfix(_view, span.End, p)) return new CommitResult(true, CommitBehavior.CancelCommit);
                return CommitResult.Unhandled;
            }, CommitResult.Unhandled);
        }
    }

    [Export(typeof(IVsTextViewCreationListener))]
    [ContentType(EditorContentTypes.Cpp)]
    [TextViewRole(PredefinedTextViewRoles.Document)]
    internal sealed class SnippetCommandFilterProvider : IVsTextViewCreationListener
    {
        private static readonly Logger L = Log.For("Snippets");

        [Import]
        internal IVsEditorAdaptersFactoryService Adapters { get; set; }

        [Import]
        internal IAsyncCompletionBroker CompletionBroker { get; set; }

        public void VsTextViewCreated(IVsTextView textViewAdapter) => Safe.Execute(L, "snippets.filter.attach", () =>
        {
            var view = Adapters?.GetWpfTextView(textViewAdapter);
            if (view == null) return;
            var filter = new SnippetCommandFilter(view, CompletionBroker);
            if (ErrorHandler.Succeeded(textViewAdapter.AddCommandFilter(filter, out IOleCommandTarget next))) filter.Next = next;
        });
    }

    /// <summary>Tab expands an abbreviation at line start; while a template session runs Tab / Shift+Tab / Enter / Esc drive its fields.</summary>
    internal sealed class SnippetCommandFilter : IOleCommandTarget
    {
        private static readonly Logger L = Log.For("Snippets");
        private readonly IWpfTextView _view;
        private readonly IAsyncCompletionBroker _completion;

        public SnippetCommandFilter(IWpfTextView view, IAsyncCompletionBroker completion)
        {
            _view = view;
            _completion = completion;
        }

        public IOleCommandTarget Next { get; set; }

        public int QueryStatus(ref Guid pguidCmdGroup, uint cCmds, OLECMD[] prgCmds, IntPtr pCmdText) =>
            Next?.QueryStatus(ref pguidCmdGroup, cCmds, prgCmds, pCmdText) ?? (int)Constants.OLECMDERR_E_NOTSUPPORTED;

        public int Exec(ref Guid pguidCmdGroup, uint nCmdID, uint nCmdexecopt, IntPtr pvaIn, IntPtr pvaOut)
        {
            if (pguidCmdGroup == VSConstants.VSStd2K && Handle((VSConstants.VSStd2KCmdID)nCmdID)) return VSConstants.S_OK;
            return Next?.Exec(ref pguidCmdGroup, nCmdID, nCmdexecopt, pvaIn, pvaOut) ?? (int)Constants.OLECMDERR_E_NOTSUPPORTED;
        }

        private bool Handle(VSConstants.VSStd2KCmdID cmd)
        {
            if (cmd != VSConstants.VSStd2KCmdID.TAB && cmd != VSConstants.VSStd2KCmdID.BACKTAB && cmd != VSConstants.VSStd2KCmdID.RETURN &&
                cmd != VSConstants.VSStd2KCmdID.CANCEL) return false;
            if (_completion != null && _completion.IsCompletionActive(_view)) return false;
            return Safe.Execute(L, "snippets.key", () =>
            {
                var session = SnippetSession.Current(_view);
                if (session != null)
                {
                    switch (cmd)
                    {
                        case VSConstants.VSStd2KCmdID.TAB: return session.Next();
                        case VSConstants.VSStd2KCmdID.BACKTAB: return session.Previous();
                        case VSConstants.VSStd2KCmdID.RETURN:
                            session.Finish(true, "enter");
                            return true;
                        default:
                            session.Finish(false, "escape");
                            return false;
                    }
                }
                if (cmd != VSConstants.VSStd2KCmdID.TAB || !SnippetNames.SnippetsActive || !_view.Selection.IsEmpty) return false;
                var caret = _view.Caret.Position.BufferPosition;
                var line = caret.GetContainingLine();
                int s = SnippetCatalog.ShortcutAtLineStart(line.GetText(), caret.Position - line.Start.Position, out string word);
                var t = s >= 0 ? SnippetCatalog.Find(word) : null;
                if (t == null) return false;
                SnippetNames.ExpandSnippet(_view, new SnapshotSpan(line.Start + s, caret), t, "tab");
                return true;
            }, false, ("command", cmd.ToString()));
        }
    }
}
