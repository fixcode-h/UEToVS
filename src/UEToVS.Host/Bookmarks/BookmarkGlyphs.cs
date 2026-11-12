using System;
using System.Collections.Generic;
using System.ComponentModel.Composition;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using Microsoft.VisualStudio.Text;
using Microsoft.VisualStudio.Text.Editor;
using Microsoft.VisualStudio.Text.Formatting;
using Microsoft.VisualStudio.Text.Tagging;
using Microsoft.VisualStudio.Utilities;
using UEToVS.Core.Bookmarks;
using UEToVS.Core.Diagnostics;
using UEToVS.UI.Theming;

namespace UEToVS.Host.Bookmarks
{
    internal sealed class BookmarkTag : IGlyphTag
    {
        public BookmarkTag(char? mnemonic) => Mnemonic = mnemonic;

        public char? Mnemonic { get; }
    }

    /// <summary>Shows bookmarks in the glyph margin and keeps them on their line while the text is edited.</summary>
    internal sealed class BookmarkTagger : ITagger<BookmarkTag>
    {
        private static readonly Logger L = Log.For("Bookmarks");
        private readonly ITextBuffer _buffer;
        private readonly string _path;
        private readonly Dictionary<Bookmark, ITrackingPoint> _points = new Dictionary<Bookmark, ITrackingPoint>();

        public BookmarkTagger(ITextBuffer buffer, string path)
        {
            _buffer = buffer;
            _path = path;
            // Weak: the store lives for the session, buffers come and go (no leak of closed documents).
            var weak = new WeakReference<BookmarkTagger>(this);
            EventHandler handler = null;
            handler = (s, e) =>
            {
                if (weak.TryGetTarget(out var t)) t.OnStoreChanged(s, e);
                else BookmarkService.Store.Changed -= handler;
            };
            BookmarkService.Store.Changed += handler;
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
            try
            {
                Track();
                var snap = _buffer.CurrentSnapshot;
                TagsChanged?.Invoke(this, new SnapshotSpanEventArgs(new SnapshotSpan(snap, 0, snap.Length)));
            }
            catch (Exception ex)
            {
                L.Error("bookmark.tagger.refresh.failed", "Refreshing bookmark glyphs failed", ex, ("file", _path));
            }
        }

        private void OnBufferChanged(object sender, TextContentChangedEventArgs e)
        {
            if (_points.Count == 0) return;
            try
            {
                foreach (var kv in _points)
                {
                    int line = kv.Value.GetPoint(e.After).GetContainingLine().LineNumber;
                    if (line != kv.Key.Location.Line) BookmarkService.Store.Move(kv.Key, line);
                }
            }
            catch (Exception ex)
            {
                L.Error("bookmark.tagger.move.failed", "Moving bookmarks after an edit failed", ex, ("file", _path));
            }
        }

        public IEnumerable<ITagSpan<BookmarkTag>> GetTags(NormalizedSnapshotSpanCollection spans)
        {
            if (spans.Count == 0 || _points.Count == 0) yield break;
            var snap = spans[0].Snapshot;
            foreach (var kv in _points.ToArray())
            {
                var line = kv.Value.GetPoint(snap).GetContainingLine();
                if (spans.IntersectsWith(new NormalizedSnapshotSpanCollection(line.Extent)))
                    yield return new TagSpan<BookmarkTag>(new SnapshotSpan(line.Start, 0), new BookmarkTag(kv.Key.Mnemonic));
            }
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

    /// <summary>Accent-colored square with the mnemonic (or ✓), themed through UEToVS.UI tokens.</summary>
    internal sealed class BookmarkGlyphFactory : IGlyphFactory
    {
        public UIElement GenerateGlyph(IWpfTextViewLine line, IGlyphTag tag)
        {
            if (!(tag is BookmarkTag b)) return null;
            var text = new TextBlock
            {
                Text = b.Mnemonic?.ToString() ?? "✓",
                FontSize = 9,
                FontWeight = FontWeights.Bold,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
            };
            var border = new Border { Width = 13, Height = 13, CornerRadius = new CornerRadius(2), Child = text };
            ThemeManager.Instance.Attach(border);
            border.Bind(Border.BackgroundProperty, Tokens.Accent);
            text.Bind(TextBlock.ForegroundProperty, Tokens.TextOnAccent);
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
