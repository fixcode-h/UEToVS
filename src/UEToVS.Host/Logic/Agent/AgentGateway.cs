using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;
using UEToVS.Core.Diagnostics;

namespace UEToVS.Host.Logic.Agent
{
    /// <summary>
    /// Local model gateway on 127.0.0.1 that VS Copilot Chat talks to as an "Ollama" provider. VS only lets an Ollama
    /// model point at a custom URL and then speaks OpenAI Chat Completions to scheme://host:port/v1; the gateway maps the
    /// VS model id to the configured API (<see cref="AgentApiConfig.Routes"/>), puts the real key on the request (VS never
    /// sees it) and streams the upstream response back unchanged, so upstream errors reach the chat as they are.
    ///
    /// Endpoints: POST /v1/chat/completions (forwarded), GET /v1/models, and the Ollama discovery calls VS's "添加模型提供程序 →
    /// Ollama" dialog makes (GET /api/tags, POST /api/show, GET /api/version, GET /). Requests from browsers (an Origin
    /// header) or with a foreign Host header are refused so web pages can't use the keys through it.
    /// </summary>
    public sealed class AgentGateway : IDisposable
    {
        private static readonly Logger L = Log.For("AgentGateway");
        private static readonly TimeSpan RequestTimeout = TimeSpan.FromMinutes(30);

        private readonly Func<string, string> _keyFor;
        private readonly HttpClient _http;
        private readonly object _gate = new object();
        private TcpListener _listener;
        private CancellationTokenSource _cts;
        private Dictionary<string, AgentRoute> _routes = new Dictionary<string, AgentRoute>(StringComparer.OrdinalIgnoreCase);
        private IReadOnlyList<AgentRoute> _ordered = Array.Empty<AgentRoute>();
        private Dictionary<string, (int Limit, SemaphoreSlim Gate)> _limits = new Dictionary<string, (int, SemaphoreSlim)>(StringComparer.Ordinal);
        private long _requests;

        /// <param name="keyFor">API id → key (null/empty when none is stored).</param>
        /// <param name="handler">Upstream handler (tests); null = default handler with the system proxy.</param>
        public AgentGateway(Func<string, string> keyFor, HttpMessageHandler handler = null)
        {
            _keyFor = keyFor ?? (_ => null);
            _http = handler == null ? new HttpClient() : new HttpClient(handler, disposeHandler: false);
            _http.Timeout = System.Threading.Timeout.InfiniteTimeSpan;
        }

        public bool IsRunning { get { lock (_gate) return _listener != null; } }
        public int Port { get; private set; }
        public string LastError { get; private set; }
        public long RequestCount => Interlocked.Read(ref _requests);

        /// <summary>Swaps the routing table (config applied); requests in flight keep their old route.</summary>
        public void Update(AgentApiConfig config)
        {
            var routes = config.Routes();
            var map = new Dictionary<string, AgentRoute>(StringComparer.OrdinalIgnoreCase);
            foreach (var r in routes) map[r.VsId] = r;
            lock (_gate)
            {
                var limits = new Dictionary<string, (int, SemaphoreSlim)>(StringComparer.Ordinal);
                foreach (var api in config.Apis)
                {
                    if (api.MaxConcurrency <= 0) continue;
                    limits[api.Id] = _limits.TryGetValue(api.Id, out var old) && old.Limit == api.MaxConcurrency
                        ? old
                        : (api.MaxConcurrency, new SemaphoreSlim(api.MaxConcurrency, api.MaxConcurrency));
                }
                _limits = limits;
                _routes = map;
                _ordered = routes;
            }
        }

        /// <summary>Starts listening on 127.0.0.1:<paramref name="port"/>; false (with <see cref="LastError"/>) when the port is taken.</summary>
        public bool Start(int port)
        {
            lock (_gate)
            {
                if (_listener != null && Port == port) return true;
                StopLocked();
                try
                {
                    var listener = new TcpListener(IPAddress.Loopback, port);
                    listener.Start();
                    _listener = listener;
                    _cts = new CancellationTokenSource();
                    Port = port;
                    LastError = null;
                    var ct = _cts.Token;
                    Task.Run(() => AcceptLoopAsync(listener, ct));
                    L.Info("gateway.start", null, ("port", port));
                    return true;
                }
                catch (Exception ex)
                {
                    LastError = ex.Message;
                    L.Warn("gateway.start.failed", "port unavailable", ex, ("port", port));
                    return false;
                }
            }
        }

        public void Stop()
        {
            lock (_gate) StopLocked();
        }

        private void StopLocked()
        {
            if (_listener == null) return;
            try { _cts.Cancel(); } catch (ObjectDisposedException) { }
            try { _listener.Stop(); } catch (SocketException) { }
            L.Info("gateway.stop", null, ("port", Port));
            _listener = null;
            _cts.Dispose();
            _cts = null;
        }

        public void Dispose()
        {
            Stop();
            _http.Dispose();
        }

        private async Task AcceptLoopAsync(TcpListener listener, CancellationToken ct)
        {
            while (!ct.IsCancellationRequested)
            {
                TcpClient client;
                try { client = await listener.AcceptTcpClientAsync().ConfigureAwait(false); }
                catch (ObjectDisposedException) { return; }
                catch (SocketException) when (ct.IsCancellationRequested) { return; }
                catch (InvalidOperationException) { return; }
                catch (Exception ex)
                {
                    L.Warn("gateway.accept", null, ex);
                    continue;
                }
                _ = Task.Run(() => HandleAsync(client, ct));
            }
        }

        private async Task HandleAsync(TcpClient client, CancellationToken stopping)
        {
            using (client)
            using (var timeout = CancellationTokenSource.CreateLinkedTokenSource(stopping))
            {
                timeout.CancelAfter(RequestTimeout);
                var ct = timeout.Token;
                client.NoDelay = true;
                var stream = client.GetStream();
                GatewayRequest req = null;
                try
                {
                    req = await GatewayHttp.ReadRequestAsync(stream, ct).ConfigureAwait(false);
                    if (req == null) return;
                    Interlocked.Increment(ref _requests);
                    await DispatchAsync(req, stream, ct).ConfigureAwait(false);
                }
                catch (Exception ex) when (ex is IOException || ex is SocketException || ex is ObjectDisposedException || ex is OperationCanceledException)
                {
                    // client went away / VS closed / request timed out: nothing to answer.
                    L.Debug("gateway.connection.closed", ex.Message, ("path", req?.Path));
                }
                catch (Exception ex)
                {
                    L.Warn("gateway.request.failed", null, ex, ("path", req?.Path));
                    try { await GatewayHttp.WriteJsonAsync(stream, 500, ErrorJson("UEToVS gateway: " + ex.Message), ct).ConfigureAwait(false); }
                    catch (Exception) { /* connection already broken */ }
                }
            }
        }

        private async Task DispatchAsync(GatewayRequest req, Stream stream, CancellationToken ct)
        {
            if (!IsLocalCaller(req))
            {
                await GatewayHttp.WriteJsonAsync(stream, 403, ErrorJson("UEToVS gateway only serves local clients"), ct).ConfigureAwait(false);
                return;
            }
            string path = req.Path.TrimEnd('/');
            switch (req.Method + " " + path)
            {
                case "GET ":
                case "HEAD ":
                    await GatewayHttp.WriteJsonAsync(stream, 200, "\"Ollama is running (UEToVS gateway)\"", ct).ConfigureAwait(false);
                    return;
                case "GET /api/version":
                    await GatewayHttp.WriteJsonAsync(stream, 200, "{\"version\":\"0.6.0\"}", ct).ConfigureAwait(false);
                    return;
                case "GET /api/tags":
                    await GatewayHttp.WriteJsonAsync(stream, 200, TagsJson(), ct).ConfigureAwait(false);
                    return;
                case "POST /api/show":
                    await ShowAsync(req, stream, ct).ConfigureAwait(false);
                    return;
                case "GET /v1/models":
                case "GET /models":
                    await GatewayHttp.WriteJsonAsync(stream, 200, ModelsJson(), ct).ConfigureAwait(false);
                    return;
                case "POST /v1/chat/completions":
                case "POST /chat/completions":
                    await ForwardChatAsync(req, stream, ct).ConfigureAwait(false);
                    return;
                default:
                    await GatewayHttp.WriteJsonAsync(stream, 404, ErrorJson("UEToVS gateway: " + req.Method + " " + req.Path + " is not supported"), ct).ConfigureAwait(false);
                    return;
            }
        }

        /// <summary>Loopback socket is a given; additionally refuse browser requests (DNS rebinding / CSRF from web pages).</summary>
        private bool IsLocalCaller(GatewayRequest req)
        {
            if (!string.IsNullOrEmpty(req.Header("Origin"))) return false;
            string host = req.Header("Host");
            if (string.IsNullOrEmpty(host)) return true;
            int colon = host.LastIndexOf(':');
            string name = colon > 0 && host.IndexOf(']') < colon ? host.Substring(0, colon) : host;
            return name == "127.0.0.1" || name.Equals("localhost", StringComparison.OrdinalIgnoreCase) || name == "[::1]";
        }

        private IReadOnlyList<AgentRoute> Snapshot() { lock (_gate) return _ordered; }

        private AgentRoute Find(string vsId)
        {
            if (string.IsNullOrEmpty(vsId)) return null;
            lock (_gate)
            {
                if (_routes.TryGetValue(vsId, out var r)) return r;
                // Ollama-style "name:latest" tags.
                if (vsId.EndsWith(":latest", StringComparison.OrdinalIgnoreCase) && _routes.TryGetValue(vsId.Substring(0, vsId.Length - 7), out r)) return r;
                return null;
            }
        }

        private string ModelsJson()
        {
            var data = new JArray(Snapshot().Select(r => new JObject { ["id"] = r.VsId, ["object"] = "model", ["owned_by"] = r.Api.DisplayName }));
            return new JObject { ["object"] = "list", ["data"] = data }.ToString(Newtonsoft.Json.Formatting.None);
        }

        private string TagsJson()
        {
            var models = new JArray(Snapshot().Select(r => new JObject
            {
                ["name"] = r.VsId,
                ["model"] = r.VsId,
                ["modified_at"] = "2025-01-01T00:00:00Z",
                ["size"] = 0,
                ["digest"] = string.Empty,
                ["details"] = new JObject { ["family"] = r.Api.DisplayName, ["format"] = "api" },
            }));
            return new JObject { ["models"] = models }.ToString(Newtonsoft.Json.Formatting.None);
        }

        private async Task ShowAsync(GatewayRequest req, Stream stream, CancellationToken ct)
        {
            string id = null;
            try
            {
                var o = JObject.Parse(req.BodyText);
                id = (string)(o["model"] ?? o["name"]);
            }
            catch (Exception) { /* answered as not found below */ }
            var r = Find(id);
            if (r == null)
            {
                await GatewayHttp.WriteJsonAsync(stream, 404, ErrorJson("model '" + id + "' not found"), ct).ConfigureAwait(false);
                return;
            }
            var caps = new JArray("completion");
            if (r.Model.ToolCalling) caps.Add("tools");
            if (r.Model.Vision) caps.Add("vision");
            var show = new JObject
            {
                ["capabilities"] = caps,
                ["details"] = new JObject { ["family"] = r.Api.DisplayName, ["format"] = "api" },
                ["model_info"] = new JObject { ["general.architecture"] = "uetovs", ["uetovs.context_length"] = r.Model.ContextTokens },
            };
            await GatewayHttp.WriteJsonAsync(stream, 200, show.ToString(Newtonsoft.Json.Formatting.None), ct).ConfigureAwait(false);
        }

        private async Task ForwardChatAsync(GatewayRequest req, Stream stream, CancellationToken ct)
        {
            JObject body;
            try { body = JObject.Parse(req.BodyText); }
            catch (Exception)
            {
                await GatewayHttp.WriteJsonAsync(stream, 400, ErrorJson("UEToVS gateway: request body is not JSON"), ct).ConfigureAwait(false);
                return;
            }
            string vsId = (string)body["model"];
            var route = Find(vsId);
            if (route == null)
            {
                await GatewayHttp.WriteJsonAsync(stream, 404, ErrorJson("UEToVS: model '" + vsId + "' is not configured or not enabled (UEToVS 设置 > Agent / API)"), ct).ConfigureAwait(false);
                return;
            }
            string root = AgentApiConfig.ResolveApiRoot(route.Api.BaseUrl);
            string key = _keyFor(route.Api.Id);
            if (root == null || string.IsNullOrEmpty(key))
            {
                string why = root == null ? "has no valid URL" : "has no API key";
                await GatewayHttp.WriteJsonAsync(stream, 401, ErrorJson("UEToVS: API '" + route.Api.DisplayName + "' " + why + " (UEToVS 设置 > Agent / API)"), ct).ConfigureAwait(false);
                return;
            }
            body["model"] = route.Model.Id;

            SemaphoreSlim limit = null;
            lock (_gate) if (_limits.TryGetValue(route.Api.Id, out var l)) limit = l.Gate;
            if (limit != null) await limit.WaitAsync(ct).ConfigureAwait(false);
            var sw = Stopwatch.StartNew();
            int status = 0;
            try
            {
                using (var up = new HttpRequestMessage(HttpMethod.Post, root + "/chat/completions"))
                {
                    up.Content = new StringContent(body.ToString(Newtonsoft.Json.Formatting.None), Encoding.UTF8, "application/json");
                    AgentApiClient.ApplyKey(up, route.Api.KeyMode, key);
                    if (req.Header("Accept") is string accept) up.Headers.TryAddWithoutValidation("Accept", accept);
                    HttpResponseMessage resp;
                    try { resp = await _http.SendAsync(up, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false); }
                    catch (HttpRequestException ex)
                    {
                        status = 502;
                        await GatewayHttp.WriteJsonAsync(stream, 502, ErrorJson("UEToVS: " + route.Api.DisplayName + " unreachable: " + Innermost(ex).Message), ct).ConfigureAwait(false);
                        return;
                    }
                    using (resp)
                    {
                        status = (int)resp.StatusCode;
                        string contentType = resp.Content?.Headers.ContentType?.ToString() ?? "application/json";
                        await GatewayHttp.WriteHeadAsync(stream, status, resp.ReasonPhrase, contentType, null, ct).ConfigureAwait(false);
                        if (resp.Content != null)
                        {
                            using (var src = await resp.Content.ReadAsStreamAsync().ConfigureAwait(false))
                            {
                                var buf = new byte[16 * 1024];
                                int n;
                                while ((n = await src.ReadAsync(buf, 0, buf.Length, ct).ConfigureAwait(false)) > 0)
                                {
                                    await stream.WriteAsync(buf, 0, n, ct).ConfigureAwait(false);
                                    await stream.FlushAsync(ct).ConfigureAwait(false);
                                }
                            }
                        }
                    }
                }
            }
            finally
            {
                limit?.Release();
                L.Info("gateway.chat", null, ("model", vsId), ("api", route.Api.DisplayName), ("upstreamModel", route.Model.Id),
                       ("status", status), ("ms", sw.ElapsedMilliseconds), ("stream", (bool?)body["stream"] ?? false));
            }
        }

        public static string ErrorJson(string message) =>
            new JObject { ["error"] = new JObject { ["message"] = message, ["type"] = "uetovs_gateway" } }.ToString(Newtonsoft.Json.Formatting.None);

        private static Exception Innermost(Exception ex)
        {
            while (ex.InnerException != null) ex = ex.InnerException;
            return ex;
        }
    }
}
