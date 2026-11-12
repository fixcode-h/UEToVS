using System;
using System.Windows;
using System.Windows.Media;
using Microsoft.VisualStudio.PlatformUI;
using Microsoft.VisualStudio.Shell;
using UEToVS.Core.Diagnostics;
using UEToVS.UI.Logic;

namespace UEToVS.UI.Theming
{
    /// <summary>Supplies the raw host theme colors. The default reads VS theme resources.</summary>
    public interface IThemePaletteSource
    {
        /// <summary>Returns false when no host theme is available (e.g. the gallery outside VS).</summary>
        bool TryRead(out ThemePalette palette);

        /// <summary>The host UI font (VS environment font), or null.</summary>
        FontFamily UiFontFamily { get; }
    }

    /// <summary>
    /// Reads VS theme colors from the application resources (VS publishes every <c>ThemeResourceKey</c> there),
    /// so it needs no VS services and works on any thread that owns the Application dispatcher.
    /// </summary>
    public sealed class VsThemePaletteSource : IThemePaletteSource
    {
        private static readonly Logger L = Log.For("UI");

        public FontFamily UiFontFamily { get; private set; }

        public bool TryRead(out ThemePalette palette)
        {
            palette = null;
            try
            {
                var app = Application.Current;
                if (app == null) return false;
                var bg = Get(app, EnvironmentColors.ToolWindowBackgroundColorKey);
                var fg = Get(app, EnvironmentColors.ToolWindowTextColorKey);
                if (!bg.HasValue || !fg.HasValue) return false;
                palette = new ThemePalette
                {
                    Name = "VS",
                    Background = bg.Value,
                    Foreground = fg.Value,
                    PopupBackground = Get(app, EnvironmentColors.CommandBarMenuBackgroundGradientBeginColorKey),
                    Border = Get(app, EnvironmentColors.ToolWindowBorderColorKey),
                    GrayText = Get(app, EnvironmentColors.SystemGrayTextColorKey),
                    Accent = Get(app, EnvironmentColors.SystemHighlightColorKey),
                    SystemAccent = ReadWindowsAccent(),
                    InputBackground = Get(app, CommonControlsColors.TextBoxBackgroundColorKey),
                    InputText = Get(app, CommonControlsColors.TextBoxTextColorKey),
                    InputBorder = Get(app, CommonControlsColors.TextBoxBorderColorKey),
                    InputBorderFocused = Get(app, CommonControlsColors.TextBoxBorderFocusedColorKey),
                    ToolTipBackground = Get(app, EnvironmentColors.ToolTipColorKey),
                    ToolTipText = Get(app, EnvironmentColors.ToolTipTextColorKey),
                    Link = Get(app, EnvironmentColors.ControlLinkTextColorKey),
                    IsHighContrast = SystemParameters.HighContrast,
                };
                if (app.TryFindResource(VsFonts.EnvironmentFontSizeKey) is double size && size > 6) palette.BaseFontSize = size;
                UiFontFamily = app.TryFindResource(VsFonts.EnvironmentFontFamilyKey) as FontFamily;
                return true;
            }
            catch (Exception ex)
            {
                L.Warn("ui.theme.read_failed", "Reading VS theme colors failed; using fallback palette", ex);
                palette = null;
                return false;
            }
        }

        /// <summary>
        /// Windows accent color (what Windows 11 Fluent controls use): HKCU\Software\Microsoft\Windows\DWM\AccentColor,
        /// a DWORD in 0xAABBGGRR order. Null when missing or unreadable (the VS theme accent is used then).
        /// </summary>
        internal static Rgba? ReadWindowsAccent()
        {
            try
            {
                using (var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\DWM"))
                {
                    if (key?.GetValue("AccentColor") is int abgr)
                    {
                        uint v = unchecked((uint)abgr);
                        return new Rgba((byte)(v & 0xFF), (byte)((v >> 8) & 0xFF), (byte)((v >> 16) & 0xFF), 255);
                    }
                }
            }
            catch (Exception ex)
            {
                L.Debug("ui.theme.system_accent_failed", "Reading the Windows accent color failed: " + ex.GetType().Name);
            }
            return null;
        }

        private static Rgba? Get(Application app, object key)
        {
            var v = app.TryFindResource(key);
            if (v is Color c) return new Rgba(c.R, c.G, c.B, c.A);
            if (v is SolidColorBrush b) return new Rgba(b.Color.R, b.Color.G, b.Color.B, b.Color.A);
            return null;
        }
    }

    /// <summary>A fixed palette (gallery theme toggle, tests, or a host that computes colors itself).</summary>
    public sealed class FixedPaletteSource : IThemePaletteSource
    {
        private readonly ThemePalette _palette;

        public FixedPaletteSource(ThemePalette palette, FontFamily font = null)
        {
            _palette = palette ?? throw new ArgumentNullException(nameof(palette));
            UiFontFamily = font;
        }

        public FontFamily UiFontFamily { get; }

        public bool TryRead(out ThemePalette palette)
        {
            palette = _palette.Clone();
            return true;
        }
    }
}
