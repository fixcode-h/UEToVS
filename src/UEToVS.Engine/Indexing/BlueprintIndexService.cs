using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using UEToVS.Contracts;
using UEToVS.Core.Diagnostics;
using UEToVS.Engine.Unreal;

namespace UEToVS.Engine.Indexing
{
    /// <summary>
    /// Keeps <see cref="BlueprintIndex"/> in line with the project's assets: loads the saved index at start, refreshes in
    /// the background (2 threads, low priority) once the C++ index is ready and whenever the asset table changes
    /// (debounced), then saves it. Engine content is not read: Blueprints that matter for project code live in the
    /// project and its plugins.
    /// </summary>
    public sealed class BlueprintIndexService : IDisposable
    {
        private static readonly Logger L = Log.For("Blueprints");

        private readonly IndexOrchestrator _index;
        private readonly string _file;
        private readonly CancellationToken _shutdown;
        private readonly object _gate = new object();
        private int _refreshedVersion = -1;
        private CancellationTokenSource _pending;
        private Task _running = Task.CompletedTask;

        public BlueprintIndexService(IndexOrchestrator index, string uetovsRoot, CancellationToken shutdown)
        {
            _index = index;
            _file = BlueprintIndex.DefaultPath(uetovsRoot);
            _shutdown = shutdown;
            var sw = Stopwatch.StartNew();
            if (Index.Load(_file))
                L.Info("blueprints.loaded", null, ("file", _file), ("assets", Index.Count), ("blueprints", Index.SearchableCount), ("ms", sw.ElapsedMilliseconds));
            _index.StatusChanged += OnStatusChanged;
        }

        public BlueprintIndex Index { get; } = new BlueprintIndex();

        /// <summary>Debounce between an asset change and the refresh.</summary>
        public TimeSpan Debounce { get; set; } = TimeSpan.FromSeconds(3);

        /// <summary>Completes when the refresh in flight (if any) ends; for tests and the bench.</summary>
        public Task Idle { get { lock (_gate) return _running; } }

        private void OnStatusChanged()
        {
            if (_index.Phase != IndexPhase.Ready) return;
            int v = _index.Assets.Version;
            if (v == Volatile.Read(ref _refreshedVersion)) return;
            Schedule(Debounce);
        }

        /// <summary>Refreshes now (or after <paramref name="delay"/>), replacing a refresh that has not started yet.</summary>
        public Task Schedule(TimeSpan delay)
        {
            lock (_gate)
            {
                _pending?.Cancel();
                var cts = CancellationTokenSource.CreateLinkedTokenSource(_shutdown);
                _pending = cts;
                var previous = _running;
                _running = Task.Run(async () =>
                {
                    try { await previous.ConfigureAwait(false); }
                    catch (Exception) { } // a failed earlier run is already logged; this one starts fresh
                    try
                    {
                        if (delay > TimeSpan.Zero) await Task.Delay(delay, cts.Token).ConfigureAwait(false);
                        Refresh(cts.Token);
                    }
                    catch (OperationCanceledException) { } // replaced by a newer request or shutting down
                    catch (Exception ex) { L.Warn("blueprints.refresh.failed", "Blueprint index refresh failed", ex); }
                });
                return _running;
            }
        }

        private void Refresh(CancellationToken ct)
        {
            int version = _index.Assets.Version;
            var assets = _index.Assets.Snapshot()
                .Where(a => a.Origin == ItemOrigin.Project || a.Origin == ItemOrigin.ProjectPlugin)
                .Select(a => (a.FilePath, ObjectPath(a)))
                .ToList();
            var sw = Stopwatch.StartNew();
            int read = Index.Refresh(assets, ct, maxParallelism: 2);
            Volatile.Write(ref _refreshedVersion, version);
            if (read > 0 || !File.Exists(_file))
            {
                try { Index.Save(_file); }
                catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException) { L.Warn("blueprints.save.failed", "Saving the Blueprint index failed", ex, ("file", _file)); }
            }
            L.Info("blueprints.refreshed", null, ("assets", assets.Count), ("read", read), ("blueprints", Index.SearchableCount), ("ms", sw.ElapsedMilliseconds));
        }

        private static string ObjectPath(AssetEntry a) =>
            a.PackageDir == null ? null : a.PackageDir + "/" + a.Name + "." + a.Name;

        // ---------------- answers for navigation ----------------

        /// <summary>
        /// Blueprint hits for Find References: assets using <paramref name="symbol"/> (a member of a class) or, for a class,
        /// Blueprints deriving from it. Each hit points at the .uasset (line 0) and is name-based (<see cref="ResolutionConfidence.Scoped"/>
        /// when the class name is in the asset too).
        /// </summary>
        public List<ReferenceHit> ReferenceHits(SymbolInfo symbol, ICollection<string> classAndSubclasses, int max = 500)
        {
            var hits = new List<ReferenceHit>();
            if (symbol == null || string.IsNullOrEmpty(symbol.Name) || !Index.IsReady) return hits;
            bool isType = symbol.Kind == SymbolKind.Class || symbol.Kind == SymbolKind.Struct || symbol.Kind == SymbolKind.Enum;
            if (isType)
            {
                var ue = classAndSubclasses.Select(BlueprintIndex.UeClassName).ToList();
                foreach (var e in Flatten(Index.Derived(ue)).Take(max))
                    hits.Add(Hit(e, ReferenceKind.TypeUse, ResolutionConfidence.Exact, "Blueprint " + e.Name + " : " + UassetReader.ClassName(e.ParentClass)));
                // other Blueprints using the type (variables, casts, struct pins)
                var derived = new HashSet<string>(hits.Select(h => h.Location.FilePath), StringComparer.OrdinalIgnoreCase);
                foreach (var e in Index.Usages(BlueprintIndex.UeClassName(symbol.Name), null, max))
                    if (!derived.Contains(e.FilePath) && hits.Count < max)
                        hits.Add(Hit(e, ReferenceKind.TypeUse, ResolutionConfidence.NameOnly, (e.IsMap ? "Level " : "Blueprint ") + e.Name + " uses " + symbol.Name));
                return hits;
            }
            if (string.IsNullOrEmpty(symbol.Container)) return hits;
            var owners = classAndSubclasses.Select(BlueprintIndex.UeClassName).ToList();
            foreach (var e in Index.Usages(symbol.Name, owners, max))
                hits.Add(Hit(e, symbol.Kind == SymbolKind.Function || symbol.Kind == SymbolKind.Method ? ReferenceKind.Call : ReferenceKind.Read,
                    ResolutionConfidence.Scoped, (e.IsMap ? "Level " : "Blueprint ") + e.Name + " uses " + symbol.Name));
            return hits;
        }

        public static IEnumerable<BlueprintEntry> Flatten(IEnumerable<DerivedBlueprint> nodes)
        {
            foreach (var n in nodes)
            {
                yield return n.Entry;
                foreach (var c in Flatten(n.Children)) yield return c;
            }
        }

        private static ReferenceHit Hit(BlueprintEntry e, ReferenceKind kind, ResolutionConfidence confidence, string text) => new ReferenceHit
        {
            Location = new SourceLocation { FilePath = e.FilePath, Line = 0, Column = 0 },
            Length = 0,
            Kind = kind | ReferenceKind.Blueprint,
            LineText = text + (e.ObjectPath != null ? "  (" + e.ObjectPath + ")" : string.Empty),
            LineTextColumn = 0,
            Context = e.ObjectPath,
            Confidence = confidence,
            Origin = ItemOrigin.Project,
        };

        /// <summary>Derived Blueprints as type hierarchy nodes (asset rows: no symbol, <see cref="TypeHierarchyNode.Asset"/> set).</summary>
        public List<TypeHierarchyNode> HierarchyNodes(ICollection<string> classAndSubclasses, int maxNodes = 300)
        {
            var nodes = new List<TypeHierarchyNode>();
            if (!Index.IsReady) return nodes;
            int count = 0;
            TypeHierarchyNode Map(DerivedBlueprint d)
            {
                count++;
                var n = new TypeHierarchyNode
                {
                    DisplayName = d.Entry.Name,
                    Asset = new AssetItem { Name = d.Entry.Name, FilePath = d.Entry.FilePath, ObjectPath = d.Entry.ObjectPath, ClassName = "Blueprint", Origin = ItemOrigin.Project },
                };
                foreach (var c in d.Children)
                {
                    if (count >= maxNodes) { n.Truncated = true; break; }
                    n.Children.Add(Map(c));
                }
                return n;
            }
            foreach (var d in Index.Derived(classAndSubclasses.Select(BlueprintIndex.UeClassName).ToList()))
            {
                if (count >= maxNodes) break;
                nodes.Add(Map(d));
            }
            return nodes;
        }

        public void Dispose()
        {
            _index.StatusChanged -= OnStatusChanged;
            lock (_gate) _pending?.Cancel();
        }
    }
}
