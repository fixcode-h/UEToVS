using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Windows.Controls;
using System.Windows.Input;
using Microsoft.VisualStudio.Shell;
using UEToVS.UI;

namespace UEToVS.Search
{
    /// <summary>"在窗口中打开": keeps a Search Everywhere result list in a dockable tool window (Rider "Open in Find Tool Window").</summary>
    [Guid("5d3a9b8e-6f0c-4c1e-9a43-3b7e2f8d1c55")]
    public sealed class ResultsToolWindow : ToolWindowPane
    {
        private readonly ResultList _list = new ResultList();
        private readonly TextBlock _header = new TextBlock { Margin = new System.Windows.Thickness(6, 4, 6, 4) };

        public ResultsToolWindow() : base(null)
        {
            Caption = "UEToVS 搜索结果";
            var dock = new DockPanel();
            _header.SetResourceReference(TextBlock.ForegroundProperty, Theme.Gray);
            DockPanel.SetDock(_header, Dock.Top);
            dock.Children.Add(_header);
            dock.Children.Add(_list);
            dock.SetResourceReference(Panel.BackgroundProperty, Theme.Background);
            Theme.ApplyWindowFont(dock);
            _list.ItemActivated += (item, mode) => item?.Activate?.Invoke(OpenMode.KeepOpen);
            dock.PreviewKeyDown += (s, e) =>
            {
                if (e.Key == Key.Down) { _list.Move(1); e.Handled = true; }
                else if (e.Key == Key.Up) { _list.Move(-1); e.Handled = true; }
                else if (e.Key == Key.Enter) { _list.ActivateSelected(OpenMode.KeepOpen); e.Handled = true; }
            };
            dock.Focusable = true;
            Content = dock;
        }

        internal void SetResults(List<SearchItem> items, string query)
        {
            _header.Text = $"“{query}” — {items.FindAll(i => i.IsSelectable).Count} 项（单击或 Enter 打开）";
            _list.SetItems(items);
        }
    }
}
