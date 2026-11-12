using System;
using System.Globalization;

namespace UEToVS.Host.Logic.Settings
{
    /// <summary>
    /// Text form of one remembered popup geometry (UEToVS.UI PopupState) for the VS settings store:
    /// "w=760;h=500;split=0.42;preview=1" (invariant culture; split/preview optional). Pure; never throws.
    /// </summary>
    public static class PopupStateText
    {
        public static string Format(double width, double height, double previewSplit, bool? previewVisible)
        {
            var ci = CultureInfo.InvariantCulture;
            string s = "w=" + width.ToString("0.##", ci) + ";h=" + height.ToString("0.##", ci);
            if (!double.IsNaN(previewSplit) && !double.IsInfinity(previewSplit)) s += ";split=" + previewSplit.ToString("0.####", ci);
            if (previewVisible.HasValue) s += ";preview=" + (previewVisible.Value ? "1" : "0");
            return s;
        }

        /// <summary>False when the text is empty/garbled or width/height are missing or not positive.</summary>
        public static bool TryParse(string text, out double width, out double height, out double previewSplit, out bool? previewVisible)
        {
            width = height = 0;
            previewSplit = double.NaN;
            previewVisible = null;
            if (string.IsNullOrWhiteSpace(text)) return false;
            foreach (var part in text.Split(';'))
            {
                int eq = part.IndexOf('=');
                if (eq <= 0) continue;
                string key = part.Substring(0, eq).Trim();
                string val = part.Substring(eq + 1).Trim();
                switch (key)
                {
                    case "w": width = Num(val); break;
                    case "h": height = Num(val); break;
                    case "split":
                        double sp = Num(val);
                        previewSplit = sp > 0 && sp < 1 ? sp : double.NaN;
                        break;
                    case "preview":
                        previewVisible = val == "1" ? true : val == "0" ? false : (bool?)null;
                        break;
                }
            }
            return width > 0 && height > 0 && !double.IsNaN(width) && !double.IsNaN(height);
        }

        private static double Num(string v) =>
            double.TryParse(v, NumberStyles.Float, CultureInfo.InvariantCulture, out double d) && !double.IsInfinity(d) ? d : double.NaN;
    }
}
