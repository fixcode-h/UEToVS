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

        public Task ShutdownAsync(CancellationToken ct) =>
            InvokeAsync("rpc.shutdown", _ =>
            {
                // Reply first, then stop: the host should not see a broken pipe as an error.
                Task.Delay(100).ContinueWith(t => _host.RequestShutdown("rpc-shutdown"), TaskScheduler.Default);
                return Task.CompletedTask;
            }, ct, ("conn", _connectionId));

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
