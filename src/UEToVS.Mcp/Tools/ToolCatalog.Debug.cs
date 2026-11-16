using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using UEToVS.Mcp.Data;

namespace UEToVS.Mcp.Tools
{
    /// <summary>
    /// v0.8.0 VS debug control: three tools that forward to the pipe VS's UEToVS opens (docs/design/VS-调试控制.md).
    /// They need Visual Studio running with this solution; everything else in this server works without it.
    /// MCP arguments are snake_case like the other tools and are renamed to the protocol's camelCase here.
    /// </summary>
    public sealed partial class ToolCatalog
    {
        private DebugControlClient _debug;

        /// <summary>Tests point the tools at a fake endpoint.</summary>
        internal DebugControlClient Debug
        {
            get => _debug ??= new DebugControlClient(_index.SolutionDir);
            set => _debug = value;
        }

        private static readonly Dictionary<string, string> DebugArgNames = new Dictionary<string, string>
        {
            ["no_debug"] = "noDebug", ["wait_ms"] = "waitMs", ["timeout_ms"] = "timeoutMs", ["hit_count"] = "hitCount", ["max_chars"] = "maxChars",
        };

        private const string FrameProps =
            "\"frame\":{\"type\":\"integer\",\"minimum\":0,\"description\":\"Stack frame, 0 = top (default: the current frame).\"}," +
            "\"thread\":{\"type\":\"integer\",\"description\":\"Thread id from action=threads (default: the stopped thread).\"}";

        private void RegisterDebugTools()
        {
            Register(DebugSessionTool());
            Register(DebugBreakpointsTool());
            Register(DebugInspectTool());
        }

        private ToolDefinition DebugSessionTool()
        {
            var t = Define("debug_session", "Start / stop / step UE in the VS debugger", ToolGuides.Description("debug_session"),
                Schema(
                    "\"action\":{\"type\":\"string\",\"enum\":[\"status\",\"start\",\"stop\",\"restart\",\"attach\",\"build\",\"pause\",\"continue\",\"step\",\"wait\"],\"description\":\"What to do.\"}," +
                    "\"configuration\":{\"type\":\"string\",\"description\":\"start/build: solution configuration, e.g. 'Development Editor' or 'DebugGame Editor|Win64' (default: current).\"}," +
                    "\"project\":{\"type\":\"string\",\"description\":\"start/build: startup project name (default: current startup project).\"}," +
                    "\"no_debug\":{\"type\":\"boolean\",\"description\":\"start: Ctrl+F5, no debugger attached.\"}," +
                    "\"map\":{\"type\":\"string\",\"description\":\"start: map to open, e.g. /Game/Maps/Test (this launch only).\"}," +
                    "\"game\":{\"type\":\"boolean\",\"description\":\"start: add -game (standalone game process, this launch only).\"}," +
                    "\"args\":{\"type\":\"string\",\"description\":\"start: extra command line, e.g. '-log -windowed' (this launch only).\"}," +
                    "\"detach\":{\"type\":\"boolean\",\"description\":\"stop: only detach, UE keeps running.\"}," +
                    "\"pid\":{\"type\":\"integer\",\"description\":\"attach: process id.\"}," +
                    "\"name\":{\"type\":\"string\",\"description\":\"attach: process name part (default UnrealEditor).\"}," +
                    "\"kind\":{\"type\":\"string\",\"enum\":[\"over\",\"into\",\"out\",\"toLine\"],\"description\":\"step: over (default) / into / out / toLine (run to file+line).\"}," +
                    "\"file\":{\"type\":\"string\",\"description\":\"step toLine: source file.\"}," +
                    "\"line\":{\"type\":\"integer\",\"minimum\":1,\"description\":\"step toLine: 1-based line.\"}," +
                    "\"wait_ms\":{\"type\":\"integer\",\"minimum\":0,\"maximum\":120000,\"description\":\"pause/continue/step/build: also wait up to this long for the next stop (step default 10000, pause 5000, others 0).\"}," +
                    "\"timeout_ms\":{\"type\":\"integer\",\"minimum\":0,\"maximum\":120000,\"description\":\"wait: max wait (default 30000).\"}",
                    "action"),
                (a, ct) => ForwardDebugAsync(a, ct, a.Choice("action", null, "status", "start", "stop", "restart", "attach", "build", "pause", "continue", "step", "wait")),
                readOnly: false);
            t.Destructive = true;
            return t;
        }

        private ToolDefinition DebugBreakpointsTool() => Define("debug_breakpoints", "VS breakpoints", ToolGuides.Description("debug_breakpoints"),
            Schema(
                "\"action\":{\"type\":\"string\",\"enum\":[\"list\",\"add\",\"remove\",\"enable\",\"clear\"],\"description\":\"What to do.\"}," +
                "\"file\":{\"type\":\"string\",\"description\":\"Source file (absolute or relative to the solution / .uproject folder).\"}," +
                "\"line\":{\"type\":\"integer\",\"minimum\":1,\"description\":\"1-based line.\"}," +
                "\"function\":{\"type\":\"string\",\"description\":\"Function breakpoint, e.g. AMyCharacter::Jump.\"}," +
                "\"condition\":{\"type\":\"string\",\"description\":\"add: C++ condition, e.g. Health <= 0.\"}," +
                "\"hit_count\":{\"type\":\"integer\",\"minimum\":1,\"description\":\"add: stop on the Nth hit.\"}," +
                "\"message\":{\"type\":\"string\",\"description\":\"add: tracepoint, logs e.g. 'HP={Health}' to the Debug output and keeps running.\"}," +
                "\"data\":{\"type\":\"string\",\"description\":\"add: data breakpoint on a value, e.g. this->Health (only while stopped; stops when it changes).\"}," +
                "\"size\":{\"type\":\"integer\",\"enum\":[1,2,4,8],\"description\":\"add data: bytes watched (default 4).\"}," +
                "\"index\":{\"type\":\"integer\",\"minimum\":1,\"description\":\"remove/enable: number from list.\"}," +
                "\"indexes\":{\"type\":\"array\",\"items\":{\"type\":\"integer\"},\"description\":\"remove/enable: several numbers from list.\"}," +
                "\"enabled\":{\"type\":\"boolean\",\"description\":\"enable: true (default) or false.\"}," +
                "\"scope\":{\"type\":\"string\",\"enum\":[\"agent\",\"all\"],\"description\":\"clear: 'agent' (default, only breakpoints set through these tools) or 'all'.\"}," +
                PagingProps + "," + FrameProps,
                "action"),
            (a, ct) => ForwardDebugAsync(a, ct, "bp." + a.Choice("action", null, "list", "add", "remove", "enable", "clear")),
            readOnly: false);

        private ToolDefinition DebugInspectTool() => Define("debug_inspect", "Inspect stopped UE (stack, variables, output)", ToolGuides.Description("debug_inspect"),
            Schema(
                "\"action\":{\"type\":\"string\",\"enum\":[\"stack\",\"threads\",\"locals\",\"eval\",\"output\",\"dump\"],\"description\":\"What to read.\"}," +
                "\"expr\":{\"type\":\"string\",\"description\":\"eval: C++ expression, e.g. this->Health, GetName(), Items.Num().\"}," +
                "\"exprs\":{\"type\":\"array\",\"items\":{\"type\":\"string\"},\"maxItems\":10,\"description\":\"eval: several expressions (watch list).\"}," +
                "\"depth\":{\"type\":\"integer\",\"minimum\":0,\"maximum\":3,\"description\":\"locals/eval: member levels to expand (default 1).\"}," +
                "\"filter\":{\"type\":\"string\",\"description\":\"locals: variable name part; threads: name/location part; output: keep lines containing it.\"}," +
                "\"cursor\":{\"type\":\"integer\",\"minimum\":1,\"description\":\"output: 'next' of the previous call, returns only new lines.\"}," +
                "\"max_chars\":{\"type\":\"integer\",\"minimum\":200,\"maximum\":10000,\"description\":\"output: max characters (default 3000, newest kept).\"}," +
                "\"full\":{\"type\":\"boolean\",\"description\":\"dump: include the heap (GBs for an editor).\"}," +
                PagingProps + "," + FrameProps,
                "action"),
            (a, ct) => ForwardDebugAsync(a, ct, a.Choice("action", null, "stack", "threads", "locals", "eval", "output", "dump")),
            readOnly: false);

        private async Task<ToolOutput> ForwardDebugAsync(ToolArgs a, CancellationToken ct, string op)
        {
            var args = new JsonObject();
            foreach (var name in Get(op.StartsWith("bp.") ? "debug_breakpoints" : DebugToolOf(op)).Arguments)
            {
                if (name == "action") continue;
                var e = a.Element(name);
                if (e == null) continue;
                args[DebugArgNames.TryGetValue(name, out var mapped) ? mapped : name] = JsonNode.Parse(e.Value.GetRawText());
            }
            int waitMs = op == "wait" ? a.Int("timeout_ms", 30000, 0, 120000) : a.Int("wait_ms", op == "step" ? 10000 : op == "pause" ? 5000 : 0, 0, 120000);
            var result = await Debug.CallAsync(op, args, waitMs + 15000, ct).ConfigureAwait(false);
            return new ToolOutput { Payload = result, ResultCount = 1, Source = "vs" };
        }

        private static string DebugToolOf(string op) =>
            new[] { "stack", "threads", "locals", "eval", "output", "dump" }.Contains(op) ? "debug_inspect" : "debug_session";
    }
}
