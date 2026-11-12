using System;
using System.Collections.Generic;
using System.ComponentModel.Composition;
using System.Windows;
using Microsoft.VisualStudio.Text;
using Microsoft.VisualStudio.Text.Classification;
using Microsoft.VisualStudio.Text.Editor;
using Microsoft.VisualStudio.Text.Tagging;
using Microsoft.VisualStudio.Utilities;
using UEToVS.Contracts;
using UEToVS.Core.Diagnostics;
using UEToVS.Host.Diagnostics;
using UEToVS.Host.IntelliSense;

namespace UEToVS.Host.Editor
{
    /// <summary>Strikethrough classification of deprecated API uses (Fonts and Colors: "UEToVS Deprecated API").</summary>
    internal static class DeprecatedClassification
    {
        public const string Name = "UEToVS.DeprecatedApi";

#pragma warning disable 169, 649 // MEF export
        [Export, Name(Name)] internal static ClassificationTypeDefinition Type;
#pragma warning restore 169, 649
    }

    [Export(typeof(EditorFormatDefinition))]
    [ClassificationType(ClassificationTypeNames = DeprecatedClassification.Name)]
    [Name(DeprecatedClassification.Name)]
    [UserVisible(true)]
    [Order(After = Priority.High)]
    internal sealed class DeprecatedFormat : ClassificationFormatDefinition
    {
        public DeprecatedFormat()
        {
            DisplayName = "UEToVS Deprecated API";
            TextDecorations = System.Windows.TextDecorations.Strikethrough;
        }
    }

    [Export(typeof(IViewTaggerProvider))]
    [ContentType(EditorContentTypes.Cpp)]
    [TagType(typeof(ClassificationTag))]
    [TextViewRole(PredefinedTextViewRoles.Document)]
    internal sealed class DeprecatedStrikethroughProvider : IViewTaggerProvider
    {
        [Import]
        internal IClassificationTypeRegistryService Registry { get; set; }

        public ITagger<T> CreateTagger<T>(ITextView textView, ITextBuffer buffer) where T : ITag
        {
            if (!(textView is IWpfTextView view) || buffer != textView.TextBuffer) return null;
            if (EditorServices.Document(buffer) == null) return null;
            var type = Registry?.GetClassificationType(DeprecatedClassification.Name);
            if (type == null) return null;
            return view.Properties.GetOrCreateSingletonProperty(typeof(DeprecatedStrikethroughTagger), () => new DeprecatedStrikethroughTagger(view, type)) as ITagger<T>;
        }
    }

    /// <summary>
    /// Draws a strikethrough over the UE0011 (deprecated API) spans of the view's <see cref="CodeDiagnosticsTagger"/> (the squiggle
    /// carries the message); follows its updates, computes nothing itself.
    /// </summary>
    internal sealed class DeprecatedStrikethroughTagger : ITagger<ClassificationTag>
    {
        private static readonly Logger L = Log.For("Editing");
        private readonly IWpfTextView _view;
        private readonly ClassificationTag _tag;
        private CodeDiagnosticsTagger _source;

        public DeprecatedStrikethroughTagger(IWpfTextView view, IClassificationType type)
        {
            _view = view;
            _tag = new ClassificationTag(type);
            _view.Closed += OnClosed;
            _view.LayoutChanged += OnLayoutChanged; // the diagnostics tagger may be created after this one
        }

        private void OnLayoutChanged(object sender, TextViewLayoutChangedEventArgs e)
        {
            if (Source == null) return;
            _view.LayoutChanged -= OnLayoutChanged;
            var s = _view.TextSnapshot;
            TagsChanged?.Invoke(this, new SnapshotSpanEventArgs(new SnapshotSpan(s, 0, s.Length)));
        }

        public event EventHandler<SnapshotSpanEventArgs> TagsChanged;

        private CodeDiagnosticsTagger Source
        {
            get
            {
                if (_source != null) return _source;
                _source = CodeDiagnosticsTagger.For(_view);
                if (_source != null) _source.TagsChanged += OnSourceChanged;
                return _source;
            }
        }

        public IEnumerable<ITagSpan<ClassificationTag>> GetTags(NormalizedSnapshotSpanCollection spans)
        {
            if (spans.Count == 0 || IntelliSenseContext.Settings?.DeprecationHints != true) yield break;
            var source = Source;
            if (source == null) yield break;
            List<(CodeDiagnostic Item, SnapshotSpan Span)> items;
            try
            {
                items = source.Spans(spans[0].Snapshot, CodeDiagnosticCodes.DeprecatedApi);
            }
            catch (Exception ex)
            {
                L.Warn("editor.deprecation.strikethrough.failed", "Reading the deprecated API spans failed", ex);
                yield break;
            }
            foreach (var (_, span) in items)
                if (spans.IntersectsWith(new NormalizedSnapshotSpanCollection(span))) yield return new TagSpan<ClassificationTag>(span, _tag);
        }

        private void OnSourceChanged(object sender, SnapshotSpanEventArgs e) => TagsChanged?.Invoke(this, e);

        private void OnClosed(object sender, EventArgs e)
        {
            _view.Closed -= OnClosed;
            _view.LayoutChanged -= OnLayoutChanged;
            if (_source != null) _source.TagsChanged -= OnSourceChanged;
            _source = null;
        }
    }
}
