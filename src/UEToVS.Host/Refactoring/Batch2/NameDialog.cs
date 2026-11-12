using System;
using System.Windows;
using System.Windows.Controls;
using UEToVS.UI.Atoms;
using UEToVS.UI.Controls;
using UEToVS.UI.Shells;

namespace UEToVS.Host.Refactoring.Batch2
{
    /// <summary>
    /// Small modal dialog of the batch-2 refactorings: a name box (validated as typed), optional check boxes and a
    /// monospace preview line. Built from the UI library (DialogShell). UI thread.
    /// </summary>
    internal sealed class NameDialog
    {
        private readonly DialogShell _shell;
        private readonly StackPanel _form = new StackPanel();
        private readonly UiText _error = new UiText((string)null, TextVariant.Secondary) { Margin = new Thickness(0, 2, 0, 0) };
        private readonly UiText _preview = new UiText((string)null, TextVariant.Mono) { Margin = new Thickness(0, 10, 0, 0), TextWrapping = TextWrapping.Wrap };

        public NameDialog(Func<string> title, Func<string> primary, string label, string initialName)
        {
            _shell = new DialogShell(title, primary) { VisibleSize = new Size(560, 300) };
            NameBox = new TextInput { Text = initialName ?? string.Empty, Margin = new Thickness(0, 4, 0, 0) };
            _form.Children.Add(new UiText(label, TextVariant.Secondary));
            _form.Children.Add(NameBox);
            _form.Children.Add(_error);
            _shell.Form = _form;
            NameBox.TextChanged += (s, e) => UEToVS.UI.Theming.Ui.Safe("refactor.batch2.name", Refresh);
            NameBox.Loaded += (s, e) => UEToVS.UI.Theming.Ui.Safe("refactor.batch2.focus", () =>
            {
                NameBox.Focus();
                NameBox.SelectAll();
            });
        }

        public TextInput NameBox { get; }

        public string Name => NameBox.Text?.Trim() ?? string.Empty;

        public string Description
        {
            get => _shell.Description;
            set => _shell.Description = value;
        }

        /// <summary>Error text for a name (null: usable).</summary>
        public Func<string, string> NameError { get; set; }

        /// <summary>Preview line for a name (null: no preview).</summary>
        public Func<string, string> Preview { get; set; }

        public UiCheckBox AddCheckBox(string label, bool isChecked)
        {
            var box = new UiCheckBox(label, isChecked) { Margin = new Thickness(0, 8, 0, 0) };
            box.Checked += (s, e) => UEToVS.UI.Theming.Ui.Safe("refactor.batch2.option", Refresh);
            box.Unchecked += (s, e) => UEToVS.UI.Theming.Ui.Safe("refactor.batch2.option", Refresh);
            _form.Children.Add(box);
            return box;
        }

        public bool ShowModal(IntPtr owner)
        {
            _form.Children.Add(_preview);
            Refresh();
            _shell.Validate = () => NameError?.Invoke(Name) == null;
            return _shell.ShowModal(owner);
        }

        private void Refresh()
        {
            string error = NameError?.Invoke(Name);
            _error.Text = error;
            _error.Visibility = string.IsNullOrEmpty(error) ? Visibility.Collapsed : Visibility.Visible;
            _shell.IsPrimaryEnabled = error == null;
            string preview = Preview?.Invoke(Name);
            _preview.Text = preview;
            _preview.Visibility = string.IsNullOrEmpty(preview) ? Visibility.Collapsed : Visibility.Visible;
        }
    }
}
