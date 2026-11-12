using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using Microsoft.VisualStudio.Shell;
using UEToVS.Core.Diagnostics;
using UEToVS.Host.Diagnostics;
using UEToVS.Host.Logic.Actions;
using UEToVS.Host.Logic.Localization;
using UEToVS.Host.Shell;
using UEToVS.UI.Logic;
using UEToVS.UI.Molecules;
using UEToVS.UI.Patterns;
using UEToVS.UI.Shells;
using TextRange = UEToVS.UI.Logic.TextRange;

namespace UEToVS.Host.Integration
{
    /// <summary>
    /// Find Action (§7.4) on UEToVS.UI's <see cref="CommandPalette{T}"/>. The palette is created on first use and kept
    /// (instant re-open, remembered query/history); each Show rebinds it to the registry and a fresh action context.
    /// </summary>
    internal sealed class ActionPaletteProvider : ICommandPaletteProvider
    {
        private static readonly Logger L = Log.For("FindAction");
        private readonly ActionPaletteSource _source = new ActionPaletteSource();
        private CommandPalette<ActionMatch> _palette;

        public void Show(ActionRegistry registry, ActionContext context, string initialText)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            _source.Bind(registry, context);
            if (_palette == null) _palette = new CommandPalette<ActionMatch>("uetovs.findAction", _source, new Size(640, 420));
            IntPtr owner = VsMainWindow.Handle;
            _palette.OwnerHandle = owner;
            _palette.Show(owner != IntPtr.Zero ? PopupAnchor.CenterOf(owner) : null, initialText);
            L.Info("findAction.shown", null, ("actions", registry?.Count ?? 0));
        }
    }

    /// <summary><see cref="ICommandPaletteSource{T}"/> over <see cref="ActionRegistry.Query"/> / <see cref="ActionRegistry.ExecuteAsync"/>.</summary>
    internal sealed class ActionPaletteSource : ICommandPaletteSource<ActionMatch>
    {
        private const string TabId = "actions";
        private static readonly Logger L = Log.For("FindAction");
        private ActionRegistry _registry;
        private ActionContext _context;

        public ActionPaletteSource()
        {
            Tabs = new[]
            {
                new PaletteTab(TabId, () => Strings.PaletteTabActions, 0)
                {
                    SupportsPreview = false,
                    Placeholder = () => Strings.PalettePlaceholder,
                    EmptyHint = () => Strings.PaletteEmptyHint,
                },
            };
        }

        public IReadOnlyList<PaletteTab> Tabs { get; }

        public IReadOnlyList<PaletteFilter> Filters { get; } = Array.Empty<PaletteFilter>();

        public IItemPresenter<ActionMatch> Presenter { get; } = new ActionPresenter();

        public void Bind(ActionRegistry registry, ActionContext context)
        {
            _registry = registry;
            _context = context ?? new ActionContext { Source = ActionSource.FindAction };
        }

        public Task<PaletteResult<ActionMatch>> QueryAsync(PaletteQuery query, IProgress<PaletteResult<ActionMatch>> partial, CancellationToken cancellationToken)
        {
            var registry = _registry;
            if (registry == null) return Task.FromResult(new PaletteResult<ActionMatch>());
            try
            {
                // In-memory and small (tens of actions): synchronous on the UI thread is well within budget.
                var items = registry.Query(query?.Text, _context)
                                    .Where(m => !string.Equals(m.Action.Id, ActionIds.FindAction, StringComparison.OrdinalIgnoreCase))
                                    .ToList();
                return Task.FromResult(new PaletteResult<ActionMatch>
                {
                    Items = items,
                    TabCounts = new Dictionary<string, int> { [TabId] = items.Count },
                    IsComplete = true,
                });
            }
            catch (Exception ex)
            {
                L.Error("findAction.query.failed", "Find Action query failed", ex, ("query", query?.Text));
                throw; // the palette shows its error state
            }
        }

        public CodePreviewRequest GetPreview(ActionMatch item) => null;

        public bool Invoke(ActionMatch item, InvokeKind kind, PaletteQuery query, IReadOnlyList<ActionMatch> all)
        {
            var registry = _registry;
            if (item?.Action == null || registry == null) return false;
            string id = item.Action.Id;
            string title = item.Action.Title;
            var context = _context;
            L.Info("findAction.invoke", null, ("id", id), ("kind", kind.ToString()), ("queryLength", query?.Text?.Length ?? 0));
            Safe.RunAsync(L, "findAction.execute", async () =>
            {
                await Task.Yield(); // let the palette close before the action runs (it may open dialogs or windows)
                bool ok = await registry.ExecuteAsync(id, context);
                if (!ok) VsStatusBar.SetText(Strings.ActionFailed(title));
            }, ("id", id));
            return true;
        }

        private sealed class ActionPresenter : IItemPresenter<ActionMatch>
        {
            public void Present(ActionMatch m, RowData row)
            {
                row.Primary = m.Action.Title;
                var ranges = m.TitleMatch?.Ranges;
                row.PrimaryRanges = ranges == null || ranges.Count == 0
                    ? null
                    : ranges.Select(r => new TextRange(r.Start, r.Length)).ToArray();
                row.Right = Strings.CategoryName(m.Action.Category);
                row.ToolTip = m.Action.Id;
                row.AutomationName = m.Action.Title;
            }
        }
    }
}
