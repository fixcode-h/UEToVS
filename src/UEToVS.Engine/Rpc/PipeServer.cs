using System;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO.Pipes;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Threading;
using System.Threading.Tasks;
using StreamJsonRpc;
using UEToVS.Contracts;
using UEToVS.Core.Diagnostics;

namespace UEToVS.Engine.Rpc
{
    /// <summary>
    /// Accepts host connections on a named pipe, one JsonRpc (MessagePack, see <see cref="RpcProtocol"/>) per
    /// connection. Several connections may be open at once and a host may reconnect any number of times.
    /// Windows: the pipe ACL grants access to the current user's SID only (§10.6). Other OSes: default permissions.
    /// </summary>
    public sealed class PipeServer
    {
        private static readonly Logger L = Log.For("Rpc");

        private readonly string _pipeName;
        private readonly EngineHost _host;
        private readonly ConcurrentDictionary<int, Connection> _connections = new ConcurrentDictionary<int, Connection>();
        private int _nextId;

        public PipeServer(string pipeName, EngineHost host)
        {
            _pipeName = pipeName;
            _host = host;
            host.Index.StatusChanged += OnStatusChanged;
            host.Lua.StatusChanged += OnStatusChanged;
        }

        public int ConnectionCount => _connections.Count;

        /// <summary>Completes once the pipe exists and the first accept is pending (tests connect after this).</summary>
        public Task Listening => _listening.Task;

        private readonly TaskCompletionSource<bool> _listening = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

        public async Task RunAsync(CancellationToken ct)
        {
            L.Info("rpc.listen", "Listening", ("pipe", _pipeName), ("acl", OperatingSystem.IsWindows() ? "current-user" : "default"));
            int consecutiveFailures = 0;
            while (!ct.IsCancellationRequested)
            {
                NamedPipeServerStream server = null;
                try
                {
                    server = CreatePipe();
                    _listening.TrySetResult(true);
                    await server.WaitForConnectionAsync(ct).ConfigureAwait(false);
                    consecutiveFailures = 0;
                }
                catch (OperationCanceledException)
                {
                    server?.Dispose();
                    break;
                }
                catch (Exception ex)
                {
                    server?.Dispose();
                    consecutiveFailures++;
                    L.Error("rpc.accept.failed", "Accepting a pipe connection failed", ex, ("pipe", _pipeName), ("attempt", consecutiveFailures));
                    if (consecutiveFailures >= 10)
                    {
                        _listening.TrySetException(ex);
                        _host.RequestShutdown("pipe-accept-failed", 3);
                        break;
                    }
                    try { await Task.Delay(Math.Min(5000, 100 * consecutiveFailures), ct).ConfigureAwait(false); }
                    catch (OperationCanceledException) { break; }
                    continue;
                }

                int id = Interlocked.Increment(ref _nextId);
                try
                {
                    var conn = new Connection(id, server, _host, this);
                    _connections[id] = conn;
                    Interlocked.Increment(ref _host.Connections);
                    conn.Start();
                }
                catch (Exception ex)
                {
                    L.Error("rpc.connection.setup_failed", "Setting up a connection failed", ex, ("conn", id));
                    server.Dispose();
                }
            }
            foreach (var c in _connections.Values) c.Dispose();
            L.Info("rpc.stopped", null, ("pipe", _pipeName));
        }

        private NamedPipeServerStream CreatePipe()
        {
            if (OperatingSystem.IsWindows())
            {
                var security = new PipeSecurity();
                var user = WindowsIdentity.GetCurrent().User;
                security.AddAccessRule(new PipeAccessRule(user, PipeAccessRights.FullControl, AccessControlType.Allow));
                security.SetOwner(user);
                return NamedPipeServerStreamAcl.Create(_pipeName, PipeDirection.InOut, NamedPipeServerStream.MaxAllowedServerInstances,
                    PipeTransmissionMode.Byte, PipeOptions.Asynchronous, 0, 0, security);
            }
            return new NamedPipeServerStream(_pipeName, PipeDirection.InOut, NamedPipeServerStream.MaxAllowedServerInstances,
                PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
        }

        private void OnStatusChanged()
        {
            foreach (var c in _connections.Values) c.Status.Signal();
        }

        internal void Remove(Connection c)
        {
            if (_connections.TryRemove(c.Id, out _)) Interlocked.Decrement(ref _host.Connections);
        }

        internal sealed class Connection : IDisposable
        {
            private readonly NamedPipeServerStream _stream;
            private readonly EngineHost _host;
            private readonly PipeServer _owner;
            private readonly JsonRpc _rpc;
            private readonly Stopwatch _since = Stopwatch.StartNew();

            public Connection(int id, NamedPipeServerStream stream, EngineHost host, PipeServer owner)
            {
                Id = id;
                _stream = stream;
                _host = host;
                _owner = owner;
                Service = new EngineService(host, id);
                _rpc = new JsonRpc(RpcProtocol.CreateHandler(stream));
                _rpc.ActivityTracingStrategy = new ActivityTracingStrategy();
                _rpc.AddLocalRpcTarget<IEngineService>(Service, null);
                _rpc.TraceSource.Switch.Level = SourceLevels.Warning;
                _rpc.TraceSource.Listeners.Clear();
                _rpc.TraceSource.Listeners.Add(new RpcTraceListener(id));
                _rpc.Disconnected += (s, e) =>
                {
                    var level = e.Reason == DisconnectedReason.RemotePartyTerminated || e.Reason == DisconnectedReason.LocallyDisposed ? LogLevel.Info : LogLevel.Warn;
                    L.Write(level, "rpc.disconnected", e.Description, e.Exception, Math.Round(_since.Elapsed.TotalMilliseconds),
                        new (string, object)[] { ("conn", Id), ("reason", e.Reason.ToString()) });
                };
                Status = new StatusNotifier(this);
            }

            public int Id { get; }
            public EngineHost Host => _host;
            public EngineService Service { get; }
            public StatusNotifier Status { get; }

            public void Start()
            {
                _rpc.StartListening();
                L.Info("rpc.connected", null, ("conn", Id), ("open", _owner.ConnectionCount));
                _ = _rpc.Completion.ContinueWith(t =>
                {
                    if (t.IsFaulted) L.Debug("rpc.completion.faulted", t.Exception?.GetBaseException().Message, ("conn", Id));
                    Dispose();
                }, TaskScheduler.Default);
            }

            /// <summary>Sends <see cref="IEngineCallbacks.OnStatusChangedAsync"/> as a notification (no reply needed).</summary>
            public async Task NotifyStatusAsync(EngineStatus status)
            {
                if (_rpc.IsDisposed || !Service.HandshakeCompleted) return;
                try
                {
                    await _rpc.NotifyAsync(RpcProtocol.StatusChangedMethod, status).ConfigureAwait(false);
                }
                catch (Exception ex) when (ex is ConnectionLostException || ex is ObjectDisposedException || ex is System.IO.IOException)
                {
                    L.Debug("rpc.notify.lost", ex.Message, ("conn", Id));
                }
            }

            public void Dispose()
            {
                _owner.Remove(this);
                Status.Dispose();
                try { _rpc.Dispose(); } catch { }
                try { _stream.Dispose(); } catch { }
            }
        }

        /// <summary>Coalesces status changes: at most one notification per <see cref="MinInterval"/>, always the latest state.</summary>
        internal sealed class StatusNotifier : IDisposable
        {
            public static readonly TimeSpan MinInterval = TimeSpan.FromMilliseconds(250);
            private readonly Connection _conn;
            private readonly Timer _timer;
            private readonly object _gate = new object();
            private DateTime _lastSentUtc;
            private bool _scheduled;
            private IndexPhase _lastPhase = (IndexPhase)255;

            public StatusNotifier(Connection conn)
            {
                _conn = conn;
                _timer = new Timer(_ => Send(), null, Timeout.Infinite, Timeout.Infinite);
            }

            public void Signal()
            {
                lock (_gate)
                {
                    if (_scheduled) return;
                    _scheduled = true;
                    var wait = MinInterval - (DateTime.UtcNow - _lastSentUtc);
                    if (wait < TimeSpan.Zero) wait = TimeSpan.Zero;
                    try { _timer.Change(wait, Timeout.InfiniteTimeSpan); } catch (ObjectDisposedException) { }
                }
            }

            private void Send()
            {
                lock (_gate)
                {
                    _scheduled = false;
                    _lastSentUtc = DateTime.UtcNow;
                }
                try
                {
                    var status = _conn.Host.GetStatus();
                    if (status.Phase != _lastPhase)
                    {
                        _lastPhase = status.Phase;
                        L.Debug("rpc.notify.status", null, ("conn", _conn.Id), ("phase", status.Phase.ToString()));
                    }
                    _ = _conn.NotifyStatusAsync(status);
                }
                catch (Exception ex)
                {
                    L.Warn("rpc.notify.failed", "Status notification failed", ex, ("conn", _conn.Id));
                }
            }

            public void Dispose() => _timer.Dispose();
        }
    }

    /// <summary>Routes StreamJsonRpc's own warnings/errors (serialization failures, protocol violations) into our log.</summary>
    internal sealed class RpcTraceListener : TraceListener
    {
        private static readonly Logger L = Log.For("StreamJsonRpc");
        private readonly int _conn;

        public RpcTraceListener(int conn) => _conn = conn;

        public override void TraceEvent(TraceEventCache eventCache, string source, TraceEventType eventType, int id, string message)
        {
            if (eventType > TraceEventType.Warning) return;
            L.Write(eventType <= TraceEventType.Error ? LogLevel.Error : LogLevel.Warn, "rpc.trace", message, null, null, new (string, object)[] { ("conn", _conn), ("id", id) });
        }

        public override void TraceEvent(TraceEventCache eventCache, string source, TraceEventType eventType, int id, string format, params object[] args)
        {
            if (eventType > TraceEventType.Warning) return;
            string msg;
            try { msg = args == null || args.Length == 0 ? format : string.Format(System.Globalization.CultureInfo.InvariantCulture, format, args); }
            catch (FormatException) { msg = format; }
            TraceEvent(eventCache, source, eventType, id, msg);
        }

        public override void TraceData(TraceEventCache eventCache, string source, TraceEventType eventType, int id, object data)
        {
            if (eventType > TraceEventType.Warning) return;
            TraceEvent(eventCache, source, eventType, id, data?.ToString());
        }

        public override void Write(string message) { }

        public override void WriteLine(string message) { }
    }
}
