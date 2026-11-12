using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using StreamJsonRpc;
using UEToVS.Contracts;
using UEToVS.Core.Diagnostics;
using UEToVS.Core.Matching;

namespace UEToVS.Mcp.Data
{
    /// <summary>Where an answer came from (reported to the agent and logged).</summary>
    public static class Sources
    {
        /// <summary>The running UEToVS.Engine (live: includes unsaved-to-segment changes of the current session).</summary>
        public const string Engine = "engine";

        /// <summary>Index segments on disk: the last compaction plus the in-memory delta the engine flushes at shutdown (≤5 s).</summary>
        public const string Index = "index";

        /// <summary>Name-based answer computed from the index when the engine's semantic answer was empty.</summary>
        public const string IndexHeuristic = "index-heuristic";
    }

    public sealed class Answer<T>
    {
        public T Value { get; set; }
        public string Source { get; set; }
        public bool IndexReady { get; set; }

        /// <summary>Short explanation for the agent (e.g. why a fallback was used); null when nothing to say.</summary>
        public string Note { get; set; }
    }

    /// <summary>A tool failure the agent can act on (bad argument, nothing to query). Becomes an isError result.</summary>
    public sealed class ToolException : Exception
    {
        public ToolException(string code, string message) : base(message) => Code = code;

        public string Code { get; }
    }

    /// <summary>
    /// Data access for the MCP tools: the running engine first (<see cref="EngineConnection"/>), the on-disk index
    /// otherwise (<see cref="OfflineIndex"/>), and name-based fallbacks from the index where the engine's navigation
    /// RPCs cannot resolve the symbol (or the engine is not running). All methods are thread-safe and cancellable.
    /// </summary>
    public sealed class CodeIndex : IDisposable
    {
        private static readonly Logger L = Log.For("Mcp.Data");
        public static readonly TimeSpan EngineCallTimeout = TimeSpan.FromSeconds(15);

        private readonly EngineConnection _engine;
        private readonly OfflineIndex _offline;

        public CodeIndex(string solutionDir, DataSourceMode mode, EngineConnection engine = null, OfflineIndex offline = null)
        {
            SolutionDir = solutionDir;
            Mode = mode;
            _engine = engine ?? new EngineConnection(solutionDir);
            _offline = offline ?? new OfflineIndex(solutionDir);
            Lua = new LuaIndexReader(solutionDir);
        }

        public string SolutionDir { get; }
        public DataSourceMode Mode { get; }
        public EngineConnection Engine => _engine;
        public OfflineIndex Offline => _offline;
        public SourceTextCache Texts { get; } = new SourceTextCache();

        /// <summary>Lua script index (lua-index.json written by the engine); always read from disk.</summary>
        public LuaIndexReader Lua { get; }

        // ------------------------------------------------------------------ engine plumbing

        private async Task<(bool Ok, T Value)> TryEngineAsync<T>(string op, Func<IEngineService, CancellationToken, Task<T>> call, CancellationToken ct)
        {
            if (Mode == DataSourceMode.IndexOnly) return (false, default);
            var engine = await _engine.GetAsync(ct).ConfigureAwait(false);
            if (engine == null)
            {
                if (Mode == DataSourceMode.EngineOnly)
                    throw new ToolException("engine_unavailable", "UEToVS.Engine is not running for this solution (" + (_engine.LastError ?? "no engine.json") + "). Open the solution in Visual Studio, or start the server with --source auto.");
                return (false, default);
            }
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(EngineCallTimeout);
            try
            {
                return (true, await call(engine, timeout.Token).ConfigureAwait(false));
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex) when (IsTransportFailure(ex) || ex is OperationCanceledException)
            {
                if (!(ex is OperationCanceledException)) _engine.Invalidate(ex); // a timeout keeps the connection
                L.Warn("mcp.engine.call_failed", "Engine call failed; using the index on disk", ex, ("op", op));
                if (Mode == DataSourceMode.EngineOnly) throw new ToolException("engine_unavailable", "Lost the connection to UEToVS.Engine: " + ex.Message);
                return (false, default);
            }
        }

        private static bool IsTransportFailure(Exception ex) =>
            ex is ConnectionLostException || ex is ObjectDisposedException || ex is IOException || ex is RemoteRpcException && !(ex is RemoteInvocationException);

        private void RequireOffline()
        {
            if (!_offline.IsAvailable)
                throw new ToolException("no_index", "No UEToVS index for " + SolutionDir + " (" + _offline.SymbolsDirectory + " missing) and no running engine. " +
                                                    "Open the solution in Visual Studio with UEToVS once so it builds the index, or pass --solution-dir.");
        }

        // ------------------------------------------------------------------ search

        public async Task<Answer<SearchResponse>> SearchAsync(SearchRequest req, CancellationToken ct)
        {
            var (ok, resp) = await TryEngineAsync("search", (e, c) => e.SearchAsync(req, c), ct).ConfigureAwait(false);
            if (ok) return new Answer<SearchResponse> { Value = resp ?? new SearchResponse(), Source = Sources.Engine, IndexReady = resp?.IndexReady ?? false };

            RequireOffline();
            var copy = new SearchRequest
            {
                Pattern = req.Pattern,
                Container = req.Container,
                IncludeTypes = req.IncludeTypes,
                IncludeSymbols = req.IncludeSymbols,
                IncludeFiles = req.IncludeFiles,
                IncludeAssets = false,
                IncludeNonProject = req.IncludeNonProject,
                RequireUe = req.RequireUe,
                MaxResultsPerCategory = req.MaxResultsPerCategory,
                RecentPaths = req.RecentPaths,
            };
            var result = (req.IncludeTypes || req.IncludeSymbols || req.IncludeFiles)
                ? _offline.Use(s => s.Search(copy, ct), new SearchResponse())
                : new SearchResponse();
            result.IndexReady = true;
            if (req.IncludeAssets && req.RequireUe == UeFlags.None && string.IsNullOrEmpty(req.Container))
                AddAssets(result, req, ct);
            return new Answer<SearchResponse> { Value = result, Source = Sources.Index, IndexReady = true, Note = OfflineNote() };
        }

        private void AddAssets(SearchResponse resp, SearchRequest req, CancellationToken ct)
        {
            var matcher = new FuzzyMatcher(req.Pattern);
            if (matcher.IsEmpty) return;
            int max = req.MaxResultsPerCategory > 0 ? req.MaxResultsPerCategory : 100;
            var hits = new List<SearchHit>();
            foreach (var a in _offline.Assets(req.IncludeNonProject, ct))
            {
                var m = matcher.MatchUnreal(a.Name);
                if (!m.IsMatch) continue;
                bool project = a.Origin == ItemOrigin.Project || a.Origin == ItemOrigin.ProjectPlugin;
                if (!project && !req.IncludeNonProject)
                {
                    resp.NonProjectHitCount++;
                    continue;
                }
                hits.Add(new SearchHit { Category = ResultCategory.Asset, Score = m.Score + (project ? 50 : 0), Asset = a });
            }
            resp.Hits.AddRange(hits.OrderByDescending(h => h.Score).ThenBy(h => h.Asset.ObjectPath, StringComparer.OrdinalIgnoreCase).Take(max));
        }

        private string OfflineNote() =>
            Mode == DataSourceMode.IndexOnly ? null :
            "UEToVS.Engine is not running; answered from the index on disk (written " +
            (_offline.Info?.ManifestWrittenUtc is DateTime t && t > DateTime.MinValue ? t.ToString("u") : "earlier") + "). Recent edits may be missing.";

        // ------------------------------------------------------------------ symbols

        public async Task<Answer<List<SymbolInfo>>> FindSymbolsAsync(string name, string container, CancellationToken ct)
        {
            var (ok, list) = await TryEngineAsync("findSymbols", (e, c) => e.FindSymbolsAsync(name, container, c), ct).ConfigureAwait(false);
            if (ok) return new Answer<List<SymbolInfo>> { Value = list ?? new List<SymbolInfo>(), Source = Sources.Engine, IndexReady = true };
            RequireOffline();
            var r = _offline.Use(s => s.FindByName(name, container, ct).ToList(), new List<SymbolInfo>());
            return new Answer<List<SymbolInfo>> { Value = r, Source = Sources.Index, IndexReady = true, Note = OfflineNote() };
        }

        public async Task<Answer<List<SymbolInfo>>> GetFileSymbolsAsync(string path, CancellationToken ct)
        {
            var (ok, list) = await TryEngineAsync("getFileSymbols", (e, c) => e.GetFileSymbolsAsync(path, c), ct).ConfigureAwait(false);
            if (ok) return new Answer<List<SymbolInfo>> { Value = list ?? new List<SymbolInfo>(), Source = Sources.Engine, IndexReady = true };
            RequireOffline();
            var r = _offline.Use(s => s.GetFileSymbols(path).ToList(), new List<SymbolInfo>());
            return new Answer<List<SymbolInfo>> { Value = r, Source = Sources.Index, IndexReady = true, Note = OfflineNote() };
        }

        // ------------------------------------------------------------------ navigation

        /// <summary>
        /// Go to definition at a caret. Uses the engine's semantic navigation when it resolves; otherwise reads
        /// the identifier at the caret and looks it up by name (qualifier as container), definitions first.
        /// </summary>
        public async Task<Answer<NavigationResponse>> NavigateAsync(CaretRequest caret, NavigationKind kind, CancellationToken ct)
        {
            var (ok, nav) = await TryEngineAsync("navigate", (e, c) => e.NavigateAsync(new NavigationRequest { Caret = caret, Kind = kind }, c), ct).ConfigureAwait(false);
            if (ok && nav != null && nav.Targets != null && nav.Targets.Count > 0)
                return new Answer<NavigationResponse> { Value = nav, Source = Sources.Engine, IndexReady = nav.IndexReady };

            var text = Texts.Get(caret.FilePath);
            if (text == null) throw new ToolException("file_not_found", "Cannot read " + caret.FilePath);
            var ident = CppText.IdentifierAt(text.Line(caret.Line), caret.Column);
            var resp = new NavigationResponse { Word = ident?.Name ?? string.Empty, Reason = "name-lookup", Confidence = ResolutionConfidence.None };
            if (ident == null)
                return new Answer<NavigationResponse> { Value = resp, Source = ok ? Sources.Engine : Sources.Index, Note = "No identifier at that position." };
            var found = await FindSymbolsAsync(ident.Value.Name, ident.Value.Qualifier, ct).ConfigureAwait(false);
            var list = found.Value;
            if (list.Count == 0 && ident.Value.Qualifier != null)
                list = (await FindSymbolsAsync(ident.Value.Name, null, ct).ConfigureAwait(false)).Value;
            var ranked = RankForNavigation(list, kind, caret).ToList();
            resp.Targets = ranked.Select(s => new NavigationTarget { Symbol = s, Role = s.IsDefinition ? NavigationTargetRole.Definition : NavigationTargetRole.Declaration }).ToList();
            resp.Resolved = ranked.FirstOrDefault();
            resp.Confidence = ranked.Count == 1 ? ResolutionConfidence.Scoped : ranked.Count > 1 ? ResolutionConfidence.NameOnly : ResolutionConfidence.None;
            resp.IndexReady = found.IndexReady;
            return new Answer<NavigationResponse>
            {
                Value = resp,
                Source = Sources.IndexHeuristic,
                IndexReady = found.IndexReady,
                Note = "Resolved by name (semantic navigation unavailable for this position); several candidates may be unrelated overloads or same-named members.",
            };
        }

        private static IEnumerable<SymbolInfo> RankForNavigation(List<SymbolInfo> list, NavigationKind kind, CaretRequest caret)
        {
            bool preferDecl = kind == NavigationKind.Declaration;
            return list
                .Where(s => !(SamePath(s.Location?.FilePath, caret?.FilePath) && s.Location?.Line == caret?.Line)) // not the caret itself
                .OrderBy(s => s.IsDefinition == preferDecl ? 1 : 0)
                .ThenBy(s => s.Origin == ItemOrigin.Project || s.Origin == ItemOrigin.ProjectPlugin ? 0 : 1)
                .ThenBy(s => s.Kind == SymbolKind.Macro ? 1 : 0)
                .ThenBy(s => s.Location?.FilePath, StringComparer.OrdinalIgnoreCase)
                .ThenBy(s => s.Location?.Line ?? 0);
        }

        /// <summary>Find references: engine first; otherwise all indexed occurrences of the name (text-level, name-based).</summary>
        public async Task<Answer<ReferencesResponse>> FindReferencesAsync(ReferencesRequest req, string fallbackName, CancellationToken ct)
        {
            var (ok, refs) = await TryEngineAsync("findReferences", (e, c) => e.FindReferencesAsync(req, c), ct).ConfigureAwait(false);
            if (ok && refs != null && refs.Hits != null && refs.Hits.Count > 0)
                return new Answer<ReferencesResponse> { Value = refs, Source = Sources.Engine, IndexReady = refs.IndexReady };

            string name = req.Symbol?.Name ?? fallbackName;
            if (string.IsNullOrEmpty(name)) return new Answer<ReferencesResponse> { Value = new ReferencesResponse(), Source = ok ? Sources.Engine : Sources.Index, Note = "No symbol to search for." };
            RequireOffline();
            var resp = new ReferencesResponse { Symbol = req.Symbol, IndexReady = true };
            int max = req.MaxResults > 0 ? req.MaxResults : 5000;
            var hits = _offline.Use(s => s.FindOccurrences(name, ct).Take(max + 1).ToList(), new List<Engine.Index.OccurrenceHit>());
            if (hits.Count > max)
            {
                resp.Truncated = true;
                hits.RemoveAt(hits.Count - 1);
            }
            var files = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var h in hits)
            {
                ct.ThrowIfCancellationRequested();
                files.Add(h.Path);
                var text = Texts.Get(h.Path);
                int line = 0, col = 0;
                string lineText = null;
                if (text != null)
                {
                    (line, col) = text.Position(h.Offset);
                    lineText = text.Line(line);
                }
                resp.Hits.Add(new ReferenceHit
                {
                    Location = new SourceLocation { FilePath = h.Path, Line = line, Column = col },
                    Length = name.Length,
                    Kind = MapRole(h.Role),
                    LineText = Trim(lineText),
                    Confidence = ResolutionConfidence.NameOnly,
                });
            }
            resp.FilesScanned = files.Count;
            return new Answer<ReferencesResponse>
            {
                Value = resp,
                Source = Sources.IndexHeuristic,
                IndexReady = true,
                Note = "Name-based: every indexed use of '" + name + "' (same-named members of other types included). Declarations are listed by get_file_outline/search_symbols.",
            };
        }

        /// <summary>OccurrenceRole (Read 1, Write 2, Call 4, MemberAccess 8, TypeUse 16) → ReferenceKind.</summary>
        public static ReferenceKind MapRole(byte role)
        {
            var k = ReferenceKind.None;
            if ((role & 1) != 0) k |= ReferenceKind.Read;
            if ((role & 2) != 0) k |= ReferenceKind.Write;
            if ((role & 4) != 0) k |= ReferenceKind.Call;
            if ((role & 16) != 0) k |= ReferenceKind.TypeUse;
            return k;
        }

        private static string Trim(string line)
        {
            if (line == null) return null;
            line = line.Replace('\t', ' ').Trim();
            return line.Length > 200 ? line.Substring(0, 199) + "…" : line;
        }

        public async Task<Answer<TypeHierarchyResponse>> GetTypeHierarchyAsync(TypeHierarchyRequest req, CancellationToken ct, bool includeNonProject = false)
        {
            var (ok, th) = await TryEngineAsync("getTypeHierarchy", (e, c) => e.GetTypeHierarchyAsync(req, c), ct).ConfigureAwait(false);
            if (ok && th != null && th.Root != null)
                return new Answer<TypeHierarchyResponse> { Value = th, Source = Sources.Engine, IndexReady = th.IndexReady };
            if (string.IsNullOrEmpty(req.TypeName))
                return new Answer<TypeHierarchyResponse> { Value = th ?? new TypeHierarchyResponse(), Source = Sources.Engine, Note = "Type not resolved at that position; pass type_name." };

            var hierarchy = new HierarchyFallback(this, req, ct) { IncludeNonProject = includeNonProject };
            var resp = await hierarchy.BuildAsync().ConfigureAwait(false);
            return new Answer<TypeHierarchyResponse>
            {
                Value = resp,
                Source = Sources.IndexHeuristic,
                IndexReady = true,
                Note = "Built from class signatures in the index" + (includeNonProject ? "." : "; derived types from project code only (scope='all' adds engine code)."),
            };
        }

        public async Task<SymbolInfo> FindTypeAsync(string name, string container, CancellationToken ct)
        {
            var found = await FindSymbolsAsync(name, container, ct).ConfigureAwait(false);
            return found.Value
                .Where(s => IsType(s.Kind))
                .OrderBy(s => s.IsDefinition ? 0 : 1)
                .ThenBy(s => s.Origin == ItemOrigin.Project || s.Origin == ItemOrigin.ProjectPlugin ? 0 : 1)
                .FirstOrDefault();
        }

        public static bool IsType(SymbolKind k) =>
            k == SymbolKind.Class || k == SymbolKind.Struct || k == SymbolKind.Union || k == SymbolKind.Enum || k == SymbolKind.Typedef ||
            k == SymbolKind.Delegate || k == SymbolKind.Concept;

        private readonly object _derivedGate = new object();
        private (DateTime Manifest, bool All, Dictionary<string, List<SymbolInfo>> Map) _derived;

        /// <summary>
        /// Types whose class signature names <paramref name="baseName"/> as a base. Built from the index once per
        /// on-disk index version: every class/struct declaration of every indexed header (project headers only unless
        /// <paramref name="includeNonProject"/>), keyed by base type name. Occurrences cannot be used: the parser records
        /// identifier uses in function bodies only, not in base clauses.
        /// </summary>
        public List<SymbolInfo> DerivedTypes(string baseName, bool includeNonProject, int max, CancellationToken ct)
        {
            var map = DerivedMap(includeNonProject, ct);
            if (map == null || !map.TryGetValue(baseName, out var list)) return new List<SymbolInfo>();
            return list.Where(s => includeNonProject || s.Origin == ItemOrigin.Project || s.Origin == ItemOrigin.ProjectPlugin).Take(max).ToList();
        }

        private Dictionary<string, List<SymbolInfo>> DerivedMap(bool includeNonProject, CancellationToken ct)
        {
            if (!_offline.IsAvailable) return null;
            var manifest = _offline.ManifestTimeUtc;
            lock (_derivedGate)
            {
                var d = _derived;
                if (d.Map != null && d.Manifest == manifest && (d.All || !includeNonProject)) return d.Map;
            }
            var sw = System.Diagnostics.Stopwatch.StartNew();
            int files = 0, edges = 0;
            var built = _offline.Use(snap =>
            {
                var m = new Dictionary<string, List<SymbolInfo>>(StringComparer.Ordinal);
                foreach (var f in snap.ListFiles(ct))
                {
                    if (!includeNonProject && f.Origin != ItemOrigin.Project && f.Origin != ItemOrigin.ProjectPlugin) continue;
                    if (!IsHeader(f.Path)) continue;
                    files++;
                    foreach (var s in snap.GetFileSymbols(f.Path))
                    {
                        if (s.Kind != SymbolKind.Class && s.Kind != SymbolKind.Struct) continue;
                        if (string.IsNullOrEmpty(s.Signature) || s.Signature.IndexOf(':') < 0) continue;
                        foreach (var b in CppText.ParseBases(s.Signature))
                        {
                            string key = CppText.BaseTypeName(b);
                            if (string.IsNullOrEmpty(key)) continue;
                            if (!m.TryGetValue(key, out var l)) m[key] = l = new List<SymbolInfo>();
                            l.Add(s);
                            edges++;
                        }
                    }
                }
                return m;
            });
            L.Info("mcp.derived.built", null, ("files", files), ("edges", edges), ("includeNonProject", includeNonProject), ("ms", Math.Round(sw.Elapsed.TotalMilliseconds, 1)));
            lock (_derivedGate) _derived = (manifest, includeNonProject, built);
            return built;
        }

        private static bool IsHeader(string path) =>
            path != null && (path.EndsWith(".h", StringComparison.OrdinalIgnoreCase) || path.EndsWith(".hpp", StringComparison.OrdinalIgnoreCase) ||
                             path.EndsWith(".hh", StringComparison.OrdinalIgnoreCase) || path.EndsWith(".hxx", StringComparison.OrdinalIgnoreCase) ||
                             path.EndsWith(".inl", StringComparison.OrdinalIgnoreCase));

        public async Task<Answer<MembersResponse>> GetMembersAsync(MembersRequest req, CancellationToken ct)
        {
            var (ok, m) = await TryEngineAsync("getMembers", (e, c) => e.GetMembersAsync(req, c), ct).ConfigureAwait(false);
            if (ok && m != null && m.Type != null)
                return new Answer<MembersResponse> { Value = m, Source = Sources.Engine, IndexReady = true };

            var resp = new MembersResponse();
            var type = await FindTypeAsync(req.TypeName, req.Container, ct).ConfigureAwait(false);
            if (type == null)
                return new Answer<MembersResponse> { Value = resp, Source = Sources.IndexHeuristic, Note = "Type '" + req.TypeName + "' not found in the index." };
            resp.Type = type;
            int max = req.MaxResults > 0 ? req.MaxResults : 2000;
            var visited = new HashSet<string>(StringComparer.Ordinal);
            var current = type;
            for (int depth = 0; current != null && depth < 16 && resp.Members.Count < max; depth++)
            {
                if (!visited.Add(current.Name)) break;
                string full = string.IsNullOrEmpty(current.Container) ? current.Name : current.Container + "::" + current.Name;
                var outline = await GetFileSymbolsAsync(current.Location?.FilePath, ct).ConfigureAwait(false);
                foreach (var s in outline.Value)
                {
                    if (s.Container != full && s.Container != current.Name) continue;
                    if (resp.Members.Count >= max)
                    {
                        resp.Truncated = true;
                        break;
                    }
                    resp.Members.Add(s);
                }
                if (!req.IncludeInherited) break;
                var bases = CppText.ParseBases(current.Signature);
                if (bases.Count == 0) break;
                current = await FindTypeAsync(CppText.BaseTypeName(bases[0]), null, ct).ConfigureAwait(false);
            }
            return new Answer<MembersResponse>
            {
                Value = resp,
                Source = Sources.IndexHeuristic,
                IndexReady = true,
                Note = req.IncludeInherited ? "Members declared in the type's header plus its primary base chain (index-based)." : "Members declared in the type's header (index-based).",
            };
        }

        // ------------------------------------------------------------------ status

        public async Task<Answer<EngineStatus>> GetStatusAsync(CancellationToken ct)
        {
            var (ok, st) = await TryEngineAsync("getStatus", (e, c) => e.GetStatusAsync(c), ct).ConfigureAwait(false);
            if (ok) return new Answer<EngineStatus> { Value = st, Source = Sources.Engine, IndexReady = st?.Phase == IndexPhase.Ready };
            var stats = _offline.Use(s => s.GetStats());
            var ws = _offline.Workspace();
            var status = new EngineStatus
            {
                Phase = stats != null ? IndexPhase.Ready : IndexPhase.NotStarted,
                FileCount = stats?.FileCount ?? 0,
                SymbolCount = stats?.SymbolCount ?? 0,
                ProjectRoot = ws?.ProjectRoot,
                EngineRoot = ws?.EngineRoot,
                UnrealVersion = ws?.EngineVersion?.ToString(),
            };
            return new Answer<EngineStatus> { Value = status, Source = Sources.Index, IndexReady = stats != null, Note = stats == null ? null : OfflineNote() };
        }

        // ---- v0.4.5 构建结果 / MCP 自检：get_diagnostics

        /// <summary>
        /// UE rule diagnostics of whole files: the running engine (sees files as saved on disk) or, without one, the same
        /// DiagnosticsProvider in-process over the on-disk index (<see cref="OfflineDiagnostics"/>). Skipped = "engine-file",
        /// "not-indexed" (offline only; still checked) or "not-found".
        /// </summary>
        public async Task<Answer<List<(string Path, string Skipped, CodeDiagnosticsResponse Result)>>> GetDiagnosticsAsync(IReadOnlyList<string> paths, CancellationToken ct)
        {
            var list = new List<(string, string, CodeDiagnosticsResponse)>();
            var existing = new List<string>();
            foreach (var p in paths)
            {
                if (File.Exists(p)) existing.Add(p);
                else list.Add((p, "not-found", null));
            }
            if (existing.Count == 0) return new Answer<List<(string, string, CodeDiagnosticsResponse)>> { Value = list, Source = Sources.Index, IndexReady = true };
            var (ok, engineResults) = await TryEngineAsync("getDiagnostics", async (e, c) =>
            {
                var r = new List<(string, string, CodeDiagnosticsResponse)>();
                foreach (var p in existing)
                {
                    var resp = await e.GetDiagnosticsAsync(new CodeDiagnosticsRequest { Document = new CaretRequest { FilePath = p }, StartLine = 0, EndLine = int.MaxValue }, c)
                        .ConfigureAwait(false);
                    r.Add((p, null, resp ?? new CodeDiagnosticsResponse()));
                }
                return r;
            }, ct).ConfigureAwait(false);
            if (ok)
            {
                list.AddRange(engineResults);
                return new Answer<List<(string, string, CodeDiagnosticsResponse)>> { Value = list, Source = Sources.Engine, IndexReady = true };
            }
            RequireOffline();
            var offline = _offline.Use(s => OfflineDiagnostics.Run(s, existing, ct), new List<(string, string, CodeDiagnosticsResponse)>());
            list.AddRange(offline);
            return new Answer<List<(string, string, CodeDiagnosticsResponse)>> { Value = list, Source = Sources.Index, IndexReady = true, Note = OfflineNote() };
        }

        // ---- v0.4.5 end

        public static bool SamePath(string a, string b) =>
            a != null && b != null && string.Equals(a.Replace('\\', '/').TrimEnd('/'), b.Replace('\\', '/').TrimEnd('/'), StringComparison.OrdinalIgnoreCase);

        public void Dispose()
        {
            _engine.Dispose();
            _offline.Dispose();
        }
    }

    /// <summary>Type hierarchy from the index alone: bases from class signatures, derived types from base-clause occurrences.</summary>
    internal sealed class HierarchyFallback
    {
        private readonly CodeIndex _index;
        private readonly TypeHierarchyRequest _req;
        private readonly CancellationToken _ct;
        private int _nodes;

        public HierarchyFallback(CodeIndex index, TypeHierarchyRequest req, CancellationToken ct)
        {
            _index = index;
            _req = req;
            _ct = ct;
        }

        /// <summary>Derived types outside the project (engine classes deriving from the root) too.</summary>
        public bool IncludeNonProject { get; set; }

        private int MaxNodes => _req.MaxNodes > 0 ? Math.Min(_req.MaxNodes, 5000) : 500;
        private int MaxDepth => _req.MaxDepth > 0 ? Math.Min(_req.MaxDepth, 32) : 8;

        public async Task<TypeHierarchyResponse> BuildAsync()
        {
            var resp = new TypeHierarchyResponse { IndexReady = true };
            var root = await _index.FindTypeAsync(_req.TypeName, _req.Container, _ct).ConfigureAwait(false);
            resp.Root = root ?? new SymbolInfo { Name = _req.TypeName, Container = _req.Container, Kind = SymbolKind.Class };
            if (root == null) return resp;
            if (_req.Direction != HierarchyDirection.Derived)
                resp.Bases = await BasesAsync(root, 1, new HashSet<string>(StringComparer.Ordinal) { root.Name }).ConfigureAwait(false);
            if (_req.Direction != HierarchyDirection.Bases)
                resp.Derived = Derived(root.Name, 1, new HashSet<string>(StringComparer.Ordinal) { root.Name });
            resp.NodeCount = _nodes;
            return resp;
        }

        private async Task<List<TypeHierarchyNode>> BasesAsync(SymbolInfo type, int depth, HashSet<string> seen)
        {
            var list = new List<TypeHierarchyNode>();
            foreach (var written in CppText.ParseBases(type.Signature))
            {
                if (_nodes >= MaxNodes) break;
                _nodes++;
                string name = CppText.BaseTypeName(written);
                var sym = await _index.FindTypeAsync(name, null, _ct).ConfigureAwait(false);
                var node = new TypeHierarchyNode { Symbol = sym, DisplayName = written };
                if (sym != null && seen.Add(name))
                {
                    if (depth < MaxDepth) node.Children = await BasesAsync(sym, depth + 1, seen).ConfigureAwait(false);
                    else node.Truncated = CppText.ParseBases(sym.Signature).Count > 0;
                }
                list.Add(node);
            }
            return list;
        }

        private List<TypeHierarchyNode> Derived(string name, int depth, HashSet<string> seen)
        {
            var list = new List<TypeHierarchyNode>();
            foreach (var d in _index.DerivedTypes(name, IncludeNonProject, Math.Max(1, MaxNodes - _nodes), _ct))
            {
                if (_nodes >= MaxNodes) break;
                _nodes++;
                var node = new TypeHierarchyNode { Symbol = d, DisplayName = d.Name };
                if (seen.Add(d.Name))
                {
                    if (depth < MaxDepth) node.Children = Derived(d.Name, depth + 1, seen);
                    else node.Truncated = true;
                }
                list.Add(node);
            }
            return list;
        }
    }
}
