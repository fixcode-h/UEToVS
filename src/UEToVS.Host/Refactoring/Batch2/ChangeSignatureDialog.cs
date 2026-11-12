using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using UEToVS.Host.Logic.Localization;
using UEToVS.Host.Logic.Refactoring;
using UEToVS.UI.Atoms;
using UEToVS.UI.Controls;
using UEToVS.UI.Shells;

namespace UEToVS.Host.Refactoring.Batch2
{
    /// <summary>
    /// Parameter editor of Change Signature: one row per parameter (type, name, default, call value for new ones; up / down /
    /// remove), "add parameter", the override-chain option and a live preview of the new signature with the first validation
    /// error. Rows are rebuilt only when the list changes shape (add / remove / move). UI thread.
    /// </summary>
    internal sealed class ChangeSignatureDialog
    {
        private readonly DialogShell _shell;
        private readonly IReadOnlyList<EditableParameter> _original;
        private readonly List<EditableParameter> _items;
        private readonly string _prefix;
        private readonly StackPanel _rows = new StackPanel();
        private readonly UiText _preview = new UiText((string)null, TextVariant.Mono) { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 4, 0, 0) };
        private readonly UiText _error = new UiText((string)null, TextVariant.Secondary) { Margin = new Thickness(0, 4, 0, 0) };
        private readonly UiCheckBox _family;

        /// <param name="prefix">Text before the parameter list in the preview, e.g. <c>void AMyActor::Fire</c>.</param>
        /// <param name="familyCount">Base declarations + overrides; 0 hides the override-chain option.</param>
        public ChangeSignatureDialog(string name, string prefix, IReadOnlyList<EditableParameter> original, int familyCount)
        {
            _original = original;
            _items = original.Select(p => p.Clone()).ToList();
            _prefix = prefix;
            _shell = new DialogShell(() => Refactor2Strings.ChangeSignatureTitle(name), () => Refactor2Strings.Next) { VisibleSize = new Size(860, 480) };
            _shell.Description = Refactor2Strings.ChangeSignatureDescription;

            var form = new StackPanel();
            form.Children.Add(Row(new UiText(Refactor2Strings.ParameterType, TextVariant.Secondary), new UiText(Refactor2Strings.ParameterName, TextVariant.Secondary),
                new UiText(Refactor2Strings.ParameterDefault, TextVariant.Secondary), new UiText(Refactor2Strings.ParameterCallValue, TextVariant.Secondary), null));
            var scroll = new ScrollViewer { Content = _rows, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, MaxHeight = 220 };
            form.Children.Add(scroll);
            var add = new UiButton(() => Refactor2Strings.AddParameter) { HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(0, 8, 0, 0) };
            add.Click += (s, e) => UEToVS.UI.Theming.Ui.Safe("refactor.signature.add", () =>
            {
                _items.Add(new EditableParameter { Type = "int32", Name = FreeName() });
                Rebuild();
            });
            form.Children.Add(add);
            _family = new UiCheckBox(Refactor2Strings.IncludeOverrides, true) { Margin = new Thickness(0, 10, 0, 0), Visibility = familyCount > 0 ? Visibility.Visible : Visibility.Collapsed };
            form.Children.Add(_family);
            form.Children.Add(new UiText(Refactor2Strings.SignaturePreview, TextVariant.Secondary) { Margin = new Thickness(0, 12, 0, 0) });
            form.Children.Add(_preview);
            form.Children.Add(_error);
            _shell.Form = form;
            Rebuild();
        }

        /// <summary>Change the base declarations and overrides too.</summary>
        public bool IncludeFamily => _family.Visibility == Visibility.Visible && _family.IsChecked == true;

        /// <summary>The edited list, or null when cancelled.</summary>
        public List<EditableParameter> ShowModal(IntPtr owner)
        {
            _shell.Validate = () => Error() == null;
            return _shell.ShowModal(owner) ? _items.Select(Trimmed).ToList() : null;
        }

        private static EditableParameter Trimmed(EditableParameter p) => new EditableParameter
        {
            OriginalIndex = p.OriginalIndex,
            Type = ChangeSignaturePlanner.NormalizeSpaces(p.Type ?? string.Empty),
            Name = string.IsNullOrWhiteSpace(p.Name) ? null : p.Name.Trim(),
            DefaultValue = string.IsNullOrWhiteSpace(p.DefaultValue) ? null : p.DefaultValue.Trim(),
            CallValue = string.IsNullOrWhiteSpace(p.CallValue) ? null : p.CallValue.Trim(),
        };

        private string Error()
        {
            var list = _items.Select(Trimmed).ToList();
            string code = ChangeSignaturePlanner.Validate(list);
            if (code != null) return Refactor2Strings.ValidationError(code);
            if (ChangeSignaturePlanner.IsUnchanged(_original, list)) return Refactor2Strings.Unchanged;
            return null;
        }

        private void Refresh()
        {
            var list = _items.Select(Trimmed).ToList();
            _preview.Text = _prefix + "(" + ChangeSignaturePlanner.Format(list, true) + ")";
            string error = Error();
            _error.Text = error;
            _error.Visibility = error == null ? Visibility.Collapsed : Visibility.Visible;
            _shell.IsPrimaryEnabled = error == null;
        }

        private void Rebuild()
        {
            _rows.Children.Clear();
            for (int i = 0; i < _items.Count; i++) _rows.Children.Add(Editor(i));
            Refresh();
        }

        private FrameworkElement Editor(int index)
        {
            var p = _items[index];
            TextInput Box(string value, Action<string> set, bool enabled = true)
            {
                var box = new TextInput { Text = value ?? string.Empty, IsEnabled = enabled, Margin = new Thickness(0, 0, 6, 0) };
                box.TextChanged += (s, e) => UEToVS.UI.Theming.Ui.Safe("refactor.signature.edit", () =>
                {
                    set(box.Text);
                    Refresh();
                });
                return box;
            }
            var buttons = new StackPanel { Orientation = Orientation.Horizontal };
            buttons.Children.Add(Button(Refactor2Strings.MoveUp, index > 0, () => Swap(index, index - 1)));
            buttons.Children.Add(Button(Refactor2Strings.MoveDown, index < _items.Count - 1, () => Swap(index, index + 1)));
            buttons.Children.Add(Button(Refactor2Strings.RemoveParameter, true, () =>
            {
                _items.RemoveAt(index);
                Rebuild();
            }));
            return Row(Box(p.Type, v => p.Type = v), Box(p.Name, v => p.Name = v), Box(p.DefaultValue, v => p.DefaultValue = v),
                Box(p.CallValue, v => p.CallValue = v, p.IsNew), buttons);
        }

        private static UiButton Button(string text, bool enabled, Action click)
        {
            var b = new UiButton(text, ButtonKind.Ghost) { IsEnabled = enabled, Margin = new Thickness(2, 0, 0, 0), Padding = new Thickness(6, 2, 6, 2) };
            b.Click += (s, e) => UEToVS.UI.Theming.Ui.Safe("refactor.signature.row", click);
            return b;
        }

        private void Swap(int a, int b)
        {
            (_items[a], _items[b]) = (_items[b], _items[a]);
            Rebuild();
        }

        private string FreeName()
        {
            for (int i = 1; ; i++)
            {
                string n = i == 1 ? "NewParam" : "NewParam" + i;
                if (!_items.Any(p => string.Equals(p.Name?.Trim(), n, StringComparison.Ordinal))) return n;
            }
        }

        /// <summary>Type 3* | name 2* | default 1.5* | call value 1.5* | buttons auto.</summary>
        private static Grid Row(UIElement type, UIElement name, UIElement def, UIElement call, UIElement buttons)
        {
            var g = new Grid { Margin = new Thickness(0, 0, 0, 4) };
            foreach (var w in new[] { 3.0, 2.0, 1.5, 1.5 }) g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(w, GridUnitType.Star) });
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(150) });
            var cells = new[] { type, name, def, call, buttons };
            for (int i = 0; i < cells.Length; i++)
            {
                if (cells[i] == null) continue;
                Grid.SetColumn(cells[i], i);
                g.Children.Add(cells[i]);
            }
            return g;
        }
    }
}
