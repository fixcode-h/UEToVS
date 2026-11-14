using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using UEToVS.Core.Diagnostics;

namespace UEToVS.Host.Logic.Link
{
    /// <summary>Reply of the editor plugin. <see cref="Reached"/> false = no editor (not running, plugin missing, timeout).</summary>
    public sealed class EditorLinkReply
    {
        public static readonly EditorLinkReply Unreachable = new EditorLinkReply(false, false, null, null);

        public EditorLinkReply(bool reached, bool ok, string message, JObject raw)
        {
            Reached = reached;
            Ok = ok;
            Message = message;
            Raw = raw;
        }

        public bool Reached { get; }
        public bool Ok { get; }
        public string Message { get; }
        public JObject Raw { get; }
    }

    /// <summary>
    /// Client of the UELink editor plugin's push stream (ue/UELink, shared with Agent Panel; was UEToVSLink): a localhost
    /// TCP server inside the running Unreal Editor that speaks one JSON object per line ({"cmd": "ping" | "openAsset" |
    /// "browseAsset" | "liveCoding", ...} → {"ok": bool, "message": string}). Port and token come from the card UELink
    /// writes to &lt;Project&gt;/Saved/UELink/instances/&lt;PID&gt;.json (<see cref="LinkCard"/>); without a card (old
    /// UEToVSLink) the port comes from &lt;Project&gt;/Saved/UEToVSLink/port.txt and no token is sent.
    /// Ported from the prototype; no VS types (tested against a fake server). Every call is logged with its latency.
    /// </summary>
    public sealed class UeEditorLinkClient
    {
        private static readonly Logger L = Log.For("EditorLink");
        private readonly Func<string> _projectRoot;
        private readonly Func<int> _portOverride;

        /// <param name="projectRoot">Folder of the .uproject (null when none).</param>
        /// <param name="portOverride">Fixed port from the settings; 0 = read port.txt.</param>
        public UeEditorLinkClient(Func<string> projectRoot, Func<int> portOverride)
        {
            _projectRoot = projectRoot ?? (() => null);
            _portOverride = portOverride ?? (() => 0);
        }

        public static string PortFile(string projectRoot) =>
            string.IsNullOrEmpty(projectRoot) ? null : Path.Combine(projectRoot, "Saved", "UEToVSLink", "port.txt");

        /// <summary>
        /// Token of the editor that <see cref="ResolvePort"/> picked (from its card; null for an old plugin or a fixed port
        /// that matches no card). Every command carries it: UELink checks it (later versions require it).
        /// </summary>
        public string Token { get; private set; }

        /// <summary>For tests: replaces the "is this PID still running" check of the cards.</summary>
        internal Func<int, bool> ProcessAlive { get; set; }

        public int ResolvePort()
        {
            int port = _portOverride();
            var cards = LinkCard.ReadLive(_projectRoot(), ProcessAlive);
            if (port > 0)
            {
                Token = cards.FirstOrDefault(c => c.StreamPort == port)?.Token;
                return port;
            }
            if (cards.Count > 0)
            {
                Token = cards[0].Token;
                return cards[0].StreamPort;
            }
            Token = null;
            string file = PortFile(_projectRoot());
            if (file == null) return 0;
            try
            {
                return File.Exists(file) && int.TryParse(File.ReadAllText(file).Trim(), out port) && port > 0 && port < 65536 ? port : 0;
            }
            catch (Exception ex)
            {
                L.Warn("link.port.read.failed", "Reading the editor link port file failed", ex, ("file", file));
                return 0;
            }
        }

        /// <summary>Sends one command; never throws (an unreachable editor returns <see cref="EditorLinkReply.Unreachable"/>).</summary>
        public async Task<EditorLinkReply> SendAsync(JObject command, int timeoutMs, CancellationToken ct = default)
        {
            string cmd = command?["cmd"]?.ToString();
            int port = ResolvePort();
            command = LinkProtocol.WithToken(command, Token);
            if (port <= 0)
            {
                L.Info("link.noPort", "Editor link port unknown (editor not running or plugin missing)", ("cmd", cmd), ("portFile", PortFile(_projectRoot())));
                return EditorLinkReply.Unreachable;
            }
            var sw = Stopwatch.StartNew();
            try
            {
                using (var cts = CancellationTokenSource.CreateLinkedTokenSource(ct))
                using (var client = new TcpClient())
                {
                    cts.CancelAfter(timeoutMs);
                    using (cts.Token.Register(() => { try { client.Close(); } catch (Exception) { } }))
                    {
                        await client.ConnectAsync("127.0.0.1", port).ConfigureAwait(false);
                        using (var stream = client.GetStream())
                        using (var writer = new StreamWriter(stream, new UTF8Encoding(false), 1024, true) { NewLine = "\n", AutoFlush = true })
                        using (var reader = new StreamReader(stream, Encoding.UTF8, false, 1024, true))
                        {
                            await writer.WriteLineAsync(command.ToString(Formatting.None)).ConfigureAwait(false);
                            string line = await reader.ReadLineAsync().ConfigureAwait(false);
                            cts.Token.ThrowIfCancellationRequested();
                            if (string.IsNullOrEmpty(line))
                            {
                                L.Warn("link.emptyReply", "Editor link closed without a reply", ("cmd", cmd), ("port", port));
                                return EditorLinkReply.Unreachable;
                            }
                            var obj = JObject.Parse(line);
                            bool ok = obj["ok"]?.Type == JTokenType.Boolean && obj["ok"].Value<bool>();
                            string message = obj["message"]?.ToString();
                            L.Info("link.reply", null, ("cmd", cmd), ("port", port), ("ok", ok), ("message", message), ("ms", Math.Round(sw.Elapsed.TotalMilliseconds, 1)));
                            return new EditorLinkReply(true, ok, message, obj);
                        }
                    }
                }
            }
            catch (Exception ex) when (ex is SocketException || ex is IOException || ex is ObjectDisposedException || ex is OperationCanceledException ||
                                       ex is InvalidOperationException || ex is JsonException)
            {
                bool canceled = ct.IsCancellationRequested;
                L.Info("link.unreachable", "Editor link not reachable", ("cmd", cmd), ("port", port), ("canceled", canceled), ("error", ex.GetType().Name + ": " + ex.Message),
                       ("ms", Math.Round(sw.Elapsed.TotalMilliseconds, 1)));
                return EditorLinkReply.Unreachable;
            }
        }

        /// <summary>Ping with the protocol version (v0.4.5); a protocol-1 plugin ignores the field and answers as before.</summary>
        public Task<EditorLinkReply> PingAsync(CancellationToken ct = default) =>
            SendAsync(LinkProtocol.Ping(), 1000, ct);

        private string _lastCapabilities;

        /// <summary>What the running plugin supports (null when the editor is not reachable).</summary>
        public async Task<LinkCapabilities> GetCapabilitiesAsync(CancellationToken ct = default)
        {
            var r = await PingAsync(ct).ConfigureAwait(false);
            if (!r.Reached) return null;
            var caps = LinkProtocol.ParseCapabilities(r.Raw);
            // Polled by the settings card: log only when something changed.
            string key = caps.ToString() + "|" + caps.Project + "|" + caps.EngineVersion;
            if (Interlocked.Exchange(ref _lastCapabilities, key) == key) return caps;
            L.Info("link.capabilities", null, ("protocol", caps.Protocol), ("plugin", caps.PluginVersion), ("features", string.Join(",", caps.Features)),
                ("project", caps.Project), ("engine", caps.EngineVersion));
            return caps;
        }

        /// <summary>v0.4.5 (protocol 2): asks the editor to run "Automation RunTests &lt;filter&gt;"; results arrive as tests events.</summary>
        public Task<EditorLinkReply> RunTestsAsync(string runId, string filter, CancellationToken ct = default) =>
            SendAsync(LinkProtocol.RunTests(runId, filter), 5000, ct);

        public Task<EditorLinkReply> OpenAssetAsync(string objectPath, CancellationToken ct = default) =>
            SendAsync(new JObject { ["cmd"] = "openAsset", ["path"] = objectPath }, 3000, ct);

        public Task<EditorLinkReply> BrowseToAssetAsync(string objectPath, CancellationToken ct = default) =>
            SendAsync(new JObject { ["cmd"] = "browseAsset", ["path"] = objectPath }, 3000, ct);

        /// <summary>The plugin answers once the compile was started (not when it finishes).</summary>
        public Task<EditorLinkReply> LiveCodingAsync(CancellationToken ct = default) =>
            SendAsync(new JObject { ["cmd"] = "liveCoding" }, 5000, ct);
    }
}
