using System;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace UEToVS.Host.Logic.Debugging
{
    /// <summary>Executes one validated op against VS's debugger (DTE in the host; a fake in tests).</summary>
    public interface IDebugBackend
    {
        Task<JObject> ExecuteAsync(string op, DebugArgs args, CancellationToken ct);
    }

    /// <summary>
    /// Turns one request line into one response line: parses, checks the protocol version and the token (constant time),
    /// routes the op and wraps the result or the error. Never throws: a broken request still gets an answer, so clients
    /// never hang on a line that will not come.
    /// </summary>
    public sealed class DebugDispatcher
    {
        private readonly byte[] _token;
        private readonly IDebugBackend _backend;

        public DebugDispatcher(string token, IDebugBackend backend)
        {
            if (string.IsNullOrEmpty(token)) throw new ArgumentException("token required", nameof(token));
            _token = Encoding.UTF8.GetBytes(token);
            _backend = backend ?? throw new ArgumentNullException(nameof(backend));
        }

        /// <summary>A fresh random token (hex, 32 bytes) for the endpoint file.</summary>
        public static string NewToken()
        {
            var bytes = new byte[32];
            using (var rng = RandomNumberGenerator.Create()) rng.GetBytes(bytes);
            var sb = new StringBuilder(64);
            foreach (byte b in bytes) sb.Append(b.ToString("x2"));
            return sb.ToString();
        }

        /// <summary>
        /// Parses and authenticates a request line. Returns null when it may run (<paramref name="req"/> set), else the error
        /// response line to send back.
        /// </summary>
        public string Authorize(string line, out JObject req)
        {
            req = null;
            JObject r;
            try { r = JObject.Parse(line ?? ""); }
            catch (JsonException) { return Error(null, "请求不是 JSON 对象"); }
            var id = r["id"];
            int v = r.Value<int?>("v") ?? DebugProtocol.Version;
            if (v != DebugProtocol.Version)
                return Error(id, $"协议版本不一致：VS 里的 UEToVS 用第 {DebugProtocol.Version} 版，请求是第 {v} 版。把 UEToVS 和调用方（面板 UE 插件或 UEToVS 的 MCP）升级到同一版");
            if (!TokenMatches(r.Value<string>("token")))
                return Error(id, "令牌不对：VS 可能重开过，重新读一次 .vs/UEToVS/Debug/endpoint.json 再试");
            req = r;
            return null;
        }

        public async Task<string> HandleLineAsync(string line, CancellationToken ct)
        {
            JToken id = null;
            string op = null;
            try
            {
                string denied = Authorize(line, out var req);
                if (denied != null) return denied;
                id = req["id"];
                op = req.Value<string>("op");
                if (!DebugProtocol.IsKnown(op))
                    return Error(id, $"不认识的操作 {op ?? "(空)"}，可用：{string.Join(", ", DebugProtocol.Ops)}");

                var result = await _backend.ExecuteAsync(op, new DebugArgs(req["args"] as JObject), ct).ConfigureAwait(false) ?? new JObject();
                string text = new JObject { ["id"] = id, ["ok"] = true, ["result"] = result }.ToString(Formatting.None);
                if (text.Length > DebugProtocol.MaxResultChars)
                    return Error(id, $"结果太长（{text.Length} 字）：调小 depth / limit，或用 filter 只看需要的部分");
                return text;
            }
            catch (DebugControlException ex)
            {
                return Error(id, ex.Message);
            }
            catch (OperationCanceledException)
            {
                return Error(id, "请求被取消（VS 正在关闭解决方案或退出）");
            }
            catch (Exception ex)
            {
                // COM errors from the debugger ("operation not supported in the current state"...) are worth showing verbatim.
                return Error(id, $"VS 调试器执行 {op} 出错：{ex.Message}");
            }
        }

        private bool TokenMatches(string token)
        {
            if (token == null) return false;
            var got = Encoding.UTF8.GetBytes(token);
            int diff = got.Length ^ _token.Length;
            for (int i = 0; i < _token.Length; i++) diff |= _token[i] ^ (i < got.Length ? got[i] : 0);
            return diff == 0;
        }

        private static string Error(JToken id, string message) =>
            new JObject { ["id"] = id, ["ok"] = false, ["error"] = message }.ToString(Formatting.None);
    }
}
