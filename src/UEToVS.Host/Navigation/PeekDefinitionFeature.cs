using System;
using System.Collections.Generic;
using System.ComponentModel.Composition;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.VisualStudio;
using Microsoft.VisualStudio.Editor;
using Microsoft.VisualStudio.Language.Intellisense;
using Microsoft.VisualStudio.OLE.Interop;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Text;
using Microsoft.VisualStudio.Text.Editor;
using Microsoft.VisualStudio.TextManager.Interop;
using Microsoft.VisualStudio.Utilities;
using UEToVS.Contracts;
using UEToVS.Core.Diagnostics;
using UEToVS.Host.Editor;
using UEToVS.Host.Integration;
using UEToVS.Host.IntelliSense;
using UEToVS.Host.Logic.Localization;
using UEToVS.Host.Logic.Navigation;
using UEToVS.Host.Shell;
using Safe = UEToVS.Host.Diagnostics.Safe;

namespace UEToVS.Host.Navigation
{
    // ---- v0.4.5 navigation gaps
    /// <summary>
    /// Peek Definition (Alt+F12) after the IntelliSense takeover: VS's own C++ peek has nothing to work with then, so the
    /// command filter below asks the engine for the definition(s) and opens VS's peek window (IPeekBroker) on them through
    /// <see cref="UeToVsPeekableItemSource"/>. Without the takeover nothing is intercepted and VS behaves as before.
    /// </summary>
    internal static class PeekDefinitionFeature
    {
        private static readonly Logger L = Log.For("Navigation");
        private static readonly LatestRequestGate Gate = new LatestRequestGate();

        /// <summary>Key in ITextView.Properties of the results the next peek session of that view shows.</summary>
        internal static readonly object PendingKey = typeof(PeekDefinitionFeature);

        internal static IPeekBroker Broker { get; set; }

        internal static IPeekResultFactory ResultFactory { get; set; }

        /// <summary>Takeover on, Unreal solution: Alt+F12 is ours.</summary>
        internal static bool Active => IntelliSenseContext.IsUnrealSolution && IntelliSenseContext.TakenOver;

        public static async Task ExecuteAsync(string source, IWpfTextView view = null)
        {
            await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
            view = view ?? EditorServices.ActiveView();
            if (view == null || view.IsClosed || !EditorServices.IsCpp(view.TextBuffer))
            {
                VsStatusBar.SetText(NavStrings.NoActiveCppEditor);
                return;
            }
            var (gen, ct) = Gate.Begin(NavigationContext.Disposal);
            var caret = await EditorServices.CaretRequestAsync(view);
            if (caret == null) return;
            var engine = NavigationContext.Engine;
            var r = engine == null
                ? EngineCallResult<NavigationResponse>.Fail(EngineCallStatus.Unavailable, 0)
                : await engine.NavigateAsync(new NavigationRequest { Caret = caret, Kind = NavigationKind.Definition }, ct);
            await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
            if (!Gate.IsCurrent(gen) || view.IsClosed) return;
            if (r.Status != EngineCallStatus.Ok)
            {
                string msg = EngineCallText.ForStatus(r.Status);
                if (msg != null) VsStatusBar.SetText(msg);
                L.Info("navigation.peek", null, ("source", source), ("status", r.Status.ToString()));
                return;
            }
            var targets = (r.Value.Targets ?? new List<NavigationTarget>())
                .Where(t => t?.Symbol?.Location != null && !string.IsNullOrEmpty(t.Symbol.Location.FilePath) && File.Exists(t.Symbol.Location.FilePath))
                .ToList();
            L.Info("navigation.peek", null, ("source", source), ("word", r.Value.Word), ("targets", targets.Count), ("reason", r.Value.Reason),
                   ("engineMs", r.Value.ElapsedMs), ("broker", Broker != null));
            if (targets.Count == 0)
            {
                VsStatusBar.SetText(NavGapStrings.PeekNoDefinition(r.Value.Word ?? "?"));
                return;
            }
            if (Broker == null || ResultFactory == null)
            {
                // No peek services (should not happen in VS 2022): fall back to a plain jump.
                var loc = NavigationPresenters.ToLocation(targets[0].Symbol.Location);
                if (loc != null) await NavigationService.Instance.JumpToAsync(loc, "peek");
                return;
            }
            view.Properties[PendingKey] = new PendingPeek(r.Value.Word, targets);
            Broker.TriggerPeekSession(view, PredefinedPeekRelationships.Definitions.Name);
        }

        internal sealed class PendingPeek
        {
            public PendingPeek(string word, IReadOnlyList<NavigationTarget> targets)
            {
                Word = word;
                Targets = targets;
            }

            public string Word { get; }
            public IReadOnlyList<NavigationTarget> Targets { get; }
        }
    }

    /// <summary>Hands VS's peek services to <see cref="PeekDefinitionFeature"/>; supplies our items to peek sessions of C++ buffers.</summary>
    [Export(typeof(IPeekableItemSourceProvider))]
    [ContentType(EditorContentTypes.Cpp)]
    [Name("UEToVS.PeekDefinition")]
    [SupportsStandaloneFiles(true)]
    internal sealed class UeToVsPeekableItemSourceProvider : IPeekableItemSourceProvider
    {
        [ImportingConstructor]
        public UeToVsPeekableItemSourceProvider(IPeekBroker broker, IPeekResultFactory factory)
        {
            PeekDefinitionFeature.Broker = broker;
            PeekDefinitionFeature.ResultFactory = factory;
        }

        public IPeekableItemSource TryCreatePeekableItemSource(ITextBuffer textBuffer) => new UeToVsPeekableItemSource();
    }

    /// <summary>Adds our item only to the session that <see cref="PeekDefinitionFeature"/> triggered (results waiting on the view).</summary>
    internal sealed class UeToVsPeekableItemSource : IPeekableItemSource
    {
        public void AugmentPeekSession(IPeekSession session, IList<IPeekableItem> peekableItems)
        {
            if (session == null || peekableItems == null) return;
            if (!string.Equals(session.RelationshipName, PredefinedPeekRelationships.Definitions.Name, StringComparison.Ordinal)) return;
            var props = session.TextView?.Properties;
            if (props == null || !props.TryGetProperty(PeekDefinitionFeature.PendingKey, out PeekDefinitionFeature.PendingPeek pending)) return;
            props.RemoveProperty(PeekDefinitionFeature.PendingKey);
            peekableItems.Add(new UeToVsPeekableItem(pending));
        }

        public void Dispose() { }
    }

    internal sealed class UeToVsPeekableItem : IPeekableItem, IPeekResultSource
    {
        private readonly PeekDefinitionFeature.PendingPeek _pending;

        public UeToVsPeekableItem(PeekDefinitionFeature.PendingPeek pending) => _pending = pending;

        public string DisplayName => _pending.Word;

        public IEnumerable<IPeekRelationship> Relationships { get { yield return PredefinedPeekRelationships.Definitions; } }

        public IPeekResultSource GetOrCreateResultSource(string relationshipName) => this;

        public void FindResults(string relationshipName, IPeekResultCollection resultCollection, CancellationToken cancellationToken, IFindPeekResultsCallback callback)
        {
            var factory = PeekDefinitionFeature.ResultFactory;
            if (factory == null || resultCollection == null) return;
            foreach (var t in _pending.Targets)
            {
                if (cancellationToken.IsCancellationRequested) return;
                var s = t.Symbol;
                var loc = s.Location;
                string role = t.Role == NavigationTargetRole.Definition ? NavGapStrings.PeekRoleDefinition : NavGapStrings.PeekRoleDeclaration;
                string file = Path.GetFileName(loc.FilePath);
                var info = new PeekResultDisplayInfo(file + " (" + role + ")", loc.FilePath, SymbolPresentation.QualifiedName(s), s.Signature ?? loc.FilePath);
                int len = Math.Max(1, s.Name?.Length ?? 1);
                // Span = the name's line (the peek view scrolls there); id = the name itself.
                resultCollection.Add(factory.Create(info, loc.FilePath, loc.Line, 0, loc.Line, loc.Column + len, loc.Line, loc.Column));
            }
        }
    }

    /// <summary>Alt+F12 (Edit.PeekDefinition, VSStd12 cmdidPeekDefinition) in C++ editors goes to UEToVS while the takeover is on.</summary>
    [Export(typeof(IVsTextViewCreationListener))]
    [ContentType(EditorContentTypes.Cpp)]
    [TextViewRole(PredefinedTextViewRoles.Document)]
    internal sealed class PeekDefinitionFilterProvider : IVsTextViewCreationListener
    {
        private static readonly Logger L = Log.For("Editor");

        [Import]
        internal IVsEditorAdaptersFactoryService Adapters { get; set; }

        public void VsTextViewCreated(IVsTextView textViewAdapter) => Safe.Execute(L, "editor.peekFilter.attach", () =>
        {
            var view = Adapters?.GetWpfTextView(textViewAdapter);
            if (view == null) return;
            var filter = new PeekDefinitionCommandFilter(view);
            if (ErrorHandler.Succeeded(textViewAdapter.AddCommandFilter(filter, out IOleCommandTarget next))) filter.Next = next;
            else L.Warn("editor.peekFilter.attachFailed", "AddCommandFilter failed");
        });
    }

    internal sealed class PeekDefinitionCommandFilter : IOleCommandTarget
    {
        private static readonly Logger L = Log.For("Editor");
        private static readonly Guid Std12 = VSConstants.CMDSETID.StandardCommandSet12_guid;
        private readonly IWpfTextView _view;

        public PeekDefinitionCommandFilter(IWpfTextView view) => _view = view;

        public IOleCommandTarget Next { get; set; }

        private static bool IsPeek(ref Guid group, uint id) => group == Std12 && id == (uint)VSConstants.VSStd12CmdID.PeekDefinition;

        public int QueryStatus(ref Guid pguidCmdGroup, uint cCmds, OLECMD[] prgCmds, IntPtr pCmdText)
        {
            if (cCmds == 1 && IsPeek(ref pguidCmdGroup, prgCmds[0].cmdID) && PeekDefinitionFeature.Active)
            {
                prgCmds[0].cmdf = (uint)(OLECMDF.OLECMDF_SUPPORTED | OLECMDF.OLECMDF_ENABLED);
                return VSConstants.S_OK;
            }
            return Next?.QueryStatus(ref pguidCmdGroup, cCmds, prgCmds, pCmdText) ?? (int)Constants.OLECMDERR_E_NOTSUPPORTED;
        }

        public int Exec(ref Guid pguidCmdGroup, uint nCmdID, uint nCmdexecopt, IntPtr pvaIn, IntPtr pvaOut)
        {
            if (IsPeek(ref pguidCmdGroup, nCmdID) && PeekDefinitionFeature.Active)
            {
                L.Info("editor.peek.intercepted", null);
                var view = _view;
                Safe.RunAsync(L, "editor.peek", () => PeekDefinitionFeature.ExecuteAsync("altF12", view));
                return VSConstants.S_OK;
            }
            return Next?.Exec(ref pguidCmdGroup, nCmdID, nCmdexecopt, pvaIn, pvaOut) ?? (int)Constants.OLECMDERR_E_NOTSUPPORTED;
        }
    }
}
