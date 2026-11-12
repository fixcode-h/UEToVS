using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using Microsoft.VisualStudio.PlatformUI;
using Microsoft.VisualStudio.Shell;
using UEToVS.Search;

namespace UEToVS.UI
{
    /// <summary>VS theme resource keys and small helpers for code-built WPF UI (follows Dark/Light/Blue themes).</summary>
    internal static class Theme
    {
        public static readonly object Background = EnvironmentColors.ToolWindowBackgroundBrushKey;
        public static readonly object Foreground = EnvironmentColors.ToolWindowTextBrushKey;
        public static readonly object Border = EnvironmentColors.ToolWindowBorderBrushKey;
        public static readonly object Gray = EnvironmentColors.SystemGrayTextBrushKey;
        public static readonly object SelectedBackground = EnvironmentColors.SystemHighlightBrushKey;
        public static readonly object SelectedForeground = EnvironmentColors.SystemHighlightTextBrushKey;
        public static readonly object Accent = EnvironmentColors.ControlLinkTextBrushKey;
        public static readonly object HeaderBackground = EnvironmentColors.ToolWindowTabSelectedTabBrushKey;
        public static readonly object HeaderForeground = EnvironmentColors.ToolWindowTabSelectedTextBrushKey;
        public static readonly object InputBackground = CommonControlsColors.TextBoxBackgroundBrushKey;
        public static readonly object InputForeground = CommonControlsColors.TextBoxTextBrushKey;
        public static readonly object InputBorder = CommonControlsColors.TextBoxBorderBrushKey;

        public static void ApplyWindowFont(FrameworkElement e)
        {
            e.SetResourceReference(TextElement.FontFamilyProperty, VsFonts.EnvironmentFontFamilyKey);
            e.SetResourceReference(TextElement.FontSizeProperty, VsFonts.EnvironmentFontSizeKey);
        }

        public static Brush BadgeBrush(BadgeColor c)
        {
            switch (c)
            {
                case BadgeColor.Blue: return Frozen(Color.FromRgb(0x3C, 0x8D, 0xDE));
                case BadgeColor.Green: return Frozen(Color.FromRgb(0x4E, 0xA6, 0x52));
                case BadgeColor.Purple: return Frozen(Color.FromRgb(0x9C, 0x6A, 0xD6));
                case BadgeColor.Orange: return Frozen(Color.FromRgb(0xD8, 0x8B, 0x2E));
                case BadgeColor.Red: return Frozen(Color.FromRgb(0xC8, 0x5A, 0x5A));
                case BadgeColor.Teal: return Frozen(Color.FromRgb(0x2E, 0xA3, 0x9E));
                default: return Frozen(Color.FromRgb(0x80, 0x80, 0x80));
            }
        }

        private static Brush Frozen(Color c)
        {
            var b = new SolidColorBrush(c);
            b.Freeze();
            return b;
        }

        public static TextBlock Text(string text, object brushKey = null, bool bold = false)
        {
            var t = new TextBlock { Text = text ?? string.Empty, TextTrimming = TextTrimming.CharacterEllipsis, VerticalAlignment = VerticalAlignment.Center };
            t.SetResourceReference(TextBlock.ForegroundProperty, brushKey ?? Foreground);
            t.Tag = brushKey ?? Foreground; // theme key to restore after selection
            if (bold) t.FontWeight = FontWeights.SemiBold;
            return t;
        }

        /// <summary>Text with highlighted [start,len] ranges drawn bold in the accent color.</summary>
        public static TextBlock Highlighted(string text, int[] ranges, bool dimmed)
        {
            var tb = new TextBlock { TextTrimming = TextTrimming.CharacterEllipsis, VerticalAlignment = VerticalAlignment.Center };
            tb.SetResourceReference(TextBlock.ForegroundProperty, dimmed ? Gray : Foreground);
            tb.Tag = dimmed ? Gray : Foreground;
            text = text ?? string.Empty;
            var spans = new List<(int s, int l)>();
            for (int i = 0; ranges != null && i + 1 < ranges.Length; i += 2)
            {
                int s = System.Math.Max(0, ranges[i]);
                int l = System.Math.Min(ranges[i + 1], text.Length - s);
                if (l > 0) spans.Add((s, l));
            }
            spans.Sort((a, b) => a.s.CompareTo(b.s));
            int pos = 0;
            foreach (var (s, l) in spans)
            {
                if (s < pos) continue;
                if (s > pos) tb.Inlines.Add(new Run(text.Substring(pos, s - pos)));
                var r = new Run(text.Substring(s, l)) { FontWeight = FontWeights.Bold };
                r.SetResourceReference(TextElement.ForegroundProperty, Accent);
                tb.Inlines.Add(r);
                pos = s + l;
            }
            if (pos < text.Length) tb.Inlines.Add(new Run(text.Substring(pos)));
            return tb;
        }

        public static Border Badge(string text, BadgeColor color)
        {
            var b = new Border
            {
                Width = 18,
                Height = 16,
                CornerRadius = new CornerRadius(3),
                Background = BadgeBrush(color),
                Margin = new Thickness(0, 0, 8, 0),
                VerticalAlignment = VerticalAlignment.Center,
                Child = new TextBlock
                {
                    Text = text ?? "",
                    Foreground = Brushes.White,
                    FontSize = 10.5,
                    FontWeight = FontWeights.Bold,
                    HorizontalAlignment = HorizontalAlignment.Center,
                    VerticalAlignment = VerticalAlignment.Center,
                },
            };
            return b;
        }
    }
}
