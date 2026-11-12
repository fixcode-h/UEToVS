using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json;
using UEToVS.Core.Diagnostics;

namespace UEToVS.Host.Logic.Link
{
    public enum LinkSubscriptionState
    {
        /// <summary>Not wanted (no topic requested) or stopped.</summary>
        Idle = 0,

        /// <summary>Wanted but the editor is not reachable (not running, plugin missing); retrying with back-off.</summary>
        Waiting = 1,

        Connecting = 2,
        Connected = 3,

        /// <summary>The editor answered but its UEToVSLink is a protocol-1 plugin: no streaming until it is updated.</summary>
        PluginTooOld = 4,
    }

    /// <summary>
    /// The persistent UEToVSLink connection (protocol 2): <c>subscribe</c> with the wanted topics, then read the event
    /// lines the plugin pushes and raise <see cref="EventReceived"/> (background thread, in order). Topics can change
    /// while connected (another <c>subscribe</c> line). While at least one topic is wanted it reconnects with back-off
    /// (1 s → 10 s; only a port-file read per attempt while the editor is down); with no topic wanted it disconnects.
    /// A protocol-1 plugin is detected from its "unknown command" reply and not retried until <see cref="Reset"/>.
    /// Never throws to the caller; every state change is logged (link.subscription.*).
    /// </summary>
    public sealed class EditorLinkSubscription : IDisposable
    {
        private static readonly Logger L = Log.For("EditorLink");
        private static readonly TimeSpan ConnectTimeout = TimeSpan.FromSeconds(2);
        private const int MaxLineChars = 4 * 1024 * 1024;

        private readonly Func<int> _resolvePort;
        private readonly object _gate = new object();
        private readonly SemaphoreSlim _writeLock = new SemaphoreSlim(1, 1);
        private readonly HashSet<string> _topics = new HashSet<string>(StringComparer.Ordinal);
        private CancellationTokenSource _cts;
        private Task _loop;
        private StreamWriter _writer;
        private LinkSubscriptionState _state;
        private bool _disposed;
        private long _eventsReceived;
        private long _parseErrors;

        /// <param name="resolvePort">Current plugin port (port file / settings); 0 = unknown.</param>
        public EditorLinkSubscription(Func<int> resolvePort)
        {
            _resolvePort = resolvePort ?? (() => 0);
        }

        public LinkSubscriptionState State
        {
            get
            {
                lock (_gate) return _state;
            }
        }

        public long EventsReceived => Interlocked.Read(ref _eventsReceived);

        /// <summary>Raised for every parsed event line (background thread). Handlers must be quick and must not throw.</summary>
        public event Action<LinkEvent> EventReceived;

        /// <summary>Raised on state changes (background thread).</summary>
        public event Action<LinkSubscriptionState> StateChanged;

        /// <summary>Back-off bounds (tests shorten them).</summary>
        public TimeSpan MinRetryDelay { get; set; } = TimeSpan.FromSeconds(1);

        public TimeSpan MaxRetryDelay { get; set; } = TimeSpan.FromSeconds(10);

        public IReadOnlyCollection<string> Topics
        {
            get
            {
                lock (_gate) return _topics.ToArray();
            }
        }

        /// <summary>Adds / removes a topic; starts or stops the connection loop as needed.</summary>
        public void SetTopic(string topic, bool wanted)
        {
            if (string.IsNullOrEmpty(topic)) return;
            bool changed;
            lock (_gate)
            {
                if (_disposed) return;
                changed = wanted ? _topics.Add(topic) : _topics.Remove(topic);
            }
            if (!changed) return;
            L.Info("link.subscription.topics", null, ("topic", topic), ("wanted", wanted), ("topics", string.Join(",", Topics)));
            Update();
        }

        /// <summary>Forget "plugin too old" (port file changed / plugin installed) and try again.</summary>
        public void Reset()
        {
            lock (_gate)
            {
                if (_state != LinkSubscriptionState.PluginTooOld) return;
            }
            SetState(LinkSubscriptionState.Idle);
            Update();
        }

        /// <summary>Waits until connected (or the timeout passes). True when connected.</summary>
        public async Task<bool> WaitConnectedAsync(TimeSpan timeout, CancellationToken ct)
        {
            var sw = Stopwatch.StartNew();
            while (sw.Elapsed < timeout && !ct.IsCancellationRequested)
            {
                var s = State;
                if (s == LinkSubscriptionState.Connected) return true;
                if (s == LinkSubscriptionState.PluginTooOld) return false;
                if (s == LinkSubscriptionState.Idle)
                {
                    lock (_gate)
                        if (_topics.Count == 0) return false; // nothing wanted: the loop is not (or no longer) running
                }
                try
                {
                    await Task.Delay(50, ct).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    return false;
                }
            }
            return State == LinkSubscriptionState.Connected;
        }

        private void Update()
        {
            bool wanted;
            StreamWriter writer;
            lock (_gate)
            {
                if (_disposed) return;
                wanted = _topics.Count > 0;
                writer = _writer;
                if (wanted && _loop == null && _state != LinkSubscriptionState.PluginTooOld)
                {
                    _cts = new CancellationTokenSource();
                    var token = _cts.Token;
                    _loop = Task.Run(() => RunAsync(token));
                    return;
                }
                if (!wanted && _loop != null)
                {
                    _cts.Cancel();
                    _cts = null;
                    _loop = null;
                    return;
                }
            }
            if (wanted && writer != null) _ = SendSubscribeAsync(writer);
        }

        private async Task SendSubscribeAsync(StreamWriter writer)
        {
            try
            {
                string line = LinkProtocol.Subscribe(Topics).ToString(Formatting.None);
                await WriteLineAsync(writer, line).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is IOException || ex is ObjectDisposedException || ex is InvalidOperationException || ex is SocketException)
            {
                L.Info("link.subscription.resubscribe.failed", null, ("error", ex.GetType().Name + ": " + ex.Message));
            }
        }

        private async Task WriteLineAsync(StreamWriter writer, string line)
        {
            // One writer per connection; serialize writes (subscribe lines from the UI vs. the loop).
            await _writeLock.WaitAsync().ConfigureAwait(false);
            try
            {
                await writer.WriteLineAsync(line).ConfigureAwait(false);
            }
            finally
            {
                _writeLock.Release();
            }
        }

        private async Task RunAsync(CancellationToken ct)
        {
            var delay = MinRetryDelay;
            int attempts = 0;
            while (!ct.IsCancellationRequested)
            {
                attempts++;
                int port = 0;
                try
                {
                    port = _resolvePort();
                }
                catch (Exception ex)
                {
                    L.Warn("link.subscription.port.failed", "Resolving the editor link port failed", ex);
                }
                if (port <= 0)
                {
                    SetState(LinkSubscriptionState.Waiting);
                    if (attempts == 1) L.Info("link.subscription.waiting", "Editor not reachable (no port); retrying", ("topics", string.Join(",", Topics)));
                }
                else
                {
                    var outcome = await ConnectAndReadAsync(port, ct).ConfigureAwait(false);
                    if (outcome == Outcome.TooOld)
                    {
                        SetState(LinkSubscriptionState.PluginTooOld);
                        lock (_gate)
                        {
                            _loop = null;
                            _cts = null;
                        }
                        return;
                    }
                    if (outcome == Outcome.WasConnected)
                    {
                        delay = MinRetryDelay;
                        attempts = 0;
                    }
                    if (!ct.IsCancellationRequested) SetState(LinkSubscriptionState.Waiting);
                }
                try
                {
                    await Task.Delay(delay, ct).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
                delay = TimeSpan.FromMilliseconds(Math.Min(MaxRetryDelay.TotalMilliseconds, delay.TotalMilliseconds * 2));
            }
            SetState(LinkSubscriptionState.Idle);
        }

        private enum Outcome
        {
            Unreachable,
            WasConnected,
            TooOld,
        }

        private async Task<Outcome> ConnectAndReadAsync(int port, CancellationToken ct)
        {
            SetState(LinkSubscriptionState.Connecting);
            var sw = Stopwatch.StartNew();
            bool connected = false;
            long events = 0;
            string reason = null;
            using (var client = new TcpClient())
            using (ct.Register(() => { try { client.Close(); } catch (Exception) { } }))
            {
                try
                {
                    var connect = client.ConnectAsync("127.0.0.1", port);
                    if (await Task.WhenAny(connect, Task.Delay(ConnectTimeout, ct)).ConfigureAwait(false) != connect)
                    {
                        reason = "connect-timeout";
                        return Outcome.Unreachable;
                    }
                    await connect.ConfigureAwait(false);
                    client.NoDelay = true;
                    using (var stream = client.GetStream())
                    using (var writer = new StreamWriter(stream, new UTF8Encoding(false), 4096, true) { NewLine = "\n", AutoFlush = true })
                    using (var reader = new StreamReader(stream, Encoding.UTF8, false, 64 * 1024, true))
                    {
                        await WriteLineAsync(writer, LinkProtocol.Subscribe(Topics).ToString(Formatting.None)).ConfigureAwait(false);
                        string first = await ReadLineAsync(reader, ct).ConfigureAwait(false);
                        var reply = first == null ? null : LinkProtocol.ParseEvent(first, out _) as SubscribeReplyEvent;
                        if (reply == null)
                        {
                            reason = first == null ? "closed-before-reply" : "bad-reply";
                            return Outcome.Unreachable;
                        }
                        if (!reply.Ok)
                        {
                            reason = "refused: " + reply.Message;
                            bool tooOld = LinkProtocol.IsUnknownCommand(reply.Ok, reply.Message);
                            L.Info("link.subscription.refused", tooOld ? "UEToVSLink plugin is too old for streaming (protocol 1)" : "Subscription refused",
                                ("port", port), ("message", reply.Message), ("tooOld", tooOld));
                            return tooOld ? Outcome.TooOld : Outcome.Unreachable;
                        }
                        connected = true;
                        lock (_gate) _writer = writer;
                        SetState(LinkSubscriptionState.Connected);
                        L.Info("link.subscription.connected", null, ("port", port), ("protocol", reply.Protocol), ("topics", string.Join(",", Topics)),
                            ("ms", Math.Round(sw.Elapsed.TotalMilliseconds, 1)));
                        // Topics may have changed between the subscribe line and now.
                        await WriteLineAsync(writer, LinkProtocol.Subscribe(Topics).ToString(Formatting.None)).ConfigureAwait(false);
                        while (!ct.IsCancellationRequested)
                        {
                            string line = await ReadLineAsync(reader, ct).ConfigureAwait(false);
                            if (line == null)
                            {
                                reason = "closed";
                                break;
                            }
                            var e = LinkProtocol.ParseEvent(line, out string error);
                            if (e == null)
                            {
                                if (Interlocked.Increment(ref _parseErrors) <= 20)
                                    L.Warn("link.subscription.badLine", "Unparsable line from the editor link", ("error", error), ("length", line.Length));
                                continue;
                            }
                            events++;
                            Interlocked.Increment(ref _eventsReceived);
                            Raise(e);
                        }
                    }
                }
                catch (Exception ex) when (ex is SocketException || ex is IOException || ex is ObjectDisposedException || ex is OperationCanceledException ||
                                           ex is InvalidOperationException || ex is InvalidDataException)
                {
                    reason = ex.GetType().Name + ": " + ex.Message;
                }
                finally
                {
                    lock (_gate) _writer = null;
                    if (connected)
                        L.Info("link.subscription.disconnected", null, ("port", port), ("reason", ct.IsCancellationRequested ? "stopped" : reason), ("events", events),
                            ("sec", Math.Round(sw.Elapsed.TotalSeconds, 1)));
                    else
                        L.Debug("link.subscription.unreachable", null, ("port", port), ("reason", reason), ("ms", Math.Round(sw.Elapsed.TotalMilliseconds, 1)));
                }
            }
            return connected ? Outcome.WasConnected : Outcome.Unreachable;
        }

        private static async Task<string> ReadLineAsync(StreamReader reader, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            string line = await reader.ReadLineAsync().ConfigureAwait(false);
            if (line != null && line.Length > MaxLineChars) throw new InvalidDataException("line too long: " + line.Length);
            return line;
        }

        private void Raise(LinkEvent e)
        {
            try
            {
                EventReceived?.Invoke(e);
            }
            catch (Exception ex)
            {
                L.Error("link.subscription.handler.failed", "An editor link event handler threw", ex, ("event", e.Kind));
            }
        }

        private void SetState(LinkSubscriptionState state)
        {
            lock (_gate)
            {
                if (_state == state) return;
                _state = state;
            }
            try
            {
                StateChanged?.Invoke(state);
            }
            catch (Exception ex)
            {
                L.Error("link.subscription.stateHandler.failed", "An editor link state handler threw", ex, ("state", state.ToString()));
            }
        }

        public void Dispose()
        {
            lock (_gate)
            {
                if (_disposed) return;
                _disposed = true;
                _topics.Clear();
                _cts?.Cancel();
                _cts = null;
                _loop = null;
            }
        }
    }
}
