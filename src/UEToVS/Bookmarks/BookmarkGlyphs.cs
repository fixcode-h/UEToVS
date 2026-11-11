using System;
using System.Collections.Generic;
using System.ComponentModel.Composition;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Microsoft.VisualStudio.Text;
using Microsoft.VisualStudio.Text.Editor;
using Microsoft.VisualStudio.Text.Formatting;
using Microsoft.VisualStudio.Text.Tagging;
using Microsoft.VisualStudio.Utilities;
using UEToVS.Core.Bookmarks;
using UEToVS.Core.Navigation;

namespace UEToVS.Bookmarks
{
    internal sealed class BookmarkTag : IGlyphTag
    {
        public BookmarkTag(char? mnemonic) => Mnemonic = mnemonic;

        public char? Mnemonic { get; }
    }

    /// <summary>Shows bookmarks in the glyph margin and keeps them on their line while the text is edited.</summary>
    internal sealed class BookmarkTagger : ITagger<BookmarkTag>
    {
        private readonly ITextBuffer _buffer;
        private readonly string _path;
        private readonly Dictionary<Bookmark, ITrackingPoint> _points = new Dictionary<Bookmark, ITrackingPoint>();

        public BookmarkTagger(ITextBuffer buffer, string path)
        {
            _buffer = buffer;
            _path = path;
            BookmarkService.Store.Changed += OnStoreChanged;
            _buffer.Changed += OnBufferChanged;
            Track();
        }

        public event EventHandler<SnapshotSpanEventArgs> TagsChanged;

        private void Track()
        {
            _points.Clear();
            var snap = _buffer.CurrentSnapshot;
            foreach (var b in BookmarkService.Store.InFile(_path))
            {
                int line = Math.Min(b.Location.Line, snap.LineCount - 1);
                if (line < 0) continue;
                _points[b] = snap.CreateTrackingPoint(snap.GetLineFromLineNumber(line).Start, PointTrackingMode.Negative);
            }
        }

        private void OnStoreChanged(object sender, EventArgs e)
        {
            Track();
            Raise();
        }

        private void OnBufferChanged(object sender, TextContentChangedEventArgs e)
        {
            var snap = e.After;
            foreach (var kv in _points)
            {
                int line = kv.Value.GetPoint(snap).GetContainingLine().LineNumber;
                if (line != kv.Key.Location.Line) BookmarkService.Store.Move(kv.Key, line);
            }
        }

        private void Raise()
        {
            var snap = _buffer.CurrentSnapshot;
            TagsChanged?.Invoke(this, new SnapshotSpanEventArgs(new SnapshotSpan(snap, 0, snap.Length)));
        }

        public IEnumerable<ITagSpan<BookmarkTag>> GetTags(NormalizedSnapshotSpanCollection spans)
        {
            if (spans.Count == 0 || _points.Count == 0) yield break;
            var snap = spans[0].Snapshot;
            foreach (var kv in _points.ToArray())
            {
                var point = kv.Value.GetPoint(snap);
                var line = point.GetContainingLine();
                var span = new SnapshotSpan(line.Start, 0);
                if (spans.IntersectsWith(new NormalizedSnapshotSpanCollection(line.Extent)))
                    yield return new TagSpan<BookmarkTag>(span, new BookmarkTag(kv.Key.Mnemonic));
            }
        }

        public void Detach()
        {
            BookmarkService.Store.Changed -= OnStoreChanged;
            _buffer.Changed -= OnBufferChanged;
        }
    }

    [Export(typeof(ITaggerProvider))]
    [ContentType("text")]
    [TagType(typeof(BookmarkTag))]
    internal sealed class BookmarkTaggerProvider : ITaggerProvider
    {
        public ITagger<T> CreateTagger<T>(ITextBuffer buffer) where T : ITag
        {
            if (!buffer.Properties.TryGetProperty(typeof(ITextDocument), out ITextDocument doc)) return null;
            return buffer.Properties.GetOrCreateSingletonProperty(typeof(BookmarkTagger), () => new BookmarkTagger(buffer, doc.FilePath)) as ITagger<T>;
        }
    }

    internal sealed class BookmarkGlyphFactory : IGlyphFactory
    {
        public UIElement GenerateGlyph(IWpfTextViewLine line, IGlyphTag tag)
        {
            if (!(tag is BookmarkTag b)) return null;
            var border = new Border
            {
                Width = 13,
                Height = 13,
                CornerRadius = new CornerRadius(2),
                Background = new SolidColorBrush(Color.FromRgb(0xE0, 0x9A, 0x2B)),
                Child = new TextBlock
                {
                    Text = b.Mnemonic?.ToString() ?? "✓",
                    FontSize = 9,
                    FontWeight = FontWeights.Bold,
                    Foreground = Brushes.White,
                    HorizontalAlignment = HorizontalAlignment.Center,
                    VerticalAlignment = VerticalAlignment.Center,
                },
            };
            return border;
        }
    }

    [Export(typeof(IGlyphFactoryProvider))]
    [Name("UEToVSBookmarkGlyph")]
    [Order(After = "VsTextMarker")]
    [ContentType("text")]
    [TagType(typeof(BookmarkTag))]
    internal sealed class BookmarkGlyphFactoryProvider : IGlyphFactoryProvider
    {
        public IGlyphFactory GetGlyphFactory(IWpfTextView view, IWpfTextViewMargin margin) => new BookmarkGlyphFactory();
    }
}
