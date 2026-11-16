using System;
using System.IO;
using System.IO.Pipes;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using UEToVS.Core.Storage;
using UEToVS.Mcp.Tools;

namespace UEToVS.Mcp.Data
{
    /// <summary>
    /// Client of the VS debug control pipe (docs/design/VS-调试控制.md): reads &lt;solution&gt;\.vs\UEToVS\Debug\endpoint.json,
    /// sends one request line, reads one response line. One connection per call: the server is in VS, which may be restarted
    /// between calls (new pipe, new token), so nothing is cached.
    /// </summary>
    public sealed class DebugControlClient
    {
        public const int ProtocolVersion = 1;
        private const int ConnectTimeoutMs = 2000;

        private readonly string _endpointFile;
        private int _nextId;

        public DebugControlClient(string solutionDir) => _endpointFile = StoragePaths.ForSolution(solutionDir).DebugEndpoint;

        public DebugControlClient(string endpointFile, bool isFile) => _endpointFile = endpointFile;

        /// <summary>Runs one op; returns its result or throws a <see cref="ToolException"/> carrying VS's (Chinese) message.</summary>
        public async Task<JsonObject> CallAsync(string op, JsonObject args, int responseTimeoutMs, CancellationToken ct)
        {
            var (pipe, token) = ReadEndpoint();
            var req = new JsonObject
            {
                ["v"] = ProtocolVersion,
                ["id"] = Interlocked.Increment(ref _nextId).ToString(),
                ["token"] = token,
                ["op"] = op,
                ["args"] = args ?? new JsonObject(),
            };
            using var client = new NamedPipeClientStream(".", pipe, PipeDirection.InOut, PipeOptions.Asynchronous);
            try
            {
                using var connectCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                connectCts.CancelAfter(ConnectTimeoutMs);
                await client.ConnectAsync(connectCts.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                throw NotRunning("连不上 VS 的调试控制管道（VS 可能已经关了或卡住）");
            }
            catch (IOException ex)
            {
                throw NotRunning("连不上 VS 的调试控制管道：" + ex.Message);
            }

            var utf8 = new UTF8Encoding(false);
            using var writer = new StreamWriter(client, utf8, 4096, leaveOpen: true) { NewLine = "\n", AutoFlush = true };
            using var reader = new StreamReader(client, utf8, false, 4096, leaveOpen: true);
            await writer.WriteLineAsync(req.ToJsonString().AsMemory(), ct).ConfigureAwait(false);

            using var readCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            readCts.CancelAfter(responseTimeoutMs);
            string line;
            try
            {
                line = await reader.ReadLineAsync(readCts.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                throw new ToolException("vs_timeout", $"VS 在 {responseTimeoutMs / 1000} 秒内没有回应 {op}（VS 可能弹了对话框在等人点）");
            }
            if (line == null) throw new ToolException("vs_closed", "VS 断开了连接（可能正在关闭解决方案）");

            JsonObject resp;
            try { resp = JsonNode.Parse(line) as JsonObject; }
            catch (JsonException) { resp = null; }
            if (resp == null) throw new ToolException("vs_bad_response", "VS 的回应不是 JSON：" + Cut(line));
            if (resp["ok"]?.GetValue<bool>() == true) return resp["result"] as JsonObject ?? new JsonObject();
            throw new ToolException("vs_error", resp["error"]?.GetValue<string>() ?? "VS 返回了错误");
        }

        private (string Pipe, string Token) ReadEndpoint()
        {
            if (!File.Exists(_endpointFile))
                throw NotRunning("VS 没开这个解决方案，或者 VS 里的 UEToVS 低于 0.8.0 / 关了「允许 Agent 通过 VS 调试 UE」");
            JsonObject o;
            try { o = JsonNode.Parse(File.ReadAllText(_endpointFile)) as JsonObject; }
            catch (Exception ex) when (ex is IOException || ex is JsonException) { throw NotRunning("读不了 " + _endpointFile + "：" + ex.Message); }
            int protocol = o?["protocol"]?.GetValue<int>() ?? 0;
            if (protocol != ProtocolVersion)
                throw new ToolException("vs_protocol", $"VS 里的 UEToVS 用第 {protocol} 版调试协议，这个 MCP 服务是第 {ProtocolVersion} 版：把两边升级到同一版 UEToVS");
            string pipe = o?["pipe"]?.GetValue<string>();
            string token = o?["token"]?.GetValue<string>();
            if (string.IsNullOrEmpty(pipe) || string.IsNullOrEmpty(token)) throw NotRunning(_endpointFile + " 内容不完整");
            return (pipe, token);
        }

        private ToolException NotRunning(string why) =>
            new ToolException("vs_not_running", why + "。用 VS 2022 打开这个 UE 项目的 .sln（装 UEToVS 0.8.0 及以上）再试。");

        private static string Cut(string s) => s.Length <= 200 ? s : s.Substring(0, 200) + "…";
    }
}
