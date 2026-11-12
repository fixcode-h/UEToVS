using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using UEToVS.UI.Atoms;
using UEToVS.UI.Controls;
using UEToVS.UI.Logic;
using UEToVS.UI.Molecules;
using UEToVS.UI.Theming;

namespace UEToVS.UI.Shells
{
    /// <summary>
    /// Modal dialog chrome: title (+ description), form area, optional preview area (right or bottom), button row
    /// (primary + cancel). Enter = primary (unless in a multi-line box), Esc = cancel. Draggable by the title.
    /// </summary>
    public class DialogShell : ChromeWindow
    {
        private readonly UiText _title = new UiText((string)null, TextVariant.Title);
        private readonly UiText _description = new UiText((string)null, TextVariant.Secondary) { TextWrapping = TextWrapping.Wrap, TextTrimming = TextTrimming.None, Margin = new Thickness(0, 4, 0, 0) };
        private readonly ContentControl _form = new ContentControl { Focusable = false };
        private readonly Border _previewHost = new Border { BorderThickness = new Thickness(1), Visibility = Visibility.Collapsed };
        private readonly StackPanel _buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        private readonly Grid _content = new Grid();
        private readonly UiButton _primary;
        private readonly UiButton _cancel;
        private PreviewPosition _previewPos = PreviewPosition.Right;

        public DialogShell(Func<string> title, Func<string> primaryText = null, Func<string> cancelText = null)
        {
            Resizable = true;
            MinVisibleWidth = 360;
            MinVisibleHeight = 160;
            VisibleSize = new Size(560, 360);
            _primary = new UiButton(primaryText ?? (() => Strings.CommonOk), ButtonKind.Primary) { MinWidth = 80, IsDefault = false };
            _cancel = new UiButton(cancelText ?? (() => Strings.CommonCancel), ButtonKind.Secondary) { MinWidth = 80, Margin = new Thickness(8, 0, 0, 0) };
            _buttons.Children.Add(_primary);
            _buttons.Children.Add(_cancel);
            _previewHost.Bind(Border.BorderBrushProperty, Tokens.BorderSubtle);
            _previewHost.Bind(Border.CornerRadiusProperty, Tokens.CornerRadiusControl);

            var header = new StackPanel { Margin = new Thickness(20, 16, 20, 12), Background = System.Windows.Media.Brushes.Transparent };
            header.Children.Add(_title);
            header.Children.Add(_description);
            EnableDrag(header);

            var footer = new Border { Padding = new Thickness(20, 10, 20, 14), Child = _buttons };

            var root = new DockPanel { LastChildFill = true };
            DockPanel.SetDock(header, Dock.Top);
            DockPanel.SetDock(footer, Dock.Bottom);
            root.Children.Add(header);
            root.Children.Add(footer);
            _content.Margin = new Thickness(20, 0, 20, 0);
            root.Children.Add(_content);
            Body = root;
            LayoutContent();

            Strings.Track(_title, () =>
            {
                _title.Text = title?.Invoke() ?? string.Empty;
                Title = _title.Text;
            });

            _primary.Click += (s, e) => Ui.Safe("ui.dialog.primary", Accept);
            _cancel.Click += (s, e) => Ui.Safe("ui.dialog.cancel", Cancel);
            PreviewKeyDown += (s, e) => Ui.Safe("ui.dialog.key", () =>
            {
                if (e.Key == Key.Escape) { Cancel(); e.Handled = true; }
                else if (e.Key == Key.Enter && Keyboard.Modifiers == ModifierKeys.None
                         && !(e.OriginalSource is TextBox tb && tb.AcceptsReturn)
                         && !(e.OriginalSource is System.Windows.Controls.Primitives.ButtonBase))
                {
                    Accept();
                    e.Handled = true;
                }
            });
        }

        /// <summary>Called before closing with OK; return false to keep the dialog open (validation).</summary>
        public Func<bool> Validate { get; set; }

        public string Description
        {
            get => _description.Text;
            set
            {
                _description.Text = value ?? string.Empty;
                _description.Visibility = string.IsNullOrEmpty(value) ? Visibility.Collapsed : Visibility.Visible;
            }
        }

        public UIElement Form
        {
            get => _form.Content as UIElement;
            set => _form.Content = value;
        }

        public UIElement Preview
        {
            get => _previewHost.Child;
            set
            {
                _previewHost.Child = value;
                _previewHost.Visibility = value == null ? Visibility.Collapsed : Visibility.Visible;
            }
        }

        public PreviewPosition PreviewPosition
        {
            get => _previewPos;
            set
            {
                _previewPos = value;
                LayoutContent();
            }
        }

        public UiButton PrimaryButton => _primary;
        public UiButton CancelButton => _cancel;

        public bool IsPrimaryEnabled
        {
            get => _primary.IsEnabled;
            set => _primary.IsEnabled = value;
        }

        private void LayoutContent()
        {
            _content.Children.Clear();
            _content.RowDefinitions.Clear();
            _content.ColumnDefinitions.Clear();
            if (_previewPos == PreviewPosition.Right)
            {
                _content.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star), MinWidth = 200 });
                _content.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                Grid.SetColumn(_previewHost, 1);
                Grid.SetRow(_previewHost, 0);
                _previewHost.Margin = new Thickness(16, 0, 0, 0);
            }
            else
            {
                _content.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
                _content.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
                Grid.SetRow(_previewHost, 1);
                Grid.SetColumn(_previewHost, 0);
                _previewHost.Margin = new Thickness(0, 12, 0, 0);
            }
            var scroller = new UiScrollViewer { Content = _form };
            _content.Children.Add(scroller);
            _content.Children.Add(_previewHost);
        }

        /// <summary>Shows modally, centered on the owner window (or on the mouse monitor).</summary>
        public bool ShowModal(IntPtr owner)
        {
            if (owner != IntPtr.Zero) OwnerHandle = owner;
            PlaceAt(owner != IntPtr.Zero ? PopupAnchor.CenterOf(owner) : PopupAnchor.Mouse(AnchorMode.Center));
            Loaded += (s, e) => Ui.Safe("ui.dialog.focus", () => MoveFocus(new TraversalRequest(FocusNavigationDirection.First)));
            return ShowDialog() == true;
        }

        private void Accept()
        {
            if (!_primary.IsEnabled) return;
            if (Validate != null && !Validate()) return;
            DialogResult = true;
        }

        private void Cancel()
        {
            DialogResult = false;
        }
    }

    /// <summary>
    /// Tool-window content shell (inside a VS ToolWindowPane): optional title row with action buttons, toolbar,
    /// banner area, content, status footer. Attaches the theme tokens to itself.
    /// </summary>
    public class ToolWindowShell : DockPanel
    {
        private readonly UiText _title = new UiText((string)null, TextVariant.Strong);
        private readonly StackPanel _titleActions = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        private readonly Border _titleBar = new Border { Padding = new Thickness(10, 4, 6, 4), BorderThickness = new Thickness(0, 0, 0, 1), Visibility = Visibility.Collapsed };
        private readonly DockPanel _toolbar = new DockPanel { LastChildFill = false };
        private readonly Border _toolbarHost = new Border { Padding = new Thickness(6, 4, 6, 4), BorderThickness = new Thickness(0, 0, 0, 1), Visibility = Visibility.Collapsed };
        private readonly StackPanel _banners = new StackPanel();
        private readonly ContentControl _content = new ContentControl { Focusable = false };
        private Func<string> _titleText;

        public ToolWindowShell()
        {
            LastChildFill = true;
            ThemeManager.Instance.Attach(this);
            this.Bind(BackgroundProperty, Tokens.SurfaceBase);
            Footer = new StatusFooter();

            var titleDock = new DockPanel { LastChildFill = true };
            DockPanel.SetDock(_titleActions, Dock.Right);
            titleDock.Children.Add(_titleActions);
            titleDock.Children.Add(_title);
            _titleBar.Child = titleDock;
            _titleBar.Bind(Border.BorderBrushProperty, Tokens.BorderSubtle);
            _toolbarHost.Child = _toolbar;
            _toolbarHost.Bind(Border.BorderBrushProperty, Tokens.BorderSubtle);

            SetDock(_titleBar, Dock.Top);
            SetDock(_toolbarHost, Dock.Top);
            SetDock(_banners, Dock.Top);
            SetDock(Footer, Dock.Bottom);
            Children.Add(_titleBar);
            Children.Add(_toolbarHost);
            Children.Add(_banners);
            Children.Add(Footer);
            Children.Add(_content);
            Strings.Track(this, () =>
            {
                if (_titleText != null) _title.Text = _titleText();
                AutomationProperties.SetName(this, _title.Text ?? string.Empty);
            });
        }

        public StatusFooter Footer { get; }

        /// <summary>Title row text (VS already shows the pane caption; use for sub-titles). Null hides the row.</summary>
        public Func<string> TitleText
        {
            get => _titleText;
            set
            {
                _titleText = value;
                _title.Text = value?.Invoke() ?? string.Empty;
                AutomationProperties.SetName(this, _title.Text);
                UpdateTitleBar();
            }
        }

        public void AddTitleAction(UIElement action)
        {
            if (action is FrameworkElement fe) fe.Margin = new Thickness(2, 0, 0, 0);
            _titleActions.Children.Add(action);
            UpdateTitleBar();
        }

        /// <summary>Adds a toolbar item (left by default, or docked right).</summary>
        public void AddToolbarItem(UIElement item, bool right = false)
        {
            if (item is FrameworkElement fe && fe.Margin == default) fe.Margin = new Thickness(0, 0, 4, 0);
            SetDock(item, right ? Dock.Right : Dock.Left);
            _toolbar.Children.Add(item);
            _toolbarHost.Visibility = Visibility.Visible;
        }

        public void ShowBanner(Banner banner)
        {
            if (banner == null) return;
            banner.Margin = new Thickness(6, 6, 6, 0);
            banner.Closed += (s, e) => Ui.Safe("ui.toolwindow.banner_closed", () => _banners.Children.Remove(banner));
            _banners.Children.Add(banner);
        }

        public UIElement Body
        {
            get => _content.Content as UIElement;
            set => _content.Content = value;
        }

        public bool ShowFooter
        {
            get => Footer.Visibility == Visibility.Visible;
            set => Footer.Visibility = value ? Visibility.Visible : Visibility.Collapsed;
        }

        private void UpdateTitleBar() =>
            _titleBar.Visibility = _titleText != null || _titleActions.Children.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
    }
}
