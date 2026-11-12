using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Media;
using System.Windows.Threading;
using Microsoft.VisualStudio.PlatformUI;
using UEToVS.Core.Diagnostics;
using UEToVS.UI.Logic;

namespace UEToVS.UI.Theming
{
    /// <summary>
    /// Owns the design tokens. (Re)builds them from the host theme palette + <see cref="AppearanceSettings"/>
    /// and pushes the values into every attached resource dictionary in place, so all components that reference
    /// tokens via <c>SetResourceReference</c> update live — no visual-tree walk.
    ///
    /// Usage: <c>ThemeManager.Instance.Attach(rootElement)</c> on each root (PopupShell/DialogShell/ToolWindowShell
    /// do this themselves). Host: call <see cref="Apply"/> with the saved settings at startup and after the options
    /// page is applied; theme changes are picked up from <c>VSColorTheme.ThemeChanged</c> automatically.
    /// Must be used on the UI thread.
    /// </summary>
    public sealed class ThemeManager
    {
        private static readonly Logger L = Log.For("UI");
        private static ThemeManager _instance;

        private readonly List<WeakReference<ResourceDictionary>> _dictionaries = new List<WeakReference<ResourceDictionary>>();
        private readonly Dictionary<object, object> _values = new Dictionary<object, object>();
        private AppearanceSettings _committed = new AppearanceSettings();
        private AppearanceSettings _preview;
        private IThemePaletteSource _source = new VsThemePaletteSource();
        private bool _vsHooked;
        private FontFamily _codeFont = new FontFamily("Cascadia Mono, Consolas, Courier New");
        private double? _codeFontSize;

        public static ThemeManager Instance
        {
            get
            {
                if (_instance == null)
                {
                    _instance = new ThemeManager();
                    _instance.Rebuild("init");
                    _instance.TryHookHostThemeEvents();
                }
                return _instance;
            }
        }

        private ThemeManager() { }

        /// <summary>Raised after tokens were rebuilt (theme change, settings applied/previewed).</summary>
        public event EventHandler Changed;

        /// <summary>The committed (saved) settings. Preview values are not reflected here.</summary>
        public AppearanceSettings Settings => _committed.Clone();

        /// <summary>The settings currently in effect (preview if one is active).</summary>
        public AppearanceSettings EffectiveSettings => (_preview ?? _committed).Clone();

        public bool IsPreviewing => _preview != null;

        /// <summary>Resolved tokens currently in effect.</summary>
        public TokenSet Current { get; private set; }

        public bool IsDark => Current?.IsDark ?? true;

        /// <summary>Animation duration in effect (Zero when animations are off).</summary>
        public TimeSpan AnimationDuration => TimeSpan.FromMilliseconds(Current?.Number(TokenNames.AnimationMs) ?? 0);

        public double RowHeight => Current?.Number(TokenNames.RowHeight) ?? 24;

        /// <summary>Where colors come from. Default: VS theme resources. Setting it rebuilds.</summary>
        public IThemePaletteSource PaletteSource
        {
            get => _source;
            set
            {
                _source = value ?? new VsThemePaletteSource();
                Rebuild("palette-source");
            }
        }

        /// <summary>Editor font for CodePreview (the host reads it from Fonts and Colors → Text Editor).</summary>
        public void SetCodeFont(FontFamily family, double? sizeDip)
        {
            if (family != null) _codeFont = family;
            _codeFontSize = sizeDip;
            Rebuild("code-font");
        }

        /// <summary>Commits new settings (normalized) and refreshes everything. Ends any preview.</summary>
        public void Apply(AppearanceSettings settings)
        {
            var s = (settings ?? new AppearanceSettings()).Clone();
            foreach (var n in s.Normalize()) L.Warn("ui.settings.normalized", n);
            _committed = s;
            _preview = null;
            Rebuild("apply");
        }

        /// <summary>Shows settings live without committing them (options page while editing).</summary>
        public void Preview(AppearanceSettings settings)
        {
            var s = (settings ?? new AppearanceSettings()).Clone();
            s.Normalize();
            _preview = s;
            Rebuild("preview");
        }

        /// <summary>Drops the preview and returns to the committed settings.</summary>
        public void EndPreview()
        {
            if (_preview == null) return;
            _preview = null;
            Rebuild("end-preview");
        }

        /// <summary>Re-reads the host palette (call when the host theme changed by other means).</summary>
        public void Refresh() => Rebuild("refresh");

        /// <summary>
        /// Makes <paramref name="root"/> resolve tokens: merges a token dictionary into its resources and sets the
        /// inherited UI font, font size, foreground and CrispImage background. Idempotent.
        /// </summary>
        public void Attach(FrameworkElement root)
        {
            if (root == null) return;
            foreach (var md in root.Resources.MergedDictionaries)
                if (md is TokenDictionary) return;
            var dict = new TokenDictionary();
            foreach (var kv in _values) dict[kv.Key] = kv.Value;
            root.Resources.MergedDictionaries.Add(dict);
            lock (_dictionaries)
            {
                _dictionaries.RemoveAll(w => !w.TryGetTarget(out _));
                _dictionaries.Add(new WeakReference<ResourceDictionary>(dict));
            }
            root.SetResourceReference(System.Windows.Documents.TextElement.FontFamilyProperty, Tokens.FontFamilyUi);
            root.SetResourceReference(System.Windows.Documents.TextElement.FontSizeProperty, Tokens.FontSizeBody);
            root.SetResourceReference(System.Windows.Documents.TextElement.ForegroundProperty, Tokens.TextPrimary);
            try
            {
                root.SetResourceReference(ImageThemingUtilities.ImageBackgroundColorProperty, Tokens.ColorOf(TokenNames.SurfacePopup));
            }
            catch (Exception ex)
            {
                L.Warn("ui.theme.image_bg_failed", "Could not bind CrispImage background color", ex);
            }
        }

        /// <summary>Marker type so Attach can detect an existing token dictionary.</summary>
        private sealed class TokenDictionary : ResourceDictionary { }

        private void TryHookHostThemeEvents()
        {
            if (_vsHooked) return;
            _vsHooked = true;
            try
            {
                HookVs();
            }
            catch (Exception ex)
            {
                // Outside VS (gallery) the VS theme service is not available.
                L.Debug("ui.theme.no_vs", "VSColorTheme not available: " + ex.GetType().Name);
            }
            try
            {
                SystemParameters.StaticPropertyChanged += (s, e) =>
                {
                    try
                    {
                        if (e.PropertyName == nameof(SystemParameters.HighContrast)) OnHostThemeChanged();
                    }
                    catch (Exception ex)
                    {
                        L.Error("ui.theme.hc_changed_failed", "High-contrast change handling failed", ex);
                    }
                };
            }
            catch (Exception ex)
            {
                L.Warn("ui.theme.hook_hc_failed", "Could not hook high-contrast changes", ex);
            }
        }

        // Separate method so a missing VS assembly surfaces as a catchable exception at the call site.
        [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
        private void HookVs()
        {
            VSColorTheme.ThemeChanged += _ =>
            {
                try { OnHostThemeChanged(); }
                catch (Exception ex) { L.Error("ui.theme.changed_failed", "Theme change handling failed", ex); }
            };
        }

        private void OnHostThemeChanged()
        {
            var d = Application.Current?.Dispatcher;
            if (d != null && !d.CheckAccess()) d.BeginInvoke(new Action(() => Rebuild("host-theme")), DispatcherPriority.Background);
            else Rebuild("host-theme");
        }

        private void Rebuild(string reason)
        {
            using (L.Time("ui.theme.rebuild", 20, ("reason", reason)))
            {
                ThemePalette palette = null;
                FontFamily uiFont = null;
                try
                {
                    if (_source != null && _source.TryRead(out var p)) { palette = p; uiFont = _source.UiFontFamily; }
                }
                catch (Exception ex)
                {
                    L.Warn("ui.theme.source_failed", "Palette source threw; using fallback", ex);
                }
                if (palette == null) palette = ThemePalette.FallbackDark();

                var settings = _preview ?? _committed;
                TokenSet tokens;
                try
                {
                    tokens = TokenDerivation.Derive(palette, settings);
                }
                catch (Exception ex)
                {
                    L.Error("ui.theme.derive_failed", "Token derivation failed; using defaults", ex);
                    tokens = TokenDerivation.Derive(ThemePalette.FallbackDark(), new AppearanceSettings());
                }
                Current = tokens;
                var values = BuildValues(tokens, uiFont);
                Push(values);
            }
            try
            {
                Changed?.Invoke(this, EventArgs.Empty);
            }
            catch (Exception ex)
            {
                L.Error("ui.theme.changed_handler_failed", "A ThemeManager.Changed handler threw", ex);
            }
        }

        private Dictionary<object, object> BuildValues(TokenSet t, FontFamily uiFont)
        {
            var v = new Dictionary<object, object>();
            foreach (var kv in t.Colors)
            {
                var c = Color.FromArgb(kv.Value.A, kv.Value.R, kv.Value.G, kv.Value.B);
                var b = new SolidColorBrush(c);
                b.Freeze();
                v[Tokens.Key(kv.Key)] = b;
                v[Tokens.ColorOf(kv.Key)] = c;
            }
            foreach (var kv in t.Numbers) v[Tokens.Key(kv.Key)] = kv.Value;

            double rp = t.Number(TokenNames.RadiusPopup), rc = t.Number(TokenNames.RadiusControl), rb = t.Number(TokenNames.RadiusBadge);
            v[Tokens.CornerRadiusPopup] = new CornerRadius(rp);
            v[Tokens.CornerRadiusControl] = new CornerRadius(rc);
            v[Tokens.CornerRadiusBadge] = new CornerRadius(rb);
            v[Tokens.CornerRadiusPill] = new CornerRadius(t.IsHighContrast ? 0 : 999);
            v[Tokens.CornerRadiusInner] = new CornerRadius(Math.Max(0, rc - 1));
            v[Tokens.CornerRadiusFocus] = new CornerRadius(t.IsHighContrast ? 0 : rc + 3);
            v[Tokens.ThicknessFocus] = new Thickness(t.Number(TokenNames.FocusThickness));
            double px = t.Number(TokenNames.PadControlX), py = t.Number(TokenNames.PadControlY);
            v[Tokens.ThicknessControlPadding] = new Thickness(px, py, px, py);
            v[Tokens.ThicknessRowPadding] = new Thickness(t.Number(TokenNames.PadRowX), 0, t.Number(TokenNames.PadRowX), 0);
            double pp = t.Number(TokenNames.PadPopup);
            v[Tokens.ThicknessPopupPadding] = new Thickness(pp);
            v[Tokens.FontFamilyUi] = uiFont ?? SystemFonts.MessageFontFamily;
            v[Tokens.FontFamilyCode] = _codeFont;
            if (_codeFontSize.HasValue && _codeFontSize.Value > 6) v[Tokens.FontSizeCode] = _codeFontSize.Value;
            v[Tokens.MatchFontWeight] = t.Number(TokenNames.MatchBold) > 0 ? FontWeights.Bold : FontWeights.Normal;
            if (t.Number(TokenNames.MatchUnderline) > 0)
            {
                var deco = new System.Windows.TextDecorationCollection(TextDecorations.Underline);
                deco.Freeze();
                v[Tokens.MatchDecorations] = deco;
            }
            else
            {
                var none = new System.Windows.TextDecorationCollection();
                none.Freeze();
                v[Tokens.MatchDecorations] = none;
            }
            return v;
        }

        private void Push(Dictionary<object, object> values)
        {
            var changed = new List<KeyValuePair<object, object>>();
            foreach (var kv in values)
            {
                if (_values.TryGetValue(kv.Key, out var old) && SameValue(old, kv.Value)) continue;
                _values[kv.Key] = kv.Value;
                changed.Add(kv);
            }
            if (changed.Count == 0) return;
            lock (_dictionaries)
            {
                _dictionaries.RemoveAll(w => !w.TryGetTarget(out _));
                foreach (var w in _dictionaries)
                {
                    if (!w.TryGetTarget(out var d)) continue;
                    foreach (var kv in changed)
                    {
                        try { d[kv.Key] = kv.Value; }
                        catch (Exception ex) { L.Error("ui.theme.push_failed", "Setting token resource failed: " + kv.Key, ex); }
                    }
                }
            }
        }

        private static bool SameValue(object a, object b)
        {
            if (a is SolidColorBrush ba && b is SolidColorBrush bb) return ba.Color == bb.Color;
            if (a is TextDecorationCollection ta && b is TextDecorationCollection tb) return ta.Count == tb.Count;
            return Equals(a, b);
        }
    }
}
