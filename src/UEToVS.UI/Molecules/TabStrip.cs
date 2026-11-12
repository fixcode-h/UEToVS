using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Automation.Peers;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using UEToVS.UI.Logic;
using UEToVS.UI.Theming;

namespace UEToVS.UI.Molecules
{
    /// <summary>A tab of a <see cref="TabStrip"/>. Title is a function so it follows the UI language.</summary>
    public sealed class TabDefinition
    {
        public TabDefinition(string id, Func<string> title)
        {
            Id = id ?? throw new ArgumentNullException(nameof(id));
            Title = title ?? (() => id);
        }

        public string Id { get; }
        public Func<string> Title { get; }

        /// <summary>Optional tooltip / shortcut hint.</summary>
        public string Shortcut { get; set; }
    }

    /// <summary>
    /// Rider-style text tabs: gray titles, the selected one in primary color with an accent underline, optional
    /// counts. <see cref="Next"/>/<see cref="Previous"/> cycle (the palette maps Tab / Shift+Tab to them);
    /// ←/→ work when the strip itself has focus.
    /// </summary>
    public class TabStrip : Border
    {
        private readonly StackPanel _panel = new StackPanel { Orientation = Orientation.Horizontal };
        private readonly List<TabItemView> _views = new List<TabItemView>();
        private IReadOnlyList<TabDefinition> _tabs = Array.Empty<TabDefinition>();
        private int _selected = -1;

        public TabStrip()
        {
            Child = _panel;
            Focusable = true;
            FocusVisualStyle = null;
            KeyboardNavigation.SetDirectionalNavigation(this, KeyboardNavigationMode.None);
            AutomationProperties.SetName(this, "Tabs");
            Strings.Track(this, Relabel);
        }

        public event EventHandler SelectionChanged;

        public IReadOnlyList<TabDefinition> Tabs => _tabs;

        public void SetTabs(IReadOnlyList<TabDefinition> tabs, int selected = 0)
        {
            _tabs = tabs ?? Array.Empty<TabDefinition>();
            _panel.Children.Clear();
            _views.Clear();
            for (int i = 0; i < _tabs.Count; i++)
            {
                int idx = i;
                var v = new TabItemView(_tabs[i]);
                v.MouseLeftButtonDown += (s, e) => Ui.Safe("ui.tabs.click", () => { SelectedIndex = idx; e.Handled = true; });
                _views.Add(v);
                _panel.Children.Add(v);
            }
            _selected = -1;
            SelectedIndex = _tabs.Count == 0 ? -1 : Math.Max(0, Math.Min(selected, _tabs.Count - 1));
        }

        public int SelectedIndex
        {
            get => _selected;
            set
            {
                if (value < -1 || value >= _views.Count || value == _selected) return;
                _selected = value;
                for (int i = 0; i < _views.Count; i++) _views[i].IsSelected = i == value;
                SelectionChanged?.Invoke(this, EventArgs.Empty);
            }
        }

        public string SelectedId => _selected >= 0 && _selected < _tabs.Count ? _tabs[_selected].Id : null;

        public void Select(string id)
        {
            for (int i = 0; i < _tabs.Count; i++)
                if (_tabs[i].Id == id) { SelectedIndex = i; return; }
        }

        public void Next()
        {
            if (_views.Count > 0) SelectedIndex = (_selected + 1) % _views.Count;
        }

        public void Previous()
        {
            if (_views.Count > 0) SelectedIndex = (_selected - 1 + _views.Count) % _views.Count;
        }

        /// <summary>Sets the count shown next to a tab title (null hides it).</summary>
        public void SetCount(string id, int? count)
        {
            for (int i = 0; i < _tabs.Count; i++)
                if (_tabs[i].Id == id) _views[i].Count = count;
        }

        public void ClearCounts()
        {
            foreach (var v in _views) v.Count = null;
        }

        private void Relabel()
        {
            foreach (var v in _views) v.Relabel();
        }

        protected override void OnKeyDown(KeyEventArgs e)
        {
            base.OnKeyDown(e);
            Ui.Safe("ui.tabs.key", () =>
            {
                if (e.Key == Key.Right) { Next(); e.Handled = true; }
                else if (e.Key == Key.Left) { Previous(); e.Handled = true; }
            });
        }

        private sealed class TabItemView : Border
        {
            private readonly TabDefinition _def;
            private readonly TextBlock _title = new TextBlock { VerticalAlignment = VerticalAlignment.Center };
            private readonly TextBlock _count = new TextBlock { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(5, 0, 0, 0), Visibility = Visibility.Collapsed };
            private readonly Border _underline = new Border { Height = 2, CornerRadius = new CornerRadius(1), VerticalAlignment = VerticalAlignment.Bottom };
            private bool _selected;
            private int? _countValue;

            public TabItemView(TabDefinition def)
            {
                _def = def;
                var sp = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
                sp.Children.Add(_title);
                sp.Children.Add(_count);
                var grid = new Grid();
                grid.Children.Add(sp);
                grid.Children.Add(_underline);
                Child = grid;
                Padding = new Thickness(8, 0, 8, 0);
                Height = 30;
                Background = Brushes.Transparent;
                Cursor = Cursors.Hand;
                _count.Bind(TextBlock.ForegroundProperty, Tokens.TextSecondary);
                _count.Bind(TextBlock.FontSizeProperty, Tokens.FontSizeSmall);
                _underline.Bind(BackgroundProperty, Tokens.Accent);
                Relabel();
                Apply();
                MouseEnter += (s, e) => Ui.Safe("ui.tab.enter", () => { if (!_selected) _title.Bind(TextBlock.ForegroundProperty, Tokens.TextPrimary); });
                MouseLeave += (s, e) => Ui.Safe("ui.tab.leave", Apply);
            }

            public void Relabel()
            {
                _title.Text = _def.Title() ?? _def.Id;
                UpdateName();
                if (!string.IsNullOrEmpty(_def.Shortcut)) Controls.Tip.Set(this, _title.Text, _def.Shortcut);
            }

            public bool IsSelected
            {
                get => _selected;
                set
                {
                    _selected = value;
                    Apply();
                }
            }

            public int? Count
            {
                set
                {
                    _countValue = value;
                    _count.Text = value.HasValue ? PerfMath.FormatCount(value.Value) : string.Empty;
                    _count.Visibility = value.HasValue ? Visibility.Visible : Visibility.Collapsed;
                    UpdateName();
                }
            }

            private void UpdateName() =>
                AutomationProperties.SetName(this, _countValue.HasValue ? _title.Text + " " + _countValue.Value : _title.Text);

            private void Apply()
            {
                _title.Bind(TextBlock.ForegroundProperty, _selected ? Tokens.TextPrimary : Tokens.TextSecondary);
                _underline.Visibility = _selected ? Visibility.Visible : Visibility.Hidden;
            }

            protected override AutomationPeer OnCreateAutomationPeer() => new TabPeer(this);

            private sealed class TabPeer : FrameworkElementAutomationPeer
            {
                public TabPeer(TabItemView owner) : base(owner) { }

                protected override AutomationControlType GetAutomationControlTypeCore() => AutomationControlType.TabItem;

                protected override bool IsContentElementCore() => true;
            }
        }
    }
}
