using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Microsoft.VisualStudio.Imaging;
using UEToVS.UI.Atoms;
using UEToVS.UI.Controls;
using UEToVS.UI.Logic;
using UEToVS.UI.Molecules;
using UEToVS.UI.Shells;

namespace UEToVS.Host.Refactoring
{
    /// <summary>A row of <see cref="CheckListDialog{T}"/>: a group header or a checkable item.</summary>
    internal sealed class CheckRow<T>
    {
        public T Item { get; set; }
        public string Header { get; set; }
        public int HeaderCount { get; set; }
        public bool Checked { get; set; }

        /// <summary>Listed only (e.g. engine references): shown dimmed, never checked.</summary>
        public bool ReadOnly { get; set; }

        public bool IsHeader => Header != null;
    }

    /// <summary>
    /// Modal dialog built from the UI library (DialogShell + optional extra form content + filter + VirtualList): a list of
    /// items with check boxes (Space / double-click / click toggles, Ctrl+A checks all visible), group headers, read-only rows.
    /// Used by Implement Virtual (pick methods) and Rename (preview of every change). Returns the checked items or null
    /// when cancelled. UI thread.
    /// </summary>
    internal sealed class CheckListDialog<T>
    {
        private readonly DialogShell _shell;
        private readonly SearchInput _filter = new SearchInput(true);
        private readonly VirtualList<CheckRow<T>> _list;
        private readonly UiText _summary = new UiText((string)null, TextVariant.Secondary) { Margin = new Thickness(0, 6, 0, 0) };
        private readonly Action<T, RowData> _present;
        private readonly Func<T, string> _filterText;
        private List<CheckRow<T>> _rows = new List<CheckRow<T>>();

        public CheckListDialog(Func<string> title, Func<string> primary, Action<T, RowData> present, Func<T, string> filterText, Size size, string placementId = null)
        {
            _present = present;
            _filterText = filterText;
            _shell = new DialogShell(title, primary) { VisibleSize = size, PreviewPosition = PreviewPosition.Bottom, PlacementId = placementId };
            _list = new VirtualList<CheckRow<T>>(new DelegatePresenter<CheckRow<T>>(Present, r => !r.IsHeader)) { AlwaysActive = true, MinHeight = 160 };
            Form = new StackPanel();
            var root = new DockPanel { LastChildFill = true, Margin = new Thickness(8) };
            DockPanel.SetDock(_filter, Dock.Top);
            DockPanel.SetDock(_summary, Dock.Bottom);
            root.Children.Add(_filter);
            root.Children.Add(_summary);
            _list.Margin = new Thickness(0, 6, 0, 0);
            root.Children.Add(_list);
            // extra controls in the form area; the list fills the dialog's lower (preview) area
            _shell.Form = Form;
            _shell.Preview = root;
            _filter.TextChanged += (s, t) => UEToVS.UI.Theming.Ui.Safe("refactor.checklist.filter", Refresh);
            _list.ItemInvoked += (s, e) => UEToVS.UI.Theming.Ui.Safe("refactor.checklist.toggle", () => Toggle(e.Item));
            _list.PreviewKeyDown += (s, e) => UEToVS.UI.Theming.Ui.Safe("refactor.checklist.key", () =>
            {
                if (e.Key == Key.Space && _list.HasSelection)
                {
                    Toggle(_list.SelectedItem);
                    e.Handled = true;
                }
                else if (e.Key == Key.A && Keyboard.Modifiers == ModifierKeys.Control)
                {
                    bool all = Visible().Where(r => !r.IsHeader && !r.ReadOnly).Any(r => !r.Checked);
                    foreach (var r in Visible()) if (!r.IsHeader && !r.ReadOnly) r.Checked = all;
                    _list.InvalidateAllRows();
                    UpdateSummary();
                    e.Handled = true;
                }
            });
        }

        /// <summary>Extra controls above the list (name box, options).</summary>
        public StackPanel Form { get; }

        public DialogShell Shell => _shell;

        public string Description
        {
            get => _shell.Description;
            set => _shell.Description = value;
        }

        /// <summary>Summary under the list, e.g. "12 / 15 已选".</summary>
        public Func<IReadOnlyList<CheckRow<T>>, string> Summary { get; set; }

        /// <summary>Extra validation (name box) on OK; the dialog also refuses OK with nothing checked unless <see cref="AllowEmpty"/>.</summary>
        public Func<bool> Validate { get; set; }

        public bool AllowEmpty { get; set; }

        public IReadOnlyList<CheckRow<T>> Rows => _rows;

        public List<T> ShowModal(IReadOnlyList<CheckRow<T>> rows, IntPtr owner)
        {
            _rows = rows.ToList();
            Refresh();
            _shell.Validate = () => (Validate == null || Validate()) && (AllowEmpty || _rows.Any(r => !r.IsHeader && r.Checked));
            if (_list.Count > 1 && _rows[0].IsHeader) _list.SelectedIndex = 1;
            bool ok = _shell.ShowModal(owner);
            return ok ? _rows.Where(r => !r.IsHeader && r.Checked && !r.ReadOnly).Select(r => r.Item).ToList() : null;
        }

        private IEnumerable<CheckRow<T>> Visible() => _list.Items ?? Array.Empty<CheckRow<T>>();

        private void Refresh()
        {
            string q = _filter.Text ?? string.Empty;
            if (q.Length == 0 || _filterText == null) _list.Items = _rows;
            else
            {
                var shown = new List<CheckRow<T>>();
                CheckRow<T> header = null;
                foreach (var r in _rows)
                {
                    if (r.IsHeader) { header = r; continue; }
                    string t = _filterText(r.Item) ?? string.Empty;
                    if (t.IndexOf(q, StringComparison.OrdinalIgnoreCase) < 0 && !HighlightRanges.TryMatchSubsequence(t, q, out _)) continue;
                    if (header != null) { shown.Add(header); header = null; }
                    shown.Add(r);
                }
                _list.Items = shown;
            }
            UpdateSummary();
        }

        private void Toggle(CheckRow<T> row)
        {
            if (row == null || row.IsHeader || row.ReadOnly) return;
            row.Checked = !row.Checked;
            _list.InvalidateAllRows();
            UpdateSummary();
        }

        private void UpdateSummary()
        {
            int total = _rows.Count(r => !r.IsHeader && !r.ReadOnly), on = _rows.Count(r => !r.IsHeader && !r.ReadOnly && r.Checked);
            _summary.Text = Summary?.Invoke(_rows) ?? (on + " / " + total);
            _shell.IsPrimaryEnabled = AllowEmpty || on > 0;
        }

        private void Present(CheckRow<T> row, RowData d)
        {
            if (row.IsHeader)
            {
                d.IsGroupHeader = true;
                d.Primary = row.Header;
                d.GroupCount = row.HeaderCount;
                return;
            }
            _present(row.Item, d);
            d.Moniker = row.ReadOnly ? KnownMonikers.Lock : row.Checked ? KnownMonikers.CheckBoxChecked : KnownMonikers.CheckBoxUnchecked;
            d.Glyph = null;
            if (row.ReadOnly) d.IsDimmed = true;
        }
    }
}
