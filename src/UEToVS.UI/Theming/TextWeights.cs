using System.Windows;

namespace UEToVS.UI.Theming
{
    /// <summary>
    /// Font weights of the UI text, as in Agent Panel (index.css --font-weight-normal): regular text is 500, one step
    /// heavier than usual. The bundled Noto Sans SC is unhinted and looks thin and uneven at 400 in the 11–13px UI sizes;
    /// at 500 the strokes hold. Every bundled UI family ships a 500 face; system fonts without one (Segoe UI, Microsoft
    /// YaHei UI) fall back to their regular face. Use <see cref="Body"/> wherever "not bold" is meant, not FontWeights.Normal.
    /// </summary>
    public static class TextWeights
    {
        public static readonly FontWeight Body = FontWeight.FromOpenTypeWeight(500);
    }
}
