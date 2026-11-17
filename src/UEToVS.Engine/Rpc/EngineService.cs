using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using StreamJsonRpc;
using UEToVS.Contracts;
using UEToVS.Core.Diagnostics;
using UEToVS.Engine.Indexing;
using UEToVS.Engine.IntelliSense;
using UEToVS.Engine.Scheduling;

namespace UEToVS.Engine.Rpc
{
    /// <summary>
    /// <see cref="IEngineService"/> for one host connection. Every call gets a request id (<see cref="Log.RequestId"/>),
    /// a timing scope with its §9.1 budget, watchdog tracking, and exception logging with stack. Heavy work runs on the
    /// scheduler's lanes; cheap calls answer inline.
    /// </summary>
    public sealed class EngineService : IEngineService
    {
        private static readonly Logger L = Log.For("Rpc");
        private static long _requestCounter;

        private readonly EngineHost _host;
        private readonly int _connectionId;
        private int _handshakeDone;
        private int _warnedNoHandshake;

        public EngineService(EngineHost host, int connectionId)
        {
            _host = host;
            _connectionId = connectionId;
        }

        public bool HandshakeCompleted => Volatile.Read(ref _handshakeDone) == 1;

        private string NewRequestId()
        {
            // With ActivityTracingStrategy on both sides the host's trace id arrives here: logs correlate across processes.
            var a = Activity.Current;
            if (a != null && a.TraceId != default) return a.TraceId.ToHexString().Substring(0, 16);
            return $"c{_connectionId}-{Interlocked.Increment(ref _requestCounter)}";
        }

        private async Task<T> InvokeAsync<T>(string evt, Func<CancellationToken, Task<T>> body, CancellationToken ct, params (string Key, object Value)[] data)
        {
            Log.RequestId = NewRequestId();
            if (!HandshakeCompleted && evt != "rpc.handshake" && Interlocked.Exchange(ref _warnedNoHandshake, 1) == 0)
                L.Warn("rpc.no_handshake", "Call before handshake (allowed, but the host should handshake first)", ("method", evt), ("conn", _connectionId));
            var scope = L.Time(evt, RpcBudgets.For(evt), data);
            using var tracked = _host.Watchdog.Track(evt);
            try
            {
                return await body(ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                L.Debug("rpc.cancelled", null, ("method", evt), ("ms", Math.Round(scope.ElapsedMs, 1)));
                throw;
            }
            catch (LocalRpcException)
            {
                scope.Fail(); // logged where it was raised
                throw;
            }
            catch (Exception ex)
            {
                scope.Fail();
                L.Error(evt + ".failed", evt + " failed", ex, data);
                throw;
            }
            finally
            {
                scope.Dispose();
            }
        }

        private Task InvokeAsync(string evt, Func<CancellationToken, Task> body, CancellationToken ct, params (string Key, object Value)[] data) =>
            InvokeAsync<bool>(evt, async c => { await body(c).ConfigureAwait(false); return true; }, ct, data);

        public Task<HandshakeResponse> HandshakeAsync(HandshakeRequest request, CancellationToken ct) =>
            InvokeAsync("rpc.handshake", _ =>
            {
                if (request == null) throw Error(RpcErrorCodes.InvalidArgument, "Handshake request is missing");
                if (request.ProtocolVersion != ProtocolInfo.Version)
                {
                    string msg = $"Protocol mismatch: host speaks v{request.ProtocolVersion}, engine speaks v{ProtocolInfo.Version}. Reinstall the extension.";
                    L.Error("rpc.handshake.version_mismatch", msg, null, ("hostProtocol", request.ProtocolVersion), ("engineProtocol", ProtocolInfo.Version),
                        ("hostVersion", request.HostVersion), ("conn", _connectionId));
                    throw Error(RpcErrorCodes.ProtocolMismatch, msg);
                }
                Volatile.Write(ref _handshakeDone, 1);
                L.Info("rpc.handshake.ok", null, ("conn", _connectionId), ("hostVersion", request.HostVersion), ("vsVersion", request.VsVersion),
                    ("solutionDir", request.SolutionDir), ("project", request.ProjectFile), ("engineOverride", request.EngineRootOverride));
                if (!string.IsNullOrEmpty(request.SolutionDir) && !SamePath(request.SolutionDir, _host.Options.SolutionDir))
                    L.Warn("rpc.handshake.solution_mismatch", "Host solution differs from the engine's --solution-dir", ("host", request.SolutionDir), ("engine", _host.Options.SolutionDir));
                _host.Reconfigure(request.ProjectFile, request.EngineRootOverride);
                return Task.FromResult(new HandshakeResponse
                {
                    ProtocolVersion = ProtocolInfo.Version,
                    EngineVersion = EngineHost.Version,
                    ProcessId = Environment.ProcessId,
                    LogDirectory = _host.Paths.Logs,
                    IndexDirectory = _host.Paths.Index,
                });
            }, ct, ("protocol", request?.ProtocolVersion), ("hostVersion", request?.HostVersion));

        public Task<EngineStatus> GetStatusAsync(CancellationToken ct) =>
            InvokeAsync("rpc.getStatus", _ => Task.FromResult(_host.GetStatus()), ct);

        public Task<SearchResponse> SearchAsync(SearchRequest request, CancellationToken ct) =>
            InvokeAsync("rpc.search", c => _host.Scheduler.RunAsync(WorkPriority.Interactive, "rpc.search", t => _host.Search.Search(request, t), c),
                ct, ("pattern", request?.Pattern), ("container", request?.Container), ("nonProject", request?.IncludeNonProject), ("assets", request?.IncludeAssets));

        public Task<List<SymbolInfo>> GetFileSymbolsAsync(string filePath, CancellationToken ct) =>
            InvokeAsync("rpc.getFileSymbols", c => _host.Scheduler.RunAsync(WorkPriority.Visible, "rpc.getFileSymbols", t =>
            {
                var list = _host.Index.Backend.GetFileSymbols(filePath).ToList();
                list.Sort((a, b) => a.Location.Line != b.Location.Line ? a.Location.Line.CompareTo(b.Location.Line) : a.Location.Column.CompareTo(b.Location.Column));
                return list;
            }, c), ct, ("path", filePath));

        public Task<List<SymbolInfo>> FindSymbolsAsync(string name, string container, CancellationToken ct) =>
            InvokeAsync("rpc.findSymbols", c => _host.Scheduler.RunAsync(WorkPriority.Interactive, "rpc.findSymbols",
                t => _host.Index.Backend.FindSymbols(name, container).ToList(), c), ct, ("name", name), ("container", container));

        public Task NotifyFilesChangedAsync(List<FileChange> changes, CancellationToken ct) =>
            InvokeAsync("rpc.notifyFilesChanged", _ =>
            {
                _host.Index.DropBuffers(changes); // saved: the disk is current again
                _host.Changes.Enqueue(changes, "host");
                return Task.CompletedTask;
            }, ct, ("count", changes?.Count ?? 0));

        public Task UpdateUnsavedBufferAsync(UnsavedBufferUpdate update, CancellationToken ct) =>
            InvokeAsync("rpc.updateUnsavedBuffer", c => update == null ? Task.CompletedTask : _host.Index.ApplyBufferAsync(update.FilePath, update.Text, c),
                ct, ("file", update?.FilePath), ("chars", update?.Text?.Length ?? -1));

        public Task SetLogLevelAsync(string level, CancellationToken ct) =>
            InvokeAsync("rpc.setLogLevel", _ =>
            {
                if (!Log.TryParseLevel(level, out var lvl))
                {
                    L.Warn("log.level.invalid", "Unknown log level", ("level", level));
                    throw Error(RpcErrorCodes.InvalidArgument, "Unknown log level: " + level + " (Error/Warn/Info/Debug/Trace)");
                }
                var old = Log.MinLevel;
                Log.MinLevel = lvl;
                // Written at Warn so the change is visible even when the new level hides Info.
                L.Warn("log.level.changed", null, ("from", old.ToString()), ("to", lvl.ToString()));
                return Task.CompletedTask;
            }, ct, ("level", level));

        public Task<PerformanceSnapshot> GetPerformanceSnapshotAsync(CancellationToken ct) =>
            InvokeAsync("rpc.getPerformanceSnapshot", _ => Task.FromResult(_host.GetPerformanceSnapshot()), ct);

        public Task SetBuildInProgressAsync(bool building, CancellationToken ct) =>
            InvokeAsync("rpc.setBuildInProgress", _ =>
            {
                _host.Scheduler.SetBuildInProgress(building);
                return Task.CompletedTask;
            }, ct, ("building", building));

        public Task SetBackgroundPausedAsync(bool paused, CancellationToken ct) =>
            InvokeAsync("rpc.setBackgroundPaused", _ =>
            {
                _host.Scheduler.SetUserPaused(paused);
                return Task.CompletedTask;
            }, ct, ("paused", paused));

        // ---- M2 navigation (NavigationService): navigation / highlights on the Interactive lane, references / hierarchy Visible + cancellable ----

        public Task<NavigationResponse> NavigateAsync(NavigationRequest request, CancellationToken ct) =>
            InvokeAsync("rpc.navigate", c =>
            {
                if (request?.Caret == null) throw Error(RpcErrorCodes.InvalidArgument, "Navigate needs a caret");
                return _host.Scheduler.RunAsync(WorkPriority.Interactive, "rpc.navigate", t => _host.Navigation.Navigate(request, t), c);
            }, ct, ("kind", request?.Kind.ToString()), ("file", request?.Caret?.FilePath), ("line", request?.Caret?.Line), ("col", request?.Caret?.Column),
                ("buffer", request?.Caret?.Text != null));

        public Task<ReferencesResponse> FindReferencesAsync(ReferencesRequest request, CancellationToken ct) =>
            InvokeAsync("rpc.findReferences", c =>
            {
                if (request == null || (request.Caret == null && request.Symbol == null)) throw Error(RpcErrorCodes.InvalidArgument, "FindReferences needs a caret or a symbol");
                return _host.Scheduler.RunAsync(WorkPriority.Visible, "rpc.findReferences", t => WithIni(WithBlueprints(_host.Navigation.FindReferences(request, t), request), request), c);
            }, ct, ("file", request?.Caret?.FilePath), ("line", request?.Caret?.Line), ("symbol", request?.Symbol?.Name), ("nonProject", request?.IncludeNonProject),
                ("max", request?.MaxResults));

        public Task<TypeHierarchyResponse> GetTypeHierarchyAsync(TypeHierarchyRequest request, CancellationToken ct) =>
            InvokeAsync("rpc.getTypeHierarchy", c =>
            {
                if (request == null || (request.Caret == null && string.IsNullOrEmpty(request.TypeName)))
                    throw Error(RpcErrorCodes.InvalidArgument, "GetTypeHierarchy needs a caret or a type name");
                return _host.Scheduler.RunAsync(WorkPriority.Visible, "rpc.getTypeHierarchy", t => WithBlueprints(_host.Navigation.GetTypeHierarchy(request, t), request), c);
            }, ct, ("type", request?.TypeName), ("file", request?.Caret?.FilePath), ("direction", request?.Direction.ToString()));

        /// <summary>v0.7.3: Blueprint / level assets using the symbol (or deriving from the class), appended after the code hits.</summary>
        internal ReferencesResponse WithBlueprints(ReferencesResponse resp, ReferencesRequest request)
        {
            if (resp?.Symbol == null) return resp;
            try
            {
                var s = resp.Symbol;
                bool isType = s.Kind == SymbolKind.Class || s.Kind == SymbolKind.Struct || s.Kind == SymbolKind.Enum;
                var owners = new List<string> { isType ? s.Name : s.Container };
                if (string.IsNullOrEmpty(owners[0])) return resp;
                int room = Math.Max(0, (request?.MaxResults ?? 5000) - resp.Hits.Count);
                var hits = _host.Blueprints.ReferenceHits(s, owners, Math.Min(500, room));
                resp.Hits.AddRange(hits);
                if (hits.Count > 0) L.Debug("rpc.findReferences.blueprints", null, ("symbol", s.Name), ("hits", hits.Count));
            }
            catch (Exception ex) when (!(ex is OperationCanceledException))
            {
                L.Warn("rpc.findReferences.blueprints.failed", "Blueprint usages failed; code references are still returned", ex);
            }
            return resp;
        }

        /// <summary>v0.7.3: ini lines setting a UPROPERTY(Config) member, under the owner's section or a subclass's.</summary>
        internal ReferencesResponse WithIni(ReferencesResponse resp, ReferencesRequest request)
        {
            var s = resp?.Symbol;
            if (s == null || string.IsNullOrEmpty(s.Container) || !IniReferences.IsConfigProperty(s.Specifiers)) return resp;
            if (s.Kind != SymbolKind.Field && s.Kind != SymbolKind.Variable) return resp;
            try
            {
                var src = _host.IntelliSense.StringSources;
                if (src?.Config == null || src.Files == null) return resp;
                var classes = new List<string>();
                var seen = new HashSet<string>(StringComparer.Ordinal);
                var queue = new Queue<string>();
                queue.Enqueue(s.Container);
                while (queue.Count > 0 && seen.Count < 200)
                {
                    string cls = queue.Dequeue();
                    if (!seen.Add(cls)) continue;
                    classes.Add(BlueprintIndex.UeClassName(cls));
                    foreach (var d in _host.Index.Backend.FindDerivedTypes(cls))
                        if (d.Symbol?.Name != null) queue.Enqueue(d.Symbol.Name);
                }
                int room = Math.Max(0, (request?.MaxResults ?? 5000) - resp.Hits.Count);
                var hits = IniReferences.Find(src.Config.Files(src.Files), classes, s.Name, Math.Min(500, room));
                resp.Hits.AddRange(hits);
                if (hits.Count > 0) L.Debug("rpc.findReferences.ini", null, ("symbol", s.Name), ("hits", hits.Count));
            }
            catch (Exception ex) when (!(ex is OperationCanceledException))
            {
                L.Warn("rpc.findReferences.ini.failed", "Config ini usages failed; code references are still returned", ex);
            }
            return resp;
        }

        /// <summary>v0.7.3: Blueprints deriving from the root and from each C++ derived type (derived direction only).</summary>
        internal TypeHierarchyResponse WithBlueprints(TypeHierarchyResponse resp, TypeHierarchyRequest request)
        {
            if (resp?.Root == null || request?.Direction == HierarchyDirection.Bases) return resp;
            try
            {
                int added = 0;
                void Attach(List<TypeHierarchyNode> list, string typeName)
                {
                    var bps = _host.Blueprints.HierarchyNodes(new[] { typeName });
                    foreach (var n in list.ToList())
                        if (n.Symbol != null) Attach(n.Children, n.Symbol.Name);
                    list.AddRange(bps);
                    added += bps.Count;
                }
                Attach(resp.Derived, resp.Root.Name);
                resp.NodeCount += added;
                if (added > 0) L.Debug("rpc.getTypeHierarchy.blueprints", null, ("type", resp.Root.Name), ("blueprints", added));
            }
            catch (Exception ex) when (!(ex is OperationCanceledException))
            {
                L.Warn("rpc.getTypeHierarchy.blueprints.failed", "Derived Blueprints failed; the C++ hierarchy is still returned", ex);
            }
            return resp;
        }

        /// <summary>
        /// v0.7.3 Code Vision: one <see cref="InlayHintKind.Usages"/> hint at the end of each reflected declaration line in the
        /// range (UCLASS / USTRUCT / UINTERFACE: derived C++ types and Blueprints; UFUNCTION / UPROPERTY: Blueprints using it).
        /// Uses the saved file's symbols; nothing is shown when every count is 0.
        /// </summary>
        internal InlayHintsResponse WithCodeVision(InlayHintsResponse resp, InlayHintsRequest request, CancellationToken ct)
        {
            if (resp == null || request?.CodeVision != true || string.IsNullOrEmpty(request.Document?.FilePath)) return resp;
            try
            {
                var text = request.Document.Text ?? DocumentText(request.Document.FilePath);
                string[] lines = text?.Split('\n');
                foreach (var s in _host.Index.Backend.GetFileSymbols(request.Document.FilePath))
                {
                    ct.ThrowIfCancellationRequested();
                    int symbolLine = s.Location?.Line ?? -1;
                    if (symbolLine < 0) continue;
                    // Rider style: the hint goes on its own row above the declaration, i.e. above its UCLASS / UFUNCTION... macro
                    int line = CodeVisionAnchor(lines, symbolLine);
                    if (line < request.StartLine || line > request.EndLine) continue;
                    var hint = CodeVisionHint(s);
                    if (hint == null) continue;
                    hint.Line = line;
                    hint.Column = lines != null && line < lines.Length ? Indent(lines[line]) : 0;
                    resp.Items.Add(hint);
                }
            }
            catch (Exception ex) when (!(ex is OperationCanceledException))
            {
                L.Warn("rpc.getInlayHints.codeVision.failed", "Code Vision failed; the other hints are still returned", ex);
            }
            return resp;
        }

        private static readonly string[] ReflectionMacros = { "UCLASS", "USTRUCT", "UINTERFACE", "UFUNCTION", "UPROPERTY" };

        /// <summary>
        /// v0.7.3: the line a Code Vision row is drawn above: the declaration's reflection macro when it is on one of the few
        /// lines just above (a multi-line macro included), otherwise the declaration line itself.
        /// </summary>
        internal static int CodeVisionAnchor(string[] lines, int declLine)
        {
            if (lines == null || declLine <= 0 || declLine >= lines.Length) return declLine;
            for (int j = declLine - 1; j >= 0 && j >= declLine - 8; j--)
            {
                string t = lines[j].Trim();
                foreach (var m in ReflectionMacros)
                    if (t.StartsWith(m, StringComparison.Ordinal) && t.Substring(m.Length).TrimStart().StartsWith("(", StringComparison.Ordinal)) return j;
                if (t.Length == 0 || t.EndsWith(";", StringComparison.Ordinal) || t.EndsWith("{", StringComparison.Ordinal) || t.EndsWith("}", StringComparison.Ordinal) ||
                    t.StartsWith("//", StringComparison.Ordinal) || t.EndsWith("*/", StringComparison.Ordinal) || t.StartsWith("#", StringComparison.Ordinal)) break;
            }
            return declLine;
        }

        private static int Indent(string line)
        {
            int i = 0;
            while (i < line.Length && (line[i] == ' ' || line[i] == '\t')) i++;
            return i;
        }

        private string DocumentText(string path)
        {
            var bytes = _host.Navigation.Documents.GetFile(path)?.Text;
            return bytes == null ? null : System.Text.Encoding.UTF8.GetString(bytes);
        }

        private InlayHint CodeVisionHint(SymbolInfo s)
        {
            const UeFlags types = UeFlags.UClass | UeFlags.UStruct | UeFlags.UInterface;
            if ((s.Ue & types) != 0 && (s.Kind == SymbolKind.Class || s.Kind == SymbolKind.Struct))
            {
                int derived = _host.Index.Backend.FindDerivedTypes(s.Name).Count;
                int bps = BlueprintIndexService.Flatten(_host.Blueprints.Index.Derived(new[] { BlueprintIndex.UeClassName(s.Name) })).Count();
                return derived + bps == 0 ? null : new InlayHint { Kind = InlayHintKind.Usages, SymbolName = s.Name, DerivedTypes = derived, Blueprints = bps };
            }
            if ((s.Ue & (UeFlags.UFunction | UeFlags.UProperty)) != 0 && !string.IsNullOrEmpty(s.Container))
            {
                int bps = _host.Blueprints.Index.Usages(s.Name, new[] { BlueprintIndex.UeClassName(s.Container) }, 999).Count;
                return bps == 0 ? null : new InlayHint { Kind = InlayHintKind.Usages, SymbolName = s.Container + "::" + s.Name, Blueprints = bps };
            }
            return null;
        }

        /// <summary>
        /// v0.7.6: what a Code Vision count stands for, listed when the user clicks it (Rider). Same queries as
        /// <see cref="CodeVisionHint"/>, so the list matches the number: a class gets its direct C++ subclasses and the
        /// Blueprints deriving from it (Blueprint children under their parent); a member gets the Blueprints / levels using it.
        /// </summary>
        public Task<CodeVisionItemsResponse> GetCodeVisionItemsAsync(CodeVisionItemsRequest request, CancellationToken ct) =>
            InvokeAsync("rpc.getCodeVisionItems", c => string.IsNullOrWhiteSpace(request?.SymbolName)
                    ? Task.FromResult(new CodeVisionItemsResponse())
                    : _host.Scheduler.RunAsync(WorkPriority.Interactive, "rpc.getCodeVisionItems", t => CodeVisionItems(request, t), c),
                ct, ("symbol", request?.SymbolName), ("max", request?.MaxItems));

        internal CodeVisionItemsResponse CodeVisionItems(CodeVisionItemsRequest request, CancellationToken ct)
        {
            var sw = Stopwatch.StartNew();
            int max = Math.Max(1, Math.Min(request.MaxItems, 2000));
            var bpIndex = _host.Blueprints.Index;
            var resp = new CodeVisionItemsResponse { BlueprintIndexReady = bpIndex.IsReady };
            string name = request.SymbolName.Trim();
            int sep = name.LastIndexOf("::", StringComparison.Ordinal);
            if (sep < 0)
            {
                foreach (var d in _host.Index.Backend.FindDerivedTypes(name))
                {
                    if (d?.Symbol == null) continue;
                    if (resp.DerivedTypes.Count >= max) { resp.Truncated = true; break; }
                    resp.DerivedTypes.Add(d.Symbol);
                }
                resp.DerivedTypes.Sort((a, b) => string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase));
                void Walk(IEnumerable<DerivedBlueprint> nodes, int depth)
                {
                    foreach (var n in nodes.Where(n => n?.Entry != null).OrderBy(n => n.Entry.Name, StringComparer.OrdinalIgnoreCase))
                    {
                        ct.ThrowIfCancellationRequested();
                        if (resp.Blueprints.Count >= max) { resp.Truncated = true; return; }
                        resp.Blueprints.Add(CodeVisionBlueprintOf(n.Entry, depth));
                        Walk(n.Children, depth + 1);
                    }
                }
                Walk(bpIndex.Derived(new[] { BlueprintIndex.UeClassName(name) }), 0);
            }
            else
            {
                string container = name.Substring(0, sep), member = name.Substring(sep + 2);
                var used = bpIndex.Usages(member, new[] { BlueprintIndex.UeClassName(container) }, max + 1);
                if (used.Count > max) resp.Truncated = true;
                // Blueprints first, then levels; by name inside each group
                foreach (var e in used.Take(max).OrderBy(e => e.IsMap ? 1 : 0).ThenBy(e => e.Name, StringComparer.OrdinalIgnoreCase))
                    resp.Blueprints.Add(CodeVisionBlueprintOf(e, 0, member: true));
            }
            resp.ElapsedMs = sw.Elapsed.TotalMilliseconds;
            L.Debug("rpc.getCodeVisionItems.result", null, ("symbol", name), ("derived", resp.DerivedTypes.Count), ("blueprints", resp.Blueprints.Count),
                ("truncated", resp.Truncated), ("bpIndexReady", resp.BlueprintIndexReady), ("ms", Math.Round(resp.ElapsedMs, 1)));
            return resp;
        }

        private static CodeVisionBlueprint CodeVisionBlueprintOf(BlueprintEntry e, int depth, bool member = false) => new CodeVisionBlueprint
        {
            Asset = new AssetItem { Name = e.Name, FilePath = e.FilePath, ObjectPath = e.ObjectPath, ClassName = e.IsMap ? "World" : "Blueprint", Origin = ItemOrigin.Project },
            ParentClass = member ? null : UEToVS.Engine.Unreal.UassetReader.ClassName(e.ParentClass ?? e.NativeParentClass),
            Depth = depth,
            IsLevel = e.IsMap,
        };

        public Task<HighlightResponse> GetHighlightsAsync(CaretRequest caret, CancellationToken ct) =>
            InvokeAsync("rpc.getHighlights", c => caret == null
                    ? Task.FromResult(new HighlightResponse { Word = string.Empty })
                    : _host.Scheduler.RunAsync(WorkPriority.Interactive, "rpc.getHighlights", t => _host.Navigation.GetHighlights(caret, t), c),
                ct, ("file", caret?.FilePath), ("line", caret?.Line), ("version", caret?.TextVersion));

        public Task<CaretContextResponse> GetCaretContextAsync(CaretRequest caret, CancellationToken ct) =>
            InvokeAsync("rpc.getCaretContext", c => caret == null
                    ? Task.FromResult(new CaretContextResponse())
                    : _host.Scheduler.RunAsync(WorkPriority.Interactive, "rpc.getCaretContext", t => _host.Navigation.GetCaretContext(caret, t), c),
                ct, ("file", caret?.FilePath), ("line", caret?.Line));

        public Task<MembersResponse> GetMembersAsync(MembersRequest request, CancellationToken ct) =>
            InvokeAsync("rpc.getMembers", c => string.IsNullOrEmpty(request?.TypeName)
                    ? Task.FromResult(new MembersResponse())
                    : _host.Scheduler.RunAsync(WorkPriority.Interactive, "rpc.getMembers", t => _host.Navigation.GetMembers(request, t), c),
                ct, ("type", request?.TypeName), ("container", request?.Container), ("inherited", request?.IncludeInherited));

        // ---- indexing settings: stubs, implemented by the indexing work ----

        public Task SetIndexingSettingsAsync(IndexingSettings settings, CancellationToken ct) =>
            InvokeAsync("rpc.setIndexingSettings", _ =>
            {
                if (settings == null) throw Error(RpcErrorCodes.InvalidArgument, "Indexing settings are missing");
                _host.ApplyIndexingSettings(settings);
                return Task.CompletedTask;
            }, ct, ("cppThreads", settings?.CppThreads), ("assetThreads", settings?.AssetThreads), ("duringBuild", settings?.DuringBuild),
                ("includeEngine", settings?.IncludeEngine), ("sharedEngineCache", settings?.SharedEngineCache));

        public Task RebuildIndexAsync(RebuildScope scope, CancellationToken ct) =>
            InvokeAsync("rpc.rebuildIndex", _ =>
            {
                _host.RebuildIndex(scope);
                return Task.CompletedTask;
            }, ct, ("scope", scope.ToString()));

        // ---- M3 IntelliSense (IntelliSenseService): completion / Quick Info / parameter info Interactive, colouring Visible ----

        public Task<CompletionResponse> GetCompletionsAsync(CompletionRequest request, CancellationToken ct) =>
            InvokeAsync("rpc.getCompletions", c =>
            {
                if (request?.Caret == null) throw Error(RpcErrorCodes.InvalidArgument, "GetCompletions needs a caret");
                return _host.Scheduler.RunAsync(WorkPriority.Interactive, "rpc.getCompletions", t => _host.IntelliSense.GetCompletions(request, t), c);
            }, ct, ("file", request?.Caret?.FilePath), ("line", request?.Caret?.Line), ("col", request?.Caret?.Column), ("trigger", request?.TriggerChar.ToString()),
                ("explicit", request?.Explicit), ("version", request?.Caret?.TextVersion));

        public Task<QuickInfoResponse> GetQuickInfoAsync(CaretRequest caret, CancellationToken ct) =>
            InvokeAsync("rpc.getQuickInfo", c => caret == null
                    ? Task.FromResult(new QuickInfoResponse())
                    : _host.Scheduler.RunAsync(WorkPriority.Interactive, "rpc.getQuickInfo", t => _host.IntelliSense.GetQuickInfo(caret, t), c),
                ct, ("file", caret?.FilePath), ("line", caret?.Line), ("col", caret?.Column));

        public Task<SignatureHelpResponse> GetSignatureHelpAsync(CaretRequest caret, CancellationToken ct) =>
            InvokeAsync("rpc.getSignatureHelp", c => caret == null
                    ? Task.FromResult(new SignatureHelpResponse())
                    : _host.Scheduler.RunAsync(WorkPriority.Interactive, "rpc.getSignatureHelp", t => _host.IntelliSense.GetSignatureHelp(caret, t), c),
                ct, ("file", caret?.FilePath), ("line", caret?.Line), ("col", caret?.Column));

        public Task<ClassifyResponse> ClassifyAsync(ClassifyRequest request, CancellationToken ct) =>
            InvokeAsync("rpc.classify", c => request?.Document == null
                    ? Task.FromResult(new ClassifyResponse { TextVersion = request?.Document?.TextVersion ?? 0 })
                    : _host.Scheduler.RunAsync(WorkPriority.Visible, "rpc.classify", t => _host.IntelliSense.Classify(request, t), c),
                ct, ("file", request?.Document?.FilePath), ("from", request?.StartLine), ("to", request?.EndLine), ("version", request?.Document?.TextVersion));

        public Task<CodeDiagnosticsResponse> GetDiagnosticsAsync(CodeDiagnosticsRequest request, CancellationToken ct) =>
            InvokeAsync("rpc.getDiagnostics", c => request?.Document == null
                    ? Task.FromResult(new CodeDiagnosticsResponse { TextVersion = request?.Document?.TextVersion ?? 0 })
                    : _host.Scheduler.RunAsync(WorkPriority.Visible, "rpc.getDiagnostics", t => _host.IntelliSense.GetDiagnostics(request, t), c),
                ct, ("file", request?.Document?.FilePath), ("from", request?.StartLine), ("to", request?.EndLine), ("version", request?.Document?.TextVersion));

        public Task ShutdownAsync(CancellationToken ct) =>
            InvokeAsync("rpc.shutdown", _ =>
            {
                // Reply first, then stop: the host should not see a broken pipe as an error.
                Task.Delay(100).ContinueWith(t => _host.RequestShutdown("rpc-shutdown"), TaskScheduler.Default);
                return Task.CompletedTask;
            }, ct, ("conn", _connectionId));

        // ---- M4 refactoring (RefactoringService): Interactive lane, the user is waiting on Alt+Enter / a dialog ----

        public Task<RefactoringContextResponse> GetRefactoringContextAsync(RefactoringContextRequest request, CancellationToken ct) =>
            InvokeAsync("rpc.getRefactoringContext", c => request?.Caret == null
                    ? Task.FromResult(new RefactoringContextResponse { Word = string.Empty })
                    : _host.Scheduler.RunAsync(WorkPriority.Interactive, "rpc.getRefactoringContext", t => _host.Refactoring.GetContext(request, t), c),
                ct, ("file", request?.Caret?.FilePath), ("line", request?.Caret?.Line), ("col", request?.Caret?.Column), ("checkIncludes", request?.CheckIncludes));

        public Task<OverridableMethodsResponse> GetOverridableMethodsAsync(OverridableMethodsRequest request, CancellationToken ct) =>
            InvokeAsync("rpc.getOverridableMethods", c => request == null || (request.Caret == null && string.IsNullOrEmpty(request.TypeName))
                    ? Task.FromResult(new OverridableMethodsResponse())
                    : _host.Scheduler.RunAsync(WorkPriority.Interactive, "rpc.getOverridableMethods", t => _host.Refactoring.GetOverridableMethods(request, t), c),
                ct, ("type", request?.TypeName), ("file", request?.Caret?.FilePath), ("line", request?.Caret?.Line));

        // ---- v0.4.5 navigation gaps: call hierarchy (one level per call, Visible lane, cancellable like references)

        public Task<CallHierarchyResponse> GetCallHierarchyAsync(CallHierarchyRequest request, CancellationToken ct) =>
            InvokeAsync("rpc.getCallHierarchy", c =>
            {
                if (request == null || (request.Caret == null && request.Function == null))
                    throw Error(RpcErrorCodes.InvalidArgument, "GetCallHierarchy needs a caret or a function");
                return _host.Scheduler.RunAsync(WorkPriority.Visible, "rpc.getCallHierarchy", t => _host.Navigation.GetCallHierarchy(request, t), c);
            }, ct, ("direction", request?.Direction.ToString()), ("file", request?.Caret?.FilePath), ("line", request?.Caret?.Line),
                ("function", request?.Function?.Name), ("nonProject", request?.IncludeNonProject));
        // ---- v0.4.5 编辑体验: Visible lane (viewport work, superseded by newer requests) ----

        public Task<InlayHintsResponse> GetInlayHintsAsync(InlayHintsRequest request, CancellationToken ct) =>
            InvokeAsync("rpc.getInlayHints", c => request?.Document == null
                    ? Task.FromResult(new InlayHintsResponse { TextVersion = request?.Document?.TextVersion ?? 0 })
                    : _host.Scheduler.RunAsync(WorkPriority.Visible, "rpc.getInlayHints", t => WithCodeVision(_host.IntelliSense.GetInlayHints(request, t), request, t), c),
                ct, ("file", request?.Document?.FilePath), ("from", request?.StartLine), ("to", request?.EndLine), ("version", request?.Document?.TextVersion));
        // ---- v0.4.5 UE editor link (UeCodeService): the user is waiting (test runner, double-clicked log line) ----

        public Task<AutomationTestsResponse> FindAutomationTestsAsync(AutomationTestsRequest request, CancellationToken ct) =>
            InvokeAsync("rpc.findAutomationTests", c => _host.UeCode.FindAutomationTestsAsync(request, c), ct, ("includeEngine", request?.IncludeEngine));

        public Task<LogSitesResponse> FindLogSitesAsync(LogSitesRequest request, CancellationToken ct) =>
            InvokeAsync("rpc.findLogSites", c => _host.UeCode.FindLogSitesAsync(request, c), ct, ("category", request?.Category), ("includeEngine", request?.IncludeEngine));
        // ---- v0.4.5 heavy features: the work runs on the services' own BelowNormal threads; these calls only start / poll / page

        public Task<ProjectInspectionResponse> ProjectInspectionAsync(ProjectInspectionRequest request, CancellationToken ct) =>
            InvokeAsync("rpc.projectInspection", c => request?.Action == HeavyJobAction.Results
                    ? _host.Scheduler.RunAsync(WorkPriority.Visible, "rpc.projectInspection", t => _host.Inspection.Handle(request, t), c)
                    : Task.FromResult(_host.Inspection.Handle(request, c)),
                ct, ("action", request?.Action.ToString()), ("plugins", request?.IncludePlugins), ("rule", request?.Rule), ("offset", request?.Offset));

        public Task<DeprecationScanResponse> DeprecationScanAsync(DeprecationScanRequest request, CancellationToken ct) =>
            InvokeAsync("rpc.deprecationScan", c => Task.FromResult(_host.Upgrade.Handle(request)), ct, ("action", request?.Action.ToString()), ("plugins", request?.IncludePlugins));

        private static LocalRpcException Error(int code, string message) => new LocalRpcException(message) { ErrorCode = code };

        private static bool SamePath(string a, string b)
        {
            try
            {
                return string.Equals(System.IO.Path.GetFullPath(a).TrimEnd('/', '\\'), System.IO.Path.GetFullPath(b).TrimEnd('/', '\\'), StringComparison.OrdinalIgnoreCase);
            }
            catch (Exception) { return false; }
        }
    }
}
