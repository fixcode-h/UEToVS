using System;
using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Threading;
using System.Threading.Tasks;
using MessagePack;
using MessagePack.Resolvers;
using StreamJsonRpc;
using UEToVS.Contracts;
using UEToVS.Core.Diagnostics;
using UEToVS.Host.Logic.Localization;
using UEToVS.Host.Logic.Engine;

namespace UEToVS.Host.Engine
{
    internal enum EngineState
    {
        Stopped,
        Starting,
        Connected,

        /// <summary>Waiting for the backoff delay before an automatic restart.</summary>
        Restarting,

        /// <summary>Gave up (restart budget exhausted, runtime missing, start failure). Features degrade.</summary>
        Unavailable,

        /// <summary>Protocol version mismatch: reinstall needed. Never restarted automatically.</summary>
        Incompatible,

        /// <summary>The engine executable is not there (e.g. a VSIX built before the engine project existed).</summary>
        NotInstalled,
    }

    internal sealed class EngineClientOptions
    {
        public string ExtensionDirectory { get; set; }
        public string SolutionDirectory { get; set; }
        public string ProjectFile { get; set; }
        public string EnginePathOverride { get; set; }
        public string UnrealEngineRootOverride { get; set; }
        public LogLevel LogLevel { get; set; } = LogLevel.Info;
        public string HostVersion { get; set; }
        public string VsVersion { get; set; }
        public TimeSpan RequestTimeout { get; set; } = TimeSpan.FromSeconds(30);
        public TimeSpan ConnectTimeout { get; set; } = TimeSpan.FromSeconds(20);
        public TimeSpan HandshakeTimeout { get; set; } = TimeSpan.FromSeconds(15);
        public TimeSpan HeartbeatInterval { get; set; } = TimeSpan.FromSeconds(30);
    }

    /// <summary>Thrown by <see cref="EngineClient.InvokeAsync{T}"/> when the engine is not connected.</summary>
    internal sealed class EngineUnavailableException : Exception
    {
        public EngineUnavailableException(string message, Exception inner = null) : base(message, inner) { }
    }

    /// <summary>
    /// Client side of the engine process (L1, §5.2): launches &lt;ext&gt;\Engine\UEToVS.Engine.exe, connects over a named pipe,
    /// handshakes (protocol version), and restarts it with exponential backoff (max 3 / 10 min, §10.2).
    ///
    /// Wire format (must match UEToVS.Engine): named pipe <see cref="ProtocolInfo.PipeName"/>(devenv pid, solution dir);
    /// StreamJsonRpc over <see cref="LengthHeaderMessageHandler"/> with <see cref="MessagePackFormatter"/> configured with
    /// MessagePackSerializerOptions.Standard.WithResolver(ContractlessStandardResolver.Instance).WithSecurity(UntrustedData);
    /// ActivityTracingStrategy (shared request ids); default method names (as declared in <see cref="IEngineService"/> /
    /// <see cref="IEngineCallbacks"/>, registered via AddLocalRpcTarget&lt;IEngineCallbacks&gt;). Handshake is the first call;
    /// remote error -32050 = protocol mismatch.
    ///
    /// Everything runs on background threads; nothing here ever blocks the UI thread. When the engine is unavailable,
    /// <see cref="TryInvokeAsync{T}"/> returns (false, default) so features degrade instead of failing.
    /// </summary>
    internal sealed partial class EngineClient : IEngineCallbacks, IDisposable
    {
        private static readonly Logger L = Log.For("Engine");
        private readonly EngineClientOptions _options;
        private readonly RestartPolicy _policy = new RestartPolicy();
        private readonly SemaphoreSlim _lifecycle = new SemaphoreSlim(1, 1);
        private readonly CancellationTokenSource _disposeCts = new CancellationTokenSource();
        private Process _process;
        private JsonRpc _rpc;
        private IEngineService _service;
        private int _generation;
        private volatile EngineState _state = EngineState.Stopped;
        private IndexPhase? _lastPhase;
        private Timer _heartbeat;
        private int _heartbeatFailures;

        public EngineClient(EngineClientOptions options)
        {
            _options = options ?? throw new ArgumentNullException(nameof(options));
        }

        public EngineState State => _state;
        public bool IsConnected => _state == EngineState.Connected && _service != null;
        public EngineStatus LastStatus { get; private set; }
        public HandshakeResponse Handshake { get; private set; }
        public int? ProcessId { get; private set; }
        public string ExecutablePath { get; private set; }
        public int TotalStarts { get; private set; }
        public string LastError { get; private set; }

        public event EventHandler StateChanged;
        public event EventHandler<EngineStatus> StatusChanged;

        /// <summary>Raised (once) when automatic restarts are exhausted or the engine cannot run; the host shows a banner.</summary>
        public event EventHandler<string> GaveUp;

        public LogLevel LogLevel
        {
            get => _options.LogLevel;
            set => _options.LogLevel = value;
        }

        // ------------------------------------------------------------------ lifecycle

        /// <summary>Starts (or restarts) the engine on a background thread. Never throws.</summary>
        public Task StartAsync() => Task.Run(() => StartCoreAsync("start"));

        /// <summary>Manual restart (command): resets the restart budget.</summary>
        public Task RestartAsync()
        {
            _policy.Reset();
            L.Info("engine.restart.manual", "Engine restart requested by the user");
            return Task.Run(() => StartCoreAsync("manual"));
        }

        private async Task StartCoreAsync(string reason)
        {
            if (_disposeCts.IsCancellationRequested) return;
            await _lifecycle.WaitAsync(_disposeCts.Token).ConfigureAwait(false);
            int gen;
            try
            {
                await StopCoreAsync("restart:" + reason).ConfigureAwait(false);
                gen = Interlocked.Increment(ref _generation);
                SetState(EngineState.Starting);

                string exe = EngineLaunch.ResolveExecutable(_options.ExtensionDirectory, _options.EnginePathOverride, out string source);
                ExecutablePath = exe;
                if (!File.Exists(exe))
                {
                    LastError = "engine executable not found: " + exe;
                    L.Warn("engine.notFound", "Engine executable not found; UEToVS runs without the engine (degraded)", ("path", exe), ("source", source));
                    SetState(EngineState.NotInstalled);
                    return;
                }

                int pid = Process.GetCurrentProcess().Id;
                string pipe = ProtocolInfo.PipeName(pid, _options.SolutionDirectory);
                string args = EngineLaunch.BuildArguments(pipe, _options.SolutionDirectory, pid, _options.LogLevel.ToString(),
                                                          _options.ProjectFile, _options.UnrealEngineRootOverride);
                var psi = new ProcessStartInfo(exe, args)
                {
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardError = true,
                    RedirectStandardOutput = true,
                    WorkingDirectory = Path.GetDirectoryName(exe),
                };
                Process p;
                var sw = Stopwatch.StartNew();
                try
                {
                    p = Process.Start(psi);
                }
                catch (Exception ex)
                {
                    LastError = ex.Message;
                    L.Error("engine.start.failed", "Cannot start the engine process", ex, ("path", exe), ("args", args));
                    SetState(EngineState.Unavailable);
                    GaveUp?.Invoke(this, Strings.EngineStartFailed(ex.Message));
                    return;
                }
                TotalStarts++;
                _process = p;
                ProcessId = p.Id;
                p.EnableRaisingEvents = true;
                p.OutputDataReceived += (_, e) => { if (e.Data != null) L.Debug("engine.stdout", e.Data, ("pid", ProcessId)); };
                p.ErrorDataReceived += (_, e) => { if (e.Data != null) L.Warn("engine.stderr", e.Data, ("pid", ProcessId)); };
                p.Exited += (_, __) => OnProcessExited(p, gen);
                p.BeginOutputReadLine();
                p.BeginErrorReadLine();
                L.Info("engine.started", "Engine process started", ("pid", p.Id), ("path", exe), ("source", source), ("pipe", pipe),
                       ("args", args), ("reason", reason), ("starts", TotalStarts));

                // Connect + handshake.
                var stream = await ConnectPipeAsync(pipe, p).ConfigureAwait(false);
                if (stream == null)
                {
                    LastError = "pipe connect failed";
                    L.Error("engine.connect.failed", "Could not connect to the engine pipe", null, ("pipe", pipe), ("ms", sw.ElapsedMilliseconds),
                            ("exited", SafeHasExited(p)));
                    KillQuietly(p); // exit handler schedules the restart
                    return;
                }
                var rpc = CreateRpcWithCallbacks(stream);
                var service = rpc.Attach<IEngineService>();
                rpc.Disconnected += (s, e) => OnDisconnected(e, gen);
                rpc.StartListening();
                _rpc = rpc;

                HandshakeResponse hs;
                try
                {
                    using (var cts = CancellationTokenSource.CreateLinkedTokenSource(_disposeCts.Token))
                    {
                        cts.CancelAfter(_options.HandshakeTimeout);
                        hs = await service.HandshakeAsync(new HandshakeRequest
                        {
                            ProtocolVersion = ProtocolInfo.Version,
                            HostVersion = _options.HostVersion,
                            VsVersion = _options.VsVersion,
                            SolutionDir = _options.SolutionDirectory,
                            ProjectFile = _options.ProjectFile,
                            EngineRootOverride = string.IsNullOrWhiteSpace(_options.UnrealEngineRootOverride) ? null : _options.UnrealEngineRootOverride,
                        }, cts.Token).ConfigureAwait(false);
                    }
                }
                catch (RemoteInvocationException ex) when (ex.ErrorCode == RpcErrorCodes.ProtocolMismatch)
                {
                    LastError = "protocol mismatch (engine rejected handshake): " + ex.Message;
                    L.Error("engine.protocol.mismatch", "Engine rejected the handshake (protocol mismatch); reinstall UEToVS", ex,
                            ("host", ProtocolInfo.Version), ("errorCode", ex.ErrorCode));
                    SetState(EngineState.Incompatible);
                    GaveUp?.Invoke(this, Strings.EngineIncompatible);
                    Interlocked.Increment(ref _generation);
                    KillQuietly(p);
                    return;
                }
                catch (Exception ex)
                {
                    LastError = "handshake failed: " + ex.Message;
                    L.Error("engine.handshake.failed", "Handshake failed", ex, ("pid", p.Id));
                    KillQuietly(p);
                    return;
                }
                Handshake = hs;
                if (hs == null || hs.ProtocolVersion != ProtocolInfo.Version)
                {
                    LastError = $"protocol mismatch: host {ProtocolInfo.Version}, engine {hs?.ProtocolVersion}";
                    L.Error("engine.protocol.mismatch", "Engine protocol version does not match; reinstall UEToVS", null,
                            ("host", ProtocolInfo.Version), ("engine", hs?.ProtocolVersion), ("engineVersion", hs?.EngineVersion));
                    SetState(EngineState.Incompatible);
                    GaveUp?.Invoke(this, Strings.EngineIncompatible);
                    Interlocked.Increment(ref _generation); // the exit below must not trigger a restart
                    KillQuietly(p);
                    return;
                }
                _service = service;
                _heartbeatFailures = 0;
                SetState(EngineState.Connected);
                L.Info("engine.connected", "Engine connected", ("pid", hs.ProcessId), ("engineVersion", hs.EngineVersion), ("protocol", hs.ProtocolVersion),
                       ("logDir", hs.LogDirectory), ("indexDir", hs.IndexDirectory), ("ms", sw.ElapsedMilliseconds));
                StartHeartbeat(gen);
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (Exception ex)
            {
                LastError = ex.Message;
                L.Error("engine.start.unexpected", "Unexpected error while starting the engine", ex);
                SetState(EngineState.Unavailable);
                return;
            }
            finally
            {
                _lifecycle.Release();
            }

            // Initial status (the engine also pushes OnStatusChangedAsync).
            var (ok, status) = await TryInvokeAsync("getStatus", (s, ct) => s.GetStatusAsync(ct), CancellationToken.None).ConfigureAwait(false);
            if (ok && gen == _generation) await OnStatusChangedAsync(status).ConfigureAwait(false);
        }

        /// <summary>Serializer options; must match UEToVS.Engine exactly.</summary>
        internal static readonly MessagePackSerializerOptions SerializerOptions =
            MessagePackSerializerOptions.Standard.WithResolver(ContractlessStandardResolver.Instance).WithSecurity(MessagePackSecurity.UntrustedData);

        /// <summary>The wire format shared with UEToVS.Engine (see class remarks).</summary>
        internal static JsonRpc CreateRpcConnection(Stream stream)
        {
            var formatter = new MessagePackFormatter();
            formatter.SetMessagePackSerializerOptions(SerializerOptions);
            var handler = new LengthHeaderMessageHandler(stream, stream, formatter);
            return new JsonRpc(handler)
            {
                // W3C trace context on every request so host and engine log lines share request ids.
                ActivityTracingStrategy = new ActivityTracingStrategy(),
            };
        }

        private JsonRpc CreateRpcWithCallbacks(Stream stream)
        {
            var rpc = CreateRpcConnection(stream);
            // Only IEngineCallbacks is exposed to the engine (not the client's own public methods).
            rpc.AddLocalRpcTarget<IEngineCallbacks>(new CallbackTarget(this), null);
            return rpc;
        }

        private sealed class CallbackTarget : IEngineCallbacks
        {
            private readonly EngineClient _owner;
            public CallbackTarget(EngineClient owner) => _owner = owner;
            public Task OnStatusChangedAsync(EngineStatus status) => _owner.OnStatusChangedAsync(status);
        }

        private async Task<Stream> ConnectPipeAsync(string pipe, Process p)
        {
            var deadline = DateTime.UtcNow + _options.ConnectTimeout;
            int attempts = 0;
            while (DateTime.UtcNow < deadline && !_disposeCts.IsCancellationRequested)
            {
                if (SafeHasExited(p)) return null;
                attempts++;
                var stream = new NamedPipeClientStream(".", pipe, PipeDirection.InOut, PipeOptions.Asynchronous);
                try
                {
                    await stream.ConnectAsync(500, _disposeCts.Token).ConfigureAwait(false);
                    L.Debug("engine.pipe.connected", null, ("attempts", attempts));
                    return stream;
                }
                catch (TimeoutException)
                {
                    stream.Dispose();
                }
                catch (IOException)
                {
                    stream.Dispose();
                    await Task.Delay(100, _disposeCts.Token).ConfigureAwait(false);
                }
            }
            return null;
        }

        private void OnProcessExited(Process p, int gen)
        {
            int code = -1;
            try { code = p.ExitCode; } catch (Exception) { }
            bool current = gen == Volatile.Read(ref _generation) && ReferenceEquals(p, _process);
            bool intentional = _disposeCts.IsCancellationRequested || !current;
            if (intentional)
            {
                L.Info("engine.exited", "Engine process exited", ("pid", SafeId(p)), ("code", code), ("intentional", true));
                return;
            }
            L.Warn("engine.exited.unexpected", "Engine process exited unexpectedly", ("pid", SafeId(p)), ("code", code), ("codeHex", "0x" + code.ToString("X8")));
            _service = null;
            StopHeartbeat();
            if (EngineLaunch.IsMissingRuntimeExitCode(code))
            {
                LastError = ".NET 8 runtime missing";
                L.Error("engine.runtime.missing", "The engine needs the .NET 8 runtime", null, ("code", code));
                SetState(EngineState.Unavailable);
                GaveUp?.Invoke(this, Strings.EngineRuntimeMissing);
                return;
            }
            ScheduleRestart("exit code " + code);
        }

        private void OnDisconnected(JsonRpcDisconnectedEventArgs e, int gen)
        {
            if (gen != Volatile.Read(ref _generation) || _disposeCts.IsCancellationRequested) return;
            L.Warn("engine.disconnected", "Engine connection lost", e.Exception, ("reason", e.Reason.ToString()), ("description", e.Description));
            _service = null;
            var p = _process;
            // A live process without a connection is useless: kill it, the exit handler restarts.
            if (p != null && !SafeHasExited(p)) KillQuietly(p);
        }

        private void ScheduleRestart(string why)
        {
            if (!_policy.TryScheduleRestart(DateTime.UtcNow, out var delay, out int attempt))
            {
                LastError = "restart budget exhausted";
                L.Error("engine.restart.exhausted", "Engine crashed too often; automatic restart stopped", null,
                        ("max", _policy.MaxRestarts), ("windowMin", _policy.Window.TotalMinutes), ("why", why));
                SetState(EngineState.Unavailable);
                GaveUp?.Invoke(this, Strings.EngineRestartExhausted((int)_policy.Window.TotalMinutes, _policy.MaxRestarts));
                return;
            }
            SetState(EngineState.Restarting);
            L.Warn("engine.restart.scheduled", "Restarting the engine", ("attempt", attempt), ("delayMs", delay.TotalMilliseconds), ("why", why));
            _ = Task.Run(async () =>
            {
                try
                {
                    await Task.Delay(delay, _disposeCts.Token).ConfigureAwait(false);
                    await StartCoreAsync("auto#" + attempt).ConfigureAwait(false);
                }
                catch (OperationCanceledException) { }
                catch (Exception ex) { L.Error("engine.restart.failed", "Restart failed", ex); }
            });
        }

        // ------------------------------------------------------------------ heartbeat (§10.2: engine hang → restart)

        private void StartHeartbeat(int gen)
        {
            StopHeartbeat();
            var interval = _options.HeartbeatInterval;
            _heartbeat = new Timer(_ => _ = HeartbeatAsync(gen), null, interval, interval);
        }

        private void StopHeartbeat()
        {
            var t = Interlocked.Exchange(ref _heartbeat, null);
            t?.Dispose();
        }

        private async Task HeartbeatAsync(int gen)
        {
            if (gen != _generation || !IsConnected) return;
            try
            {
                var svc = _service;
                if (svc == null) return;
                using (var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10)))
                {
                    var status = await svc.GetStatusAsync(cts.Token).ConfigureAwait(false);
                    _heartbeatFailures = 0;
                    if (status?.Resources != null)
                        L.Debug("engine.heartbeat", null, ("privateMB", Math.Round(status.Resources.PrivateBytes / 1048576.0, 1)), ("phase", status.Phase.ToString()));
                }
            }
            catch (Exception ex)
            {
                int n = Interlocked.Increment(ref _heartbeatFailures);
                L.Warn("engine.heartbeat.failed", "Engine did not answer the heartbeat", ex, ("consecutive", n));
                if (n >= 2 && gen == _generation)
                {
                    L.Error("engine.hung", "Engine unresponsive; killing it for a restart", null, ("pid", ProcessId));
                    var p = _process;
                    if (p != null) KillQuietly(p);
                }
            }
        }

        // ------------------------------------------------------------------ requests

        /// <summary>
        /// Invokes the engine with a timeout (default <see cref="EngineClientOptions.RequestTimeout"/>) and the caller's
        /// cancellation. Timed against <paramref name="budgetMs"/> (logs rpc.&lt;op&gt;.slow when over).
        /// Throws <see cref="EngineUnavailableException"/>, <see cref="TimeoutException"/>, <see cref="OperationCanceledException"/>
        /// or the remote error.
        /// </summary>
        public async Task<T> InvokeAsync<T>(string op, Func<IEngineService, CancellationToken, Task<T>> call, CancellationToken ct,
                                            TimeSpan? timeout = null, double budgetMs = double.MaxValue)
        {
            var svc = _service;
            if (svc == null || _state != EngineState.Connected)
                throw new EngineUnavailableException("Engine not connected (" + _state + ")");
            using (var cts = CancellationTokenSource.CreateLinkedTokenSource(ct, _disposeCts.Token))
            using (var scope = L.Time("rpc." + op, budgetMs))
            {
                var t = timeout ?? _options.RequestTimeout;
                cts.CancelAfter(t);
                try
                {
                    return await call(svc, cts.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (!ct.IsCancellationRequested && !_disposeCts.IsCancellationRequested)
                {
                    scope.Fail();
                    L.Warn("rpc.timeout", "Engine request timed out", ("op", op), ("timeoutMs", t.TotalMilliseconds));
                    throw new TimeoutException($"Engine request '{op}' timed out after {t.TotalSeconds:F0} s");
                }
                catch (ConnectionLostException ex)
                {
                    scope.Fail();
                    throw new EngineUnavailableException("Engine connection lost during " + op, ex);
                }
                catch (ObjectDisposedException ex)
                {
                    scope.Fail();
                    throw new EngineUnavailableException("Engine connection closed during " + op, ex);
                }
                catch (RemoteInvocationException ex)
                {
                    scope.Fail();
                    L.Error("rpc.remote.failed", "Engine reported an error", ex, ("op", op), ("errorCode", ex.ErrorCode));
                    throw;
                }
            }
        }

        /// <summary>Degrading variant: (false, default) when the engine is unavailable, timed out or failed (logged).</summary>
        public async Task<(bool Ok, T Value)> TryInvokeAsync<T>(string op, Func<IEngineService, CancellationToken, Task<T>> call, CancellationToken ct,
                                                                TimeSpan? timeout = null, double budgetMs = double.MaxValue)
        {
            try
            {
                return (true, await InvokeAsync(op, call, ct, timeout, budgetMs).ConfigureAwait(false));
            }
            catch (EngineUnavailableException ex)
            {
                L.Debug("rpc.unavailable", ex.Message, ("op", op));
            }
            catch (OperationCanceledException)
            {
                L.Debug("rpc.canceled", null, ("op", op));
            }
            catch (TimeoutException)
            {
                // logged in InvokeAsync
            }
            catch (RemoteInvocationException)
            {
                // logged in InvokeAsync
            }
            catch (Exception ex)
            {
                L.Error("rpc.failed", "Engine request failed", ex, ("op", op));
            }
            return (false, default(T));
        }

        /// <summary>Live log level change (options page): host side is set by the caller; this forwards to the engine.</summary>
        public async Task SetLogLevelAsync(LogLevel level)
        {
            _options.LogLevel = level;
            var (ok, _) = await TryInvokeAsync("setLogLevel", async (s, ct) => { await s.SetLogLevelAsync(level.ToString(), ct).ConfigureAwait(false); return true; },
                                               CancellationToken.None, TimeSpan.FromSeconds(5)).ConfigureAwait(false);
            L.Info("engine.logLevel", null, ("level", level.ToString()), ("applied", ok));
        }

        /// <summary>VS build started/finished: the engine pauses background indexing meanwhile (§9.4).</summary>
        public async Task<bool> SetBuildInProgressAsync(bool building)
        {
            var (ok, _) = await TryInvokeAsync("setBuildInProgress", async (s, ct) => { await s.SetBuildInProgressAsync(building, ct).ConfigureAwait(false); return true; },
                                               CancellationToken.None, TimeSpan.FromSeconds(5)).ConfigureAwait(false);
            L.Info("engine.build", null, ("building", building), ("applied", ok));
            return ok;
        }

        /// <summary>User pause/resume of background indexing (performance panel).</summary>
        public async Task<bool> SetBackgroundPausedAsync(bool paused)
        {
            var (ok, _) = await TryInvokeAsync("setBackgroundPaused", async (s, ct) => { await s.SetBackgroundPausedAsync(paused, ct).ConfigureAwait(false); return true; },
                                               CancellationToken.None, TimeSpan.FromSeconds(5)).ConfigureAwait(false);
            L.Info("engine.backgroundPaused", null, ("paused", paused), ("applied", ok));
            return ok;
        }

        /// <summary>Settings window → engine: indexing thread counts, build policy, engine sources (0 threads = engine default).</summary>
        public async Task<bool> SetIndexingSettingsAsync(IndexingSettings settings)
        {
            if (settings == null) return false;
            var (ok, _) = await TryInvokeAsync("setIndexingSettings", async (s, ct) => { await s.SetIndexingSettingsAsync(settings, ct).ConfigureAwait(false); return true; },
                                               CancellationToken.None, TimeSpan.FromSeconds(5)).ConfigureAwait(false);
            L.Info("engine.indexing.settings", null, ("cppThreads", settings.CppThreads), ("assetThreads", settings.AssetThreads),
                   ("duringBuild", settings.DuringBuild), ("includeEngine", settings.IncludeEngine), ("applied", ok));
            return ok;
        }

        /// <summary>Contract values of <see cref="RebuildScope"/> added by the engine side (Project = 3, Engine = 4); cast until the contract has them.</summary>
        public const RebuildScope ScopeProject = (RebuildScope)3;
        public const RebuildScope ScopeEngine = (RebuildScope)4;

        /// <summary>Settings window "重建项目索引 / 重建引擎索引 / 重建蓝图索引": the engine drops that index part and re-scans in the background.</summary>
        public async Task<bool> RebuildIndexAsync(RebuildScope scope)
        {
            var (ok, _) = await TryInvokeAsync("rebuildIndex", async (s, ct) => { await s.RebuildIndexAsync(scope, ct).ConfigureAwait(false); return true; },
                                               CancellationToken.None, TimeSpan.FromSeconds(10)).ConfigureAwait(false);
            L.Info("engine.index.rebuild", null, ("scope", scope.ToString()), ("scopeValue", (int)scope), ("applied", ok));
            return ok;
        }

        // ------------------------------------------------------------------ IEngineCallbacks

        public Task OnStatusChangedAsync(EngineStatus status)
        {
            if (status == null) return Task.CompletedTask;
            LastStatus = status;
            if (_lastPhase != status.Phase)
            {
                L.Info("engine.status.phase", null, ("phase", status.Phase.ToString()), ("files", status.FileCount), ("symbols", status.SymbolCount),
                       ("assets", status.AssetCount), ("parsed", status.FilesParsed), ("toParse", status.FilesToParse), ("ue", status.UnrealVersion),
                       ("engineRoot", status.EngineRoot));
                _lastPhase = status.Phase;
                if (status.Phase == IndexPhase.Failed) L.Error("engine.index.failed", "Engine reported index failure", null, ("error", status.LastError));
            }
            try
            {
                StatusChanged?.Invoke(this, status);
            }
            catch (Exception ex)
            {
                L.Error("engine.status.handler.failed", "Status handler failed", ex);
            }
            return Task.CompletedTask;
        }

        // ------------------------------------------------------------------ stop

        public Task StopAsync() => Task.Run(async () =>
        {
            await _lifecycle.WaitAsync().ConfigureAwait(false);
            try { await StopCoreAsync("stop").ConfigureAwait(false); }
            finally { _lifecycle.Release(); }
        });

        private async Task StopCoreAsync(string reason)
        {
            StopHeartbeat();
            Interlocked.Increment(ref _generation); // exits from here on are intentional
            var svc = _service;
            var rpc = _rpc;
            var p = _process;
            _service = null;
            _rpc = null;
            _process = null;
            if (p == null && rpc == null) return;
            L.Info("engine.stopping", null, ("reason", reason), ("pid", SafeId(p)));
            if (svc != null)
            {
                try
                {
                    using (var cts = new CancellationTokenSource(TimeSpan.FromSeconds(1)))
                        await svc.ShutdownAsync(cts.Token).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    L.Debug("engine.shutdown.rpc.failed", ex.Message);
                }
            }
            try { rpc?.Dispose(); } catch (Exception) { }
            if (p != null)
            {
                await Task.Run(() =>
                {
                    try
                    {
                        if (!p.HasExited && !p.WaitForExit(1500)) KillQuietly(p);
                    }
                    catch (Exception) { }
                }).ConfigureAwait(false);
            }
            if (_state != EngineState.Incompatible) SetState(EngineState.Stopped);
        }

        public void Dispose()
        {
            if (_disposeCts.IsCancellationRequested) return;
            // Fire-and-forget: never block VS shutdown. The engine also exits on its own when devenv (--parent) exits.
            var stop = Task.Run(() => StopCoreAsync("dispose"));
            _disposeCts.Cancel();
            _ = stop.ContinueWith(t => { if (t.Exception != null) L.Debug("engine.dispose.failed", t.Exception.Message); }, TaskScheduler.Default);
        }

        // ------------------------------------------------------------------ helpers

        private void SetState(EngineState s)
        {
            if (_state == s) return;
            var old = _state;
            _state = s;
            L.Info("engine.state", null, ("from", old.ToString()), ("to", s.ToString()));
            try { StateChanged?.Invoke(this, EventArgs.Empty); }
            catch (Exception ex) { L.Error("engine.state.handler.failed", "State handler failed", ex); }
        }

        private static bool SafeHasExited(Process p)
        {
            try { return p.HasExited; } catch (Exception) { return true; }
        }

        private static int SafeId(Process p)
        {
            try { return p?.Id ?? 0; } catch (Exception) { return 0; }
        }

        private static void KillQuietly(Process p)
        {
            try
            {
                if (!p.HasExited) p.Kill();
            }
            catch (Exception ex)
            {
                L.Debug("engine.kill.failed", ex.Message);
            }
        }
    }
}
