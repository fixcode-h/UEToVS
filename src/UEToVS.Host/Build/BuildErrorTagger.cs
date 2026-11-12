using System;
using System.Collections.Generic;
using System.ComponentModel.Composition;
using System.Windows.Media;
using Microsoft.VisualStudio.Text;
using Microsoft.VisualStudio.Text.Adornments;
using Microsoft.VisualStudio.Text.Classification;
using Microsoft.VisualStudio.Text.Editor;
using Microsoft.VisualStudio.Text.Tagging;
using Microsoft.VisualStudio.Utilities;
using UEToVS.Core.Diagnostics;
using UEToVS.Host.Diagnostics;
using UEToVS.Host.Editor;
using UEToVS.Host.Logic.Build;
using UEToVS.Host.Logic.Localization;
using UEToVS.Host.Navigation;

namespace UEToVS.Host.Build
{
    /// <summary>Squiggle types of compiler errors / warnings (own colours, editable in Fonts and Colors as "UEToVS Compiler …").</summary>
    internal static class CompilerErrorTypes
    {
        public const string Error = "UEToVS.CompilerError";
        public const string Warning = "UEToVS.CompilerWarning";

        [Export(typeof(ErrorTypeDefinition))]
        [Name(Error)]
        internal static ErrorTypeDefinition ErrorType = null;

        [Export(typeof(ErrorTypeDefinition))]
        [Name(Warning)]
        internal static ErrorTypeDefinition WarningType = null;
    }

    [Export(typeof(EditorFormatDefinition))]
    [Name(CompilerErrorTypes.Error)]
    [UserVisible(true)]
    internal sealed class CompilerErrorFormat : EditorFormatDefinition
    {
        public CompilerErrorFormat()
        {
            DisplayName = BuildStrings.CompilerErrorFormat;
            ForegroundColor = Color.FromRgb(0xE0, 0x45, 0x7B); // magenta-red: tells compiler errors from the UE rule squiggles (red)
        }
    }

    [Export(typeof(EditorFormatDefinition))]
    [Name(CompilerErrorTypes.Warning)]
    [UserVisible(true)]
    internal sealed class CompilerWarningFormat : EditorFormatDefinition
    {
        public CompilerWarningFormat()
        {
            DisplayName = BuildStrings.CompilerWarningFormat;
            ForegroundColor = Color.FromRgb(0xD1, 0x9A, 0x00);
        }
    }

    [Export(typeof(IViewTaggerProvider))]
    [ContentType(EditorContentTypes.Cpp)]
    [TagType(typeof(IErrorTag))]
    [TextViewRole(PredefinedTextViewRoles.Document)]
    internal sealed class BuildErrorTaggerProvider : IViewTaggerProvider
    {
        public ITagger<T> CreateTagger<T>(ITextView textView, ITextBuffer buffer) where T : ITag
        {
            if (!(textView is IWpfTextView view) || buffer != textView.TextBuffer) return null;
            if (EditorServices.Document(buffer) == null) return null;
            return view.Properties.GetOrCreateSingletonProperty(typeof(BuildErrorTagger), () => new BuildErrorTagger(view)) as ITagger<T>;
        }
    }

    /// <summary>
    /// Squiggles for the last build's errors / warnings in this file ("compiler" source, own colour). Spans are tracking spans
    /// created when the build result arrives (or the file opens), so they follow edits; cleared when the next build starts.
    /// Independent of the IntelliSense takeover (setting build.squiggles). UI thread.
    /// </summary>
    internal sealed class BuildErrorTagger : ITagger<IErrorTag>
    {
        private static readonly Logger L = Log.For("Build");
        private readonly IWpfTextView _view;
        private readonly ITextDocument _document;
        private List<(ITrackingSpan Span, IErrorTag Tag)> _items = new List<(ITrackingSpan, IErrorTag)>();

        public BuildErrorTagger(IWpfTextView view)
        {
            _view = view;
            _document = EditorServices.Document(view.TextBuffer);
            var service = BuildErrorService.Instance;
            if (service != null) service.Changed += OnChanged;
            NavigationContext.SettingsChanged += OnSettingsChanged;
            _view.Closed += OnClosed;
            Rebuild(false);
        }

        public event EventHandler<SnapshotSpanEventArgs> TagsChanged;

        private static bool Enabled => NavigationContext.Settings.BuildErrorSquiggles;

        public IEnumerable<ITagSpan<IErrorTag>> GetTags(NormalizedSnapshotSpanCollection spans)
        {
            var items = _items;
            if (spans.Count == 0 || items.Count == 0) yield break;
            var snapshot = spans[0].Snapshot;
            if (snapshot.TextBuffer != _view.TextBuffer) yield break;
            foreach (var (tracking, tag) in items)
            {
                var span = tracking.GetSpan(snapshot);
                if (span.IsEmpty || !spans.IntersectsWith(new NormalizedSnapshotSpanCollection(span))) continue;
                yield return new TagSpan<IErrorTag>(span, tag);
            }
        }

        private void OnChanged(object sender, EventArgs e) => Safe.Execute(L, "build.squiggles.update", () => Rebuild(true));

        private void OnSettingsChanged(object sender, Options.SettingsChangedEventArgs e) => Safe.Execute(L, "build.squiggles.settings", () =>
        {
            if (e.Old.BuildErrorSquiggles != e.New.BuildErrorSquiggles) Rebuild(true);
        });

        private void Rebuild(bool notify)
        {
            if (_view.IsClosed) return;
            var list = new List<(ITrackingSpan, IErrorTag)>();
            string path = _document?.FilePath;
            var set = BuildErrorService.Instance?.Current;
            if (Enabled && set != null && set.Count > 0 && !string.IsNullOrEmpty(path))
            {
                var snapshot = _view.TextBuffer.CurrentSnapshot;
                foreach (var d in set.ForFile(path))
                {
                    if (d.Severity == BuildSeverity.Info || d.Line < 1 || d.Line > snapshot.LineCount) continue;
                    var line = snapshot.GetLineFromLineNumber(d.Line - 1);
                    var (start, length) = BuildDiagnosticSpan.Compute(line.GetText(), d.Column);
                    if (length <= 0 || start + length > line.Length) continue;
                    var tracking = snapshot.CreateTrackingSpan(line.Start.Position + start, length, SpanTrackingMode.EdgeExclusive);
                    string type = d.Severity == BuildSeverity.Error ? CompilerErrorTypes.Error : CompilerErrorTypes.Warning;
                    list.Add((tracking, new ErrorTag(type, BuildStrings.CompilerTooltip(d.Tool, d.Code, d.Message))));
                }
            }
            bool had = _items.Count > 0;
            _items = list;
            if (notify && (had || list.Count > 0))
            {
                var s = _view.TextBuffer.CurrentSnapshot;
                TagsChanged?.Invoke(this, new SnapshotSpanEventArgs(new SnapshotSpan(s, 0, s.Length)));
            }
        }

        private void OnClosed(object sender, EventArgs e)
        {
            var service = BuildErrorService.Instance;
            if (service != null) service.Changed -= OnChanged;
            NavigationContext.SettingsChanged -= OnSettingsChanged;
            _view.Closed -= OnClosed;
            _items = new List<(ITrackingSpan, IErrorTag)>();
        }
    }
}
