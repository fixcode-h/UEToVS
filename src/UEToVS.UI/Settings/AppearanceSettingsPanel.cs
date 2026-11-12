using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
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
        private readonly Chip _mBold = new Chip(() => Strings.SettingsMatchBold);
        private readonly Chip _mBack = new Chip(() => Strings.SettingsMatchBackground);
        private readonly Chip _mUnder = new Chip(() => Strings.SettingsMatchUnderline);
        private readonly Chip _mAccent = new Chip(() => Strings.SettingsMatchAccent);
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
        private UiButton _resetButton;
        private bool _embedded;

        public AppearanceSettingsPanel()
        {
            ThemeManager.Instance.Attach(this);
            this.Bind(BackgroundProperty, Tokens.SurfaceBase);
            _accent = new ColorField(Strings.SettingsAccent);
            _form = BuildForm();
            _sample = BuildSample();
            Child = _layout;
            Relayout(true);
            SizeChanged += (s, e) => Ui.Safe("ui.settings.size", () => Relayout(e.NewSize.Width >= 780));
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

        /// <summary>
        /// Hosted inside the UEToVS settings window: transparent background (the window's surface shows through) and no
        /// own "恢复默认" button (the window footer resets the page).
        /// </summary>
        public bool EmbeddedInSettingsWindow
        {
            get => _embedded;
            set
            {
                _embedded = value;
                if (value) Background = Brushes.Transparent;
                else this.Bind(BackgroundProperty, Tokens.SurfaceBase);
                if (_resetButton != null) _resetButton.Visibility = value ? Visibility.Collapsed : Visibility.Visible;
                if (_live != null) _live.Margin = new Thickness(value ? 0 : 16, 0, 0, 0);
            }
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
            foreach (var (g, f) in _groups) g.Label = f();
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

        /// <summary>One setting row (title + description left, editor right) inside a <see cref="SettingsGroup"/>.</summary>
        private SettingsRow FieldRow(Func<string> title, Func<string> description, UIElement editor)
        {
            var row = new SettingsRow(title(), description?.Invoke(), editor);
            var titleText = (UiText)row.Labels.Children[0];
            _labels.Add((titleText, title));
            if (description != null && row.Labels.Children.Count > 1) _labels.Add(((UiText)row.Labels.Children[1], description));
            if (editor is FrameworkElement fe) AutomationProperties.SetLabeledBy(fe, titleText);
            return row;
        }

        private SettingsGroup Group(Func<string> label, Panel into)
        {
            var g = new SettingsGroup(label()) { Margin = new Thickness(0, 0, 0, SettingsPageLayout.GroupGap) };
            _groups.Add((g, label));
            into.Children.Add(g);
            return g;
        }

        private readonly List<(SettingsGroup Group, Func<string> Text)> _groups = new List<(SettingsGroup, Func<string>)>();

        /// <summary>"弹窗 [12]" — a caption and a narrow number box (combined radius row).</summary>
        private UIElement Labelled(Func<string> caption, NumberStepper box, double leftGap)
        {
            var p = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(leftGap, 0, 0, 0) };
            var t = Label(caption, TextVariant.Description);
            t.VerticalAlignment = VerticalAlignment.Center;
            t.Margin = new Thickness(0, 0, 6, 0);
            p.Children.Add(t);
            box.MinWidth = 64;
            box.Width = 64;
            AutomationProperties.SetLabeledBy(box, t);
            p.Children.Add(box);
            return p;
        }

        private FrameworkElement BuildForm()
        {
            RelabelSegments();
            // Right margin keeps the cards clear of the overlay scroll bar.
            var sp = new StackPanel { Margin = new Thickness(0, 0, 12, 0), MaxWidth = 760 };
            _fontScale.Unit = "%";
            _spacing.Unit = "%";
            _rowHeight.Unit = "px";
            _animMs.Unit = "ms";
            foreach (var n in new[] { _fontScale, _spacing, _rowHeight, _animMs }) n.Width = 116;

            var style = Group(() => Strings.SettingsSectionStyle, sp);
            style.Add(FieldRow(() => Strings.SettingsPreset, () => Strings.SettingsPresetDesc, _preset));
            style.Add(FieldRow(() => Strings.SettingsFontScale, () => Strings.SettingsFontScaleDesc, _fontScale));
            style.Add(FieldRow(() => Strings.SettingsRowHeight, () => Strings.SettingsRowHeightDesc, _rowHeight));
            style.Add(FieldRow(() => Strings.SettingsSpacing, () => Strings.SettingsSpacingDesc, _spacing));
            var radii = new StackPanel { Orientation = Orientation.Horizontal };
            radii.Children.Add(Labelled(() => Strings.SettingsRadiusPopupShort, _rPopup, 0));
            radii.Children.Add(Labelled(() => Strings.SettingsRadiusControlShort, _rControl, 14));
            radii.Children.Add(Labelled(() => Strings.SettingsRadiusBadgeShort, _rBadge, 14));
            style.Add(FieldRow(() => Strings.SettingsRadius, () => Strings.SettingsRadiusDesc, radii));

            var color = Group(() => Strings.SettingsSectionColor, sp);
            var accentRow = new StackPanel { Orientation = Orientation.Horizontal };
            accentRow.Children.Add(_accentMode);
            _accent.Margin = new Thickness(10, 0, 0, 0);
            accentRow.Children.Add(_accent);
            color.Add(FieldRow(() => Strings.SettingsAccent, () => Strings.SettingsAccentDesc, accentRow));
            var match = new WrapPanel { HorizontalAlignment = HorizontalAlignment.Right };
            _mBold.FontWeight = FontWeights.SemiBold;
            foreach (var chip in new[] { _mBold, _mBack, _mUnder, _mAccent })
            {
                chip.Height = 28;
                chip.Padding = new Thickness(10, 0, 10, 0);
                chip.Margin = new Thickness(6, 0, 0, 0);
                match.Children.Add(chip);
            }
            color.Add(FieldRow(() => Strings.SettingsMatch, () => Strings.SettingsMatchDesc, match));

            // Badge colors: title + description on one line, then a 3-column grid of compact cells.
            var badgeRow = new SettingsRow(Strings.SettingsBadges, null);
            _labels.Add(((UiText)badgeRow.Labels.Children[0], () => Strings.SettingsBadges));
            var desc = Label(() => Strings.SettingsBadgesDesc, TextVariant.Description);
            desc.Margin = new Thickness(0, 2, 0, 0);
            badgeRow.Labels.Children.Add(desc);
            var grid = new UniformGrid { Columns = 3 };
            foreach (BadgeKind k in TokenNames.AllBadgeKinds)
            {
                if (k == BadgeKind.Neutral) continue;
                var field = new ColorField(Strings.Badge(k), compact: true) { HorizontalAlignment = HorizontalAlignment.Right };
                _badgeFields[k] = field;
                var kind = k;
                var badge = new Badge(Strings.Badge(k), k) { VerticalAlignment = VerticalAlignment.Center };
                Strings.Track(badge, () => badge.Text = Strings.Badge(kind));
                var cellGrid = new DockPanel { LastChildFill = false };
                DockPanel.SetDock(badge, Dock.Left);
                DockPanel.SetDock(field, Dock.Right);
                cellGrid.Children.Add(badge);
                cellGrid.Children.Add(field);
                var cell = new Border { Child = cellGrid, Height = 34, Padding = new Thickness(8, 0, 6, 0), Margin = new Thickness(0, 0, 8, 8), BorderThickness = new Thickness(1) };
                cell.Bind(Border.BackgroundProperty, Tokens.SurfacePopup);
                cell.Bind(Border.BorderBrushProperty, Tokens.Divider);
                cell.Bind(Border.CornerRadiusProperty, Tokens.CornerRadiusControl);
                grid.Children.Add(cell);
            }
            grid.Margin = new Thickness(0, 10, -8, -8);
            badgeRow.Below = grid;
            badgeRow.Padding = new Thickness(16, 12, 16, 14);
            color.Add(badgeRow);

            var motion = Group(() => Strings.SettingsSectionMotion, sp);
            motion.Add(FieldRow(() => Strings.SettingsShadow, () => Strings.SettingsShadowDesc, _shadow));
            motion.Add(FieldRow(() => Strings.SettingsChrome, () => Strings.SettingsChromeDesc, _chrome));
            motion.Add(FieldRow(() => Strings.SettingsAnimations, null, _anim));
            motion.Add(FieldRow(() => Strings.SettingsAnimationMs, null, _animMs));

            var preview = Group(() => Strings.SettingsSectionPreview, sp);
            preview.Add(FieldRow(() => Strings.SettingsPreviewVisible, () => Strings.SettingsPreviewVisibleDesc, _previewVisible));
            preview.Add(FieldRow(() => Strings.SettingsPreviewPosition, null, _previewPos));

            var bottom = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 16) };
            var reset = new UiButton(() => Strings.CommonReset, ButtonKind.Secondary);
            reset.Click += (s, e) => Ui.Safe("ui.settings.reset", ResetToDefaults);
            _resetButton = reset;
            bottom.Children.Add(reset);
            _live.Margin = new Thickness(16, 0, 0, 0);
            _live.VerticalAlignment = VerticalAlignment.Center;
            bottom.Children.Add(_live);
            sp.Children.Add(bottom);

            return new UiScrollViewer { Content = sp, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled };
        }

        private FrameworkElement BuildSample()
        {
            var sp = new StackPanel { Width = 332 };
            var title = Label(() => Strings.SettingsSampleTitle, TextVariant.GroupLabel);
            title.Margin = new Thickness(2, 0, 0, 8);
            sp.Children.Add(title);

            var card = new Border { BorderThickness = new Thickness(1), ClipToBounds = true };
            card.Bind(Border.BackgroundProperty, Tokens.SurfaceRaised);
            card.Bind(Border.BorderBrushProperty, Tokens.BorderSubtle);
            card.Bind(Border.CornerRadiusProperty, Tokens.CornerRadiusPopup);
            var inner = new StackPanel { Margin = new Thickness(0, 4, 0, 0) };
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
            // The status footer draws its own top divider; no extra line here (one hairline, not two).
            var footer = new StatusFooter { Margin = new Thickness(0, 4, 0, 0) };
            footer.SetHints(new KeyHint("Enter", () => Strings.PaletteHintOpen));
            footer.AutoHideHints = false;
            footer.Count = "1 / 4";
            inner.Children.Add(footer);
            card.Child = inner;
            sp.Children.Add(card);

            var buttons = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 12, 0, 0) };
            buttons.Children.Add(new UiButton(() => Strings.SampleButtonPrimary, ButtonKind.Primary));
            buttons.Children.Add(new UiButton(() => Strings.SampleButtonSecondary, ButtonKind.Secondary) { Margin = new Thickness(8, 0, 0, 0) });
            buttons.Children.Add(new UiButton(() => Strings.SampleButtonText, ButtonKind.Ghost) { Margin = new Thickness(8, 0, 0, 0) });
            sp.Children.Add(buttons);
            return new UiScrollViewer { Content = sp, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled, VerticalAlignment = VerticalAlignment.Top };
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
                _sample.Margin = new Thickness(28, 0, 0, 0);
            }
            else
            {
                _layout.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
                _layout.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto, MaxHeight = 300 });
                Grid.SetRow(_form, 0);
                Grid.SetColumn(_form, 0);
                Grid.SetRow(_sample, 1);
                Grid.SetColumn(_sample, 0);
                _sample.Margin = new Thickness(0, 12, 0, 0);
            }
            _layout.Children.Add(_form);
            _layout.Children.Add(_sample);
        }
    }
}
