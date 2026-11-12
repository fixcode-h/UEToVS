using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using UEToVS.Contracts;
using UEToVS.Core.Diagnostics;
using UEToVS.Engine.Index;
using UEToVS.Engine.Indexing;
using UEToVS.Engine.Navigation;

namespace UEToVS.Engine.Deprecation
{
    /// <summary>
    /// v0.4.5 engine upgrade assistant: project references to APIs marked deprecated in the index (UE_DEPRECATED,
    /// UE_DEPRECATED_FORGAME, [[deprecated]]; see <see cref="DeprecationParser"/>). On demand only, one BelowNormal thread,
    /// cancellable. Reference lookup goes through the occurrence index (project segments only); each hit is resolved at its
    /// position (capped) so a project symbol that only shares the name is not reported as Exact. A replacement is offered
    /// only when the message names one that exists in the same scope with a compatible signature, so a batch rename of the
    /// identifier is enough.
    /// </summary>
    public sealed class DeprecationScanService : IDisposable
    {
        private static readonly Logger L = Log.For("Upgrade");

        /// <summary>Hits resolved through <see cref="SymbolResolver"/> per run; beyond it hits are kept as NameOnly.</summary>
        public const int MaxResolutions = 20000;

        /// <summary>Usages kept per run (the result travels over RPC in one piece).</summary>
        public const int MaxUsages = 20000;

        private readonly ISymbolIndexBackend _backend;
        private readonly FileTable _files;
        private readonly Func<bool> _indexReady;
        private readonly object _gate = new object();
        private volatile bool _enabled = true;
        private Run _run;

        public DeprecationScanService(ISymbolIndexBackend backend, FileTable files, Func<bool> indexReady)
        {
            _backend = backend ?? throw new ArgumentNullException(nameof(backend));
            _files = files;
            _indexReady = indexReady ?? (() => true);
        }

        public bool Enabled
        {
            get => _enabled;
            set
            {
                if (_enabled == value) return;
                _enabled = value;
                L.Info("upgrade.config", null, ("enabled", value));
                if (!value) Cancel("disabled");
            }
        }

        public DeprecationScanResponse Handle(DeprecationScanRequest request)
        {
            request = request ?? new DeprecationScanRequest();
            if (!_enabled)
            {
                L.Info("upgrade.disabled", null, ("action", request.Action.ToString()));
                return new DeprecationScanResponse { State = HeavyJobState.Disabled, Error = "The engine upgrade assistant is switched off in the UEToVS settings." };
            }
            switch (request.Action)
            {
                case HeavyJobAction.Start: return Start(request.IncludePlugins);
                case HeavyJobAction.Cancel:
                    Cancel("request");
                    return Status(true);
                case HeavyJobAction.Results: return Status(true);
                default: return Status(false);
            }
        }

        public DeprecationScanResponse Start(bool includePlugins)
        {
            if (!_enabled) return new DeprecationScanResponse { State = HeavyJobState.Disabled };
            lock (_gate)
            {
                if (_run != null && _run.State == HeavyJobState.Running) return _run.Snapshot(false);
                if (!(_backend is IDeprecationIndex index))
                    return new DeprecationScanResponse { State = HeavyJobState.Failed, Error = "The index backend has no deprecation data." };
                if (!_indexReady())
                {
                    L.Info("upgrade.refused", null, ("reason", "index-not-ready"));
                    return new DeprecationScanResponse { State = HeavyJobState.Failed, Error = "index-not-ready" };
                }
                _run = new Run(this, index, includePlugins);
                _run.Begin();
                return _run.Snapshot(false);
            }
        }

        public void Cancel(string reason)
        {
            Run run;
            lock (_gate) run = _run;
            run?.Cancel(reason);
        }

        /// <summary>Status; with <paramref name="withResults"/> the APIs found so far (all of them once completed).</summary>
        public DeprecationScanResponse Status(bool withResults)
        {
            Run run;
            lock (_gate) run = _run;
            return run?.Snapshot(withResults) ?? new DeprecationScanResponse { State = HeavyJobState.Idle };
        }

        public bool Wait(TimeSpan timeout)
        {
            Run run;
            lock (_gate) run = _run;
            return run == null || run.Done.Wait(timeout);
        }

        public void Dispose()
        {
            Cancel("shutdown");
            Wait(TimeSpan.FromSeconds(5));
        }

        // ================================================================== replacement check

        /// <summary>
        /// The replacement to rename usages of <paramref name="d"/> to, or null with <paramref name="note"/> saying why not:
        /// the message must name one (<see cref="DeprecationParser.SuggestReplacement"/>), in the deprecated symbol's own
        /// scope (a different owner is not a rename), present in the index, not deprecated itself, and compatible (same
        /// kind; for functions the same parameter count).
        /// </summary>
        public static string CompatibleReplacement(ISymbolIndexBackend backend, DeprecatedSymbol d, ISet<string> deprecatedKeys, out string note)
        {
            note = null;
            var sym = d.Symbol;
            string suggested = DeprecationParser.SuggestReplacement(d.Message, sym.Name);
            if (suggested == null) { note = "no-suggestion"; return null; }
            string last = TypeText.LastSegment(suggested);
            string qual = TypeText.Parent(suggested);
            if (qual != null && !TypeText.ContainerMatches(sym.Container, qual)) { note = "kind"; return null; }
            var candidates = backend.FindSymbols(last, null).Where(c => string.Equals(c.Container ?? string.Empty, sym.Container ?? string.Empty, StringComparison.Ordinal)).ToList();
            if (candidates.Count == 0) { note = "not-found"; return null; }
            bool callable = TypeText.IsCallableKind(sym.Kind);
            int wantParams = callable ? TypeText.ParameterCount(sym.Signature) : -1;
            foreach (var c in candidates)
            {
                if (deprecatedKeys != null && deprecatedKeys.Contains(Key(c))) continue;
                if (callable)
                {
                    if (!TypeText.IsCallableKind(c.Kind)) continue;
                    if (wantParams >= 0 && TypeText.ParameterCount(c.Signature) != wantParams) continue;
                    return last;
                }
                if (c.Kind == sym.Kind) return last;
            }
            note = "signature";
            return null;
        }

        internal static string Key(SymbolInfo s) => (s.Container ?? string.Empty) + "::" + s.Name + "#" + (int)s.Kind;

        // ================================================================== one run

        private sealed class Run
        {
            private readonly DeprecationScanService _owner;
            private readonly IDeprecationIndex _index;
            private readonly bool _includePlugins;
            private readonly CancellationTokenSource _cts = new CancellationTokenSource();
            private readonly Stopwatch _sw = new Stopwatch();
            private readonly object _resultGate = new object();
            private readonly List<DeprecatedApi> _apis = new List<DeprecatedApi>();
            private volatile HeavyJobState _state = HeavyJobState.Running;
            private string _error;
            private bool _hasDeprecations;
            private int _total;
            private int _done;
            private int _usages;
            private int _resolutions;
            private string _cancelReason;

            public Run(DeprecationScanService owner, IDeprecationIndex index, bool includePlugins)
            {
                _owner = owner;
                _index = index;
                _includePlugins = includePlugins;
            }

            public HeavyJobState State => _state;
            public ManualResetEventSlim Done { get; } = new ManualResetEventSlim(false);

            public void Begin()
            {
                _sw.Start();
                new Thread(Execute) { IsBackground = true, Priority = ThreadPriority.BelowNormal, Name = "UEToVS upgrade assistant" }.Start();
            }

            public void Cancel(string reason)
            {
                if (_state != HeavyJobState.Running) return;
                _cancelReason = reason;
                try { _cts.Cancel(); } catch (ObjectDisposedException) { }
            }

            private void Execute()
            {
                var ct = _cts.Token;
                var documents = new DocumentCache(64, 64L * 1024 * 1024);
                try
                {
                    var deprecated = _index.ListDeprecated(ct);
                    _hasDeprecations = deprecated.Count > 0;
                    _total = deprecated.Count;
                    L.Info("upgrade.start", null, ("deprecated", deprecated.Count), ("includePlugins", _includePlugins));
                    var keys = new HashSet<string>(deprecated.Select(d => Key(d.Symbol)), StringComparer.Ordinal);
                    // one occurrence lookup per name (overloads / same name in several classes share it)
                    foreach (var byName in deprecated.GroupBy(d => d.Symbol.Name, StringComparer.Ordinal))
                    {
                        ct.ThrowIfCancellationRequested();
                        var group = byName.ToList();
                        var hits = _index.FindProjectOccurrences(byName.Key, _includePlugins, ct);
                        if (hits.Count > 0) Collect(group, hits, keys, documents, ct);
                        Interlocked.Add(ref _done, group.Count);
                    }
                    _state = HeavyJobState.Completed;
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested)
                {
                    _state = HeavyJobState.Cancelled;
                }
                catch (Exception ex)
                {
                    _error = ex.Message;
                    _state = HeavyJobState.Failed;
                    L.Error("upgrade.failed", "Upgrade assistant scan failed", ex, ("done", _done), ("total", _total));
                }
                finally
                {
                    _sw.Stop();
                    documents.Clear();
                    List<DeprecatedApi> apis;
                    lock (_resultGate) apis = _apis.ToList();
                    var data = new (string, object)[]
                    {
                        ("deprecated", _total), ("done", _done), ("apis", apis.Count), ("usages", _usages), ("resolutions", _resolutions),
                        ("replaceable", apis.Count(a => a.Replacement != null)),
                        ("versions", string.Join(",", apis.GroupBy(a => string.IsNullOrEmpty(a.Version) ? "-" : a.Version).Select(g => g.Key + "=" + g.Sum(a => a.Usages.Count)))),
                        ("ms", Math.Round(_sw.Elapsed.TotalMilliseconds, 1)), ("includePlugins", _includePlugins), ("reason", _cancelReason),
                    };
                    if (_state == HeavyJobState.Completed) L.Info("upgrade.done", null, data);
                    else if (_state == HeavyJobState.Cancelled) L.Info("upgrade.cancelled", null, data);
                    Done.Set();
                    _cts.Dispose();
                }
            }

            private void Collect(List<DeprecatedSymbol> group, IReadOnlyList<OccurrenceHit> hits, ISet<string> keys, DocumentCache documents, CancellationToken ct)
            {
                // every same-named symbol in the index: when all are deprecated an unresolved hit still means a deprecated API
                var all = _owner._backend.FindSymbols(group[0].Symbol.Name, null);
                bool nameIsDeprecatedOnly = all.Count > 0 && all.All(s => keys.Contains(Key(s)));
                var byKey = group.GroupBy(d => Key(d.Symbol)).ToDictionary(g => g.Key, g => g.First(), StringComparer.Ordinal);
                var usages = new Dictionary<string, List<DeprecatedUsage>>(StringComparer.Ordinal);
                foreach (var fileHits in hits.GroupBy(h => h.Path, StringComparer.OrdinalIgnoreCase))
                {
                    ct.ThrowIfCancellationRequested();
                    var doc = documents.GetFile(fileHits.Key);
                    if (doc == null) continue;
                    var ctx = new NavContext(_owner._backend, _owner._files, doc, ct);
                    foreach (var h in fileHits.OrderBy(h => h.Offset))
                    {
                        if (Volatile.Read(ref _usages) >= MaxUsages) return;
                        string target = null;
                        var confidence = ResolutionConfidence.NameOnly;
                        if (Interlocked.Increment(ref _resolutions) <= MaxResolutions)
                        {
                            var r = SymbolResolver.Resolve(ctx, doc, h.Offset);
                            if (r.OnDeclaration != null || r.Local != null) continue; // a declaration / a local of the same name
                            var matched = r.Candidates.Where(c => byKey.ContainsKey(Key(c))).ToList();
                            if (matched.Count > 0)
                            {
                                target = Key(matched[0]);
                                confidence = matched.Count == r.Candidates.Count ? ResolutionConfidence.Exact : ResolutionConfidence.NameOnly;
                            }
                            else if (r.Candidates.Count > 0 && r.Confidence >= ResolutionConfidence.Scoped) continue; // resolved to something else
                        }
                        if (target == null)
                        {
                            if (!nameIsDeprecatedOnly) continue;
                            target = byKey.Keys.First();
                        }
                        var (line, col) = doc.PositionOf(h.Offset);
                        if (!usages.TryGetValue(target, out var list)) usages[target] = list = new List<DeprecatedUsage>();
                        list.Add(new DeprecatedUsage
                        {
                            FilePath = fileHits.Key, Line = line, Column = col, Length = group[0].Symbol.Name.Length,
                            LineText = Inspection.InspectionFixes.LinePreview(doc, line), Confidence = confidence,
                        });
                        Interlocked.Increment(ref _usages);
                    }
                }
                foreach (var kv in usages)
                {
                    var d = byKey[kv.Key];
                    string replacement = CompatibleReplacement(_owner._backend, d, keys, out string note);
                    var api = new DeprecatedApi { Symbol = d.Symbol, Version = d.Version ?? string.Empty, Message = d.Message ?? string.Empty, Replacement = replacement, ReplacementNote = note, Usages = kv.Value };
                    lock (_resultGate) _apis.Add(api);
                }
            }

            public DeprecationScanResponse Snapshot(bool withResults)
            {
                var r = new DeprecationScanResponse
                {
                    State = _state,
                    Error = _error,
                    IndexHasDeprecations = _hasDeprecations || _state == HeavyJobState.Running,
                    DeprecatedSymbols = _total,
                    SymbolsDone = Volatile.Read(ref _done),
                    Usages = Volatile.Read(ref _usages),
                    ElapsedMs = Math.Round(_sw.Elapsed.TotalMilliseconds, 1),
                };
                if (withResults)
                    lock (_resultGate)
                        r.Apis = _apis.OrderByDescending(a => a.Version, Comparer<string>.Create(DeprecationParser.CompareVersions))
                                      .ThenBy(a => a.Symbol.Container, StringComparer.Ordinal).ThenBy(a => a.Symbol.Name, StringComparer.Ordinal).ToList();
                return r;
            }
        }
    }
}
