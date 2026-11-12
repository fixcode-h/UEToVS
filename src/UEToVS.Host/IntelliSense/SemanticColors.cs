using System;
using System.Collections.Generic;
using System.Windows.Media;
using Microsoft.VisualStudio.Text.Classification;
using Microsoft.VisualStudio.Text.Formatting;
using UEToVS.Contracts;
using UEToVS.Core.Diagnostics;
using UEToVS.Host.Logic.IntelliSense;
using UEToVS.Host.Logic.Navigation;

namespace UEToVS.Host.IntelliSense
{
    /// <summary>
    /// Keeps the UEToVS semantic colours right for the editor theme (§7.7 "深色/浅色两套，跟随 VS 主题切换") and applies the
    /// per-kind override setting. Works on the editor-wide "text" classification format map: a colour is replaced only
    /// when it is still one of our defaults (or the colour we set last), so changes made in Fonts and Colors or by the VA
    /// import survive theme switches (<see cref="SemanticClassifications.TargetColor"/>). UI thread.
    /// </summary>
    internal static class SemanticColors
    {
        private static readonly Logger L = Log.For("IntelliSense");
        private static readonly Dictionary<SemanticKind, uint> LastApplied = new Dictionary<SemanticKind, uint>();
        private static (bool Dark, string Overrides)? _applied;

        public static Color ToColor(uint argb) => Color.FromArgb((byte)(argb >> 24), (byte)(argb >> 16), (byte)(argb >> 8), (byte)argb);

        public static uint ToArgb(Color c) => ((uint)c.A << 24) | ((uint)c.R << 16) | ((uint)c.G << 8) | c.B;

        /// <summary>Is the editor background dark?</summary>
        public static bool IsDark(Brush background) =>
            !(background is SolidColorBrush bg) || HighlightColors.IsDark(bg.Color.R, bg.Color.G, bg.Color.B);

        /// <summary>Applies theme defaults / overrides when the theme or the override setting changed since the last call.</summary>
        public static void Apply(IClassificationFormatMap map, IClassificationTypeRegistryService registry, bool dark, string overridesText, bool force = false)
        {
            if (map == null || registry == null) return;
            if (!force && _applied == (dark, overridesText ?? string.Empty)) return;
            _applied = (dark, overridesText ?? string.Empty);
            var errors = new List<string>();
            var overrides = SemanticClassifications.ParseOverrides(overridesText, errors);
            if (errors.Count > 0) L.Warn("is.colors.overrides.invalid", "Ignored entries in SemanticColorOverrides", ("entries", errors));
            int changed = 0;
            map.BeginBatchUpdate();
            try
            {
                foreach (var info in SemanticClassifications.All)
                {
                    var type = registry.GetClassificationType(info.Name);
                    if (type == null) continue;
                    var props = map.GetTextProperties(type);
                    uint? current = props.ForegroundBrushEmpty ? (uint?)null : (props.ForegroundBrush as SolidColorBrush)?.Color is Color c ? ToArgb(c) : (uint?)null;
                    uint? last = LastApplied.TryGetValue(info.Kind, out var l) ? l : (uint?)null;
                    uint? ov = overrides.TryGetValue(info.Kind, out var o) ? o : (uint?)null;
                    var target = SemanticClassifications.TargetColor(info, dark, current, ov, last);
                    if (target == null) continue;
                    map.SetTextProperties(type, props.SetForeground(ToColor(target.Value)));
                    LastApplied[info.Kind] = target.Value;
                    changed++;
                }
            }
            finally
            {
                map.EndBatchUpdate();
            }
            L.Info("is.colors.applied", null, ("dark", dark), ("changed", changed), ("overrides", overrides.Count));
        }

        /// <summary>Sets every UEToVS classification back to this theme's default (or its override) — "reset colours".</summary>
        public static int Reset(IClassificationFormatMap map, IClassificationTypeRegistryService registry, bool dark, string overridesText)
        {
            if (map == null || registry == null) return 0;
            var overrides = SemanticClassifications.ParseOverrides(overridesText);
            int n = 0;
            map.BeginBatchUpdate();
            try
            {
                foreach (var info in SemanticClassifications.All)
                {
                    var type = registry.GetClassificationType(info.Name);
                    if (type == null) continue;
                    uint want = overrides.TryGetValue(info.Kind, out var o) ? o : info.Default(dark);
                    var props = map.GetTextProperties(type).SetForeground(ToColor(want)).SetBold(false);
                    map.SetTextProperties(type, props);
                    LastApplied[info.Kind] = want;
                    n++;
                }
            }
            finally
            {
                map.EndBatchUpdate();
            }
            _applied = (dark, overridesText ?? string.Empty);
            L.Info("is.colors.reset", null, ("dark", dark), ("count", n));
            return n;
        }

        /// <summary>
        /// Writes imported colours (VA import) into the format map; they then count as user colours (theme switches keep
        /// them). The Fonts and Colors store is written separately by the importer so the colours persist.
        /// </summary>
        public static void SetImported(IClassificationFormatMap map, IClassificationTypeRegistryService registry, IEnumerable<VaColorAssignment> assignments)
        {
            if (map == null || registry == null) return;
            map.BeginBatchUpdate();
            try
            {
                foreach (var a in assignments)
                {
                    var type = registry.GetClassificationType(a.ClassificationName);
                    if (type == null) continue;
                    var props = map.GetTextProperties(type).SetForeground(ToColor(a.Foreground)).SetBold(a.Bold);
                    map.SetTextProperties(type, props);
                    LastApplied.Remove(a.Kind);
                }
            }
            finally
            {
                map.EndBatchUpdate();
            }
        }
    }
}
