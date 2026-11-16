using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;
using UEToVS.Host.Logic.Debugging;
using Xunit;

namespace UEToVS.Host.Logic.Tests
{
    public class DebugControlLogicTests
    {
        private sealed class FakeBackend : IDebugBackend
        {
            public string LastOp;
            public DebugArgs LastArgs;
            public Func<string, DebugArgs, JObject> Handler = (op, a) => new JObject { ["op"] = op };

            public Task<JObject> ExecuteAsync(string op, DebugArgs args, CancellationToken ct)
            {
                LastOp = op;
                LastArgs = args;
                return Task.FromResult(Handler(op, args));
            }
        }

        private static JObject Call(DebugDispatcher d, string json) => JObject.Parse(d.HandleLineAsync(json, CancellationToken.None).Result);

        [Fact]
        public void Dispatcher_RoutesKnownOpWithArgs()
        {
            var b = new FakeBackend();
            var d = new DebugDispatcher("secret", b);
            var r = Call(d, "{\"v\":1,\"id\":7,\"token\":\"secret\",\"op\":\"bp.add\",\"args\":{\"file\":\"A.cpp\",\"line\":12}}");
            Assert.True((bool)r["ok"]);
            Assert.Equal(7, (int)r["id"]);
            Assert.Equal("bp.add", b.LastOp);
            Assert.Equal(12, b.LastArgs.Int("line", 0, 1, 100));
        }

        [Fact]
        public void Dispatcher_RejectsBadTokenVersionOpAndJson()
        {
            var b = new FakeBackend();
            var d = new DebugDispatcher("secret", b);
            Assert.Contains("令牌", (string)Call(d, "{\"id\":1,\"token\":\"nope\",\"op\":\"status\"}")["error"]);
            Assert.Contains("令牌", (string)Call(d, "{\"id\":1,\"op\":\"status\"}")["error"]);
            Assert.Contains("协议版本", (string)Call(d, "{\"v\":2,\"token\":\"secret\",\"op\":\"status\"}")["error"]);
            Assert.Contains("不认识的操作", (string)Call(d, "{\"token\":\"secret\",\"op\":\"format_disk\"}")["error"]);
            Assert.False((bool)Call(d, "not json")["ok"]);
            Assert.Null(b.LastOp);
        }

        [Fact]
        public void Dispatcher_ReportsBackendErrorsAndOversizedResults()
        {
            var b = new FakeBackend { Handler = (op, a) => throw new DebugControlException("没有在调试") };
            var d = new DebugDispatcher("t", b);
            var r = Call(d, "{\"token\":\"t\",\"op\":\"stack\"}");
            Assert.False((bool)r["ok"]);
            Assert.Equal("没有在调试", (string)r["error"]);

            b.Handler = (op, a) => new JObject { ["big"] = new string('x', DebugProtocol.MaxResultChars + 10) };
            Assert.Contains("结果太长", (string)Call(d, "{\"token\":\"t\",\"op\":\"locals\"}")["error"]);

            b.Handler = (op, a) => throw new InvalidOperationException("COM busy");
            Assert.Contains("COM busy", (string)Call(d, "{\"token\":\"t\",\"op\":\"locals\"}")["error"]);
        }

        [Fact]
        public void NewToken_IsLongRandomHex()
        {
            string a = DebugDispatcher.NewToken(), b = DebugDispatcher.NewToken();
            Assert.Equal(64, a.Length);
            Assert.NotEqual(a, b);
            Assert.All(a, ch => Assert.True(Uri.IsHexDigit(ch)));
        }

        [Fact]
        public void Args_ValidateTypesAndRanges()
        {
            var a = new DebugArgs(JObject.Parse("{\"depth\":5,\"kind\":\"INTO\",\"s\":\" x \",\"n\":\"12\",\"b\":\"true\"}"));
            Assert.Throws<DebugControlException>(() => a.Int("depth", 1, 0, 3));
            Assert.Equal("into", a.OneOf("kind", "over", "into", "over", "out"));
            Assert.Throws<DebugControlException>(() => a.OneOf("s", "over", "into", "over"));
            Assert.Equal("x", a.Str("s"));
            Assert.Equal(12, a.IntOrNull("n"));
            Assert.True(a.Bool("b", false));
            Assert.Throws<DebugControlException>(() => a.RequireStr("missing"));
        }

        // ------------------------------------------------------------------ BreakTracker

        [Fact]
        public async Task Wait_AnswersAtOnceWhenStoppedOrIdle()
        {
            var t = new BreakTracker();
            Assert.Equal(WaitOutcome.Idle, await t.WaitAsync(1000, CancellationToken.None));
            t.OnRun();
            t.OnBreak("breakpoint");
            Assert.Equal(WaitOutcome.Break, await t.WaitAsync(1000, CancellationToken.None));
            Assert.Equal("breakpoint", t.LastReason);
            Assert.Equal(1, t.BreakCount);
        }

        [Fact]
        public async Task Wait_F5BuildsThenRunsThenBreaks()
        {
            var t = new BreakTracker();
            t.OnLaunchRequested();
            t.OnBuildBegin();
            var w = t.WaitAsync(5000, CancellationToken.None);
            t.OnBuildDone(true); // launch pending: keep waiting
            Assert.False(w.IsCompleted);
            t.OnRun();
            Assert.False(w.IsCompleted);
            t.OnBreak("exception");
            Assert.Equal(WaitOutcome.Break, await w);
        }

        [Fact]
        public async Task Wait_BuildFailureEndsPendingLaunch()
        {
            var t = new BreakTracker();
            t.OnLaunchRequested();
            t.OnBuildBegin();
            var w = t.WaitAsync(5000, CancellationToken.None);
            t.OnBuildDone(false);
            Assert.Equal(WaitOutcome.BuildFailed, await w);
            Assert.False(t.LaunchPending);
            Assert.Equal(WaitOutcome.Idle, await t.WaitAsync(100, CancellationToken.None));
        }

        [Fact]
        public async Task Wait_PlainBuildReportsDone()
        {
            var t = new BreakTracker();
            t.OnBuildBegin();
            var w = t.WaitAsync(5000, CancellationToken.None);
            t.OnBuildDone(true);
            Assert.Equal(WaitOutcome.BuildDone, await w);
        }

        [Fact]
        public async Task Wait_RestartDoesNotReportExitOfOldSession()
        {
            var t = new BreakTracker();
            t.OnRun();
            t.OnBreak("breakpoint");
            t.OnLaunchRequested(); // restart
            t.OnRun();
            var w = t.WaitAsync(5000, CancellationToken.None);
            t.OnLaunchRequested();
            t.OnDesign("stopped"); // old session ends
            Assert.False(w.IsCompleted);
            t.OnRun();
            t.OnDesign("exited"); // new session ends
            Assert.Equal(WaitOutcome.Exited, await w);
        }

        [Fact]
        public async Task Wait_TimesOutAndCleansUp()
        {
            var t = new BreakTracker();
            t.OnRun();
            Assert.Equal(WaitOutcome.Timeout, await t.WaitAsync(30, CancellationToken.None));
            Assert.Equal(0, t.WaiterCount);
        }

        [Fact]
        public async Task Stop_ClearsLaunchThatNeverStarted()
        {
            var t = new BreakTracker();
            t.OnLaunchRequested();
            var w = t.WaitAsync(5000, CancellationToken.None);
            t.OnStopRequested();
            Assert.Equal(WaitOutcome.Exited, await w);
        }

        // ------------------------------------------------------------------ values

        private sealed class Node : IValueNode
        {
            public string Name { get; set; }
            public string Type { get; set; } = "int";
            public string Value { get; set; } = "0";
            public bool IsValid { get; set; } = true;
            public List<Node> Kids = new List<Node>();
            public bool Throws;
            public IEnumerable<IValueNode> Children() => Throws ? throw new InvalidOperationException() : Kids;
        }

        [Fact]
        public void ValueTree_DepthFilterAndChildLimit()
        {
            var obj = new Node { Name = "this", Type = "AMyCharacter *", Value = "0x1 {Health=100}" };
            for (int i = 0; i < 40; i++) obj.Kids.Add(new Node { Name = "M" + i, Kids = { new Node { Name = "deep" } } });
            var w = new ValueTreeWriter(100000, maxChildren: 25);
            var arr = w.WriteAll(new IValueNode[] { obj, new Node { Name = "Other" } }, depth: 1, filter: "THIS");
            Assert.Single(arr);
            var top = (JObject)arr[0];
            Assert.Equal("AMyCharacter *", (string)top["t"]);
            Assert.Equal(25, ((JArray)top["c"]).Count);
            Assert.Equal(15, (int)top["more"]);
            Assert.Null(top["c"][0]["c"]); // depth 1: grandchildren not read
            Assert.True(w.Truncated);
        }

        [Fact]
        public void ValueTree_BudgetCutsLongValuesAndStops()
        {
            var nodes = Enumerable.Range(0, 100).Select(i => (IValueNode)new Node { Name = "v" + i, Value = new string('a', 1000) }).ToList();
            var w = new ValueTreeWriter(3000);
            var arr = w.WriteAll(nodes, 0);
            Assert.True(arr.Count < 10);
            Assert.True(w.Truncated);
            Assert.EndsWith("…", (string)arr[0]["v"]);
            Assert.True(((string)arr[0]["v"]).Length <= ValueTreeWriter.MaxValueChars + 1);
        }

        [Fact]
        public void ValueTree_InvalidAndUnexpandableValues()
        {
            var w = new ValueTreeWriter(10000);
            var bad = w.Write(new Node { Name = "p", Value = "<无法读取内存>", IsValid = false, Kids = { new Node { Name = "x" } } }, 2);
            Assert.True((bool)bad["err"]);
            Assert.Null(bad["c"]);
            var refused = w.Write(new Node { Name = "q", Throws = true }, 2);
            Assert.Null(refused["c"]);
        }

        // ------------------------------------------------------------------ helpers

        [Fact]
        public void FindConfiguration_ExactSubstringPlatformAndErrors()
        {
            var configs = new List<(string, string)>
            {
                ("Debug Editor", "Win64"), ("Development Editor", "Win64"), ("Development", "Win64"), ("Shipping", "Win64"),
                ("Development Editor", "Linux"),
            };
            Assert.Equal(1, DebugHelpers.FindConfiguration(configs, "development editor"));
            Assert.Equal(4, DebugHelpers.FindConfiguration(configs, "Development Editor|Linux"));
            Assert.Equal(3, DebugHelpers.FindConfiguration(configs, "ship"));
            var none = Assert.Throws<DebugControlException>(() => DebugHelpers.FindConfiguration(configs, "Test"));
            Assert.Contains("Development Editor|Win64", none.Message);
            Assert.Throws<DebugControlException>(() => DebugHelpers.FindConfiguration(configs, "Editor"));
        }

        [Fact]
        public void ResolveSourceFile_RelativeAgainstBases()
        {
            string sep = System.IO.Path.DirectorySeparatorChar.ToString();
            string root = sep + "work" + sep + "Game";
            string expected = System.IO.Path.GetFullPath(System.IO.Path.Combine(root, "Source", "A.cpp"));
            var exists = new HashSet<string> { expected };
            Assert.Equal(expected, DebugHelpers.ResolveSourceFile("Source/A.cpp", new[] { sep + "work", root }, exists.Contains));
            Assert.Equal(expected, DebugHelpers.ResolveSourceFile(expected, null, exists.Contains));
            Assert.Throws<DebugControlException>(() => DebugHelpers.ResolveSourceFile("Source/B.cpp", new[] { root }, exists.Contains));
        }

        [Fact]
        public void Output_CursorAndTail()
        {
            Assert.Equal(801, DebugHelpers.OutputStartLine(1000, null, 200, out bool r1));
            Assert.False(r1);
            Assert.Equal(950, DebugHelpers.OutputStartLine(1000, 950, 200, out _));
            Assert.Equal(1, DebugHelpers.OutputStartLine(10, 950, 200, out bool r2));
            Assert.True(r2);
            string tail = DebugHelpers.Tail("line1\nline2\nline3\n", 9, out int cut);
            Assert.Equal("line3\n", tail);
            Assert.Equal(12, cut);
        }

        [Fact]
        public void LaunchArgs_MapAfterProjectGameAndExtra()
        {
            const string ue = "\"$(SolutionDir)$(ProjectName).uproject\" -skipcompile";
            Assert.Equal("\"$(SolutionDir)$(ProjectName).uproject\" /Game/Maps/Test -skipcompile -game -log",
                         DebugHelpers.ComposeLaunchArgs(ue, "/Game/Maps/Test", true, " -log "));
            Assert.Equal(ue + " -game", DebugHelpers.ComposeLaunchArgs(ue + " -game", null, true, null)); // no second -game
            Assert.Equal("D:\\P\\G.uproject \"/Game/My Map\"", DebugHelpers.ComposeLaunchArgs("D:\\P\\G.uproject", "/Game/My Map", false, null));
            Assert.Equal("/Game/M -windowed", DebugHelpers.ComposeLaunchArgs("", "/Game/M", false, "-windowed"));
        }

        [Fact]
        public void ReasonText_MapsDteNames()
        {
            Assert.Equal("breakpoint", DebugHelpers.ReasonText("dbgEventReasonBreakpoint"));
            Assert.Equal("unhandledException", DebugHelpers.ReasonText("dbgEventReasonExceptionNotHandled"));
            Assert.Equal("pause", DebugHelpers.ReasonText("dbgEventReasonUserBreak"));
            Assert.Null(DebugHelpers.ReasonText(null));
        }
    }
}
