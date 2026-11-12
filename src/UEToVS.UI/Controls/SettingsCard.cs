using System.Windows;
using System.Windows.Controls;
using UEToVS.UI.Theming;

namespace UEToVS.UI.Controls
{
    /// <summary>
    /// One setting as a VS 2026 / Fluent "settings card": raised surface, subtle 1px border, 8px corners, 16px side
    /// padding, a text column on the left (title, description, notes) and the editor on the right, vertically centred
    /// and kept at its natural size (a toggle switch is never stretched or squashed by the row).
    /// </summary>
    public class SettingsCard : Border
    {
        /// <summary>Minimum card height (Fluent settings cards are tall; VS 2026 density uses a little less).</summary>
        public const double MinCardHeight = 52;

        private readonly Grid _grid = new Grid();
        private UIElement _editor;

        public SettingsCard(UIElement labels, UIElement editor = null)
        {
            _grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            _grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            if (labels is FrameworkElement lf)
            {
                lf.VerticalAlignment = VerticalAlignment.Center;
                lf.Margin = new Thickness(0, 8, 16, 8);
            }
            if (labels != null) _grid.Children.Add(labels);
            Editor = editor;

            Child = _grid;
            Padding = new Thickness(16, 2, 16, 2);
            Margin = new Thickness(0, 0, 0, 4);
            MinHeight = MinCardHeight;
            BorderThickness = new Thickness(1);
            SnapsToDevicePixels = true;
            this.Bind(BackgroundProperty, Tokens.SurfaceRaised);
            this.Bind(BorderBrushProperty, Tokens.BorderSubtle);
            this.Bind(CornerRadiusProperty, Tokens.CornerRadiusPopup);
        }

        /// <summary>The control on the right (toggle, combo box, text box, button…), or null.</summary>
        public UIElement Editor
        {
            get => _editor;
            set
            {
                if (_editor != null) _grid.Children.Remove(_editor);
                _editor = value;
                if (value == null) return;
                if (value is FrameworkElement fe)
                {
                    fe.VerticalAlignment = VerticalAlignment.Center;
                    fe.HorizontalAlignment = HorizontalAlignment.Right;
                }
                Grid.SetColumn(value, 1);
                _grid.Children.Add(value);
            }
        }
    }
}
