using System;
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json.Linq;

namespace UEToVS.Host.Logic.Debugging
{
    /// <summary>
    /// VS debug control protocol (docs/design/VS-调试控制.md). One JSON object per line over a named pipe:
    /// request <c>{"v":1,"id":"1","token":"…","op":"status","args":{…}}</c>,
    /// response <c>{"id":"1","ok":true,"result":{…}}</c> or <c>{"id":"1","ok":false,"error":"…"}</c>.
    /// Errors are Chinese and say what to do: they reach the agent and the user unchanged.
    /// </summary>
    public static class DebugProtocol
    {
        public const int Version = 1;

        /// <summary>Pipe name = prefix + devenv PID (one VS instance, one pipe).</summary>
        public const string PipePrefix = "UEToVS.Debug.";

        /// <summary>Hard cap of one serialized result; backends keep well below (≈4000) by paging and budgets.</summary>
        public const int MaxResultChars = 12000;

        /// <summary>Longest a wait may block (also the MCP tool's limit).</summary>
        public const int MaxWaitMs = 120000;

        // session
        public const string Status = "status", Start = "start", Stop = "stop", Restart = "restart", Attach = "attach", Build = "build",
                            Pause = "pause", Continue = "continue", Step = "step", Wait = "wait";

        /// <summary>
        /// v0.9.5：让这个 VS 换成另一个解决方案（Agent 面板「用 VS 打开这个项目」选了在已开着的 VS 里打开）。
        /// 先应答再切换，切换会关掉这个管道。
        /// </summary>
        public const string SolutionOpen = "solution.open";

        /// <summary>
        /// v0.9.13：给 MCP 的 rename_symbol 用，问这些文件在 VS 里有没有未保存的修改（有就不直接写磁盘，免得和编辑器缓冲区冲突）。
        /// 只读，Agent 面板不用它。args.files = 路径数组；result.unsaved = 其中有未保存修改的路径。
        /// </summary>
        public const string FilesUnsaved = "files.unsaved";

        // breakpoints
        public const string BpList = "bp.list", BpAdd = "bp.add", BpRemove = "bp.remove", BpEnable = "bp.enable", BpClear = "bp.clear";

        // inspection (break mode)
        public const string Stack = "stack", Threads = "threads", Locals = "locals", Eval = "eval", Output = "output", Dump = "dump";

        /// <summary>
        /// Keeps the connection open and pushes <c>{"event":"state","result":{…status…}}</c> lines: one at once, then one per
        /// debugger / build state change (UE page card; no polling). Not a request/response op, so not in <see cref="Ops"/>.
        /// </summary>
        public const string Subscribe = "subscribe";

        public static readonly IReadOnlyList<string> Ops = new[]
        {
            Status, Start, Stop, Restart, Attach, Build, Pause, Continue, Step, Wait, SolutionOpen, FilesUnsaved,
            BpList, BpAdd, BpRemove, BpEnable, BpClear,
            Stack, Threads, Locals, Eval, Output, Dump,
        };

        /// <summary>Ops that only make sense while the debuggee is stopped (break mode).</summary>
        public static readonly IReadOnlyList<string> BreakModeOps = new[] { Stack, Locals, Eval, Step };

        public static bool IsKnown(string op) => op != null && Ops.Contains(op);

        /// <summary>
        /// 说明这次调试会话由 Agent 在控制的操作：之后 VS 停下时不抢前台（FocusPolicy）。只读的 status、栈、变量不算，
        /// UE 页卡片的订阅、用户自己在看的会话不受影响。
        /// </summary>
        public static readonly IReadOnlyList<string> AgentControlOps = new[] { Start, Restart, Attach, Pause, Continue, Step, Wait, BpAdd };

        public static bool IsAgentControl(string op) => op != null && AgentControlOps.Contains(op);
    }

    /// <summary>A request the backend refuses; the message goes to the caller as is.</summary>
    public sealed class DebugControlException : Exception
    {
        public DebugControlException(string message) : base(message) { }
    }

    /// <summary>Typed, validated access to a request's "args" object.</summary>
    public sealed class DebugArgs
    {
        private readonly JObject _o;

        public DebugArgs(JObject o) => _o = o ?? new JObject();

        public JObject Raw => _o;

        public bool Has(string name) => _o.TryGetValue(name, out var t) && t.Type != JTokenType.Null;

        public string Str(string name, string def = null)
        {
            if (!_o.TryGetValue(name, out var t) || t.Type == JTokenType.Null) return def;
            if (t.Type != JTokenType.String) throw new DebugControlException($"参数 {name} 要是字符串");
            string s = ((string)t)?.Trim();
            return string.IsNullOrEmpty(s) ? def : s;
        }

        public string RequireStr(string name) => Str(name) ?? throw new DebugControlException($"缺少参数 {name}");

        public int Int(string name, int def, int min, int max)
        {
            int? v = IntOrNull(name);
            if (v == null) return def;
            if (v < min || v > max) throw new DebugControlException($"参数 {name} 要在 {min} 到 {max} 之间（收到 {v}）");
            return v.Value;
        }

        public int? IntOrNull(string name)
        {
            if (!_o.TryGetValue(name, out var t) || t.Type == JTokenType.Null) return null;
            if (t.Type == JTokenType.Integer) return (int)t;
            if (t.Type == JTokenType.Float && Math.Abs((double)t % 1) < 1e-9) return (int)(double)t;
            if (t.Type == JTokenType.String && int.TryParse((string)t, out int n)) return n;
            throw new DebugControlException($"参数 {name} 要是整数");
        }

        public bool Bool(string name, bool def)
        {
            if (!_o.TryGetValue(name, out var t) || t.Type == JTokenType.Null) return def;
            if (t.Type == JTokenType.Boolean) return (bool)t;
            if (t.Type == JTokenType.String && bool.TryParse((string)t, out bool b)) return b;
            throw new DebugControlException($"参数 {name} 要是 true 或 false");
        }

        /// <summary>One of <paramref name="allowed"/> (case-insensitive), or <paramref name="def"/> when absent.</summary>
        public string OneOf(string name, string def, params string[] allowed)
        {
            string s = Str(name);
            if (s == null) return def;
            string hit = allowed.FirstOrDefault(a => string.Equals(a, s, StringComparison.OrdinalIgnoreCase));
            return hit ?? throw new DebugControlException($"参数 {name} 只能是 {string.Join(" / ", allowed)}（收到 {s}）");
        }
    }
}
