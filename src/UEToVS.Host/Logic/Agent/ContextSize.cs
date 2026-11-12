using System;
using System.Globalization;

namespace UEToVS.Host.Logic.Agent
{
    /// <summary>
    /// Model context length as shown and typed on the Agent / API page: below 1M tokens in K ("128K"), from 1M on in M
    /// ("1M", "1.5M"). Input accepts "1M", "1000K", "1.5m", "128k" and a bare number (read as K).
    /// </summary>
    public static class ContextSize
    {
        public const int MinTokens = 1000;
        public const int MaxTokens = 100000000;

        public static string Format(int tokens)
        {
            if (tokens >= 1000000)
            {
                double m = tokens / 1000000.0;
                return Math.Round(m, 2).ToString("0.##", CultureInfo.InvariantCulture) + "M";
            }
            return Math.Max(1, (int)Math.Round(tokens / 1000.0)).ToString(CultureInfo.InvariantCulture) + "K";
        }

        public static bool TryParse(string text, out int tokens)
        {
            tokens = 0;
            string s = (text ?? string.Empty).Trim().Replace(",", string.Empty).Replace(" ", string.Empty);
            if (s.Length == 0) return false;
            double unit = 1000;
            char last = char.ToUpperInvariant(s[s.Length - 1]);
            if (last == 'K') s = s.Substring(0, s.Length - 1);
            else if (last == 'M') { unit = 1000000; s = s.Substring(0, s.Length - 1); }
            if (!double.TryParse(s, NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out double v) || v <= 0) return false;
            double t = Math.Round(v * unit);
            if (t < MinTokens || t > MaxTokens) return false;
            tokens = (int)t;
            return true;
        }
    }
}
