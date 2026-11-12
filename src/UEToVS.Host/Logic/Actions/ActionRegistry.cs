using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using UEToVS.Core.Diagnostics;
using UEToVS.Core.Matching;

namespace UEToVS.Host.Logic.Actions
{
    /// <summary>A ranked Find Action / Alt+Enter result.</summary>
    public sealed class ActionMatch
    {
        public IAction Action { get; set; }

        /// <summary>Match on the title (null when the hit came from id/keywords or the query was empty).</summary>
        public MatchResult TitleMatch { get; set; }

        public int Score { get; set; }
        public int Priority { get; set; }

        public override string ToString() => $"{Action.Id} score={Score} prio={Priority}";
    }

    /// <summary>
    /// The action hub (§7.4): registration, lookup by id, query for Find Action (fuzzy over title, id and keywords,
    /// filtered by <see cref="IAction.IsAvailable"/>, ranked by match then <see cref="IAction.Priority"/>), and logged,
    /// timed execution. Thread-safe. A misbehaving action (throwing IsAvailable/Priority) is logged and treated as
    /// unavailable; it never breaks the list.
    /// </summary>
    public sealed class ActionRegistry
    {
        private static readonly Logger L = Log.For("Actions");
        private readonly object _gate = new object();
        private Dictionary<string, IAction> _actions = new Dictionary<string, IAction>(StringComparer.OrdinalIgnoreCase);

        public event EventHandler Changed;

        public int Count
        {
            get { lock (_gate) return _actions.Count; }
        }

        /// <summary>Registers an action; throws when the id is taken (ids are global and stable).</summary>
        public void Register(IAction action)
        {
            if (action == null) throw new ArgumentNullException(nameof(action));
            lock (_gate)
            {
                if (_actions.ContainsKey(action.Id)) throw new InvalidOperationException("Action already registered: " + action.Id);
                var copy = new Dictionary<string, IAction>(_actions, StringComparer.OrdinalIgnoreCase) { [action.Id] = action };
                _actions = copy;
            }
            L.Debug("action.registered", null, ("id", action.Id), ("category", action.Category.ToString()));
            Changed?.Invoke(this, EventArgs.Empty);
        }

        public bool Unregister(string id)
        {
            bool removed;
            lock (_gate)
            {
                var copy = new Dictionary<string, IAction>(_actions, StringComparer.OrdinalIgnoreCase);
                removed = copy.Remove(id);
                _actions = copy;
            }
            if (removed) Changed?.Invoke(this, EventArgs.Empty);
            return removed;
        }

        public IAction Get(string id)
        {
            if (id == null) return null;
            var map = _actions;
            return map.TryGetValue(id, out var a) ? a : null;
        }

        public IReadOnlyList<IAction> All => _actions.Values.OrderBy(a => a.Id, StringComparer.Ordinal).ToList();

        /// <summary>
        /// Available actions matching <paramref name="query"/> (empty = all available), best first.
        /// Title matches rank above id/keyword matches; ties are broken by priority, then title.
        /// </summary>
        public List<ActionMatch> Query(string query, ActionContext context, int max = int.MaxValue, ActionCategory? category = null)
        {
            context = context ?? new ActionContext();
            var matcher = new FuzzyMatcher(query);
            var results = new List<ActionMatch>();
            foreach (var a in _actions.Values)
            {
                if (category.HasValue && a.Category != category.Value) continue;
                int score;
                MatchResult titleMatch = null;
                if (matcher.IsEmpty)
                {
                    score = 0;
                }
                else
                {
                    titleMatch = matcher.Match(a.Title);
                    score = titleMatch.IsMatch ? titleMatch.Score : int.MinValue;
                    if (!titleMatch.IsMatch) titleMatch = null;
                    var idMatch = matcher.Match(a.Id);
                    if (idMatch.IsMatch) score = Math.Max(score, idMatch.Score - 2000);
                    if (a.Keywords != null)
                    {
                        foreach (var k in a.Keywords)
                        {
                            var km = matcher.Match(k);
                            if (km.IsMatch) score = Math.Max(score, km.Score - 1000);
                        }
                    }
                    if (score == int.MinValue) continue;
                }
                if (!SafeIsAvailable(a, context)) continue;
                results.Add(new ActionMatch { Action = a, TitleMatch = titleMatch, Score = score, Priority = SafePriority(a, context) });
            }
            results.Sort((x, y) =>
            {
                int c = y.Score.CompareTo(x.Score);
                if (c != 0) return c;
                c = y.Priority.CompareTo(x.Priority);
                return c != 0 ? c : string.Compare(x.Action.Title, y.Action.Title, StringComparison.CurrentCultureIgnoreCase);
            });
            if (results.Count > max) results.RemoveRange(max, results.Count - max);
            return results;
        }

        /// <summary>
        /// Executes an action by id with logging and timing. Returns false when it is unknown, unavailable or failed;
        /// exceptions are logged (action.failed) and never propagate.
        /// </summary>
        public async Task<bool> ExecuteAsync(string id, ActionContext context)
        {
            context = context ?? new ActionContext();
            var a = Get(id);
            if (a == null)
            {
                L.Warn("action.unknown", "Unknown action", ("id", id), ("source", context.Source.ToString()));
                return false;
            }
            if (!SafeIsAvailable(a, context))
            {
                L.Info("action.unavailable", "Action not available in this context", ("id", id), ("source", context.Source.ToString()));
                return false;
            }
            using (var scope = L.Time("action.execute", 5000, ("id", id), ("source", context.Source.ToString())))
            {
                try
                {
                    await a.ExecuteAsync(context).ConfigureAwait(true);
                    L.Info("action.executed", null, ("id", id), ("source", context.Source.ToString()), ("ms", Math.Round(scope.ElapsedMs, 1)));
                    return true;
                }
                catch (OperationCanceledException)
                {
                    L.Info("action.canceled", null, ("id", id));
                    return false;
                }
                catch (Exception ex)
                {
                    scope.Fail();
                    L.Error("action.failed", "Action failed: " + a.Title, ex, ("id", id), ("source", context.Source.ToString()));
                    return false;
                }
            }
        }

        private static bool SafeIsAvailable(IAction a, ActionContext ctx)
        {
            try
            {
                return a.IsAvailable(ctx);
            }
            catch (Exception ex)
            {
                L.Error("action.isAvailable.failed", "IsAvailable threw", ex, ("id", a.Id));
                return false;
            }
        }

        private static int SafePriority(IAction a, ActionContext ctx)
        {
            try
            {
                return a.Priority(ctx);
            }
            catch (Exception ex)
            {
                L.Error("action.priority.failed", "Priority threw", ex, ("id", a.Id));
                return 0;
            }
        }
    }
}
