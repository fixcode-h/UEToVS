using System;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using UEToVS.Host.Logic.Localization;
using UEToVS.Host.Logic.Settings;
using UEToVS.UI.Atoms;
using UEToVS.UI.Controls;
using UEToVS.UI.Logic;
using UEToVS.UI.Perf;
using UEToVS.UI.Theming;

namespace UEToVS.Host.Settings
{
    /// <summary>
    /// Settings window "当前索引状态" card (InfoBar-like): severity icon + headline, counts, the failure reason, a
    /// progress bar while indexing, the effective thread counts, and "查看日志" / "重建" when indexing failed. Normal
    /// states use the neutral settings-card surface; failures use the Error severity colours. The content comes from
    /// <see cref="IndexStatusView"/> (pure logic) and is updated in place every second without rebuilding the card.
    /// </summary>
    internal sealed class IndexStatusCard : Border
    {
        private readonly Border _icon = new Border { Width = 16, Height = 16, CornerRadius = new CornerRadius(8), VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(0, 2, 12, 0) };
        private readonly TextBlock _iconText = new TextBlock { FontWeight = FontWeights.Bold, FontSize = 11, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
        private readonly UiText _caption;
        private readonly UiText _title = new UiText((string)null, TextVariant.Strong) { TextWrapping = TextWrapping.Wrap, TextTrimming = TextTrimming.None };
        private readonly UiText _counts = new UiText((string)null, TextVariant.Secondary) { TextWrapping = TextWrapping.Wrap, TextTrimming = TextTrimming.None, Margin = new Thickness(0, 2, 0, 0) };
        private readonly UiText _reason = new UiText((string)null, TextVariant.Body) { TextWrapping = TextWrapping.Wrap, TextTrimming = TextTrimming.None, Margin = new Thickness(0, 6, 0, 0) };
        private readonly ProgressLine _progress = new ProgressLine { Height = 4, Margin = new Thickness(0, 8, 0, 2) };
        private readonly UiText _threads = new UiText((string)null, TextVariant.Caption) { TextWrapping = TextWrapping.Wrap, TextTrimming = TextTrimming.None, Margin = new Thickness(0, 6, 0, 0) };
        private readonly UiText _result = new UiText((string)null, TextVariant.Secondary) { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 12, 0), MaxWidth = 240, TextWrapping = TextWrapping.Wrap, TextTrimming = TextTrimming.None };
        private readonly UiButton _viewLog;
        private readonly UiButton _rebuild;
        private IndexStatusTone? _tone;

        public IndexStatusCard(string caption)
        {
            _caption = new UiText(caption, TextVariant.GroupLabel) { Margin = new Thickness(0, 0, 0, 4) };
            _icon.Child = _iconText;
            _viewLog = new UiButton(() => SettingsText.ViewLog) { MinWidth = 88 };
            _rebuild = new UiButton(() => SettingsText.Rebuild, ButtonKind.Primary) { MinWidth = 88, Margin = new Thickness(8, 0, 0, 0) };
            _viewLog.Click += (s, e) => ViewLogRequested?.Invoke(this, EventArgs.Empty);
            _rebuild.Click += (s, e) => RebuildRequested?.Invoke(this, EventArgs.Empty);

            var text = new StackPanel();
            text.Children.Add(_caption);
            text.Children.Add(_title);
            text.Children.Add(_counts);
            text.Children.Add(_reason);
            text.Children.Add(_progress);
            text.Children.Add(_threads);

            var actions = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(16, 0, 0, 0) };
            actions.Children.Add(_result);
            actions.Children.Add(_viewLog);
            actions.Children.Add(_rebuild);

            var grid = new Grid();
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            Grid.SetColumn(text, 1);
            Grid.SetColumn(actions, 2);
            grid.Children.Add(_icon);
            grid.Children.Add(text);
            grid.Children.Add(actions);

            Child = grid;
            Margin = new Thickness(0, 0, 0, SettingsPageLayout.GroupGap);
            MinHeight = SettingsCard.MinCardHeight;
            Padding = new Thickness(16, 14, 16, 14);
            BorderThickness = new Thickness(1);
            SnapsToDevicePixels = true;
            this.Bind(CornerRadiusProperty, Tokens.CornerRadiusPopup);
            AutomationProperties.SetLiveSetting(this, AutomationLiveSetting.Polite);
            Update(IndexStatusView.From(null));
        }

        public event EventHandler ViewLogRequested;
        public event EventHandler RebuildRequested;

        public void SetActionResult(string text) => _result.Text = text ?? string.Empty;

        public void Update(IndexStatusViewModel vm)
        {
            if (vm == null) return;
            if (_tone != vm.Tone)
            {
                _tone = vm.Tone;
                ApplyTone(vm.Tone);
            }
            Set(_title, vm.Title);
            Set(_counts, vm.Counts);
            Set(_reason, vm.Reason);
            Set(_threads, vm.Threads);
            _progress.Visibility = vm.ShowProgress ? Visibility.Visible : Visibility.Collapsed;
            if (vm.ShowProgress) _progress.Fraction = vm.Fraction;
            _viewLog.Visibility = vm.ShowViewLog ? Visibility.Visible : Visibility.Collapsed;
            _rebuild.Visibility = vm.ShowRebuild ? Visibility.Visible : Visibility.Collapsed;
            _rebuild.Margin = new Thickness(vm.ShowViewLog ? 8 : 0, 0, 0, 0);
            if (!vm.ShowRebuild && !vm.ShowViewLog) _result.Text = string.Empty;
            string name = vm.ToString();
            if (AutomationProperties.GetName(this) != name) AutomationProperties.SetName(this, name);
        }

        private static void Set(UiText t, string value)
        {
            value = value ?? string.Empty;
            if (t.Text != value) t.Text = value;
            t.Visibility = value.Length == 0 ? Visibility.Collapsed : Visibility.Visible;
        }

        private void ApplyTone(IndexStatusTone tone)
        {
            Severity? sev = tone == IndexStatusTone.Error ? Severity.Error
                : tone == IndexStatusTone.Warning ? Severity.Warning
                : tone == IndexStatusTone.Success ? Severity.Success
                : (Severity?)null;
            bool alert = tone == IndexStatusTone.Error || tone == IndexStatusTone.Warning;
            if (alert)
            {
                // InfoBar look: severity tint for the whole card.
                this.Bind(BackgroundProperty, Tokens.SeverityBackground(sev.Value));
                this.Bind(BorderBrushProperty, Tokens.SeverityBackground(sev.Value));
            }
            else
            {
                this.Bind(BackgroundProperty, Tokens.SurfaceRaised);
                this.Bind(BorderBrushProperty, Tokens.BorderSubtle);
            }
            if (sev.HasValue)
            {
                _icon.Visibility = Visibility.Visible;
                _icon.Bind(BackgroundProperty, Tokens.Severity(sev.Value));
                _iconText.Bind(TextBlock.ForegroundProperty, Tokens.SurfacePopup);
                _iconText.Text = sev.Value == Severity.Error ? "×" : sev.Value == Severity.Warning ? "!" : "✓";
            }
            else if (tone == IndexStatusTone.Progress)
            {
                _icon.Visibility = Visibility.Visible;
                _icon.Bind(BackgroundProperty, Tokens.Accent);
                _iconText.Bind(TextBlock.ForegroundProperty, Tokens.TextOnAccent);
                _iconText.Text = "↻";
            }
            else
            {
                _icon.Visibility = Visibility.Collapsed;
            }
            _progress.SetSeverity(sev);
        }
    }
}
