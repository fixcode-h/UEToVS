using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Documents;
using TextRange = UEToVS.UI.Logic.TextRange;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Shapes;
using Microsoft.VisualStudio.Imaging;
using Microsoft.VisualStudio.Imaging.Interop;
using UEToVS.UI.Logic;
using UEToVS.UI.Theming;

namespace UEToVS.UI.Atoms
{
    public enum TextVariant
    {
        Body,
        Secondary,
        Caption,
        Small,
        Title,
        Strong,
        Mono,
        Disabled,
        Link,
        /// <summary>Group header inside a page (settings groups): semibold, body + 2.</summary>
        Subtitle,
        /// <summary>Page title (settings page heading): semibold, body + 8.</summary>
        Display,
    }

    /// <summary>Text atom: a TextBlock whose font size / color come from tokens per <see cref="Variant"/>.</summary>
    public class UiText : TextBlock
    {
        private TextVariant _variant;

        public UiText() : this((string)null, TextVariant.Body) { }

        /// <summary>Localized text: re-evaluated when the UI language changes.</summary>
        public UiText(Func<string> text, TextVariant variant = TextVariant.Body) : this(text?.Invoke(), variant)
        {
            if (text != null) Strings.Track(this, () => Text = text() ?? string.Empty);
        }

        public UiText(string text, TextVariant variant = TextVariant.Body)
        {
            Text = text ?? string.Empty;
            TextTrimming = TextTrimming.CharacterEllipsis;
            VerticalAlignment = VerticalAlignment.Center;
            Variant = variant;
        }

        public TextVariant Variant
        {
            get => _variant;
            set
            {
                _variant = value;
                Apply();
            }
        }

        private void Apply()
        {
            object fg = Tokens.TextPrimary, size = Tokens.FontSizeBody;
            FontWeight weight = FontWeights.Normal;
            switch (_variant)
            {
                case TextVariant.Secondary: fg = Tokens.TextSecondary; break;
                case TextVariant.Caption: fg = Tokens.TextSecondary; size = Tokens.FontSizeCaption; break;
                case TextVariant.Small: fg = Tokens.TextSecondary; size = Tokens.FontSizeSmall; break;
                case TextVariant.Title: size = Tokens.FontSizeTitle; weight = FontWeights.SemiBold; break;
                case TextVariant.Strong: weight = FontWeights.SemiBold; break;
                case TextVariant.Mono: size = Tokens.FontSizeCode; this.Bind(FontFamilyProperty, Tokens.FontFamilyCode); break;
                case TextVariant.Disabled: fg = Tokens.TextDisabled; break;
                case TextVariant.Link: fg = Tokens.TextLink; break;
                case TextVariant.Subtitle: size = Tokens.FontSizeSubtitle; weight = FontWeights.SemiBold; break;
                case TextVariant.Display: size = Tokens.FontSizeDisplay; weight = FontWeights.SemiBold; break;
            }
            this.Bind(ForegroundProperty, fg);
            this.Bind(FontSizeProperty, size);
            FontWeight = weight;
        }
    }

    /// <summary>
    /// Text with matched character ranges. One TextBlock + Runs (no per-character elements). Match visuals come from
    /// tokens (Text.Match color, Match.FontWeight, Match.Background, Match.Decorations) so the "匹配高亮样式" setting
    /// applies live. <see cref="Set"/> is cheap and skips work when nothing changed (list recycling).
    /// </summary>
    public class HighlightedText : TextBlock
    {
        private string _text;
        private IReadOnlyList<TextRange> _ranges;
        private bool _secondary;
        private bool _selected;

        public HighlightedText()
        {
            TextTrimming = TextTrimming.CharacterEllipsis;
            TextWrapping = TextWrapping.NoWrap;
            VerticalAlignment = VerticalAlignment.Center;
            ApplyForeground();
        }

        public string SourceText => _text;

        /// <summary>Secondary (gray) base color, e.g. the location column.</summary>
        public bool IsSecondary
        {
            get => _secondary;
            set
            {
                if (_secondary == value) return;
                _secondary = value;
                ApplyForeground();
            }
        }

        /// <summary>Row is selected: base color switches to the selection foreground tokens.</summary>
        public bool IsSelectedRow
        {
            get => _selected;
            set
            {
                if (_selected == value) return;
                _selected = value;
                ApplyForeground();
            }
        }

        private void ApplyForeground()
        {
            object key = _selected ? (_secondary ? Tokens.SelectionSecondary : Tokens.SelectionForeground)
                                   : (_secondary ? Tokens.TextSecondary : Tokens.TextPrimary);
            SetResourceReference(ForegroundProperty, key);
        }

        public void Set(string text, IReadOnlyList<TextRange> ranges)
        {
            text = text ?? string.Empty;
            if (ReferenceEquals(text, _text) && ReferenceEquals(ranges, _ranges)) return;
            _text = text;
            _ranges = ranges;
            Inlines.Clear();
            if (ranges == null || ranges.Count == 0)
            {
                Text = text;
                return;
            }
            foreach (var seg in HighlightRanges.Segments(ranges, text.Length))
            {
                var run = new Run(text.Substring(seg.Start, seg.Length));
                if (seg.IsMatch)
                {
                    run.SetResourceReference(TextElement.ForegroundProperty, Tokens.TextMatch);
                    run.SetResourceReference(TextElement.FontWeightProperty, Tokens.MatchFontWeight);
                    run.SetResourceReference(TextElement.BackgroundProperty, Tokens.MatchBackground);
                    run.SetResourceReference(Inline.TextDecorationsProperty, Tokens.MatchDecorations);
                }
                Inlines.Add(run);
            }
        }
    }

    /// <summary>VS ImageMoniker icon (CrispImage: DPI-aware, theme-inverted). Size follows the Icon.Size token.</summary>
    public class UiIcon : Decorator
    {
        private CrispImage _image;
        private ImageMoniker _moniker;

        public UiIcon()
        {
            this.Bind(WidthProperty, Tokens.IconSize);
            this.Bind(HeightProperty, Tokens.IconSize);
            VerticalAlignment = VerticalAlignment.Center;
            Focusable = false;
        }

        public UiIcon(ImageMoniker moniker) : this()
        {
            Moniker = moniker;
        }

        public static bool IsEmpty(ImageMoniker m) => m.Guid == Guid.Empty && m.Id == 0;

        public ImageMoniker Moniker
        {
            get => _moniker;
            set
            {
                if (_moniker.Guid == value.Guid && _moniker.Id == value.Id) return;
                _moniker = value;
                if (IsEmpty(value))
                {
                    if (_image != null) _image.Visibility = Visibility.Collapsed;
                    return;
                }
                try
                {
                    if (_image == null)
                    {
                        _image = new CrispImage();
                        _image.Bind(WidthProperty, Tokens.IconSize);
                        _image.Bind(HeightProperty, Tokens.IconSize);
                        Child = _image;
                    }
                    _image.Visibility = Visibility.Visible;
                    _image.Moniker = value;
                }
                catch (Exception ex)
                {
                    Ui.Logger.Warn("ui.icon.failed", "CrispImage unavailable", ex);
                }
            }
        }
    }

    /// <summary>Colored letter glyph for a symbol kind (used when no ImageMoniker fits, e.g. UE-specific kinds).</summary>
    public class TypeGlyph : Border
    {
        private readonly TextBlock _letter = new TextBlock
        {
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            FontWeight = FontWeights.Bold,
            TextAlignment = TextAlignment.Center,
        };

        private SymbolKind _kind = (SymbolKind)(-1);

        public TypeGlyph() : this(SymbolKind.Unknown) { }

        public TypeGlyph(SymbolKind kind)
        {
            Width = 16;
            Height = 16;
            CornerRadius = new CornerRadius(4);
            VerticalAlignment = VerticalAlignment.Center;
            _letter.FontSize = 10.5;
            Child = _letter;
            Kind = kind;
            Focusable = false;
        }

        public SymbolKind Kind
        {
            get => _kind;
            set
            {
                if (_kind == value) return;
                _kind = value;
                _letter.Text = SymbolGlyphs.Letter(value);
                this.Bind(BackgroundProperty, Tokens.GlyphBackground(value));
                _letter.Bind(TextBlock.ForegroundProperty, Tokens.Glyph(value));
                CornerRadius = new CornerRadius(value == SymbolKind.Function || value == SymbolKind.Method ? 8 : 4);
                AutomationProperties.SetName(this, value.ToString());
            }
        }
    }

    /// <summary>Small rounded label (UCLASS, UFUNCTION, file kind…). Tinted by default, <see cref="Solid"/> for strong emphasis.</summary>
    public class Badge : Border
    {
        private readonly TextBlock _text = new TextBlock { VerticalAlignment = VerticalAlignment.Center, FontWeight = FontWeights.SemiBold };
        private BadgeKind _kind = (BadgeKind)(-1);
        private bool _solid;

        public Badge() : this((string)null, BadgeKind.Neutral) { }

        public Badge(string text, BadgeKind kind, bool solid = false)
        {
            Padding = new Thickness(5, 0, 5, 0);
            MinHeight = 16;
            VerticalAlignment = VerticalAlignment.Center;
            this.Bind(CornerRadiusProperty, Tokens.CornerRadiusBadge);
            _text.Bind(TextBlock.FontSizeProperty, Tokens.FontSizeCaption);
            Child = _text;
            _solid = solid;
            Text = text;
            Kind = kind;
            Focusable = false;
        }

        public string Text
        {
            get => _text.Text;
            set
            {
                _text.Text = value ?? string.Empty;
                AutomationProperties.SetName(this, value ?? string.Empty);
            }
        }

        public BadgeKind Kind
        {
            get => _kind;
            set
            {
                if (_kind == value) return;
                _kind = value;
                ApplyColors();
            }
        }

        public bool Solid
        {
            get => _solid;
            set
            {
                if (_solid == value) return;
                _solid = value;
                ApplyColors();
            }
        }

        private void ApplyColors()
        {
            if (_solid)
            {
                this.Bind(BackgroundProperty, Tokens.BadgeSolid(_kind));
                _text.Foreground = Brushes.White;
            }
            else
            {
                this.Bind(BackgroundProperty, Tokens.BadgeBackground(_kind));
                _text.Bind(TextBlock.ForegroundProperty, Tokens.BadgeForeground(_kind));
            }
        }
    }

    /// <summary>Keycap shortcut hint: "Ctrl+Shift+F" → [Ctrl][Shift][F]; chords separated by ','.</summary>
    public class Kbd : StackPanel
    {
        private string _shortcut;

        public Kbd() : this((string)null) { }

        public Kbd(string shortcut)
        {
            Orientation = Orientation.Horizontal;
            VerticalAlignment = VerticalAlignment.Center;
            Focusable = false;
            Shortcut = shortcut;
        }

        public string Shortcut
        {
            get => _shortcut;
            set
            {
                if (_shortcut == value) return;
                _shortcut = value;
                Children.Clear();
                AutomationProperties.SetName(this, value ?? string.Empty);
                var chords = ShortcutText.Parse(value);
                for (int c = 0; c < chords.Count; c++)
                {
                    if (c > 0)
                        Children.Add(new UiText(",", TextVariant.Caption) { Margin = new Thickness(2, 0, 4, 0) });
                    foreach (var key in chords[c]) Children.Add(Cap(key));
                }
            }
        }

        private static Border Cap(string key)
        {
            var t = new TextBlock { Text = key, VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Center };
            t.Bind(TextBlock.FontSizeProperty, Tokens.FontSizeCaption);
            t.Bind(TextBlock.ForegroundProperty, Tokens.KbdForeground);
            var b = new Border
            {
                Child = t,
                MinWidth = 16,
                Height = 16,
                Padding = new Thickness(4, 0, 4, 0),
                Margin = new Thickness(0, 0, 2, 0),
                BorderThickness = new Thickness(1, 1, 1, 1.5),
                CornerRadius = new CornerRadius(3),
                SnapsToDevicePixels = true,
            };
            b.Bind(Border.BackgroundProperty, Tokens.KbdBackground);
            b.Bind(Border.BorderBrushProperty, Tokens.KbdBorder);
            return b;
        }
    }

    /// <summary>1px separator line (horizontal by default).</summary>
    public class Divider : Border
    {
        public Divider(bool vertical = false, double margin = 0)
        {
            if (vertical)
            {
                Width = 1;
                Margin = new Thickness(margin, 0, margin, 0);
                VerticalAlignment = VerticalAlignment.Stretch;
            }
            else
            {
                Height = 1;
                Margin = new Thickness(0, margin, 0, margin);
                HorizontalAlignment = HorizontalAlignment.Stretch;
            }
            SnapsToDevicePixels = true;
            this.Bind(BackgroundProperty, Tokens.BorderSubtle);
            Focusable = false;
        }

        public Divider() : this(false) { }
    }

    /// <summary>Busy indicator: a rotating arc. Animates only while visible; static when animations are off.</summary>
    public class Spinner : Grid
    {
        private readonly RotateTransform _rotate = new RotateTransform();
        private bool _running;

        public Spinner() : this(14) { }

        public Spinner(double size)
        {
            Width = size;
            Height = size;
            VerticalAlignment = VerticalAlignment.Center;
            double r = size / 2 - 1.5;
            double c = size / 2;
            var geo = Geometry.Parse(string.Format(System.Globalization.CultureInfo.InvariantCulture,
                "M {0},{1} A {2},{2} 0 1 1 {3},{4}", c, c - r, r, c - r, c));
            geo.Freeze();
            var track = new Ellipse { StrokeThickness = 1.5, Margin = new Thickness(1.5) };
            track.Bind(Shape.StrokeProperty, Tokens.BorderSubtle);
            var arc = new Path { Data = geo, StrokeThickness = 1.5, StrokeStartLineCap = PenLineCap.Round, StrokeEndLineCap = PenLineCap.Round };
            arc.Bind(Shape.StrokeProperty, Tokens.Accent);
            arc.RenderTransformOrigin = new Point(0.5, 0.5);
            arc.RenderTransform = _rotate;
            Children.Add(track);
            Children.Add(arc);
            Strings.Track(this, () => AutomationProperties.SetName(this, Strings.CommonLoading));
            IsVisibleChanged += (s, e) => Ui.Safe("ui.spinner.visible", Update);
            Focusable = false;
        }

        private void Update()
        {
            bool want = IsVisible && ThemeManager.Instance.AnimationDuration > TimeSpan.Zero;
            if (want == _running) return;
            _running = want;
            if (want)
            {
                var a = new DoubleAnimation(0, 360, new Duration(TimeSpan.FromMilliseconds(900))) { RepeatBehavior = RepeatBehavior.Forever };
                _rotate.BeginAnimation(RotateTransform.AngleProperty, a);
            }
            else
            {
                _rotate.BeginAnimation(RotateTransform.AngleProperty, null);
                _rotate.Angle = 0;
            }
        }
    }
}
