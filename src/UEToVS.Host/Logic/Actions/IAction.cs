using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace UEToVS.Host.Logic.Actions
{
    /// <summary>Action categories (§7.4). Shown as group headers in Find Action.</summary>
    public enum ActionCategory
    {
        Navigation,
        Refactoring,
        Generate,
        Fix,
        Unreal,
        Settings,
        Diagnostics,
    }

    /// <summary>Where an action is being invoked from; lets <see cref="IAction.Priority"/> rank per surface.</summary>
    public enum ActionSource
    {
        Menu,
        Keyboard,
        FindAction,
        AltEnter,
        LightBulb,
        ContextMenu,
        Programmatic,
    }

    /// <summary>
    /// Everything an action may look at. Built by the host per invocation; must be cheap to create. Editor-specific state
    /// (caret, selection, text view) is exposed through <see cref="Properties"/> and <see cref="Services"/> so this type
    /// stays free of VS types.
    /// </summary>
    public sealed class ActionContext
    {
        public ActionSource Source { get; set; } = ActionSource.Programmatic;

        /// <summary>Directory of the open solution, or null.</summary>
        public string SolutionDirectory { get; set; }

        public bool IsUnrealSolution { get; set; }

        /// <summary>Full path of the active document, or null.</summary>
        public string ActiveDocumentPath { get; set; }

        /// <summary>0-based caret line/column in the active document (-1 when unknown).</summary>
        public int CaretLine { get; set; } = -1;

        public int CaretColumn { get; set; } = -1;

        /// <summary>True when the engine process is connected (actions needing it should report unavailable otherwise).</summary>
        public bool EngineAvailable { get; set; }

        /// <summary>Host services (in VS: the package's IServiceProvider). May be null in tests.</summary>
        public IServiceProvider Services { get; set; }

        public IDictionary<string, object> Properties { get; } = new Dictionary<string, object>(StringComparer.Ordinal);

        public CancellationToken CancellationToken { get; set; }

        public T Get<T>(string key) where T : class => Properties.TryGetValue(key, out var v) ? v as T : null;
    }

    /// <summary>
    /// One executable capability (§7.4). Registered once in <see cref="ActionRegistry"/>, it appears in Find Action,
    /// Alt+Enter / light bulb, context menus and as a VS command. Feature modules talk to each other only through action ids.
    /// </summary>
    public interface IAction
    {
        /// <summary>Stable id, e.g. "uetovs.diagnostics.openLogFolder".</summary>
        string Id { get; }

        string Title { get; }

        ActionCategory Category { get; }

        /// <summary>Extra search terms (Chinese and English synonyms).</summary>
        IReadOnlyList<string> Keywords { get; }

        /// <summary>Must be cheap and must not block (called for every list refresh).</summary>
        bool IsAvailable(ActionContext context);

        Task ExecuteAsync(ActionContext context);

        /// <summary>Context-dependent ranking: higher first. 0 = neutral.</summary>
        int Priority(ActionContext context);
    }

    /// <summary>Simple delegate-backed action (used for host commands).</summary>
    public sealed class DelegateAction : IAction
    {
        private readonly Func<ActionContext, Task> _execute;
        private readonly Func<ActionContext, bool> _isAvailable;
        private readonly Func<ActionContext, int> _priority;

        public DelegateAction(string id, string title, ActionCategory category, Func<ActionContext, Task> execute,
                              Func<ActionContext, bool> isAvailable = null, Func<ActionContext, int> priority = null,
                              params string[] keywords)
        {
            if (string.IsNullOrWhiteSpace(id)) throw new ArgumentException("id required", nameof(id));
            Id = id;
            Title = title ?? id;
            Category = category;
            Keywords = keywords ?? new string[0];
            _execute = execute ?? throw new ArgumentNullException(nameof(execute));
            _isAvailable = isAvailable;
            _priority = priority;
        }

        public string Id { get; }
        public string Title { get; }
        public ActionCategory Category { get; }
        public IReadOnlyList<string> Keywords { get; }

        public bool IsAvailable(ActionContext context) => _isAvailable == null || _isAvailable(context);

        public Task ExecuteAsync(ActionContext context) => _execute(context);

        public int Priority(ActionContext context) => _priority?.Invoke(context) ?? 0;

        public override string ToString() => Id;
    }
}
