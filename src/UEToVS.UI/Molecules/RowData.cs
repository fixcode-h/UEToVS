using System;
using System.Collections.Generic;
using Microsoft.VisualStudio.Imaging.Interop;
using UEToVS.UI.Logic;

namespace UEToVS.UI.Molecules
{
    public readonly struct BadgeSpec
    {
        public readonly string Text;
        public readonly BadgeKind Kind;
        public readonly bool Solid;

        public BadgeSpec(string text, BadgeKind kind, bool solid = false)
        {
            Text = text;
            Kind = kind;
            Solid = solid;
        }
    }

    /// <summary>
    /// What one list row shows. A single instance is reused per realized row: the list calls
    /// <see cref="Reset"/> then <see cref="IItemPresenter{T}.Present"/>. Presenters must not keep references to it.
    /// </summary>
    public sealed class RowData
    {
        private readonly List<BadgeSpec> _badges = new List<BadgeSpec>(4);

        /// <summary>VS icon. Leave default for none (then <see cref="Glyph"/> is used if set).</summary>
        public ImageMoniker Moniker;

        /// <summary>Letter glyph when there is no moniker (UE kinds, files, assets).</summary>
        public SymbolKind? Glyph;

        public string Primary;
        public IReadOnlyList<TextRange> PrimaryRanges;

        /// <summary>Gray text right after the primary text (e.g. containing class, module). Trimmed first.</summary>
        public string Secondary;
        public IReadOnlyList<TextRange> SecondaryRanges;

        /// <summary>Right-aligned gray text (location "Actor.h:120", count).</summary>
        public string Right;

        /// <summary>Right-aligned keycap shortcut ("Ctrl+B").</summary>
        public string Shortcut;

        /// <summary>Non-selectable group header ("类型 (12)"): shows <see cref="Primary"/> + <see cref="GroupCount"/>.</summary>
        public bool IsGroupHeader;
        public int? GroupCount;

        /// <summary>Gray out (non-project items, disabled actions).</summary>
        public bool IsDimmed;

        /// <summary>Disabled rows render dimmed and cannot be invoked.</summary>
        public bool IsEnabled = true;

        /// <summary>Shows a "›" (submenu) at the right end.</summary>
        public bool HasSubmenu;

        public string ToolTip;

        /// <summary>Screen-reader text; default = Primary + Secondary + Right.</summary>
        public string AutomationName;

        // Set by TreeList.
        internal int Indent;
        internal bool? Expanded;

        public IReadOnlyList<BadgeSpec> Badges => _badges;

        public RowData AddBadge(string text, BadgeKind kind, bool solid = false)
        {
            if (!string.IsNullOrEmpty(text)) _badges.Add(new BadgeSpec(text, kind, solid));
            return this;
        }

        public void Reset()
        {
            Moniker = default;
            Glyph = null;
            Primary = null;
            PrimaryRanges = null;
            Secondary = null;
            SecondaryRanges = null;
            Right = null;
            Shortcut = null;
            IsGroupHeader = false;
            GroupCount = null;
            IsDimmed = false;
            IsEnabled = true;
            HasSubmenu = false;
            ToolTip = null;
            AutomationName = null;
            Indent = 0;
            Expanded = null;
            _badges.Clear();
        }
    }

    /// <summary>Maps a data item to a row. Features implement this instead of writing DataTemplates (§8.4).</summary>
    public interface IItemPresenter<in T>
    {
        void Present(T item, RowData row);
    }

    /// <summary>Optional: answers "is this item a selectable row?" without a full Present (group headers).</summary>
    public interface ISelectablePresenter<in T>
    {
        bool IsSelectable(T item);
    }

    /// <summary>Lambda-based presenter.</summary>
    public sealed class DelegatePresenter<T> : IItemPresenter<T>, ISelectablePresenter<T>
    {
        private readonly Action<T, RowData> _present;
        private readonly Func<T, bool> _selectable;

        public DelegatePresenter(Action<T, RowData> present, Func<T, bool> isSelectable = null)
        {
            _present = present ?? throw new ArgumentNullException(nameof(present));
            _selectable = isSelectable;
        }

        public void Present(T item, RowData row) => _present(item, row);

        public bool IsSelectable(T item)
        {
            if (_selectable != null) return _selectable(item);
            var r = new RowData();
            _present(item, r);
            return !r.IsGroupHeader && r.IsEnabled;
        }
    }

    /// <summary>Arguments of an invoke (Enter / Shift+Enter / Ctrl+Enter / double-click).</summary>
    public sealed class ListInvokeEventArgs<T> : EventArgs
    {
        public ListInvokeEventArgs(T item, int index, InvokeKind kind)
        {
            Item = item;
            Index = index;
            Kind = kind;
        }

        public T Item { get; }
        public int Index { get; }
        public InvokeKind Kind { get; }
    }
}
