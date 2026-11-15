using System;
using System.ComponentModel.Composition;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Threading.Tasks;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Text;
using Microsoft.VisualStudio.Text.Editor;
using Microsoft.VisualStudio.Text.Formatting;
using Microsoft.VisualStudio.Utilities;
using UEToVS.Contracts;
using UEToVS.Core.Diagnostics;
using UEToVS.Host.Diagnostics;
using UEToVS.Host.Logic.Editing;
using UEToVS.Host.Logic.Navigation;

namespace UEToVS.Host.Editor
{
    internal static class CodeVisionAdornments
    {
        public const string Layer = "UEToVS.CodeVision";

#pragma warning disable 169, 649 // MEF export
        [Export(typeof(AdornmentLayerDefinition))]
        [Name(Layer)]
        [Order(After = PredefinedAdornmentLayers.Text)]
        [TextViewRole(PredefinedTextViewRoles.Document)]
        internal static AdornmentLayerDefinition Definition;
#pragma warning restore 169, 649
    }

    [Export(typeof(ILineTransformSourceProvider))]
    [ContentType(EditorContentTypes.Cpp)]
    [TextViewRole(PredefinedTextViewRoles.Document)]
    internal sealed class CodeVisionLayoutProvider : ILineTransformSourceProvider
    {
        public ILineTransformSource Create(IWpfTextView textView) =>
            textView.Properties.GetOrCreateSingletonProperty(typeof(CodeVisionLayout), () => new CodeVisionLayout(textView));
    }

    /// <summary>
    /// v0.7.3 Code Vision, Rider style: "3 个派生类 · 2 个蓝图" on a row of its own above the declaration's UCLASS /
    /// UFUNCTION / UPROPERTY line (the engine anchors the hint there, at the line's indentation). The row is space added
    /// above the line by a line transform; the text is drawn in that space on an adornment layer. The hints come from the
    /// view's <see cref="InlayHintsTagger"/> (same request, same cache). v0.7.6: each count is clickable and opens the list
    /// behind it right below (<see cref="Navigation.CodeVisionFeature"/>).
    /// </summary>
    internal sealed class CodeVisionLayout : ILineTransformSource
    {
        private static readonly Logger L = Log.For("Editing");
        private static readonly Brush Foreground = Frozen(Color.FromArgb(0xFF, 0x8C, 0x8C, 0x8C));

        /// <summary>Hovered count: the UEToVS accent (Claude Code orange), like a link.</summary>
        private static readonly Brush Hover = Frozen(Color.FromArgb(0xFF, 0xD9, 0x77, 0x57));

        private readonly IWpfTextView _view;
        private readonly InlayHintsTagger _hints;
        private IAdornmentLayer _layer;
        private bool _relayoutPending;

        public CodeVisionLayout(IWpfTextView view)
        {
            _view = view;
            if (EditorServices.Document(view.TextBuffer) == null) return; // previews / projections: no hints
            _hints = view.Properties.GetOrCreateSingletonProperty(typeof(InlayHintsTagger), () => new InlayHintsTagger(view));
            _hints.CodeVisionChanged += OnCodeVisionChanged;
            _view.LayoutChanged += OnLayoutChanged;
            _view.Closed += OnClosed;
        }

        private double RowHeight => Math.Ceiling(_view.LineHeight * 0.9);

        public LineTransform GetLineTransform(ITextViewLine line, double yPosition, ViewRelativePosition placement)
        {
            var normal = line.DefaultLineTransform;
            if (_hints == null || !line.IsFirstTextViewLineForSnapshotLine) return normal;
            var hint = _hints.CodeVisionAt(line.Snapshot, line.Start.GetContainingLine().LineNumber);
            if (hint == null) return normal;
            return new LineTransform(normal.TopSpace + RowHeight, normal.BottomSpace, normal.VerticalScale, normal.Right);
        }

        private void OnLayoutChanged(object sender, TextViewLayoutChangedEventArgs e) => Safe.Execute(L, "editor.codeVision.draw", () =>
        {
            if (_layer == null) _layer = _view.GetAdornmentLayer(CodeVisionAdornments.Layer);
            foreach (var line in e.NewOrReformattedLines) Draw(line);
        });

        private void Draw(ITextViewLine line)
        {
            if (!line.IsFirstTextViewLineForSnapshotLine) return;
            var hint = _hints.CodeVisionAt(line.Snapshot, line.Start.GetContainingLine().LineNumber);
            if (hint == null) return;
            var parts = CodeVisionModel.Parts(hint);
            if (parts.Count == 0) return;
            int column = Math.Max(0, Math.Min(hint.Column, line.Length));
            double left = line.GetCharacterBounds(line.Start + column).Left;
            var props = _view.FormattedLineSource?.DefaultTextProperties;
            var font = props?.Typeface?.FontFamily ?? new FontFamily("Consolas");
            double size = Math.Max(7.0, (props?.FontRenderingEmSize ?? 13.0) * 0.8);
            // v0.7.6: each count is its own clickable piece (Rider): "2 个派生类" lists the C++ subclasses, "3 个蓝图" the Blueprints
            var row = new StackPanel { Orientation = Orientation.Horizontal };
            for (int i = 0; i < parts.Count; i++)
            {
                if (i > 0) row.Children.Add(Text(CodeVisionModel.Separator, font, size));
                row.Children.Add(Clickable(hint, parts[i], font, size));
            }
            double top = Math.Max(1.0, line.TextTop - line.Top);
            row.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
            Canvas.SetLeft(row, left);
            Canvas.SetTop(row, line.Top + Math.Max(0, (top - row.DesiredSize.Height) / 2));
            _layer.AddAdornment(AdornmentPositioningBehavior.TextRelative, line.Extent, hint, row, null);
        }

        private static TextBlock Text(string text, FontFamily font, double size) => new TextBlock
        {
            Text = text,
            FontFamily = font,
            FontSize = size,
            Foreground = Foreground,
        };

        private static readonly object HoverKey = new object();

        /// <summary>
        /// v0.7.7: the pointer is on a Code Vision count. The row lives in the space above the declaration line, so VS maps
        /// the hover to that line and Quick Info opened a big card for "UCLASS" behind the Blueprint list; Quick Info asks
        /// this first and stays quiet.
        /// </summary>
        public static bool IsPointerOver(ITextView view) =>
            view != null && view.Properties.TryGetProperty(HoverKey, out bool over) && over;

        private FrameworkElement Clickable(InlayHint hint, CodeVisionPart part, FontFamily font, double size)
        {
            var block = Text(part.Text, font, size);
            // transparent background: the whole text box takes the click, not only the glyphs
            block.Background = Brushes.Transparent;
            block.Cursor = Cursors.Hand;
            block.MouseEnter += (s, e) => { block.TextDecorations = TextDecorations.Underline; block.Foreground = Hover; _view.Properties[HoverKey] = true; };
            block.MouseLeave += (s, e) => { block.TextDecorations = null; block.Foreground = Foreground; _view.Properties[HoverKey] = false; };
            // handled on mouse down: the click must not move the caret to the line below the row
            block.MouseLeftButtonDown += (s, e) =>
            {
                e.Handled = true;
                Safe.RunAsync(L, "editor.codeVision.click", () => Navigation.CodeVisionFeature.ShowAsync(hint, part.Segment, block));
            };
            return block;
        }

        /// <summary>New / removed rows change line heights: re-format the visible lines (deferred out of a layout pass).</summary>
        private void OnCodeVisionChanged(object sender, EventArgs e)
        {
            if (_relayoutPending || _view.IsClosed) return;
            _relayoutPending = true;
            Safe.RunAsync(L, "editor.codeVision.relayout", async () =>
            {
                await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync(alwaysYield: true);
                _relayoutPending = false;
                if (_view.IsClosed || _view.InLayout || _view.TextViewLines == null) return;
                var first = _view.TextViewLines.FirstVisibleLine;
                _view.DisplayTextLineContainingBufferPosition(first.Start, first.Top - _view.ViewportTop, ViewRelativePosition.Top);
            });
        }

        private void OnClosed(object sender, EventArgs e)
        {
            _hints.CodeVisionChanged -= OnCodeVisionChanged;
            _view.LayoutChanged -= OnLayoutChanged;
            _view.Closed -= OnClosed;
        }

        private static Brush Frozen(Color c)
        {
            var b = new SolidColorBrush(c);
            b.Freeze();
            return b;
        }
    }
}
