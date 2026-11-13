using System.Windows.Media;
using UEToVS.UI.Logic;

namespace UEToVS.UI.Theming
{
    /// <summary>
    /// Font part of the theme (设置 > 外观 > 字体): the UI font is the 英文字体 + 中文字体 fallback list, the code font the
    /// 代码字体 (CodePreview). Resolved during <c>Rebuild</c> into shared FontFamily objects (<see cref="BundledFonts"/>).
    /// </summary>
    public sealed partial class ThemeManager
    {
        private FontSettings _fonts = new FontSettings();

        /// <summary>The font choices in effect.</summary>
        public FontSettings Fonts => new FontSettings { UiLatin = _fonts.UiLatin, UiCjk = _fonts.UiCjk, Code = _fonts.Code, Custom = _fonts.Custom };

        /// <summary>Applies new font choices (host: at startup and when the settings window applies) and rebuilds once.</summary>
        public void SetFonts(FontSettings fonts)
        {
            var f = fonts ?? new FontSettings();
            _fonts = new FontSettings { UiLatin = f.UiLatin, UiCjk = f.UiCjk, Code = f.Code, Custom = f.Custom };
            Rebuild("fonts");
        }

        /// <summary>UI font: the chosen list, or VS's environment font when both rows follow VS (or the bundled files are missing).</summary>
        private FontFamily UiFontFor(FontFamily vsUi)
        {
            var refs = _fonts.ComposeUi(vsUi?.Source);
            return BundledFonts.Create(refs) ?? vsUi;
        }

        /// <summary>Code font: the chosen family with CJK and monospace fallbacks; "跟随 VS" = the VS Text Editor font (<see cref="SetCodeFont"/>).</summary>
        private FontFamily CodeFontFor(FontFamily vsEditor)
        {
            var refs = _fonts.ComposeCode(vsEditor?.Source, "Cascadia Mono, Consolas, Courier New");
            return BundledFonts.Create(refs) ?? vsEditor;
        }
    }
}
