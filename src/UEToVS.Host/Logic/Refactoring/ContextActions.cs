using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using UEToVS.Contracts;
using UEToVS.Core.Diagnostics;
using UEToVS.Core.Unreal;

namespace UEToVS.Host.Logic.Refactoring
{
    /// <summary>Stable ids of the M4 refactoring actions (§7.4). Each is an IAction in the hub, so Find Action lists it too.</summary>
    public static class RefactorActionIds
    {
        public const string QuickActions = "uetovs.refactor.quickActions";
        public const string CreateImplementation = "uetovs.refactor.createImplementation";
        public const string CreateDeclaration = "uetovs.refactor.createDeclaration";
        public const string ImplementVirtual = "uetovs.refactor.implementVirtual";
        public const string AddInclude = "uetovs.refactor.addInclude";
        public const string Rename = "uetovs.refactor.rename";

        // ---- v0.4.5 refactoring batch 2 (Refactoring/Batch2/*, RefactorProviders2)
        public const string ChangeSignature = "uetovs.refactor.changeSignature";
        public const string MoveImplementation = "uetovs.refactor.moveImplementation";
        public const string MoveImplementationClass = "uetovs.refactor.moveImplementationClass";
        public const string AddForwardDeclaration = "uetovs.refactor.addForwardDeclaration";
        public const string IntroduceVariable = "uetovs.refactor.introduceVariable";
        public const string ExtractMethod = "uetovs.refactor.extractMethod";
        public const string EncapsulateField = "uetovs.refactor.encapsulateField";
        public const string GenerateGetterSetter = "uetovs.generate.getterSetter";
    }

    /// <summary>ActionContext.Properties keys used by the refactoring actions.</summary>
    public static class RefactorKeys
    {
        /// <summary>The <see cref="RefactorContext"/> the action was offered for (absent from Find Action: the action computes it).</summary>
        public const string Context = "uetovs.refactor.context";

        /// <summary>The <see cref="ContextActionItem.Argument"/> of the chosen item.</summary>
        public const string Argument = "uetovs.refactor.argument";

        /// <summary>The editor view (host type; opaque here).</summary>
        public const string View = "uetovs.editor.view";
    }

    /// <summary>What is at the caret, as the context-action providers see it (VS-free; built by the host per Alt+Enter / light bulb).</summary>
    public sealed class RefactorContext
    {
        public string FilePath { get; set; }
        public int Line { get; set; }
        public int Column { get; set; }

        /// <summary>Editor snapshot version the context was computed for (light bulb cache key).</summary>
        public long Version { get; set; }

        /// <summary>Engine answer (may be null when the engine is unavailable: only text-based actions are offered then).</summary>
        public RefactoringContextResponse Engine { get; set; }

        /// <summary>The file belongs to the project (not engine / third party): edits are allowed.</summary>
        public bool IsProjectFile { get; set; } = true;

        public bool IsHeader => RelatedNames.IsHeader(FilePath);

        public FunctionSignature Function => Engine?.Function;

        public SymbolInfo Symbol => Engine?.Symbol;

        public string Word => Engine?.Word;

        /// <summary>v0.4.5: squiggles (UE0011 deprecated API, UE0012 naming, spelling) under the caret, for the Alt+Enter fixes.</summary>
        public List<CodeDiagnostic> Diagnostics { get; set; } = new List<CodeDiagnostic>();
        // ---- v0.4.5 refactoring batch 2: selection and (lazy) text of the snapshot the context was computed for

        /// <summary>Caret offset in <see cref="Text"/> (-1 when unknown).</summary>
        public int CaretOffset { get; set; } = -1;

        /// <summary>Selection offsets in <see cref="Text"/> (-1 when there is no selection).</summary>
        public int SelectionStart { get; set; } = -1;

        public int SelectionEnd { get; set; } = -1;

        public bool HasSelection => SelectionStart >= 0 && SelectionEnd > SelectionStart;

        /// <summary>Supplies the snapshot text on first use (providers that only need the engine answer never pay for it).</summary>
        public Func<string> TextSource { get; set; }

        private string _text;

        /// <summary>Text of the snapshot (<see cref="Version"/>); null when unknown.</summary>
        public string Text => _text ?? (_text = TextSource?.Invoke());
    }

    /// <summary>One entry of the Alt+Enter menu / light bulb: runs <see cref="ActionId"/> through the action hub with <see cref="Argument"/>.</summary>
    public sealed class ContextActionItem
    {
        public ContextActionItem(string actionId, string title, int priority = 0, object argument = null)
        {
            ActionId = actionId;
            Title = title;
            Priority = priority;
            Argument = argument;
        }

        public string ActionId { get; }
        public string Title { get; }

        /// <summary>Higher first.</summary>
        public int Priority { get; }

        public object Argument { get; }

        /// <summary>Group header in the menu ("生成" / "重构" / "修复").</summary>
        public string Group { get; set; }

        /// <summary>Provider id (logs).</summary>
        public string Provider { get; set; }

        public override string ToString() => $"{ActionId} \"{Title}\" p={Priority}";
    }

    /// <summary>
    /// Supplies context actions for a caret. Must be cheap and must not block: everything it needs is in the
    /// <see cref="RefactorContext"/> (one engine call per caret, made by the host).
    /// </summary>
    public interface IContextActionProvider
    {
        string Id { get; }

        IEnumerable<ContextActionItem> GetActions(RefactorContext context);
    }

    /// <summary>
    /// Registry of context-action providers (Alt+Enter / light bulb). Collects the items of every provider for a caret,
    /// isolates misbehaving providers (logged, skipped), sorts by priority then title, removes duplicates (same action +
    /// title). Thread-safe (copy-on-write).
    /// </summary>
    public sealed class ContextActionHub
    {
        private static readonly Logger L = Log.For("Refactor");
        private readonly object _gate = new object();
        private IContextActionProvider[] _providers = Array.Empty<IContextActionProvider>();

        public int Count => _providers.Length;

        public void Register(IContextActionProvider provider)
        {
            if (provider == null) throw new ArgumentNullException(nameof(provider));
            lock (_gate)
            {
                if (_providers.Any(p => string.Equals(p.Id, provider.Id, StringComparison.Ordinal)))
                    throw new InvalidOperationException("Context action provider already registered: " + provider.Id);
                _providers = _providers.Concat(new[] { provider }).ToArray();
            }
        }

        public bool Unregister(string id)
        {
            lock (_gate)
            {
                int before = _providers.Length;
                _providers = _providers.Where(p => !string.Equals(p.Id, id, StringComparison.Ordinal)).ToArray();
                return _providers.Length != before;
            }
        }

        public List<ContextActionItem> Collect(RefactorContext context)
        {
            var result = new List<ContextActionItem>();
            if (context == null) return result;
            var sw = Stopwatch.StartNew();
            foreach (var p in _providers)
            {
                try
                {
                    foreach (var item in p.GetActions(context) ?? Enumerable.Empty<ContextActionItem>())
                    {
                        if (item == null || string.IsNullOrEmpty(item.ActionId) || string.IsNullOrEmpty(item.Title)) continue;
                        item.Provider = p.Id;
                        result.Add(item);
                    }
                }
                catch (Exception ex)
                {
                    L.Error("refactor.provider.failed", "A context action provider threw", ex, ("provider", p.Id), ("file", context.FilePath), ("line", context.Line));
                }
            }
            var seen = new HashSet<string>(StringComparer.Ordinal);
            result = result.Where(i => seen.Add(i.ActionId + "|" + i.Title)).ToList();
            result.Sort((a, b) =>
            {
                int c = b.Priority.CompareTo(a.Priority);
                return c != 0 ? c : string.Compare(a.Title, b.Title, StringComparison.CurrentCultureIgnoreCase);
            });
            if (sw.ElapsedMilliseconds > 5)
                L.Warn("refactor.providers.slow", "Context action providers took long", ("ms", sw.ElapsedMilliseconds), ("providers", _providers.Length));
            return result;
        }
    }
}
