using System;
using System.Collections.Generic;

using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
using UEToVS.UI.Atoms;
using UEToVS.UI.Logic;
using UEToVS.UI.Theming;

namespace UEToVS.UI.Controls
{
    /// <summary>
    /// Editable list of folders (warm-scheme mockup "Lua 脚本路径"): one 38px row per folder in a sunken rounded box —
    /// folder icon, the path in the code font, a "相对 .uproject" / "绝对路径" tag, a warning when the folder is missing,
    /// then "⋯" (pick another folder) and ✕ (remove). Double-click a row (or F2) to edit it in place, Enter commits, Esc
    /// cancels. <see cref="AddButton"/> ("添加文件夹…") opens the folder picker; it is a separate element so a settings row
    /// can put it on the right of the title while the list spans the row below.
    /// Value is the old ';'-separated text (<see cref="PathListModel"/>), so existing settings keep working; folders under
    /// <see cref="BaseDirectory"/> are stored relative to it.
    /// </summary>
    public class PathListEditor : StackPanel
    {
        private readonly List<string> _paths = new List<string>();
        private readonly StackPanel _rows = new StackPanel();
        private readonly Border _box;
        private readonly UiText _empty = new UiText((string)null, TextVariant.Description) { Margin = new Thickness(12, 11, 12, 11), TextWrapping = TextWrapping.Wrap, TextTrimming = TextTrimming.None };
        private readonly UiText _hint = new UiText((string)null, TextVariant.Description) { Margin = new Thickness(0, 8, 0, 0), TextWrapping = TextWrapping.Wrap, TextTrimming = TextTrimming.None };
        private int _editing = -1;

        public PathListEditor(string baseDirectory = null, string baseLabel = ".uproject")
        {
            BaseDirectory = baseDirectory;
            BaseLabel = baseLabel;
            _box = new Border { Child = _rows, BorderThickness = new Thickness(1), SnapsToDevicePixels = true, CornerRadius = new CornerRadius(10), ClipToBounds = true };
            _box.Bind(Border.BackgroundProperty, Tokens.InputBackground);
            _box.Bind(Border.BorderBrushProperty, Tokens.BorderSubtle);
            Children.Add(_box);
            Children.Add(_hint);

            AddButton = new UiButton((string)null, ButtonKind.Secondary) { Padding = new Thickness(12, 0, 12, 0) };
            Strings.Track(AddButton, () =>
            {
                AddButton.Content = IconText(SettingsIcons.Plus, Strings.PathAddFolder);
                AutomationProperties.SetName(AddButton, Strings.PathAddFolder);
            });
            AddButton.Click += (s, e) => Ui.Safe("ui.pathList.add", AddFromPicker);
            Strings.Track(this, () =>
            {
                _empty.Text = Strings.PathListEmpty;
                _hint.Text = Strings.PathListHint;
                Rebuild();
            });
        }

        /// <summary>Raised after the list changed (add, edit, remove); read <see cref="Value"/>.</summary>
        public event EventHandler ValueChanged;

        /// <summary>"添加文件夹…" — place it where it fits (settings rows put it on the right of the title).</summary>
        public UiButton AddButton { get; }

        /// <summary>Folder relative paths are resolved against (the .uproject folder); null = keep everything absolute.</summary>
        public string BaseDirectory { get; set; }

        /// <summary>Name of the base shown in the tag ("相对 .uproject").</summary>
        public string BaseLabel { get; set; }

        /// <summary>Folder picker title.</summary>
        public string PickerTitle { get; set; }

        /// <summary>Existence check (replaceable in tests / for remote paths).</summary>
        public Func<string, bool> DirectoryExists { get; set; } = SafeExists;

        /// <summary>Folder picker; receives the start folder and returns the picked folder or null. Defaults to <see cref="FolderPicker"/>.</summary>
        public Func<string, string> PickFolder { get; set; }

        /// <summary>The paths as stored: ';'-separated (empty = none).</summary>
        public string Value
        {
            get => PathListModel.Join(_paths);
            set
            {
                _paths.Clear();
                _paths.AddRange(PathListModel.Parse(value));
                _editing = -1;
                Rebuild();
            }
        }

        public IReadOnlyList<string> Paths => _paths;

        private string Pick(string start)
        {
            var pick = PickFolder ?? (s => FolderPicker.Pick(this, PickerTitle ?? Strings.PathPickTitle, s));
            return pick(start);
        }

        private void AddFromPicker()
        {
            string start = string.IsNullOrEmpty(BaseDirectory) ? null : BaseDirectory;
            string picked = Pick(start);
            if (string.IsNullOrWhiteSpace(picked)) return;
            string stored = PathListModel.ToStored(picked, BaseDirectory);
            int existing = PathListModel.IndexOf(_paths, stored);
            if (existing >= 0) { Rebuild(); FocusRow(existing); return; }
            _paths.Add(stored);
            Changed();
            FocusRow(_paths.Count - 1);
        }

        private void Replace(int index, string stored)
        {
            if (index < 0 || index >= _paths.Count) return;
            stored = (stored ?? string.Empty).Trim().Trim('"').Trim();
            _editing = -1;
            if (stored.Length == 0) { _paths.RemoveAt(index); Changed(); return; }
            // An absolute path typed inside the project is stored relative, like a picked one.
            if (PathListModel.IsRooted(stored)) stored = PathListModel.ToStored(stored, BaseDirectory);
            int dup = PathListModel.IndexOf(_paths, stored);
            if (dup >= 0 && dup != index) { _paths.RemoveAt(index); Changed(); return; }
            bool same = string.Equals(_paths[index], stored, StringComparison.Ordinal);
            _paths[index] = stored;
            if (same) Rebuild(); else Changed();
        }

        private void Remove(int index)
        {
            if (index < 0 || index >= _paths.Count) return;
            _paths.RemoveAt(index);
            _editing = -1;
            Changed();
            if (_paths.Count > 0) FocusRow(Math.Min(index, _paths.Count - 1));
            else AddButton.Focus();
        }

        private void Changed()
        {
            Rebuild();
            ValueChanged?.Invoke(this, EventArgs.Empty);
        }

        private void FocusRow(int index)
        {
            if (index >= 0 && index < _rows.Children.Count && _rows.Children[index * 2] is UIElement row)
                Dispatcher.BeginInvoke(new Action(() => row.Focus()), System.Windows.Threading.DispatcherPriority.Input);
        }

        private void Rebuild()
        {
            _rows.Children.Clear();
            if (_paths.Count == 0)
            {
                _rows.Children.Add(_empty);
                return;
            }
            for (int i = 0; i < _paths.Count; i++)
            {
                if (i > 0)
                {
                    var line = new Border { Height = 1, SnapsToDevicePixels = true };
                    line.Bind(Border.BackgroundProperty, Tokens.Divider);
                    _rows.Children.Add(line);
                }
                _rows.Children.Add(BuildRow(i));
            }
        }

        private UIElement BuildRow(int index)
        {
            string stored = _paths[index];
            string abs = PathListModel.ToAbsolute(stored, BaseDirectory);
            bool exists = abs != null && (DirectoryExists?.Invoke(abs) ?? true);
            bool relative = PathListModel.IsRelative(stored);
            bool editing = index == _editing;

            var grid = new Grid { Height = 38 };
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });                       // icon
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });  // path
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });                       // tags
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });                       // ⋯
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });                       // ✕

            var icon = new Path
            {
                Data = SettingsIcons.Folder, Width = 15, Height = 15, Stretch = Stretch.None, StrokeThickness = 1.2,
                StrokeLineJoin = PenLineJoin.Round, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 10, 0),
            };
            icon.Bind(Shape.StrokeProperty, exists ? Tokens.TextLink : Tokens.TextSecondary);
            grid.Children.Add(icon);

            var tags = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(10, 0, 4, 0) };
            Grid.SetColumn(tags, 2);
            grid.Children.Add(tags);

            UIElement pathElement;
            TextInput edit = null;
            if (editing)
            {
                edit = new TextInput { Style = Styles.BareTextInput, Text = stored, VerticalAlignment = VerticalAlignment.Center };
                edit.Bind(TextBlock.FontFamilyProperty, Tokens.FontFamilyCode);
                AutomationProperties.SetName(edit, Strings.PathEditName);
                var box = edit;
                edit.KeyDown += (s, e) => Ui.Safe("ui.pathList.editKey", () =>
                {
                    if (e.Key == Key.Enter) { e.Handled = true; Replace(index, box.Text); FocusRow(Math.Min(index, _paths.Count - 1)); }
                    else if (e.Key == Key.Escape) { e.Handled = true; _editing = -1; Rebuild(); FocusRow(index); }
                });
                edit.LostKeyboardFocus += (s, e) => Ui.Safe("ui.pathList.editBlur", () =>
                {
                    if (_editing == index && box.IsLoaded) Replace(index, box.Text);
                });
                pathElement = edit;
                tags.Children.Add(new UiText(Strings.PathEditing, TextVariant.Description) { VerticalAlignment = VerticalAlignment.Center });
            }
            else
            {
                var text = new UiText(stored, TextVariant.Body) { VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis, ToolTip = abs ?? stored };
                text.Bind(TextBlock.FontFamilyProperty, Tokens.FontFamilyCode);
                if (!exists) text.Bind(TextBlock.ForegroundProperty, Tokens.TextSecondary);
                pathElement = text;
                tags.Children.Add(PathTag(relative ? Strings.PathRelativeTag(BaseLabel) : Strings.PathAbsoluteTag));
                if (!exists)
                {
                    var warn = new UiText(Strings.PathMissing, TextVariant.Small) { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(8, 0, 0, 0) };
                    warn.Bind(TextBlock.ForegroundProperty, Tokens.Severity(Severity.Warning));
                    tags.Children.Add(warn);
                }
            }
            Grid.SetColumn(pathElement, 1);
            grid.Children.Add(pathElement);

            var browse = new IconButton((Geometry)null, () => Strings.PathBrowseRow) { Width = 28, Height = 28, Content = "⋯", Focusable = true };
            browse.Click += (s, e) => Ui.Safe("ui.pathList.browse", () =>
            {
                string picked = Pick(PathListModel.BrowseStart(_paths[index], BaseDirectory, DirectoryExists));
                if (!string.IsNullOrWhiteSpace(picked)) Replace(index, PathListModel.ToStored(picked, BaseDirectory));
            });
            Grid.SetColumn(browse, 3);
            grid.Children.Add(browse);

            var remove = new IconButton(Ui.CrossMark, () => Strings.PathRemove, null, 9) { Width = 28, Height = 28 };
            remove.Click += (s, e) => Ui.Safe("ui.pathList.remove", () => Remove(index));
            Grid.SetColumn(remove, 4);
            grid.Children.Add(remove);

            var row = new Border { Child = grid, Padding = new Thickness(12, 0, 6, 0), Focusable = !editing, FocusVisualStyle = null, Background = Brushes.Transparent, BorderThickness = new Thickness(0) };
            AutomationProperties.SetName(row, stored);
            if (editing)
            {
                row.Bind(Border.BackgroundProperty, Tokens.SurfaceRaised);
                row.BorderThickness = new Thickness(0, 0, 0, 2);
                row.Bind(Border.BorderBrushProperty, Tokens.Accent);
                row.Loaded += (s, e) => Ui.Safe("ui.pathList.editFocus", () => { edit.Focus(); edit.SelectAll(); });
            }
            else
            {
                row.MouseEnter += (s, e) => row.Bind(Border.BackgroundProperty, Tokens.SurfaceHover);
                row.MouseLeave += (s, e) => row.Background = Brushes.Transparent;
                row.MouseLeftButtonDown += (s, e) => Ui.Safe("ui.pathList.dblclick", () =>
                {
                    if (e.ClickCount != 2) return;
                    e.Handled = true;
                    _editing = index;
                    Rebuild();
                });
                row.KeyDown += (s, e) => Ui.Safe("ui.pathList.key", () =>
                {
                    if (e.OriginalSource != row) return;
                    if (e.Key == Key.F2 || e.Key == Key.Enter) { e.Handled = true; _editing = index; Rebuild(); }
                    else if (e.Key == Key.Delete) { e.Handled = true; Remove(index); }
                });
                row.GotKeyboardFocus += (s, e) => { if (e.OriginalSource == row) { row.BorderThickness = new Thickness(1); row.Bind(Border.BorderBrushProperty, Tokens.FocusStroke); } };
                row.LostKeyboardFocus += (s, e) => { row.BorderThickness = new Thickness(0); };
            }
            return row;
        }

        private static UIElement PathTag(string text)
        {
            var b = new Border { Padding = new Thickness(6, 1, 6, 1), BorderThickness = new Thickness(1), VerticalAlignment = VerticalAlignment.Center };
            b.Bind(Border.BorderBrushProperty, Tokens.BorderSubtle);
            b.Bind(Border.CornerRadiusProperty, Tokens.CornerRadiusBadge);
            b.Child = new UiText(text, TextVariant.Description);
            return b;
        }

        /// <summary>16px stroke icon + label, for buttons.</summary>
        public static UIElement IconText(Geometry icon, string text)
        {
            var p = new StackPanel { Orientation = Orientation.Horizontal };
            var glyph = new Path
            {
                Data = icon, Width = 12, Height = 12, Stretch = Stretch.None, StrokeThickness = 1.4,
                StrokeStartLineCap = PenLineCap.Round, StrokeEndLineCap = PenLineCap.Round, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 6, 0),
            };
            glyph.SetBinding(Shape.StrokeProperty, new System.Windows.Data.Binding(nameof(Control.Foreground))
            {
                RelativeSource = new System.Windows.Data.RelativeSource(System.Windows.Data.RelativeSourceMode.FindAncestor, typeof(Control), 1),
            });
            p.Children.Add(glyph);
            p.Children.Add(new TextBlock { Text = text, VerticalAlignment = VerticalAlignment.Center });
            return p;
        }

        private static bool SafeExists(string path)
        {
            try { return System.IO.Directory.Exists(path); }
            catch (Exception) { return false; }
        }
    }

    /// <summary>
    /// Single path box with a joined "浏览…" button (warm-scheme mockup "共享引擎索引位置"): code-font text input and a
    /// button on the right that opens the folder picker. Typed text is kept as is.
    /// </summary>
    public class PathInput : Border
    {
        private readonly TextInput _box;

        public PathInput(string placeholder = null)
        {
            _box = new TextInput { Style = Styles.BareTextInput, Placeholder = placeholder ?? string.Empty, VerticalAlignment = VerticalAlignment.Center, Padding = new Thickness(10, 0, 10, 0) };
            _box.Bind(TextBlock.FontFamilyProperty, Tokens.FontFamilyCode);
            _box.TextChanged += (s, e) => Ui.Safe("ui.pathInput.changed", () => TextChanged?.Invoke(this, EventArgs.Empty));
            var browse = new UiButton((string)null, ButtonKind.Secondary) { BorderThickness = new Thickness(1, 0, 0, 0), Padding = new Thickness(12, 0, 12, 0), MinWidth = 0, VerticalAlignment = VerticalAlignment.Stretch };
            Strings.Track(browse, () => browse.Content = Strings.PathBrowse);
            browse.Click += (s, e) => Ui.Safe("ui.pathInput.browse", () =>
            {
                var pick = PickFolder ?? (start => FolderPicker.Pick(this, PickerTitle ?? Strings.PathPickTitle, start));
                string start = string.IsNullOrWhiteSpace(_box.Text) ? null : _box.Text.Trim().Trim('"');
                string picked = pick(start);
                if (!string.IsNullOrWhiteSpace(picked)) _box.Text = picked;
            });
            var grid = new Grid();
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            grid.Children.Add(_box);
            Grid.SetColumn(browse, 1);
            grid.Children.Add(browse);
            Child = grid;
            BorderThickness = new Thickness(1);
            ClipToBounds = true;
            SnapsToDevicePixels = true;
            this.Bind(BackgroundProperty, Tokens.InputBackground);
            this.Bind(BorderBrushProperty, Tokens.InputBorder);
            this.Bind(CornerRadiusProperty, Tokens.CornerRadiusControl);
            this.Bind(HeightProperty, Tokens.ControlHeight);
            IsKeyboardFocusWithinChanged += (s, e) => this.Bind(BorderBrushProperty, IsKeyboardFocusWithin ? Tokens.BorderFocus : Tokens.InputBorder);
        }

        public event EventHandler TextChanged;

        public string Text
        {
            get => _box.Text;
            set => _box.Text = value ?? string.Empty;
        }

        public string PickerTitle { get; set; }

        public Func<string, string> PickFolder { get; set; }

        public string AccessibleName
        {
            set => AutomationProperties.SetName(_box, value ?? string.Empty);
        }
    }
}
