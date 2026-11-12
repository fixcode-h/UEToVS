using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel.Composition;
using System.Threading.Tasks;
using Microsoft.VisualStudio.Language.Intellisense;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Text;
using Microsoft.VisualStudio.Text.Editor;
using Microsoft.VisualStudio.Utilities;
using UEToVS.Contracts;
using UEToVS.Core.Diagnostics;
using UEToVS.Host.Diagnostics;
using UEToVS.Host.Editor;
using UEToVS.Host.Logic.IntelliSense;
using UEToVS.Host.Logic.Navigation;
using UEToVS.Host.Navigation;

namespace UEToVS.Host.IntelliSense
{
    /// <summary>Engine answer cached on the view until the broker asks for it (signature help sources are synchronous).</summary>
    internal sealed class SignatureHelpData
    {
        public SignatureHelpResponse Response { get; set; }
        public ITrackingPoint OpenParen { get; set; }
        public int ActiveParameter { get; set; }
    }

    [Export(typeof(ISignatureHelpSourceProvider))]
    [Name("UEToVS Signature Help Source")]
    [ContentType(EditorContentTypes.Cpp)]
    [Order(Before = "default")]
    internal sealed class UEToVSSignatureHelpSourceProvider : ISignatureHelpSourceProvider
    {
        public ISignatureHelpSource TryCreateSignatureHelpSource(ITextBuffer textBuffer) =>
            textBuffer.Properties.GetOrCreateSingletonProperty(typeof(UEToVSSignatureHelpSource), () => new UEToVSSignatureHelpSource(textBuffer));
    }

    /// <summary>
    /// Parameter info source (§7.6). The broker calls it synchronously on the UI thread, so it never talks to the engine:
    /// <see cref="SignatureHelpController"/> fetches the signatures asynchronously ('(' / ',' / Ctrl+Shift+Space), caches
    /// them on the view and then triggers the session; this source turns the cached answer into signatures whose current
    /// parameter (bold in VS's presenter) follows the caret.
    /// </summary>
    internal sealed class UEToVSSignatureHelpSource : ISignatureHelpSource
    {
        private static readonly Logger L = Log.For("IntelliSense");
        private readonly ITextBuffer _buffer;

        public UEToVSSignatureHelpSource(ITextBuffer buffer) => _buffer = buffer;

        public void AugmentSignatureHelpSession(ISignatureHelpSession session, IList<ISignature> signatures)
        {
            Safe.Execute(L, "is.signatureHelp.augment", () =>
            {
                if (!session.TextView.Properties.TryGetProperty(typeof(SignatureHelpData), out SignatureHelpData data) || data?.Response == null) return;
                var snapshot = _buffer.CurrentSnapshot;
                var open = data.OpenParen.GetPoint(snapshot);
                var caret = session.GetTriggerPoint(_buffer)?.GetPoint(snapshot) ?? open;
                int end = Math.Max(open.Position, caret.Position);
                var applicable = snapshot.CreateTrackingSpan(new Span(open.Position, end - open.Position), SpanTrackingMode.EdgeInclusive);
                foreach (var info in data.Response.Signatures)
                    if (info?.Label != null) signatures.Add(new UEToVSSignature(applicable, info, data.ActiveParameter));
            });
        }

        public ISignature GetBestMatch(ISignatureHelpSession session)
        {
            if (!session.TextView.Properties.TryGetProperty(typeof(SignatureHelpData), out SignatureHelpData data) || data?.Response == null) return null;
            int i = SignatureHelpModel.PickSignature(data.Response, data.ActiveParameter);
            if (i < 0) return null;
            var label = data.Response.Signatures[i]?.Label;
            foreach (var s in session.Signatures)
                if (s is UEToVSSignature u && u.Content == label) return s;
            return null;
        }

        public void Dispose()
        {
        }
    }

    internal sealed class UEToVSSignature : ISignature
    {
        private IParameter _current;

        public UEToVSSignature(ITrackingSpan applicableTo, SignatureInfo info, int activeParameter)
        {
            ApplicableToSpan = applicableTo;
            Content = info.Label;
            PrettyPrintedContent = info.Label;
            Documentation = info.Documentation ?? string.Empty;
            var ranges = SignatureHelpModel.ParameterRanges(info);
            var list = new List<IParameter>(ranges.Count);
            for (int i = 0; i < ranges.Count; i++)
            {
                var p = info.Parameters != null && i < info.Parameters.Count ? info.Parameters[i] : null;
                var locus = new Span(ranges[i].Start, ranges[i].Length);
                list.Add(new UEToVSParameter(this, p?.Name ?? info.Label.Substring(locus.Start, locus.Length), p?.Documentation ?? string.Empty, locus));
            }
            Parameters = new ReadOnlyCollection<IParameter>(list);
            SetCurrent(activeParameter);
        }

        public ITrackingSpan ApplicableToSpan { get; }
        public string Content { get; }
        public string PrettyPrintedContent { get; }
        public string Documentation { get; }
        public ReadOnlyCollection<IParameter> Parameters { get; }

        public IParameter CurrentParameter
        {
            get => _current;
            private set
            {
                if (ReferenceEquals(_current, value)) return;
                var old = _current;
                _current = value;
                CurrentParameterChanged?.Invoke(this, new CurrentParameterChangedEventArgs(old, value));
            }
        }

        public event EventHandler<CurrentParameterChangedEventArgs> CurrentParameterChanged;

        /// <summary>Highlights parameter <paramref name="index"/> (the last one for variadic overflow; none when out of range).</summary>
        public void SetCurrent(int index)
        {
            if (Parameters.Count == 0 || index < 0)
            {
                CurrentParameter = null;
                return;
            }
            if (index >= Parameters.Count) index = Content.Contains("...") ? Parameters.Count - 1 : -1;
            CurrentParameter = index < 0 ? null : Parameters[index];
        }
    }

    internal sealed class UEToVSParameter : IParameter
    {
        public UEToVSParameter(ISignature signature, string name, string documentation, Span locus)
        {
            Signature = signature;
            Name = name;
            Documentation = documentation;
            Locus = locus;
            PrettyPrintedLocus = locus;
        }

        public ISignature Signature { get; }
        public string Name { get; }
        public string Documentation { get; }
        public Span Locus { get; }
        public Span PrettyPrintedLocus { get; }
    }

    /// <summary>
    /// Per-view driver of parameter info: '(' and ',' (and Ctrl+Shift+Space) fetch signatures from the engine (latest
    /// request wins), then open VS's signature help with them; while the session is open the current parameter follows the
    /// caret locally (top-level commas, no RPC) and the session closes when the caret leaves the call.
    /// </summary>
    internal sealed class SignatureHelpController
    {
        private static readonly Logger L = Log.For("IntelliSense");
        private readonly IWpfTextView _view;
        private readonly ISignatureHelpBroker _broker;
        private readonly LatestRequestGate _gate = new LatestRequestGate();

        private SignatureHelpController(IWpfTextView view, ISignatureHelpBroker broker)
        {
            _view = view;
            _broker = broker;
            _view.Caret.PositionChanged += OnCaretMoved;
            _view.Closed += OnClosed;
        }

        public static SignatureHelpController For(IWpfTextView view, ISignatureHelpBroker broker) =>
            broker == null ? null : view.Properties.GetOrCreateSingletonProperty(typeof(SignatureHelpController), () => new SignatureHelpController(view, broker));

        /// <summary>After '(' / ',' was typed, or on Ctrl+Shift+Space (<paramref name="explicitInvoke"/>).</summary>
        public void Trigger(char typed, bool explicitInvoke)
        {
            if (!IntelliSenseContext.SignatureHelpActive) return;
            var caret = _view.Caret.Position.BufferPosition;
            var snapshot = caret.Snapshot;
            if (typed == ',' && _broker.IsSignatureHelpActive(_view))
            {
                UpdateCurrentParameter();
                return;
            }
            // Joined text of up to 20 lines before the caret, to find the call's '('.
            var startLine = snapshot.GetLineFromLineNumber(Math.Max(0, caret.GetContainingLine().LineNumber - 20));
            string text = snapshot.GetText(startLine.Start.Position, caret.Position - startLine.Start.Position);
            int open = SignatureHelpModel.FindOpenParen(text, text.Length);
            if (open < 0)
            {
                if (explicitInvoke) Dismiss();
                return;
            }
            var openPoint = new SnapshotPoint(snapshot, startLine.Start.Position + open);
            int active = SignatureHelpModel.ActiveParameter(text.Substring(open + 1));
            var capture = IntelliSenseEditor.CaptureAt(_view.TextBuffer, caret);
            var engine = IntelliSenseContext.Engine;
            if (capture == null || engine == null) return;
            var (gen, ct) = _gate.Begin(NavigationContext.Disposal);
            Safe.RunAsync(L, "is.signatureHelp.request", async () =>
            {
                var request = await Task.Run(capture, ct).ConfigureAwait(false);
                var r = await engine.GetSignatureHelpAsync(request, ct).ConfigureAwait(false);
                await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
                if (!_gate.IsCurrent(gen) || _view.IsClosed) return;
                if (!r.Ok || r.Value.Signatures == null || r.Value.Signatures.Count == 0)
                {
                    if (explicitInvoke) Dismiss();
                    return;
                }
                // Prefer the engine's '(' (it knows macros / templates); fall back to the local scan.
                var current = _view.TextSnapshot;
                var parenPoint = openPoint.TranslateTo(current, PointTrackingMode.Negative);
                if (r.Value.OpenParenLine >= 0 && r.Value.OpenParenLine < snapshot.LineCount)
                {
                    var l = snapshot.GetLineFromLineNumber(r.Value.OpenParenLine);
                    if (r.Value.OpenParenColumn >= 0 && r.Value.OpenParenColumn < l.Length && l.GetText()[r.Value.OpenParenColumn] == '(')
                        parenPoint = new SnapshotPoint(snapshot, l.Start.Position + r.Value.OpenParenColumn).TranslateTo(current, PointTrackingMode.Negative);
                }
                _view.Properties[typeof(SignatureHelpData)] = new SignatureHelpData
                {
                    Response = r.Value,
                    OpenParen = current.CreateTrackingPoint(parenPoint.Position, PointTrackingMode.Negative),
                    ActiveParameter = active,
                };
                _broker.DismissAllSessions(_view);
                var trigger = current.CreateTrackingPoint(_view.Caret.Position.BufferPosition.Position, PointTrackingMode.Positive);
                _broker.TriggerSignatureHelp(_view, trigger, true);
            });
        }

        private void OnCaretMoved(object sender, CaretPositionChangedEventArgs e)
        {
            if (!_view.Properties.ContainsProperty(typeof(SignatureHelpData))) return;
            Safe.Execute(L, "is.signatureHelp.caret", UpdateCurrentParameter);
        }

        /// <summary>Recomputes the current parameter from the text; closes the session when the caret left the call.</summary>
        private void UpdateCurrentParameter()
        {
            if (!_view.Properties.TryGetProperty(typeof(SignatureHelpData), out SignatureHelpData data)) return;
            if (!_broker.IsSignatureHelpActive(_view))
            {
                _view.Properties.RemoveProperty(typeof(SignatureHelpData));
                return;
            }
            var snapshot = _view.TextSnapshot;
            var caret = _view.Caret.Position.BufferPosition;
            var open = data.OpenParen.GetPoint(snapshot);
            if (caret.Position <= open.Position || caret.Position - open.Position > 20000)
            {
                Dismiss();
                return;
            }
            int active = SignatureHelpModel.ActiveParameter(snapshot.GetText(open.Position + 1, caret.Position - open.Position - 1));
            if (active < 0)
            {
                Dismiss();
                return;
            }
            data.ActiveParameter = active;
            foreach (var session in _broker.GetSessions(_view))
                foreach (var s in session.Signatures)
                    if (s is UEToVSSignature u) u.SetCurrent(active);
        }

        private void Dismiss()
        {
            _gate.Cancel();
            _view.Properties.RemoveProperty(typeof(SignatureHelpData));
            if (_broker.IsSignatureHelpActive(_view)) _broker.DismissAllSessions(_view);
        }

        private void OnClosed(object sender, EventArgs e)
        {
            _gate.Dispose();
            _view.Caret.PositionChanged -= OnCaretMoved;
            _view.Closed -= OnClosed;
        }
    }
}
