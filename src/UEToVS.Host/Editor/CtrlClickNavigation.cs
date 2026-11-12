using System;
using System.ComponentModel.Composition;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
using Microsoft.VisualStudio.Text;
using Microsoft.VisualStudio.Text.Editor;
using Microsoft.VisualStudio.Utilities;
using UEToVS.Contracts;
using UEToVS.Core.Diagnostics;
using UEToVS.Host.Diagnostics;
using UEToVS.Host.Logic.Navigation;
using UEToVS.Host.Navigation;

namespace UEToVS.Host.Editor
{
    /// <summary>Adornment layer for the Ctrl+hover underline (above the text).</summary>
    internal static class CtrlClickAdornment
    {
        public const string LayerName = "UEToVS.CtrlClickUnderline";

#pragma warning disable 649, 169
        [Export(typeof(AdornmentLayerDefinition))]
        [Name(LayerName)]
        [Order(After = PredefinedAdornmentLayers.Text)]
        private static AdornmentLayerDefinition _layer;
#pragma warning restore 649, 169
    }

    /// <summary>Ctrl+left-click = go to definition in C++ editors (mouse side).</summary>
    [Export(typeof(IMouseProcessorProvider))]
    [Name("UEToVS.CtrlClickMouse")]
    [Order(Before = "WordSelection")]
    [Order(Before = "GoToDefinition")]
    [ContentType(EditorContentTypes.Cpp)]
    [TextViewRole(PredefinedTextViewRoles.Document)]
    internal sealed class CtrlClickMouseProcessorProvider : IMouseProcessorProvider
    {
        public IMouseProcessor GetAssociatedProcessor(IWpfTextView wpfTextView) => CtrlClickController.For(wpfTextView);
    }

    /// <summary>Ctrl press / release while the mouse rests over a word (keyboard side: show / hide the underline).</summary>
    [Export(typeof(IKeyProcessorProvider))]
    [Name("UEToVS.CtrlClickKeys")]
    [ContentType(EditorContentTypes.Cpp)]
    [TextViewRole(PredefinedTextViewRoles.Document)]
    internal sealed class CtrlClickKeyProcessorProvider : IKeyProcessorProvider
    {
        public KeyProcessor GetAssociatedProcessor(IWpfTextView wpfTextView) => new CtrlClickKeyProcessor(CtrlClickController.For(wpfTextView));
    }

    internal sealed class CtrlClickKeyProcessor : KeyProcessor
    {
        private readonly CtrlClickController _controller;

        public CtrlClickKeyProcessor(CtrlClickController controller) => _controller = controller;

        public override bool IsInterestedInHandledEvents => true;

        public override void PreviewKeyDown(KeyEventArgs args) => OnKey(args);

        public override void PreviewKeyUp(KeyEventArgs args) => OnKey(args);

        private void OnKey(KeyEventArgs args)
        {
            // Every keystroke passes here: only modifier keys re-evaluate the hover; anything else just drops it.
            var k = args.Key == Key.System ? args.SystemKey : args.Key;
            if (k == Key.LeftCtrl || k == Key.RightCtrl || k == Key.LeftShift || k == Key.RightShift || k == Key.LeftAlt || k == Key.RightAlt)
                _controller.OnModifiersChanged();
            else
                _controller.OnOtherKey();
        }
    }

    /// <summary>
    /// Per-view Ctrl+click controller. Holding Ctrl over an identifier underlines it and shows the hand cursor; a Ctrl+click
    /// (no drag, released on the same word) moves the caret there and runs go to definition (Toggle). Ctrl+Alt / Ctrl+Shift
    /// clicks stay VS's (multi-caret, extend selection). The gesture logic is the pure <see cref="CtrlClickGesture"/>; the
    /// hit test is the lexical <see cref="IdentifierFinder"/> (no engine call while hovering). Off when the setting is off
    /// or no Unreal solution is open.
    /// </summary>
    internal sealed class CtrlClickController : MouseProcessorBase
    {
        private static readonly Logger L = Log.For("CtrlClick");

        private readonly IWpfTextView _view;
        private readonly CtrlClickGesture _gesture = new CtrlClickGesture();
        private IAdornmentLayer _layer;
        private SnapshotSpan? _hover;
        private SnapshotSpan? _armedSpan;
        private bool _cursorSet;
        private Cursor _savedCursor;

        private CtrlClickController(IWpfTextView view)
        {
            _view = view;
            _view.LayoutChanged += OnLayoutChanged;
            _view.LostAggregateFocus += OnLostFocus;
            _view.Closed += OnClosed;
        }

        public static CtrlClickController For(IWpfTextView view) =>
            view.Properties.GetOrCreateSingletonProperty(typeof(CtrlClickController), () => new CtrlClickController(view));

        private bool Enabled => NavigationContext.IsUnrealSolution && NavigationContext.Settings.CtrlClickNavigation;

        private static KeyMods Mods()
        {
            var m = Keyboard.Modifiers;
            var r = KeyMods.None;
            if ((m & ModifierKeys.Control) != 0) r |= KeyMods.Ctrl;
            if ((m & ModifierKeys.Shift) != 0) r |= KeyMods.Shift;
            if ((m & ModifierKeys.Alt) != 0) r |= KeyMods.Alt;
            return r;
        }

        // ---------------------------------------------------------------- mouse

        public override void PreprocessMouseMove(MouseEventArgs e)
        {
            // Hot path (every mouse move): no timing scope, cheap exit when Ctrl is up and nothing is shown / armed.
            var mods = Mods();
            if ((mods & KeyMods.Ctrl) == 0 && _hover == null && !_gesture.IsArmed) return;
            try
            {
                var pos = e.GetPosition(_view.VisualElement);
                _gesture.Enabled = Enabled;
                var hit = HitTest(pos);
                Apply(_gesture.OnMove(mods, hit.HasValue, pos.X, pos.Y), hit);
            }
            catch (Exception ex)
            {
                L.Error("ctrlClick.move.failed", "Ctrl+hover failed", ex);
            }
        }

        public override void PreprocessMouseLeftButtonDown(MouseButtonEventArgs e) => Safe.Execute(L, "ctrlClick.down", () =>
        {
            var mods = Mods();
            if ((mods & KeyMods.Ctrl) == 0) return;
            _gesture.Enabled = Enabled;
            var pos = e.GetPosition(_view.VisualElement);
            var hit = HitTest(pos);
            var line = hit?.Start.GetContainingLine();
            var span = hit.HasValue ? new LineSpan(hit.Value.Start.Position - line.Start.Position, hit.Value.Length) : default;
            var action = _gesture.OnLeftDown(mods, hit.HasValue, line?.LineNumber ?? -1, span, pos.X, pos.Y, e.ClickCount);
            if (action == CtrlClickAction.Arm)
            {
                _armedSpan = hit;
                e.Handled = true; // no caret move / selection start for a Ctrl+click on a word
            }
        });

        public override void PreprocessMouseLeftButtonUp(MouseButtonEventArgs e) => Safe.Execute(L, "ctrlClick.up", () =>
        {
            if (!_gesture.IsArmed) return;
            var pos = e.GetPosition(_view.VisualElement);
            var hit = HitTest(pos);
            var line = hit?.Start.GetContainingLine();
            var span = hit.HasValue ? new LineSpan(hit.Value.Start.Position - line.Start.Position, hit.Value.Length) : default;
            var action = _gesture.OnLeftUp(Mods(), hit.HasValue, line?.LineNumber ?? -1, span, pos.X, pos.Y);
            var armed = _armedSpan;
            _armedSpan = null;
            if (action != CtrlClickAction.Navigate || !hit.HasValue)
            {
                L.Debug("ctrlClick.canceled", null, ("action", action.ToString()));
                return;
            }
            e.Handled = true;
            ClearHover();
            var point = PointAt(pos) ?? hit.Value.Start;
            if (armed.HasValue && armed.Value.Snapshot == point.Snapshot && !armed.Value.Contains(point)) point = armed.Value.Start;
            _view.Selection.Clear();
            _view.Caret.MoveTo(point);
            string word = hit.Value.GetText();
            L.Info("ctrlClick.navigate", null, ("word", word), ("line", line.LineNumber), ("col", span.Start));
            Safe.RunAsync(L, "ctrlClick.navigate", () => GoToDefinitionFeature.ExecuteAsync(NavigationKind.Toggle, "ctrlClick", _view, point));
        });

        public override void PreprocessMouseLeave(MouseEventArgs e) => Safe.Execute(L, "ctrlClick.leave", () =>
        {
            _gesture.Reset();
            ClearHover();
        });

        // ---------------------------------------------------------------- keyboard / view events

        public void OnModifiersChanged() => Safe.Execute(L, "ctrlClick.keys", () =>
        {
            if (_view.IsClosed || !_view.VisualElement.IsMouseOver)
            {
                if (_hover != null) ClearHover();
                return;
            }
            var mods = Mods();
            if ((mods & KeyMods.Ctrl) == 0 && _hover == null) return;
            _gesture.Enabled = Enabled;
            var pos = Mouse.GetPosition(_view.VisualElement);
            var hit = HitTest(pos);
            var action = _gesture.OnMove(mods, hit.HasValue, pos.X, pos.Y);
            Apply(action == CtrlClickAction.Cancel ? CtrlClickAction.ClearHover : action, hit);
        });

        /// <summary>A non-modifier key while hovering (typing): drop the underline.</summary>
        public void OnOtherKey()
        {
            if (_hover != null || _gesture.IsArmed)
            {
                _gesture.Reset();
                ClearHover();
            }
        }

        private void OnLayoutChanged(object sender, TextViewLayoutChangedEventArgs e)
        {
            if (_hover != null) Safe.Execute(L, "ctrlClick.layout", () => RedrawAfterLayout(e));
        }

        private void RedrawAfterLayout(TextViewLayoutChangedEventArgs e)
        {
            if (_hover == null) return;
            // Text changed or scrolled: redraw at the new place (or drop it).
            var span = _hover.Value.TranslateTo(e.NewSnapshot, SpanTrackingMode.EdgeExclusive);
            if (span.IsEmpty) ClearHover();
            else ShowUnderline(span);
        }

        private void OnLostFocus(object sender, EventArgs e)
        {
            _gesture.Reset();
            ClearHover();
        }

        private void OnClosed(object sender, EventArgs e)
        {
            _view.LayoutChanged -= OnLayoutChanged;
            _view.LostAggregateFocus -= OnLostFocus;
            _view.Closed -= OnClosed;
        }

        // ---------------------------------------------------------------- hover visuals

        private void Apply(CtrlClickAction action, SnapshotSpan? hit)
        {
            switch (action)
            {
                case CtrlClickAction.Hover:
                    if (hit.HasValue && (_hover == null || _hover.Value != hit.Value)) ShowUnderline(hit.Value);
                    break;
                case CtrlClickAction.ClearHover:
                case CtrlClickAction.Cancel:
                    if (_hover != null) ClearHover();
                    break;
            }
        }

        private void ShowUnderline(SnapshotSpan span)
        {
            if (_view.IsClosed || _view.InLayout) return;
            if (_layer == null) _layer = _view.GetAdornmentLayer(CtrlClickAdornment.LayerName);
            _layer.RemoveAllAdornments();
            _hover = span;
            var geometry = _view.TextViewLines?.GetMarkerGeometry(span);
            if (geometry != null)
            {
                var b = geometry.Bounds;
                var brush = _view.FormattedLineSource?.DefaultTextProperties?.ForegroundBrush ?? Brushes.Gray;
                var line = new Rectangle { Width = Math.Max(1, b.Width), Height = 1, Fill = brush, IsHitTestVisible = false };
                Canvas.SetLeft(line, b.Left);
                Canvas.SetTop(line, b.Bottom - 2);
                _layer.AddAdornment(AdornmentPositioningBehavior.TextRelative, span, null, line, null);
            }
            if (!_cursorSet)
            {
                _savedCursor = _view.VisualElement.Cursor;
                _view.VisualElement.Cursor = Cursors.Hand;
                _cursorSet = true;
            }
        }

        private void ClearHover()
        {
            _hover = null;
            _layer?.RemoveAllAdornments();
            if (_cursorSet)
            {
                _view.VisualElement.Cursor = _savedCursor;
                _cursorSet = false;
            }
        }

        // ---------------------------------------------------------------- hit test

        private SnapshotPoint? PointAt(Point pos)
        {
            if (_view.IsClosed || _view.InLayout || _view.TextViewLines == null) return null;
            var line = _view.TextViewLines.GetTextViewLineContainingYCoordinate(pos.Y + _view.ViewportTop);
            return line?.GetBufferPositionFromXCoordinate(pos.X + _view.ViewportLeft);
        }

        /// <summary>Identifier (or #include path) under the mouse, or null (empty space, beyond the line end, keyword, comment, literal).</summary>
        private SnapshotSpan? HitTest(Point pos)
        {
            var p = PointAt(pos);
            if (!p.HasValue) return null;
            var line = p.Value.GetContainingLine();
            if (line.Length > 4000) return null; // minified / generated line: not worth scanning
            string text = line.GetText();
            int col = p.Value.Position - line.Start.Position;
            if (!IdentifierFinder.TryFind(text, col, false, out var s)) return null;
            return new SnapshotSpan(line.Snapshot, line.Start.Position + s.Start, s.Length);
        }
    }
}
