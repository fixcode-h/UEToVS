using System.Windows;
using System.Windows.Controls;
using UEToVS.Host.Logic.IntelliSense;
using UEToVS.Host.Logic.Localization;
using UEToVS.UI.Atoms;
using UEToVS.UI.Theming;

namespace UEToVS.Host.Settings
{
    // ---- v0.4.5 navigation gaps
    /// <summary>
    /// The "VS features after the takeover" table of the settings window (General → C++ IntelliSense): VS feature (and its
    /// key), whether it still works after the takeover, and the UEToVS replacement with its default key. Data:
    /// <see cref="TakeoverFeatures.All"/>. Built for the current UI language (the settings window is rebuilt on a change).
    /// </summary>
    internal sealed class TakeoverFeatureTable : Border
    {
        public TakeoverFeatureTable()
        {
            BorderThickness = new Thickness(1);
            Margin = new Thickness(0, 8, 0, 0);
            this.Bind(BorderBrushProperty, Tokens.BorderSubtle);
            this.Bind(CornerRadiusProperty, Tokens.CornerRadiusControl);

            var grid = new Grid();
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1.1, GridUnitType.Star), MinWidth = 140 });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto, MinWidth = 90 });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1.6, GridUnitType.Star), MinWidth = 160 });

            int row = 0;
            AddRow(grid, row++, true, NavGapStrings.TakeoverColumnFeature, NavGapStrings.TakeoverColumnState, NavGapStrings.TakeoverColumnReplacement, null);
            foreach (var f in TakeoverFeatures.All)
                AddRow(grid, row++, false, TakeoverFeatures.FeatureText(f), TakeoverFeatures.StateText(f.State), TakeoverFeatures.ReplacementText(f), f);
            Child = grid;
        }

        private static void AddRow(Grid grid, int row, bool header, string feature, string state, string replacement, TakeoverFeature f)
        {
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            if (row > 0)
            {
                var line = new Border { Height = 1, VerticalAlignment = VerticalAlignment.Top };
                line.Bind(BackgroundProperty, Tokens.BorderSubtle);
                Grid.SetRow(line, row);
                Grid.SetColumnSpan(line, 3);
                grid.Children.Add(line);
            }
            var stateVariant = header ? TextVariant.Strong
                : f.State == TakeoverFeatureState.Unavailable || f.State == TakeoverFeatureState.TurnedOff ? TextVariant.Strong
                : f.State == TakeoverFeatureState.Unknown ? TextVariant.Secondary : TextVariant.Body;
            Cell(grid, row, 0, feature, header ? TextVariant.Strong : TextVariant.Body, f?.VsCommand);
            Cell(grid, row, 1, state, stateVariant, null);
            Cell(grid, row, 2, replacement, header ? TextVariant.Strong : TextVariant.Secondary, null);
        }

        private static void Cell(Grid grid, int row, int column, string text, TextVariant variant, string tooltip)
        {
            var t = new UiText(text, variant) { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(10, 6, 10, 6), VerticalAlignment = VerticalAlignment.Center };
            if (!string.IsNullOrEmpty(tooltip)) t.ToolTip = tooltip;
            Grid.SetRow(t, row);
            Grid.SetColumn(t, column);
            grid.Children.Add(t);
        }
    }
}
