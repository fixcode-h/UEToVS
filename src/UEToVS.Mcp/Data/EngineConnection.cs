using System;
using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Reflection;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using MessagePack;
using MessagePack.Resolvers;
using StreamJsonRpc;
using UEToVS.Contracts;
using UEToVS.Core.Diagnostics;
using UEToVS.Core.Storage;

namespace UEToVS.Mcp.Data
{
    /// <summary>Parsed &lt;solution&gt;/.vs/UEToVS/engine.json (see <see cref="EngineDiscoveryFile"/>).</summary>
    public sealed class EngineDiscovery
    {
        public string Pipe { get; set; }
        public int Pid { get; set; }
        public int ProtocolVersion { get; set; }
        public string EngineVersion { get; set; }
        public string SolutionDir { get; set; }
        public DateTime StartedUtc { get; set; }

        /// <summary>Reads the discovery file; null when missing or unreadable (error explains why).</summary>
        public static EngineDiscovery TryRead(string path, out string error)
        {
            error = null;
            try
            {
                if (!File.Exists(path))
                {
                    error = "missing";
                    return null;
                }
                using var doc = JsonDocument.Parse(File.ReadAllText(path));
                var r = doc.RootElement;
                var d = new EngineDiscovery
                {
                    Pipe = Str(r, "pipe"),
                    Pid = Int(r, "pid"),
                    ProtocolVersion = Int(r, "protocolVersion"),
                    EngineVersion = Str(r, "engineVersion"),
                    SolutionDir = Str(r, "solutionDir"),
                };
                if (r.TryGetProperty("startedUtc", out var s) && s.ValueKind == JsonValueKind.String && DateTime.TryParse(s.GetString(), null,
                        System.Globalization.DateTimeStyles.RoundtripKind, out var dt)) d.StartedUtc = dt;
                if (string.IsNullOrEmpty(d.Pipe))
                {
                    error = "no pipe name";
                    return null;
                }
                return d;
            }
            catch (Exception ex)
            {
                error = ex.GetType().Name + ": " + ex.Message;
                return null;
            }
        }

        private static string Str(JsonElement e, string name) => e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

        private static int Int(JsonElement e, string name) => e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out int i) ? i : 0;

        public static bool IsProcessAlive(int pid)
        {
            if (pid <= 0) return false;
            try
            {
                using var p = Process.GetProcessById(pid);
                return !p.HasExited;
            }
            catch (Exception)
            {
                return false;
            }
        }
    }

    /// <summary>
    /// Lazily connects to the running UEToVS.Engine through the pipe named in engine.json (same MessagePack JSON-RPC
    /// wire as the VS host, see UEToVS.Engine.Rpc.RpcProtocol). Reconnects after the engine restarts; a failed attempt
    /// is not retried for <see cref="RetryDelay"/> unless engine.json changes. Thread-safe.
    /// </summary>
    public sealed class EngineConnection : IDisposable
    {
        private static readonly Logger L = Log.For("Mcp.Engine");
        public static readonly TimeSpan RetryDelay = TimeSpan.FromSeconds(3);
        public static readonly TimeSpan ConnectTimeout = TimeSpan.FromSeconds(2);

        private readonly string _discoveryPath;
        private readonly string _solutionDir;
        private readonly SemaphoreSlim _gate = new SemaphoreSlim(1, 1);
        private Session _session;
        private DateTime _lastFailureUtc = DateTime.MinValue;
        private DateTime _lastFailureFileTime = DateTime.MinValue;

        public EngineConnection(string solutionDir)
        {
            _solutionDir = solutionDir;
            _discoveryPath = EngineDiscoveryFile.PathFor(StoragePaths.ForSolution(solutionDir));
        }

        public string DiscoveryPath => _discoveryPath;

        /// <summary>Why the last connect attempt failed (for index_status), or null.</summary>
        public string LastError { get; private set; }

        public EngineDiscovery Discovery => _session?.Discovery;

        public bool IsConnected => _session != null && !_session.Rpc.IsDisposed;

        /// <summary>Returns a connected engine proxy, or null when no engine is reachable right now.</summary>
        public async Task<IEngineService> GetAsync(CancellationToken ct)
        {
            var s = _session;
            if (s != null && !s.Rpc.IsDisposed) return s.Engine;
            await _gate.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                s = _session;
                if (s != null && !s.Rpc.IsDisposed) return s.Engine;
                _session = null;
                DateTime fileTime = SafeWriteTime(_discoveryPath);
                if (DateTime.UtcNow - _lastFailureUtc < RetryDelay && fileTime == _lastFailureFileTime) return null;
                s = await TryConnectAsync(ct).ConfigureAwait(false);
                if (s == null)
                {
                    _lastFailureUtc = DateTime.UtcNow;
                    _lastFailureFileTime = fileTime;
                    return null;
                }
                _session = s;
                LastError = null;
                return s.Engine;
            }
            finally
            {
                _gate.Release();
            }
        }

        /// <summary>Drops the session after a transport failure so the next call reconnects.</summary>
        public void Invalidate(Exception reason)
        {
            var s = Interlocked.Exchange(ref _session, null);
            if (s == null) return;
            L.Warn("mcp.engine.lost", "Engine connection lost; falling back until it is back", reason, ("pipe", s.Discovery.Pipe));
            s.Dispose();
        }

        private async Task<Session> TryConnectAsync(CancellationToken ct)
        {
            var d = EngineDiscovery.TryRead(_discoveryPath, out string error);
            if (d == null)
            {
                LastError = "engine.json " + error;
                L.Debug("mcp.engine.discovery.none", null, ("path", _discoveryPath), ("error", error));
                return null;
            }
            if (!EngineDiscovery.IsProcessAlive(d.Pid))
            {
                LastError = $"engine.json is stale (pid {d.Pid} not running)";
                L.Debug("mcp.engine.discovery.stale", null, ("pid", d.Pid));
                return null;
            }
            if (d.ProtocolVersion != 0 && d.ProtocolVersion != ProtocolInfo.Version)
            {
                LastError = $"engine speaks protocol v{d.ProtocolVersion}, this MCP server v{ProtocolInfo.Version}";
                L.Warn("mcp.engine.protocol_mismatch", LastError);
                return null;
            }
            var sw = Stopwatch.StartNew();
            NamedPipeClientStream pipe = null;
            JsonRpc rpc = null;
            try
            {
                pipe = new NamedPipeClientStream(".", d.Pipe, PipeDirection.InOut, PipeOptions.Asynchronous);
                using (var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct))
                {
                    timeout.CancelAfter(ConnectTimeout);
                    await pipe.ConnectAsync(timeout.Token).ConfigureAwait(false);
                }
                var options = MessagePackSerializerOptions.Standard
                    .WithResolver(ContractlessStandardResolver.Instance)
                    .WithSecurity(MessagePackSecurity.UntrustedData);
                var formatter = new MessagePackFormatter();
                formatter.SetMessagePackSerializerOptions(options);
                rpc = new JsonRpc(new LengthHeaderMessageHandler(pipe, pipe, formatter));
                rpc.AddLocalRpcTarget<IEngineCallbacks>(new IgnoreCallbacks(), null);
                var engine = rpc.Attach<IEngineService>();
                rpc.StartListening();
                var hs = await engine.HandshakeAsync(new HandshakeRequest
                {
                    ProtocolVersion = ProtocolInfo.Version,
                    HostVersion = "UEToVS.Mcp " + Version,
                    VsVersion = "mcp",
                    SolutionDir = _solutionDir,
                    // ProjectFile / EngineRootOverride stay null: never make the engine re-index for an MCP client.
                }, ct).ConfigureAwait(false);
                L.Info("mcp.engine.connected", null, ("pipe", d.Pipe), ("pid", hs.ProcessId), ("engineVersion", hs.EngineVersion),
                    ("ms", Math.Round(sw.Elapsed.TotalMilliseconds, 1)));
                var session = new Session(d, pipe, rpc, engine);
                rpc.Disconnected += (s, e) => { if (ReferenceEquals(_session, session)) Interlocked.CompareExchange(ref _session, null, session); };
                return session;
            }
            catch (Exception ex) when (!(ex is OperationCanceledException && ct.IsCancellationRequested))
            {
                LastError = "connect failed: " + ex.GetType().Name + ": " + ex.Message;
                L.Info("mcp.engine.connect_failed", LastError, ("pipe", d.Pipe), ("pid", d.Pid), ("ms", Math.Round(sw.Elapsed.TotalMilliseconds, 1)));
                rpc?.Dispose();
                pipe?.Dispose();
                return null;
            }
        }

        private static DateTime SafeWriteTime(string path)
        {
            try { return File.Exists(path) ? File.GetLastWriteTimeUtc(path) : DateTime.MinValue; }
            catch (Exception) { return DateTime.MinValue; }
        }

        public static string Version =>
            typeof(EngineConnection).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
            ?? typeof(EngineConnection).Assembly.GetName().Version?.ToString() ?? "0.0.0";

        public void Dispose()
        {
            Interlocked.Exchange(ref _session, null)?.Dispose();
            _gate.Dispose();
        }

        private sealed class Session : IDisposable
        {
            private readonly Stream _pipe;

            public Session(EngineDiscovery d, Stream pipe, JsonRpc rpc, IEngineService engine)
            {
                Discovery = d;
                _pipe = pipe;
                Rpc = rpc;
                Engine = engine;
            }

            public EngineDiscovery Discovery { get; }
            public JsonRpc Rpc { get; }
            public IEngineService Engine { get; }

            public void Dispose()
            {
                try { Rpc.Dispose(); } catch { }
                try { _pipe.Dispose(); } catch { }
            }
        }

        private sealed class IgnoreCallbacks : IEngineCallbacks
        {
            public Task OnStatusChangedAsync(EngineStatus status) => Task.CompletedTask;
        }
    }
}
