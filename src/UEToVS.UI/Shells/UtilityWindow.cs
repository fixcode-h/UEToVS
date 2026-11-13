using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using UEToVS.UI.Atoms;
using UEToVS.UI.Controls;
using UEToVS.UI.Logic;
using UEToVS.UI.Theming;

namespace UEToVS.UI.Shells
{
    /// <summary>Persists <see cref="UtilityWindow"/> position + size per window id (host: VS per-user settings store).</summary>
    public interface IWindowPlacementStore
    {
        bool TryGetPlacement(string id, out WindowPlacement placement);
        void SavePlacement(string id, WindowPlacement placement);
    }

    /// <summary>
    /// Floating, non-modal tool window in the VS 2026 style (settings, performance panel, component gallery): themed
    /// <see cref="ChromeWindow"/> with a title bar (brand mark, title, title actions, close), draggable, resizable,
    /// owned by the VS main window, Esc closes. Position and size are remembered per <see cref="WindowId"/> through
    /// <see cref="ChromeWindow.DefaultPlacementStore"/> (saved on close by <see cref="ChromeWindow"/>) and clamped back onto a visible monitor on restore.
    /// </summary>
    public class UtilityWindow : ChromeWindow
    {
        private readonly UiText _title = new UiText((string)null, TextVariant.Body) { FontWeight = FontWeights.SemiBold, VerticalAlignment = VerticalAlignment.Center };
        private readonly StackPanel _actions = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        private readonly ContentControl _content = new ContentControl { Focusable = false };
        private readonly Size _defaultSize;
        private bool _placed;

        public UtilityWindow(string windowId, Func<string> title, Size defaultSize)
        {
            WindowId = windowId ?? "window";
            PlacementId = WindowId;
            _defaultSize = defaultSize;
            Resizable = true;
            MinVisibleWidth = 420;
            MinVisibleHeight = 260;
            VisibleSize = defaultSize;
            ShowInTaskbar = false;

            var header = new Grid { Background = Brushes.Transparent, Height = 40 };
            header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            header.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            var brand = new BrandMark(16) { Margin = new Thickness(14, 0, 10, 0), VerticalAlignment = VerticalAlignment.Center };
            Grid.SetColumn(_title, 1);
            Grid.SetColumn(_actions, 2);
            var close = new IconButton(Ui.CrossMark, () => Strings.CommonClose, "Esc", 9) { Margin = new Thickness(4, 0, 8, 0), Width = 32, Height = 28 };
            close.Click += (s, e) => Ui.Safe("ui.utility.close", Close);
            Grid.SetColumn(close, 3);
            header.Children.Add(brand);
            header.Children.Add(_title);
            header.Children.Add(_actions);
            header.Children.Add(close);
            header.MouseLeftButtonDown += (s, e) => Ui.Safe("ui.utility.header.double", () =>
            {
                if (e.ClickCount == 2) { ResetPlacement(); e.Handled = true; }
            });
            EnableDrag(header);

            var headerHost = new Border { Child = header, BorderThickness = new Thickness(0, 0, 0, 1) };
            headerHost.Bind(Border.BackgroundProperty, Tokens.SurfaceNav);
            headerHost.Bind(Border.BorderBrushProperty, Tokens.BorderSubtle);

            var root = new DockPanel { LastChildFill = true };
            DockPanel.SetDock(headerHost, Dock.Top);
            root.Children.Add(headerHost);
            root.Children.Add(_content);
            Body = root;

            Strings.Track(_title, () =>
            {
                _title.Text = title?.Invoke() ?? string.Empty;
                Title = _title.Text;
            });
            PreviewKeyDown += (s, e) => Ui.Safe("ui.utility.key", () =>
            {
                if (e.Key == Key.Escape && CloseOnEscape) { Close(); e.Handled = true; }
            });
        }

        /// <summary>Key of the remembered placement.</summary>
        public string WindowId { get; }

        public bool CloseOnEscape { get; set; } = true;

        /// <summary>The window content (below the title bar).</summary>
        public UIElement WindowContent
        {
            get => _content.Content as UIElement;
            set => _content.Content = value;
        }

        /// <summary>Adds an element to the right side of the title bar (before the close button).</summary>
        public void AddTitleAction(UIElement element)
        {
            if (element is FrameworkElement fe) fe.Margin = new Thickness(4, 0, 0, 0);
            _actions.Children.Add(element);
        }

        /// <summary>Shows (or re-activates) the window owned by <paramref name="owner"/> at its remembered placement.</summary>
        public void ShowOwned(IntPtr owner)
        {
            if (owner != IntPtr.Zero && OwnerHandle == IntPtr.Zero) OwnerHandle = owner;
            if (!_placed)
            {
                _placed = true;
                if (!RestorePlacement()) PlaceAt(owner != IntPtr.Zero ? PopupAnchor.CenterOf(owner) : PopupAnchor.Mouse(AnchorMode.Center));
            }
            if (!IsVisible) Show();
            if (WindowState == WindowState.Minimized) WindowState = WindowState.Normal;
            Activate();
        }

        /// <summary>Double-click on the title bar: back to the default size, centered on the owner.</summary>
        private void ResetPlacement()
        {
            VisibleSize = _defaultSize;
            var owner = OwnerHandle;
            PlaceAt(owner != IntPtr.Zero ? PopupAnchor.CenterOf(owner) : PopupAnchor.Mouse(AnchorMode.Center));
        }
    }
}
