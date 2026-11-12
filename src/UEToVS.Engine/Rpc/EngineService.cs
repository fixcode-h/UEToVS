using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using StreamJsonRpc;
using UEToVS.Contracts;
using UEToVS.Core.Diagnostics;
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
                _host.Changes.Enqueue(changes, "host");
                _host.Lua.NotifyChanges(changes);
                return Task.CompletedTask;
            }, ct, ("count", changes?.Count ?? 0));

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
                return _host.Scheduler.RunAsync(WorkPriority.Visible, "rpc.findReferences", t => _host.Navigation.FindReferences(request, t), c);
            }, ct, ("file", request?.Caret?.FilePath), ("line", request?.Caret?.Line), ("symbol", request?.Symbol?.Name), ("nonProject", request?.IncludeNonProject),
                ("max", request?.MaxResults));

        public Task<TypeHierarchyResponse> GetTypeHierarchyAsync(TypeHierarchyRequest request, CancellationToken ct) =>
            InvokeAsync("rpc.getTypeHierarchy", c =>
            {
                if (request == null || (request.Caret == null && string.IsNullOrEmpty(request.TypeName)))
                    throw Error(RpcErrorCodes.InvalidArgument, "GetTypeHierarchy needs a caret or a type name");
                return _host.Scheduler.RunAsync(WorkPriority.Visible, "rpc.getTypeHierarchy", t => _host.Navigation.GetTypeHierarchy(request, t), c);
            }, ct, ("type", request?.TypeName), ("file", request?.Caret?.FilePath), ("direction", request?.Direction.ToString()));

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
                ("includeEngine", settings?.IncludeEngine), ("sharedEngineCache", settings?.SharedEngineCache),
                ("luaPaths", settings?.LuaScriptPaths == null ? null : string.Join(";", settings.LuaScriptPaths)));

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
                    : _host.Scheduler.RunAsync(WorkPriority.Visible, "rpc.getInlayHints", t => _host.IntelliSense.GetInlayHints(request, t), c),
                ct, ("file", request?.Document?.FilePath), ("from", request?.StartLine), ("to", request?.EndLine), ("version", request?.Document?.TextVersion));
        // ---- v0.4.5 UE editor link (UeCodeService): the user is waiting (test runner, double-clicked log line) ----

        public Task<AutomationTestsResponse> FindAutomationTestsAsync(AutomationTestsRequest request, CancellationToken ct) =>
            InvokeAsync("rpc.findAutomationTests", c => _host.UeCode.FindAutomationTestsAsync(request, c), ct, ("includeEngine", request?.IncludeEngine));

        public Task<LogSitesResponse> FindLogSitesAsync(LogSitesRequest request, CancellationToken ct) =>
            InvokeAsync("rpc.findLogSites", c => _host.UeCode.FindLogSitesAsync(request, c), ct, ("category", request?.Category), ("includeEngine", request?.IncludeEngine));

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
