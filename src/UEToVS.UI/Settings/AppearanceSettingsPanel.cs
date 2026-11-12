using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using UEToVS.UI.Atoms;
using UEToVS.UI.Controls;
using UEToVS.UI.Logic;
using UEToVS.UI.Molecules;
using UEToVS.UI.Theming;

namespace UEToVS.UI.Settings
{
    /// <summary>
    /// "工具 > 选项 > UEToVS > 外观" editor, built only from library components. Edits a copy of
    /// <see cref="AppearanceSettings"/>; with <see cref="LivePreview"/> every change is shown immediately through
    /// <see cref="ThemeManager.Preview"/> (the whole IDE's UEToVS UI and the sample on the right update live).
    ///
    /// Host (UIElementDialogPage): <c>Child = panel</c>; OnActivate → <see cref="Load"/>(saved);
    /// OnApply → save <see cref="Settings"/> + <see cref="Commit"/>; OnClosed/cancel → <see cref="Revert"/>.
    /// </summary>
    public class AppearanceSettingsPanel : Border
    {
        private AppearanceSettings _s = new AppearanceSettings();
        private bool _loading;

        private readonly SegmentedControl _preset = new SegmentedControl();
        private readonly NumberStepper _fontScale = new NumberStepper(AppearanceSettings.MinFontScale * 100, AppearanceSettings.MaxFontScale * 100, 5, 100);
        private readonly NumberStepper _rowHeight = new NumberStepper(AppearanceSettings.MinRowHeight, AppearanceSettings.MaxRowHeight, 1, 24);
        private readonly NumberStepper _rPopup = new NumberStepper(0, AppearanceSettings.MaxRadius, 1, 6);
        private readonly NumberStepper _rControl = new NumberStepper(0, AppearanceSettings.MaxRadius, 1, 4);
        private readonly NumberStepper _rBadge = new NumberStepper(0, AppearanceSettings.MaxRadius, 1, 3);
        private readonly NumberStepper _spacing = new NumberStepper(AppearanceSettings.MinSpacingScale * 100, AppearanceSettings.MaxSpacingScale * 100, 5, 100);
        private readonly SegmentedControl _accentMode = new SegmentedControl();
        private readonly ColorField _accent;
        private readonly UiCheckBox _mBold = new UiCheckBox(() => Strings.SettingsMatchBold) { Margin = new Thickness(0, 0, 14, 0) };
        private readonly UiCheckBox _mBack = new UiCheckBox(() => Strings.SettingsMatchBackground) { Margin = new Thickness(0, 0, 14, 0) };
        private readonly UiCheckBox _mUnder = new UiCheckBox(() => Strings.SettingsMatchUnderline) { Margin = new Thickness(0, 0, 14, 0) };
        private readonly UiCheckBox _mAccent = new UiCheckBox(() => Strings.SettingsMatchAccent);
        private readonly Dictionary<BadgeKind, ColorField> _badgeFields = new Dictionary<BadgeKind, ColorField>();
        private readonly UiToggle _shadow = new UiToggle((string)null);
        private readonly SegmentedControl _chrome = new SegmentedControl();
        private readonly UiToggle _anim = new UiToggle((string)null);
        private readonly NumberStepper _animMs = new NumberStepper(0, AppearanceSettings.MaxAnimationMs, 10, 100);
        private readonly UiToggle _previewVisible = new UiToggle((string)null);
        private readonly SegmentedControl _previewPos = new SegmentedControl();
        private readonly UiToggle _live = new UiToggle(() => Strings.SettingsLivePreview, true);
        private readonly Grid _layout = new Grid();
        private readonly FrameworkElement _form;
        private readonly FrameworkElement _sample;

        public AppearanceSettingsPanel()
        {
            ThemeManager.Instance.Attach(this);
            this.Bind(BackgroundProperty, Tokens.SurfaceBase);
            _accent = new ColorField(Strings.SettingsAccent);
            _form = BuildForm();
            _sample = BuildSample();
            Child = _layout;
            Relayout(true);
            SizeChanged += (s, e) => Ui.Safe("ui.settings.size", () => Relayout(e.NewSize.Width >= 760));
            Strings.Track(this, Relabel);
            Ui.TrackTheme(this, UpdateAutoColors);
            Wire();
            Load(ThemeManager.Instance.Settings);
        }

        /// <summary>Raised after every edit (settings already normalized).</summary>
        public event EventHandler SettingsChanged;

        /// <summary>Push edits to <see cref="ThemeManager.Preview"/> as they happen (default on).</summary>
        public bool LivePreview
        {
            get => _live.IsOn;
            set => _live.IsOn = value;
        }

        /// <summary>A copy of the edited settings.</summary>
        public AppearanceSettings Settings => _s.Clone();

        public void Load(AppearanceSettings settings)
        {
            _s = (settings ?? new AppearanceSettings()).Clone();
            _s.Normalize();
            _loading = true;
            try
            {
                _preset.SelectedIndex = (int)_s.Preset;
                LoadDensity();
                _accentMode.SelectedIndex = (int)_s.AccentMode;
                _accent.SetSilently(_s.CustomAccent);
                _accent.IsEnabled = _s.AccentMode == AccentMode.Custom;
                _mBold.IsChecked = (_s.MatchStyle & MatchHighlightStyle.Bold) != 0;
                _mBack.IsChecked = (_s.MatchStyle & MatchHighlightStyle.Background) != 0;
                _mUnder.IsChecked = (_s.MatchStyle & MatchHighlightStyle.Underline) != 0;
                _mAccent.IsChecked = (_s.MatchStyle & MatchHighlightStyle.AccentColor) != 0;
                foreach (var kv in _badgeFields) kv.Value.SetSilently(_s.GetBadgeColor(kv.Key));
                _shadow.IsOn = _s.PopupShadow;
                _chrome.SelectedIndex = (int)_s.PopupChrome;
                _anim.IsOn = _s.Animations;
                _animMs.SetSilently(_s.AnimationMs);
                _animMs.IsEnabled = _s.Animations;
                _previewVisible.IsOn = _s.PreviewVisible;
                _previewPos.SelectedIndex = (int)_s.PreviewPosition;
            }
            finally
            {
                _loading = false;
            }
            UpdateAutoColors();
        }

        /// <summary>Makes the edited settings the committed ones (ThemeManager.Apply).</summary>
        public void Commit() => ThemeManager.Instance.Apply(_s);

        /// <summary>Drops the live preview and reloads the committed settings.</summary>
        public void Revert()
        {
            ThemeManager.Instance.EndPreview();
            Load(ThemeManager.Instance.Settings);
        }

        public void ResetToDefaults()
        {
            Load(new AppearanceSettings());
            Changed();
        }

        private void LoadDensity()
        {
            _fontScale.SetSilently(Math.Round(_s.FontScale * 100));
            _rowHeight.SetSilently(_s.RowHeight);
            _rPopup.SetSilently(_s.PopupRadius);
            _rControl.SetSilently(_s.ControlRadius);
            _rBadge.SetSilently(_s.BadgeRadius);
            _spacing.SetSilently(Math.Round(_s.SpacingScale * 100));
        }

        private void Changed()
        {
            if (_loading) return;
            _s.Normalize();
            if (LivePreview) ThemeManager.Instance.Preview(_s);
            SettingsChanged?.Invoke(this, EventArgs.Empty);
        }

        private void Wire()
        {
            void On(string evt, Action a) => Ui.Safe(evt, () => { if (_loading) return; a(); Changed(); });
            _preset.SelectionChanged += (s, e) => On("ui.settings.preset", () =>
            {
                _s.ApplyPreset((StylePreset)_preset.SelectedIndex);
                _loading = true;
                try { LoadDensity(); } finally { _loading = false; }
            });
            _fontScale.ValueChanged += (s, e) => On("ui.settings.font", () => _s.FontScale = _fontScale.Value / 100.0);
            _rowHeight.ValueChanged += (s, e) => On("ui.settings.row", () => _s.RowHeight = _rowHeight.Value);
            _rPopup.ValueChanged += (s, e) => On("ui.settings.rpopup", () => _s.PopupRadius = _rPopup.Value);
            _rControl.ValueChanged += (s, e) => On("ui.settings.rcontrol", () => _s.ControlRadius = _rControl.Value);
            _rBadge.ValueChanged += (s, e) => On("ui.settings.rbadge", () => _s.BadgeRadius = _rBadge.Value);
            _spacing.ValueChanged += (s, e) => On("ui.settings.spacing", () => _s.SpacingScale = _spacing.Value / 100.0);
            _accentMode.SelectionChanged += (s, e) => On("ui.settings.accentmode", () =>
            {
                _s.AccentMode = (AccentMode)_accentMode.SelectedIndex;
                _accent.IsEnabled = _s.AccentMode == AccentMode.Custom;
            });
            _accent.ValueChanged += (s, e) => On("ui.settings.accent", () =>
            {
                if (_accent.Value != null) _s.CustomAccent = _accent.Value;
                _s.AccentMode = AccentMode.Custom;
            });
            foreach (var cb in new[] { _mBold, _mBack, _mUnder, _mAccent })
            {
                cb.Checked += (s, e) => On("ui.settings.match", ReadMatch);
                cb.Unchecked += (s, e) => On("ui.settings.match", ReadMatch);
            }
            foreach (var kv in _badgeFields)
            {
                var kind = kv.Key;
                var field = kv.Value;
                field.ValueChanged += (s, e) => On("ui.settings.badge", () => _s.SetBadgeColor(kind, field.Value));
            }
            _shadow.Checked += (s, e) => On("ui.settings.shadow", () => _s.PopupShadow = true);
            _shadow.Unchecked += (s, e) => On("ui.settings.shadow", () => _s.PopupShadow = false);
            _chrome.SelectionChanged += (s, e) => On("ui.settings.chrome", () => _s.PopupChrome = (PopupChromeMode)_chrome.SelectedIndex);
            _anim.Checked += (s, e) => On("ui.settings.anim", () => { _s.Animations = true; _animMs.IsEnabled = true; });
            _anim.Unchecked += (s, e) => On("ui.settings.anim", () => { _s.Animations = false; _animMs.IsEnabled = false; });
            _animMs.ValueChanged += (s, e) => On("ui.settings.animms", () => _s.AnimationMs = (int)_animMs.Value);
            _previewVisible.Checked += (s, e) => On("ui.settings.pv", () => _s.PreviewVisible = true);
            _previewVisible.Unchecked += (s, e) => On("ui.settings.pv", () => _s.PreviewVisible = false);
            _previewPos.SelectionChanged += (s, e) => On("ui.settings.pp", () => _s.PreviewPosition = (PreviewPosition)_previewPos.SelectedIndex);
            _live.Checked += (s, e) => Ui.Safe("ui.settings.live", () => ThemeManager.Instance.Preview(_s));
            _live.Unchecked += (s, e) => Ui.Safe("ui.settings.live", () => ThemeManager.Instance.EndPreview());
        }

        private void ReadMatch()
        {
            var m = MatchHighlightStyle.None;
            if (_mBold.IsChecked == true) m |= MatchHighlightStyle.Bold;
            if (_mBack.IsChecked == true) m |= MatchHighlightStyle.Background;
            if (_mUnder.IsChecked == true) m |= MatchHighlightStyle.Underline;
            if (_mAccent.IsChecked == true) m |= MatchHighlightStyle.AccentColor;
            _s.MatchStyle = m;
        }

        private void UpdateAutoColors()
        {
            var t = ThemeManager.Instance.Current;
            if (t == null) return;
            // Auto = what the theme derives without an override (badge foreground tone).
            var plain = TokenDerivation.Derive(new ThemePalette
            {
                Background = t.Color(TokenNames.SurfaceBase),
                Foreground = t.Color(TokenNames.TextPrimary),
                PopupBackground = t.Color(TokenNames.SurfacePopup),
                IsHighContrast = t.IsHighContrast,
            }, new AppearanceSettings());
            foreach (var kv in _badgeFields) kv.Value.AutoColor = plain.Color(TokenNames.BadgeForeground(kv.Key)).ToColor();
            _accent.AutoColor = t.Color(TokenNames.Accent).ToColor();
        }

        // ------------------------------------------------------------------ layout

        private readonly List<(UiText Label, Func<string> Text)> _labels = new List<(UiText, Func<string>)>();
        private readonly List<SegmentedControl> _segmented = new List<SegmentedControl>();

        private void Relabel()
        {
            foreach (var (l, f) in _labels) l.Text = f();
            RelabelSegments();
        }

        private void RelabelSegments()
        {
            int keep;
            keep = _preset.SelectedIndex;
            _loading = true;
            try
            {
                _preset.SetOptions(new[] { Strings.Preset(StylePreset.Vs2026), Strings.Preset(StylePreset.Compact), Strings.Preset(StylePreset.Comfortable) });
                _preset.SelectedIndex = Math.Max(0, keep);
                keep = _accentMode.SelectedIndex;
                _accentMode.SetOptions(new[] { Strings.SettingsAccentFollowVs, Strings.SettingsAccentCustom });
                _accentMode.SelectedIndex = Math.Max(0, keep);
                keep = _chrome.SelectedIndex;
                _chrome.SetOptions(new[] { Strings.SettingsChromeLayered, Strings.SettingsChromeNative });
                _chrome.SelectedIndex = Math.Max(0, keep);
                keep = _previewPos.SelectedIndex;
                _previewPos.SetOptions(new[] { Strings.SettingsPreviewPositionBottom, Strings.SettingsPreviewPositionRight });
                _previewPos.SelectedIndex = Math.Max(0, keep);
            }
            finally
            {
                _loading = false;
            }
        }

        private UiText Label(Func<string> text, TextVariant v = TextVariant.Body)
        {
            var t = new UiText(text(), v) { TextWrapping = TextWrapping.Wrap, TextTrimming = TextTrimming.None };
            _labels.Add((t, text));
            return t;
        }

        private Grid FieldRow(Func<string> label, UIElement control)
        {
            var g = new Grid { Margin = new Thickness(0, 4, 0, 4) };
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(170) }); // fits English labels
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            var l = Label(label);
            l.VerticalAlignment = VerticalAlignment.Center;
            l.Margin = new Thickness(0, 0, 10, 0);
            if (control is FrameworkElement fe)
            {
                fe.HorizontalAlignment = HorizontalAlignment.Left;
                fe.VerticalAlignment = VerticalAlignment.Center;
                AutomationProperties.SetLabeledBy(fe, l);
            }
            Grid.SetColumn((UIElement)control, 1);
            g.Children.Add(l);
            g.Children.Add(control);
            return g;
        }

        private StackPanel Section(Func<string> title, StackPanel into)
        {
            var t = Label(title, TextVariant.Title);
            t.Margin = new Thickness(0, 14, 0, 4);
            into.Children.Add(t);
            into.Children.Add(new Divider(false, 2));
            return into;
        }

        private FrameworkElement BuildForm()
        {
            RelabelSegments();
            var sp = new StackPanel { Margin = new Thickness(16, 4, 16, 16), MaxWidth = 640, HorizontalAlignment = HorizontalAlignment.Left };

            Section(() => Strings.SettingsSectionStyle, sp);
            sp.Children.Add(FieldRow(() => Strings.SettingsPreset, _preset));
            sp.Children.Add(FieldRow(() => Strings.SettingsFontScale, _fontScale));
            sp.Children.Add(FieldRow(() => Strings.SettingsRowHeight, _rowHeight));
            sp.Children.Add(FieldRow(() => Strings.SettingsRadiusPopup, _rPopup));
            sp.Children.Add(FieldRow(() => Strings.SettingsRadiusControl, _rControl));
            sp.Children.Add(FieldRow(() => Strings.SettingsRadiusBadge, _rBadge));
            sp.Children.Add(FieldRow(() => Strings.SettingsSpacing, _spacing));

            Section(() => Strings.SettingsSectionColor, sp);
            var accentRow = new StackPanel { Orientation = Orientation.Horizontal };
            accentRow.Children.Add(_accentMode);
            _accent.Margin = new Thickness(10, 0, 0, 0);
            accentRow.Children.Add(_accent);
            sp.Children.Add(FieldRow(() => Strings.SettingsAccent, accentRow));
            var match = new WrapPanel();
            match.Children.Add(_mBold);
            match.Children.Add(_mBack);
            match.Children.Add(_mUnder);
            match.Children.Add(_mAccent);
            sp.Children.Add(FieldRow(() => Strings.SettingsMatch, match));
            var badgeTitle = Label(() => Strings.SettingsBadges, TextVariant.Secondary);
            badgeTitle.Margin = new Thickness(0, 8, 0, 4);
            sp.Children.Add(badgeTitle);
            var badges = new WrapPanel();
            foreach (BadgeKind k in TokenNames.AllBadgeKinds)
            {
                if (k == BadgeKind.Neutral) continue;
                var field = new ColorField(Strings.Badge(k));
                _badgeFields[k] = field;
                var cell = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 3, 18, 3), Width = 290 };
                var kind = k;
                var badge = new Badge(Strings.Badge(k), k) { Width = 92, HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(0, 0, 8, 0) };
                Strings.Track(badge, () => badge.Text = Strings.Badge(kind));
                cell.Children.Add(badge);
                cell.Children.Add(field);
                badges.Children.Add(cell);
            }
            sp.Children.Add(badges);

            Section(() => Strings.SettingsSectionMotion, sp);
            sp.Children.Add(FieldRow(() => Strings.SettingsShadow, _shadow));
            sp.Children.Add(FieldRow(() => Strings.SettingsChrome, _chrome));
            sp.Children.Add(FieldRow(() => Strings.SettingsAnimations, _anim));
            sp.Children.Add(FieldRow(() => Strings.SettingsAnimationMs, _animMs));

            Section(() => Strings.SettingsSectionPreview, sp);
            sp.Children.Add(FieldRow(() => Strings.SettingsPreviewVisible, _previewVisible));
            sp.Children.Add(FieldRow(() => Strings.SettingsPreviewPosition, _previewPos));

            var bottom = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 18, 0, 0) };
            var reset = new UiButton(() => Strings.CommonReset, ButtonKind.Secondary);
            reset.Click += (s, e) => Ui.Safe("ui.settings.reset", ResetToDefaults);
            bottom.Children.Add(reset);
            _live.Margin = new Thickness(16, 0, 0, 0);
            _live.VerticalAlignment = VerticalAlignment.Center;
            bottom.Children.Add(_live);
            sp.Children.Add(bottom);

            return new UiScrollViewer { Content = sp };
        }

        private FrameworkElement BuildSample()
        {
            var sp = new StackPanel { Margin = new Thickness(12) };
            var title = Label(() => Strings.SettingsSampleTitle, TextVariant.Title);
            title.Margin = new Thickness(0, 4, 0, 8);
            sp.Children.Add(title);

            var card = new Border { BorderThickness = new Thickness(1), Width = 320, HorizontalAlignment = HorizontalAlignment.Left };
            card.Bind(Border.BackgroundProperty, Tokens.SurfacePopup);
            card.Bind(Border.BorderBrushProperty, Tokens.BorderStrong);
            card.Bind(Border.CornerRadiusProperty, Tokens.CornerRadiusPopup);
            var inner = new StackPanel { Margin = new Thickness(0, 4, 0, 4) };
            var tabs = new TabStrip();
            tabs.SetTabs(new[] { new TabDefinition("all", () => Loc2("全部", "All")), new TabDefinition("types", () => Loc2("类型", "Types")), new TabDefinition("files", () => Loc2("文件", "Files")) });
            tabs.SetCount("types", 12);
            tabs.Margin = new Thickness(6, 0, 6, 0);
            inner.Children.Add(tabs);
            var search = new SearchInput(true) { Text = "ACha" };
            search.Padding = new Thickness(12, 6, 8, 6);
            inner.Children.Add(search);
            inner.Children.Add(new Divider());
            var rows = new (string P, int[] R, string S, BadgeKind B, string Bt, SymbolKind G, bool Sel)[]
            {
                ("ACharacter", new[] { 0, 5 }, "Engine · Character.h", BadgeKind.UClass, "UCLASS", SymbolKind.Class, true),
                ("AChaosVehicle", new[] { 0, 4 }, "ChaosVehicles", BadgeKind.UClass, "UCLASS", SymbolKind.Class, false),
                ("FCharacterMoveResponse", new[] { 1, 4 }, "CharacterMovementComponent.h", BadgeKind.UStruct, "USTRUCT", SymbolKind.Struct, false),
                ("ApplyCharge", new[] { 0, 1, 5, 3 }, "UMyAbility", BadgeKind.UFunction, "UFUNCTION", SymbolKind.Function, false),
            };
            foreach (var r in rows)
            {
                var row = new ListRow();
                row.SetResourceReference(HeightProperty, Tokens.RowHeight);
                var d = new RowData { Primary = r.P, PrimaryRanges = HighlightRanges.FromPairs(r.R), Secondary = r.S, Glyph = r.G };
                d.AddBadge(r.Bt, r.B);
                row.Bind(d, 0, 0);
                row.SetState(r.Sel, false, true);
                inner.Children.Add(row);
            }
            var footer = new StatusFooter();
            footer.SetHints(new KeyHint("Enter", () => Strings.PaletteHintOpen));
            footer.AutoHideHints = false;
            footer.Count = "1 / 4";
            inner.Children.Add(footer);
            card.Child = inner;
            sp.Children.Add(card);

            var buttons = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 12, 0, 0) };
            buttons.Children.Add(new UiButton(() => Strings.CommonOk, ButtonKind.Primary));
            buttons.Children.Add(new UiButton(() => Strings.CommonCancel, ButtonKind.Secondary) { Margin = new Thickness(8, 0, 0, 0) });
            buttons.Children.Add(new Kbd("Ctrl+Shift+T") { Margin = new Thickness(12, 0, 0, 0) });
            sp.Children.Add(buttons);
            var badges = new WrapPanel { Margin = new Thickness(0, 10, 0, 0), Width = 320, HorizontalAlignment = HorizontalAlignment.Left };
            foreach (BadgeKind k in TokenNames.AllBadgeKinds)
                badges.Children.Add(new Badge(Strings.Badge(k), k) { Margin = new Thickness(0, 0, 4, 4) });
            sp.Children.Add(badges);
            return new UiScrollViewer { Content = sp };
        }

        private static string Loc2(string zh, string en) => Core.Localization.Loc.T(zh, en);

        private bool? _wide;

        private void Relayout(bool wide)
        {
            if (_wide == wide) return;
            _wide = wide;
            _layout.Children.Clear();
            _layout.RowDefinitions.Clear();
            _layout.ColumnDefinitions.Clear();
            if (wide)
            {
                _layout.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                _layout.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                Grid.SetColumn(_form, 0);
                Grid.SetRow(_form, 0);
                Grid.SetColumn(_sample, 1);
                Grid.SetRow(_sample, 0);
            }
            else
            {
                _layout.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
                _layout.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto, MaxHeight = 300 });
                Grid.SetRow(_form, 0);
                Grid.SetColumn(_form, 0);
                Grid.SetRow(_sample, 1);
                Grid.SetColumn(_sample, 0);
            }
            _layout.Children.Add(_form);
            _layout.Children.Add(_sample);
        }
    }
}
