using System;
using System.Globalization;

namespace UEToVS.UI.Logic
{
    /// <summary>
    /// WPF-free 8-bit sRGB color with the small amount of color math the token derivation needs
    /// (WCAG luminance/contrast, HSL, mixing). Converted to <c>System.Windows.Media.Color</c> at the WPF edge.
    /// </summary>
    public readonly struct Rgba : IEquatable<Rgba>
    {
        public readonly byte R, G, B, A;

        public Rgba(byte r, byte g, byte b, byte a = 255)
        {
            R = r; G = g; B = b; A = a;
        }

        public static Rgba FromRgb(int r, int g, int b) => new Rgba(Clamp8(r), Clamp8(g), Clamp8(b));

        public static Rgba FromHex(uint rgb) => new Rgba((byte)(rgb >> 16), (byte)(rgb >> 8), (byte)rgb);

        public static readonly Rgba Black = new Rgba(0, 0, 0);
        public static readonly Rgba White = new Rgba(255, 255, 255);
        public static readonly Rgba Transparent = new Rgba(0, 0, 0, 0);

        public Rgba WithAlpha(double alpha) => new Rgba(R, G, B, Clamp8((int)Math.Round(alpha * 255)));

        public Rgba Opaque() => new Rgba(R, G, B, 255);

        /// <summary>WCAG relative luminance (0 = black, 1 = white). Alpha is ignored.</summary>
        public double Luminance
        {
            get
            {
                return 0.2126 * Lin(R) + 0.7152 * Lin(G) + 0.0722 * Lin(B);
            }
        }

        public bool IsDark => Luminance < 0.4;

        private static double Lin(byte c)
        {
            double s = c / 255.0;
            return s <= 0.03928 ? s / 12.92 : Math.Pow((s + 0.055) / 1.055, 2.4);
        }

        /// <summary>WCAG contrast ratio between two opaque colors (1..21).</summary>
        public static double Contrast(Rgba a, Rgba b)
        {
            double la = a.Luminance, lb = b.Luminance;
            if (la < lb) { var t = la; la = lb; lb = t; }
            return (la + 0.05) / (lb + 0.05);
        }

        /// <summary>Linear interpolation in sRGB space: t = 0 gives <paramref name="a"/>, t = 1 gives <paramref name="b"/>.</summary>
        public static Rgba Mix(Rgba a, Rgba b, double t)
        {
            if (t <= 0) return a;
            if (t >= 1) return b;
            return new Rgba(
                Clamp8((int)Math.Round(a.R + (b.R - a.R) * t)),
                Clamp8((int)Math.Round(a.G + (b.G - a.G) * t)),
                Clamp8((int)Math.Round(a.B + (b.B - a.B) * t)),
                Clamp8((int)Math.Round(a.A + (b.A - a.A) * t)));
        }

        /// <summary>Composites this (possibly translucent) color over an opaque background.</summary>
        public Rgba Over(Rgba background) => Mix(background.Opaque(), Opaque(), A / 255.0);

        public void ToHsl(out double h, out double s, out double l)
        {
            double r = R / 255.0, g = G / 255.0, b = B / 255.0;
            double max = Math.Max(r, Math.Max(g, b)), min = Math.Min(r, Math.Min(g, b));
            l = (max + min) / 2;
            if (max - min < 1e-9) { h = 0; s = 0; return; }
            double d = max - min;
            s = l > 0.5 ? d / (2 - max - min) : d / (max + min);
            if (max == r) h = (g - b) / d + (g < b ? 6 : 0);
            else if (max == g) h = (b - r) / d + 2;
            else h = (r - g) / d + 4;
            h *= 60;
        }

        public static Rgba FromHsl(double h, double s, double l, byte alpha = 255)
        {
            h = ((h % 360) + 360) % 360;
            s = Clamp01(s);
            l = Clamp01(l);
            if (s < 1e-9)
            {
                byte v = Clamp8((int)Math.Round(l * 255));
                return new Rgba(v, v, v, alpha);
            }
            double q = l < 0.5 ? l * (1 + s) : l + s - l * s;
            double p = 2 * l - q;
            double hk = h / 360.0;
            return new Rgba(
                Clamp8((int)Math.Round(HueToRgb(p, q, hk + 1.0 / 3) * 255)),
                Clamp8((int)Math.Round(HueToRgb(p, q, hk) * 255)),
                Clamp8((int)Math.Round(HueToRgb(p, q, hk - 1.0 / 3) * 255)),
                alpha);
        }

        private static double HueToRgb(double p, double q, double t)
        {
            if (t < 0) t += 1;
            if (t > 1) t -= 1;
            if (t < 1.0 / 6) return p + (q - p) * 6 * t;
            if (t < 1.0 / 2) return q;
            if (t < 2.0 / 3) return p + (q - p) * (2.0 / 3 - t) * 6;
            return p;
        }

        public Rgba WithLightness(double l)
        {
            ToHsl(out var h, out var s, out _);
            return FromHsl(h, s, l, A);
        }

        public double Lightness
        {
            get { ToHsl(out _, out _, out var l); return l; }
        }

        /// <summary>
        /// Moves the lightness of this color away from <paramref name="background"/> until the contrast ratio reaches
        /// <paramref name="minRatio"/> (or the lightness hits 0/1). Hue and saturation are kept.
        /// </summary>
        public Rgba EnsureContrast(Rgba background, double minRatio)
        {
            var c = Opaque();
            if (Contrast(c, background) >= minRatio) return c;
            c.ToHsl(out var h, out var s, out var l);
            bool lighter = background.IsDark;
            for (int i = 0; i < 50; i++)
            {
                l += lighter ? 0.02 : -0.02;
                if (l <= 0 || l >= 1) { l = Clamp01(l); break; }
                var t = FromHsl(h, s, l);
                if (Contrast(t, background) >= minRatio) return t;
            }
            return FromHsl(h, s, l);
        }

        public string ToHex() => A == 255
            ? string.Format(CultureInfo.InvariantCulture, "#{0:X2}{1:X2}{2:X2}", R, G, B)
            : string.Format(CultureInfo.InvariantCulture, "#{0:X2}{1:X2}{2:X2}{3:X2}", A, R, G, B);

        /// <summary>Parses #RGB, #RRGGBB or #AARRGGBB (leading '#' optional). Returns false for anything else.</summary>
        public static bool TryParse(string text, out Rgba color)
        {
            color = default;
            if (string.IsNullOrWhiteSpace(text)) return false;
            var s = text.Trim();
            if (s[0] == '#') s = s.Substring(1);
            if (!uint.TryParse(s, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var v)) return false;
            switch (s.Length)
            {
                case 3:
                    {
                        byte r = (byte)(((v >> 8) & 0xF) * 17), g = (byte)(((v >> 4) & 0xF) * 17), b = (byte)((v & 0xF) * 17);
                        color = new Rgba(r, g, b);
                        return true;
                    }
                case 6:
                    color = new Rgba((byte)(v >> 16), (byte)(v >> 8), (byte)v);
                    return true;
                case 8:
                    color = new Rgba((byte)(v >> 16), (byte)(v >> 8), (byte)v, (byte)(v >> 24));
                    return true;
                default:
                    return false;
            }
        }

        public bool Equals(Rgba other) => R == other.R && G == other.G && B == other.B && A == other.A;
        public override bool Equals(object obj) => obj is Rgba o && Equals(o);
        public override int GetHashCode() => (A << 24) | (R << 16) | (G << 8) | B;
        public static bool operator ==(Rgba a, Rgba b) => a.Equals(b);
        public static bool operator !=(Rgba a, Rgba b) => !a.Equals(b);
        public override string ToString() => ToHex();

        internal static byte Clamp8(int v) => (byte)(v < 0 ? 0 : v > 255 ? 255 : v);
        internal static double Clamp01(double v) => v < 0 ? 0 : v > 1 ? 1 : v;
    }
}
