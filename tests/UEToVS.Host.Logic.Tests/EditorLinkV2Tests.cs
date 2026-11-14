using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;
using UEToVS.Contracts;
using UEToVS.Host.Logic.Commands;
using UEToVS.Host.Logic.Link;
using UEToVS.Host.Logic.Settings;
using Xunit;

namespace UEToVS.Host.Logic.Tests
{
    public class LinkProtocolTests
    {
        [Fact]
        public void PingReplyWithoutProtocolIsProtocolOne()
        {
            var old = LinkProtocol.ParseCapabilities(JObject.Parse("{\"ok\":true,\"project\":\"P\",\"engine\":\"5.3.2\"}"));
            Assert.Equal(1, old.Protocol);
            Assert.False(old.Supports(LinkProtocol.FeatureLog));
            Assert.Equal("P", old.Project);

            var v2 = LinkProtocol.ParseCapabilities(JObject.Parse("{\"ok\":true,\"protocol\":2,\"plugin\":\"0.2.0\",\"features\":[\"log\",\"tests\",\"liveCoding\"]}"));
            Assert.Equal(2, v2.Protocol);
            Assert.True(v2.Supports(LinkProtocol.FeatureTests));
            Assert.Equal("0.2.0", v2.PluginVersion);
        }

        [Fact]
        public void CommandsCarryTheProtocolVersion()
        {
            Assert.Equal(2, (int)LinkProtocol.Ping()["protocol"]);
            var sub = LinkProtocol.Subscribe(new[] { "log", null, "tests" });
            Assert.Equal("subscribe", (string)sub["cmd"]);
            Assert.Equal(new[] { "log", "tests" }, sub["topics"].Select(t => (string)t));
            Assert.Equal("Project.A", (string)LinkProtocol.RunTests("r1", "Project.A")["filter"]);
            Assert.True(LinkProtocol.IsUnknownCommand(false, "unknown command: subscribe"));
            Assert.False(LinkProtocol.IsUnknownCommand(true, "unknown command: subscribe"));
        }

        [Fact]
        public void ParsesEveryEventKindAndToleratesGarbage()
        {
            var log = (LogBatchEvent)LinkProtocol.ParseEvent("{\"event\":\"log\",\"dropped\":3,\"lines\":[{\"t\":1.5,\"c\":\"LogTemp\",\"v\":3,\"m\":\"hi\"},{\"c\":\"X\"},{\"m\":\"bad verbosity\",\"v\":99}]}", out _);
            Assert.Equal(3, log.Dropped);
            Assert.Equal(2, log.Lines.Count);
            Assert.Equal(UeLogVerbosity.Warning, log.Lines[0].Verbosity);
            Assert.Equal(1.5, log.Lines[0].EditorTime);
            Assert.Equal(UeLogVerbosity.Log, log.Lines[1].Verbosity);

            var lc = (LiveCodingEvent)LinkProtocol.ParseEvent("{\"event\":\"liveCoding\",\"phase\":\"finished\",\"result\":\"failed\",\"lines\":[\"a\",\"b\"],\"durationMs\":1200}", out _);
            Assert.Equal(LiveCodingPhase.Finished, lc.Phase);
            Assert.Equal("failed", lc.Result);
            Assert.Equal(2, lc.Lines.Count);

            var t = (TestEvent)LinkProtocol.ParseEvent("{\"event\":\"tests\",\"phase\":\"testFinished\",\"id\":\"r1\",\"name\":\"P.A\",\"result\":\"failed\",\"errors\":[\"boom\"],\"logs\":[\"x\"]}", out _);
            Assert.Equal(TestEventPhase.TestFinished, t.Phase);
            Assert.Equal(AutomationTestOutcome.Failed, t.Result);
            Assert.Equal("boom", Assert.Single(t.Errors));

            var reply = (SubscribeReplyEvent)LinkProtocol.ParseEvent("{\"ok\":false,\"message\":\"unknown command: subscribe\"}", out _);
            Assert.False(reply.Ok);

            Assert.Null(LinkProtocol.ParseEvent("not json", out string err));
            Assert.StartsWith("json", err);
            Assert.Null(LinkProtocol.ParseEvent("{\"event\":\"future\"}", out err));
            Assert.Null(LinkProtocol.ParseEvent("", out _));
        }
    }

    public class UeLogModelTests
    {
        private static UeLogRecord R(string cat, UeLogVerbosity v, string m) => new UeLogRecord(0, cat, v, m);

        [Fact]
        public void RingBufferKeepsTheNewestLinesAndCountsCategories()
        {
            var b = new UeLogBuffer(UeLogBuffer.MinCapacity);
            for (int i = 0; i < UeLogBuffer.MinCapacity + 10; i++)
                b.Add(R(i % 2 == 0 ? "LogA" : "LogB", i < 5 ? UeLogVerbosity.Error : UeLogVerbosity.Log, "line " + i), DateTime.Now);
            Assert.Equal(UeLogBuffer.MinCapacity, b.Count);
            Assert.Equal(10, b.Evicted);
            Assert.Equal("line 10", b[0].Message);
            Assert.Equal(0, b.ErrorCount); // the five errors were the oldest lines
            Assert.Equal(UeLogBuffer.MinCapacity, b.CategoryCounts.Values.Sum());
            Assert.Null(b.BySeq(1));
            Assert.Equal("line 10", b.BySeq(b.FirstSeq).Message);
            Assert.Same(b[0].Category, b[2].Category); // interned

            b.SetCapacity(1500);
            Assert.Equal(UeLogBuffer.MinCapacity, b.Count);
            b.SetCapacity(UeLogBuffer.MinCapacity);
            Assert.Equal(UeLogBuffer.MinCapacity, b.Count);
            Assert.Equal(UeLogBuffer.DefaultCapacity, UeLogBuffer.ClampCapacity(0));
            Assert.Equal(UeLogBuffer.MaxCapacity, UeLogBuffer.ClampCapacity(int.MaxValue));
        }

        [Fact]
        public void FilterCombinesLevelsCategoryAndSearchWords()
        {
            var b = new UeLogBuffer();
            var err = b.Add(R("LogNet", UeLogVerbosity.Error, "Connection lost to Server"), DateTime.Now);
            var warn = b.Add(R("LogNet", UeLogVerbosity.Warning, "slow"), DateTime.Now);
            var vv = b.Add(R("LogAI", UeLogVerbosity.VeryVerbose, "tick"), DateTime.Now);
            var f = new UeLogFilter();
            Assert.True(f.IsEmpty);
            Assert.True(f.Matches(vv));
            f.SetLevelShown(UeLogLevel.Verbose, false);
            Assert.False(f.Matches(vv));
            f.Category = "LogNet";
            f.Search = "server  LOST";
            Assert.True(f.Matches(err));
            Assert.False(f.Matches(warn));
            f.Search = "lognet";
            Assert.True(f.Matches(warn)); // search also looks at the category
            Assert.False(f.IsEmpty);
            Assert.Equal(UeLogLevel.Error, UeLogVerbosities.LevelOf(UeLogVerbosity.Fatal));
            Assert.Equal(UeLogVerbosity.Warning, UeLogVerbosities.Parse("Warning"));
            Assert.Null(UeLogVerbosities.Parse("Verbosity"));
        }

        [Fact]
        public void ViewAppendsMatchingLinesDropsEvictedOnesAndRebuilds()
        {
            var b = new UeLogBuffer(UeLogBuffer.MinCapacity);
            var view = new UeLogView(b);
            view.Filter.Search = "keep";
            for (int i = 0; i < 10; i++) b.Add(R("LogA", UeLogVerbosity.Log, (i % 2 == 0 ? "keep " : "skip ") + i), DateTime.Now);
            Assert.Equal((5, 0), view.Sync());
            Assert.Equal("keep 0", view[0].Message);
            for (int i = 0; i < UeLogBuffer.MinCapacity; i++) b.Add(R("LogA", UeLogVerbosity.Log, "other " + i), DateTime.Now);
            Assert.Equal(0, view.Pending == 0 ? -1 : 0);
            var r = view.Sync();
            Assert.Equal(0, r.Appended);
            Assert.Equal(5, r.Removed);
            Assert.Empty(view);
            view.Filter.Search = "other 99";
            view.Rebuild();
Assert.Equal(19, view.Count); // 99, 199 … 999 and 990 … 998
            Assert.All(view, l => Assert.Contains("99", l.Message));
            view.Clear();
            Assert.Empty(view);
            Assert.Equal(0, view.Pending);
        }
    }

    public class UeLogSourceMatcherTests
    {
        private static LogSiteInfo Site(string format, string verbosity = "Log", string cat = "LogMy", string macro = "UE_LOG", int line = 1, string file = "A.cpp") =>
            new LogSiteInfo { Kind = LogSiteKind.Usage, Category = cat, Verbosity = verbosity, Format = format, Macro = macro, Location = new SourceLocation { FilePath = file, Line = line } };

        [Theory]
        [InlineData("Loaded %s in %.2f ms", "Loaded /Game/Map in 12.50 ms", true)]
        [InlineData("Loaded %s in %.2f ms", "Loaded /Game/Map in twelve ms", false)]
        [InlineData("Count=%d", "Count=-42", true)]
        [InlineData("Count=%d", "Count=4.2", false)]
        [InlineData("Id %u / %llu / %lld / %I64d", "Id 7 / 8 / -9 / 10", true)]
        [InlineData("Hex %x %#X %p", "Hex ff 0XAB 0x0000012345", true)]
        [InlineData("100%% done", "100% done", true)]
        [InlineData("Padded [%5d] [%-4d]", "Padded [   42] [7   ]", true)]
        [InlineData("Char %c!", "Char x!", true)]
        [InlineData("Float %f %e %g", "Float 1.000000 1.5e+10 nan", true)]
        [InlineData("Wide %ls %hs", "Wide a b", true)]
        [InlineData("Regex chars (a+b)*[c]?", "Regex chars (a+b)*[c]?", true)]
        [InlineData("Line1\nLine2 %s", "Line1\nLine2 tail", true)]
        [InlineData("Empty arg [%s]", "Empty arg []", true)]
        public void PrintfFormatsBecomeAnchoredPatterns(string format, string message, bool expected)
        {
            Assert.Equal(expected, UeLogFormat.Compile(format, false).IsMatch(message));
        }

        [Fact]
        public void StructuredFormatsUseNamedPlaceholders()
        {
            var p = UeLogFormat.Compile("Loading '{Name}' failed with {Error} {{literal}}", true);
            Assert.True(p.IsMatch("Loading 'Foo' failed with 3 {literal}"));
            Assert.False(p.IsMatch("Loading 'Foo' failed"));
            Assert.Equal(2, p.Placeholders);
        }

        [Fact]
        public void LiteralCharsMeasureSpecificity()
        {
            Assert.Equal(0, UeLogFormat.Compile("%s", false).LiteralChars);
            Assert.Equal(10, UeLogFormat.Compile("Hello %s world", false).LiteralChars);
            Assert.Null(UeLogFormat.Compile(null, false).Regex);
        }

        [Fact]
        public void PicksTheMostSpecificExactMatch()
        {
            var m = new UeLogSourceMatcher();
            var sites = new List<LogSiteInfo>
            {
                Site("%s", line: 1),
                Site("Spawned %s", line: 2),
                Site("Spawned %s at %s", line: 3),
                Site("Spawned %s at %s", verbosity: "Warning", line: 4),
                Site("Spawned %s at %s", cat: "LogOther", line: 5),
                Site(null, line: 6),
                new LogSiteInfo { Kind = LogSiteKind.Declaration, Category = "LogMy", Location = new SourceLocation { FilePath = "A.h" } },
            };
            var r = m.Find(sites, "LogMy", UeLogVerbosity.Log, "Spawned BP_Hero at X=1 Y=2\r\n");
            Assert.True(r.IsExact);
            Assert.Equal(3, Assert.Single(r.Sites).Location.Line);
            Assert.Equal(5, r.Candidates);
            Assert.Equal(3, r.Matched);

            var warn = m.Find(sites, "LogMy", UeLogVerbosity.Warning, "Spawned BP_Hero at X=1");
            Assert.Equal(4, Assert.Single(warn.Sites).Location.Line);

            var ambiguous = m.Find(new[] { Site("%s"), Site("%s", line: 9) }, "LogMy", UeLogVerbosity.Log, "anything");
            Assert.Equal("ambiguousFormat", ambiguous.Outcome);
            Assert.False(ambiguous.IsExact);

            Assert.Equal("noSites", m.Find(sites, "LogNone", UeLogVerbosity.Log, "x").Outcome);
            Assert.Equal("noMatch", m.Find(sites, "LogOther", UeLogVerbosity.Log, "nothing like it").Outcome);

            var tie = m.Find(new[] { Site("Saved %d", file: "A.cpp"), Site("Saved %d", file: "B.cpp") }, "LogMy", UeLogVerbosity.Log, "Saved 3");
            Assert.True(tie.IsExact);
            Assert.Equal(2, tie.Sites.Count);

            var verbVar = m.Find(new[] { Site("Dyn %d", verbosity: "InVerbosity") }, "LogMy", UeLogVerbosity.Error, "Dyn 1");
            Assert.True(verbVar.IsExact);
        }

        [Fact]
        public void LogCategoryCandidatesAndTitles()
        {
            Assert.True(LogCategoryText.IsCandidate("LogMyGame", null));
            Assert.False(LogCategoryText.IsCandidate("Logger", null));
            Assert.True(LogCategoryText.IsCandidate("MyCategory", "    UE_LOG(MyCategory, Log, TEXT(\"x\"));"));
            Assert.False(LogCategoryText.IsCandidate("Warning", "    UE_LOG(MyCategory, Warning, TEXT(\"x\"));"));
            Assert.False(LogCategoryText.IsCandidate("Foo", "int Foo = 1;"));
            Assert.Equal("UE_LOG(LogMy, Warning, \"a\\nb\")", LogCategoryText.Title(Site("a\nb", "Warning")));
            Assert.Equal("DEFINE_LOG_CATEGORY(LogMy)", LogCategoryText.Title(new LogSiteInfo { Kind = LogSiteKind.Definition, Macro = "DEFINE_LOG_CATEGORY", Category = "LogMy" }));
            Assert.True(LogCategoryText.KindRank(LogSiteKind.Declaration) < LogCategoryText.KindRank(LogSiteKind.Usage));
        }
    }

    public class LiveCodingOutputTests
    {
        [Theory]
        [InlineData(@"C:\Proj\Source\Foo.cpp(12): error C2065: 'x': undeclared identifier", @"C:\Proj\Source\Foo.cpp", 12, 0, MsvcSeverity.Error, "C2065")]
        [InlineData(@"  C:\Proj\Source\Foo.h(3,15): warning C4996: 'Old': deprecated", @"C:\Proj\Source\Foo.h", 3, 15, MsvcSeverity.Warning, "C4996")]
        [InlineData(@"[2024.05.01-10.00.00:123][  0]LogLiveCoding: Error: D:\My Game\Source\A.cpp(7): error C2143: syntax error: missing ';'", @"D:\My Game\Source\A.cpp", 7, 0, MsvcSeverity.Error, "C2143")]
        [InlineData(@"1>C:\P\B.cpp(1): fatal error C1083: Cannot open include file: 'X.h'", @"C:\P\B.cpp", 1, 0, MsvcSeverity.Error, "C1083")]
        [InlineData(@"Module.obj : error LNK2019: unresolved external symbol foo", "Module.obj", 0, 0, MsvcSeverity.Error, "LNK2019")]
        [InlineData(@"C:\P\C.cpp(9): note: see declaration of 'Bar'", @"C:\P\C.cpp", 9, 0, MsvcSeverity.Note, null)]
        public void ParsesMsvcLines(string text, string path, int line, int col, MsvcSeverity sev, string code)
        {
            Assert.True(MsvcErrorLine.TryParse(text, out var d), text);
            Assert.Equal(path, d.FilePath);
            Assert.Equal(line, d.Line);
            Assert.Equal(col, d.Column);
            Assert.Equal(sev, d.Severity);
            Assert.Equal(code, d.Code);
        }

        [Theory]
        [InlineData("Starting Live Coding compile.")]
        [InlineData("")]
        [InlineData("LogLiveCoding: Display: Live coding succeeded")]
        public void IgnoresOtherLines(string text) => Assert.False(MsvcErrorLine.TryParse(text, out _));

        private sealed class RecordingSink : IBuildOutputSink
        {
            public readonly List<string> Calls = new List<string>();
            public void BuildStarted(string source, string projectRoot) => Calls.Add("start " + source + " " + projectRoot);
            public void OutputLine(string source, string line) => Calls.Add("line " + line);
            public void BuildFinished(string source, bool? succeeded, string result) => Calls.Add("end " + (succeeded?.ToString() ?? "null") + " " + result);
        }

        private sealed class ThrowingSink : IBuildOutputSink
        {
            public void BuildStarted(string source, string projectRoot) => throw new InvalidOperationException();
            public void OutputLine(string source, string line) => throw new InvalidOperationException();
            public void BuildFinished(string source, bool? succeeded, string result) => throw new InvalidOperationException();
        }

        private static LiveCodingEvent E(LiveCodingPhase phase, string result = null, params string[] lines)
        {
            var e = new LiveCodingEvent { Phase = phase, Result = result };
            e.Lines.AddRange(lines);
            return e;
        }

        [Fact]
        public void CollectorForwardsInOrderAndSummarizesErrors()
        {
            var c = new LiveCodingOutputCollector(() => @"C:\Proj");
            var sink = new RecordingSink();
            c.AddSink(new ThrowingSink());
            c.AddSink(sink);
            LiveCodingBuild done = null;
            c.BuildCompleted += b => done = b;
            c.OnEvent(E(LiveCodingPhase.Started));
            c.OnEvent(E(LiveCodingPhase.Output, null, @"C:\Proj\A.cpp(3): error C2065: 'y': undeclared identifier", "Compiling A.cpp"));
            Assert.True(c.IsBuilding);
            c.OnEvent(E(LiveCodingPhase.Finished, "success"));
            Assert.Equal(new[] { @"start liveCoding C:\Proj", @"line C:\Proj\A.cpp(3): error C2065: 'y': undeclared identifier", "line Compiling A.cpp", "end False success" }, sink.Calls);
            Assert.NotNull(done);
            Assert.False(done.Succeeded); // errors in the output win over "success"
            Assert.Single(done.Errors);
            Assert.False(c.IsBuilding);
        }

        [Fact]
        public void CollectorRecoversFromLostStartAndFinish()
        {
            var c = new LiveCodingOutputCollector();
            var sink = new RecordingSink();
            c.AddSink(sink);
            c.OnEvent(E(LiveCodingPhase.Output, null, "a"));      // no "started"
            c.OnEvent(E(LiveCodingPhase.Started, null, "b"));     // previous build never finished
            c.OnDisconnected();
            Assert.Equal(new[] { "start liveCoding ", "line a", "end null unknown", "start liveCoding ", "line b", "end null disconnected" }, sink.Calls);
            Assert.Equal(true, LiveCodingOutputCollector.SucceededOf("noChanges"));
            Assert.Equal(false, LiveCodingOutputCollector.SucceededOf("cancelled"));
            Assert.Null(LiveCodingOutputCollector.SucceededOf("weird"));
        }
    }

    public class AutomationTestTreeTests
    {
        private static AutomationTestInfo T(string name, string file = "T.cpp", int line = 1) =>
            new AutomationTestInfo { Name = name, ClassName = "F" + name.Replace(".", ""), Location = new SourceLocation { FilePath = file, Line = line } };

        [Fact]
        public void BuildsADotPathTree()
        {
            var tree = AutomationTestTree.Build(new[] { T("Project.B.Two"), T("Project.A.One"), T("Project.A.One"), T("Other"), T(" "), null });
            Assert.Equal(3, tree.TestCount);
            Assert.Equal(1, tree.Duplicates);
            Assert.Equal(new[] { "Other", "Project" }, tree.Roots.Select(r => r.Segment).OrderBy(s => s));
            var a = tree.Find("Project.A");
            Assert.False(a.IsTest);
            Assert.Equal("Project.A.One", a.Children[0].FullPath);
            Assert.Equal(2, tree.Find("Project").CountTests());
        }

        [Fact]
        public void AppliesRunEventsAndSpecChildren()
        {
            var tree = AutomationTestTree.Build(new[] { T("Project.Spec", "S.cpp", 10), T("Project.Simple") });
            Assert.Equal(2, tree.MarkQueued(null));
            tree.Apply(new TestEvent { Phase = TestEventPhase.TestStarted, Name = "Project.Simple" });
            Assert.Equal(AutomationNodeState.Running, tree.Find("Project.Simple").State);
            var fin = new TestEvent { Phase = TestEventPhase.TestFinished, Name = "Project.Simple", Result = AutomationTestOutcome.Failed, DurationMs = 12 };
            fin.Errors.Add("Expected 1 but was 2");
            tree.Apply(fin);
            var child = tree.Apply(new TestEvent { Phase = TestEventPhase.TestFinished, Name = "Project.Spec.Describe it works", Result = AutomationTestOutcome.Passed });
            Assert.True(child.ReportedOnly);
            Assert.Equal("Project.Spec", child.Parent.FullPath);
            Assert.Equal(10, child.SourceLocation.Line); // jumps to its spec
            Assert.Equal(AutomationNodeState.Failed, tree.Find("Project").AggregateState());
            Assert.Equal(1, tree.FinishRun()); // Project.Spec itself never reported
            var s = tree.Summary();
            Assert.Equal((3, 1, 1, 0), s);
            Assert.Equal("Expected 1 but was 2", tree.Find("Project.Simple").Errors.Single());
        }

        [Fact]
        public void SearchKeepsAncestorsAndDescendants()
        {
            var tree = AutomationTestTree.Build(new[] { T("Project.Net.Replication"), T("Project.UI.Menu") });
            var keep = tree.Matching("net");
            Assert.Contains(tree.Find("Project"), keep);
            Assert.Contains(tree.Find("Project.Net.Replication"), keep);
            Assert.DoesNotContain(tree.Find("Project.UI.Menu"), keep);
            Assert.Equal(5, tree.Matching("").Count);
        }
    }

    public class EditorLinkSubscriptionTests
    {
        [Fact]
        public async Task SubscribesReceivesEventsAndChangesTopics()
        {
            var listener = new TcpListener(IPAddress.Loopback, 0);
            listener.Start();
            int port = ((IPEndPoint)listener.LocalEndpoint).Port;
            var received = new List<string>();
            var server = Task.Run(async () =>
            {
                using (var client = await listener.AcceptTcpClientAsync())
                using (var s = client.GetStream())
                using (var r = new StreamReader(s, Encoding.UTF8))
                using (var w = new StreamWriter(s, new UTF8Encoding(false)) { NewLine = "\n", AutoFlush = true })
                {
                    received.Add(await r.ReadLineAsync());
                    await w.WriteLineAsync("{\"ok\":true,\"protocol\":2}");
                    received.Add(await r.ReadLineAsync()); // re-subscribe after connect
                    await w.WriteLineAsync("garbage");
                    await w.WriteLineAsync("{\"event\":\"log\",\"lines\":[{\"c\":\"LogTemp\",\"v\":5,\"m\":\"hello\"}]}");
                    received.Add(await r.ReadLineAsync()); // topic change
                    await w.WriteLineAsync("{\"event\":\"liveCoding\",\"phase\":\"started\"}");
                    await r.ReadLineAsync(); // wait until the client goes away
                }
            });
            try
            {
                using (var sub = new EditorLinkSubscription(() => port) { MinRetryDelay = TimeSpan.FromMilliseconds(50) })
                {
                    var events = new List<LinkEvent>();
                    var got2 = new TaskCompletionSource<bool>();
                    sub.EventReceived += e =>
                    {
                        lock (events)
                        {
                            events.Add(e);
                            if (events.Count == 1) sub.SetTopic(LinkProtocol.TopicLiveCoding, true);
                            if (events.Count == 2) got2.TrySetResult(true);
                        }
                    };
                    sub.SetTopic(LinkProtocol.TopicLog, true);
                    Assert.True(await sub.WaitConnectedAsync(TimeSpan.FromSeconds(10), CancellationToken.None));
                    Assert.True(await Task.WhenAny(got2.Task, Task.Delay(10000)) == got2.Task);
                    Assert.IsType<LogBatchEvent>(events[0]);
                    Assert.IsType<LiveCodingEvent>(events[1]);
                    Assert.Contains("\"topics\":[\"log\"]", received[0]);
                    Assert.Contains("\"cmd\":\"subscribe\"", received[1]);
                    Assert.Contains("liveCoding", received[2]);
                    sub.SetTopic(LinkProtocol.TopicLog, false);
                    sub.SetTopic(LinkProtocol.TopicLiveCoding, false);
                    await Task.WhenAny(server, Task.Delay(10000));
                    Assert.True(server.IsCompleted);
                }
            }
            finally
            {
                listener.Stop();
            }
        }

        [Fact]
        public async Task ProtocolOnePluginIsDetectedAndNotRetried()
        {
            var listener = new TcpListener(IPAddress.Loopback, 0);
            listener.Start();
            int port = ((IPEndPoint)listener.LocalEndpoint).Port;
            int connections = 0;
            var server = Task.Run(async () =>
            {
                while (true)
                {
                    TcpClient client;
                    try
                    {
                        client = await listener.AcceptTcpClientAsync();
                    }
                    catch (Exception)
                    {
                        return;
                    }
                    Interlocked.Increment(ref connections);
                    using (client)
                    using (var s = client.GetStream())
                    using (var r = new StreamReader(s, Encoding.UTF8))
                    using (var w = new StreamWriter(s, new UTF8Encoding(false)) { NewLine = "\n", AutoFlush = true })
                    {
                        await r.ReadLineAsync();
                        await w.WriteLineAsync("{\"ok\":false,\"message\":\"unknown command: subscribe\"}");
                    }
                }
            });
            try
            {
                using (var sub = new EditorLinkSubscription(() => port) { MinRetryDelay = TimeSpan.FromMilliseconds(20) })
                {
                    sub.SetTopic(LinkProtocol.TopicTests, true);
                    for (int i = 0; i < 200 && sub.State != LinkSubscriptionState.PluginTooOld; i++) await Task.Delay(25);
                    Assert.Equal(LinkSubscriptionState.PluginTooOld, sub.State);
                    await Task.Delay(200);
                    Assert.Equal(1, Volatile.Read(ref connections));
                    Assert.False(await sub.WaitConnectedAsync(TimeSpan.FromMilliseconds(100), CancellationToken.None));
                }
            }
            finally
            {
                listener.Stop();
            }
        }

        [Fact]
        public async Task WaitsWhileTheEditorIsDown()
        {
            using (var sub = new EditorLinkSubscription(() => 0) { MinRetryDelay = TimeSpan.FromMilliseconds(20) })
            {
                Assert.Equal(LinkSubscriptionState.Idle, sub.State);
                sub.SetTopic(LinkProtocol.TopicLog, true);
                for (int i = 0; i < 100 && sub.State != LinkSubscriptionState.Waiting; i++) await Task.Delay(10);
                Assert.Equal(LinkSubscriptionState.Waiting, sub.State);
                sub.SetTopic(LinkProtocol.TopicLog, false);
                for (int i = 0; i < 100 && sub.State != LinkSubscriptionState.Idle; i++) await Task.Delay(10);
                Assert.Equal(LinkSubscriptionState.Idle, sub.State);
            }
        }
    }

    public class LinkPluginInstallerTests
    {
        private sealed class Temp : IDisposable
        {
            public readonly string Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "uetovs-link-" + Guid.NewGuid().ToString("N"));
            public Temp() => Directory.CreateDirectory(Path);
            public string File(string rel, string text)
            {
                string p = System.IO.Path.Combine(Path, rel.Replace('/', System.IO.Path.DirectorySeparatorChar));
                Directory.CreateDirectory(System.IO.Path.GetDirectoryName(p));
                System.IO.File.WriteAllText(p, text);
                return p;
            }
            public void Dispose()
            {
                try { Directory.Delete(Path, true); } catch (IOException) { }
            }
        }

        private static string UPlugin(int v, string name) => "{\n\t\"FileVersion\": 3,\n\t\"Version\": " + v + ",\n\t\"VersionName\": \"" + name + "\"\n}\n";

        [Theory]
        [InlineData("0.1.0", 1, "0.2.0", 2, -1)]
        [InlineData("0.10.0", 1, "0.9.0", 9, 1)]
        [InlineData("1.0", 1, "1.0.0", 1, 0)]
        [InlineData(null, 3, null, 2, 1)]
        [InlineData("beta", 1, "0.2.0", 2, -1)]
        [InlineData("2.0.0-beta.1", 2, "2.0.0", 2, -1)]
        [InlineData("2.0.0-beta.2", 2, "2.0.0-beta.10", 2, -1)]
        [InlineData("2.1.0-beta.1", 2, "2.0.0", 2, 1)]
        [InlineData("2.0.0-alpha", 2, "2.0.0-beta", 2, -1)]
        [InlineData("2.0.0-beta", 2, "2.0.0-beta.1", 2, -1)]
        public void ComparesVersions(string a, int va, string b, int vb, int sign) =>
            Assert.Equal(sign, Math.Sign(LinkPluginInstaller.CompareVersions(a, va, b, vb)));

        [Fact]
        public void FindsInstallationsAndReportsUpdates()
        {
            using (var t = new Temp())
            {
                string bundled = System.IO.Path.GetDirectoryName(t.File("bundle/UELink/UELink.uplugin", UPlugin(2, "0.2.0")));
                string project = System.IO.Path.Combine(t.Path, "Proj");
                string engine = System.IO.Path.Combine(t.Path, "Engine");
                Directory.CreateDirectory(project);
                var none = LinkPluginInstaller.GetStatus(project, engine, bundled);
                Assert.Equal(LinkPluginStatusKind.NotInstalled, none.Kind);
                Assert.True(none.CanInstall);
                Assert.Equal(LinkPluginStatusKind.NoProject, LinkPluginInstaller.GetStatus(null, engine, bundled).Kind);

                t.File("Engine/Plugins/Marketplace/UELinkFab/UELink.uplugin", UPlugin(1, "0.1.0"));
                var eng = LinkPluginInstaller.GetStatus(project, engine, bundled);
                Assert.Equal(LinkPluginLocation.EngineMarketplace, eng.Installed.Location);
                Assert.Equal(LinkPluginStatusKind.UpdateAvailable, eng.Kind);

                t.File("Proj/Plugins/Tools/UELink/UELink.uplugin", UPlugin(2, "0.2.0"));
                var proj = LinkPluginInstaller.GetStatus(project, engine, bundled);
                Assert.Equal(LinkPluginLocation.Project, proj.Installed.Location); // project wins
                Assert.Equal(LinkPluginStatusKind.UpToDate, proj.Kind);
                Assert.Equal(2, proj.All.Count);
                Assert.False(proj.CanInstall);
                Assert.True(proj.CanUninstall);
            }
        }

        [Fact]
        public void InstallCopiesSourcesAndKeepsBinaries()
        {
            using (var t = new Temp())
            {
                t.File("bundle/UELink.uplugin", UPlugin(2, "0.2.0"));
                t.File("bundle/Source/UELink/Private/A.cpp", "// a");
                t.File("bundle/Binaries/Win64/stale.dll", "never shipped");
                string bundled = System.IO.Path.Combine(t.Path, "bundle");
                string target = LinkPluginInstaller.ProjectTarget(System.IO.Path.Combine(t.Path, "Proj"));
                t.File("Proj/Plugins/UELink/Binaries/Win64/UnrealEditor-UELink.dll", "old");
                t.File("Proj/Plugins/UELink/Intermediate/x.obj", "old");
                t.File("Proj/Plugins/UELink/Source/UELink/Private/Removed.cpp", "// gone in the new version");

                var r = LinkPluginInstaller.Install(bundled, target);
                Assert.True(r.Ok, r.Detail);
                Assert.Equal(2, r.Files);
                Assert.True(File.Exists(System.IO.Path.Combine(target, "Source", "UELink", "Private", "A.cpp")));
                Assert.False(File.Exists(System.IO.Path.Combine(target, "Source", "UELink", "Private", "Removed.cpp")));
                // UELink's install contract: Binaries / Intermediate stay, UBT rebuilds them by timestamp.
                Assert.True(Directory.Exists(System.IO.Path.Combine(target, "Binaries")));
                Assert.True(Directory.Exists(System.IO.Path.Combine(target, "Intermediate")));
                Assert.False(File.Exists(System.IO.Path.Combine(target, "Binaries", "Win64", "stale.dll")));
                var record = LinkPluginInstaller.ReadInstallRecord(target);
                Assert.Equal("uetovs", record["by"].ToString());
                Assert.Equal("0.2.0", record["version"].ToString());
                Assert.Equal("files", record["mode"].ToString());
                Assert.Equal("missingSource", LinkPluginInstaller.Install(System.IO.Path.Combine(t.Path, "nope"), target).Code);
                Assert.Equal("noProject", LinkPluginInstaller.Install(bundled, null).Code);
            }
        }

        [Fact]
        public void UninstallDeletesTheFolderAndTheUProjectEntry()
        {
            using (var t = new Temp())
            {
                string dir = System.IO.Path.GetDirectoryName(t.File("Proj/Plugins/UELink/UELink.uplugin", UPlugin(1, "0.1.0")));
                t.File("Proj/Plugins/UELink/Binaries/Win64/UnrealEditor-UELink.dll", "dll");
                string uproject = t.File("Proj/Proj.uproject",
                    "{\r\n\t\"FileVersion\": 3,\r\n\t\"Plugins\": [\r\n\t\t{\r\n\t\t\t\"Name\": \"ModelingToolsEditorMode\",\r\n\t\t\t\"Enabled\": true\r\n\t\t},\r\n\t\t{\r\n\t\t\t\"Name\": \"UELink\",\r\n\t\t\t\"Enabled\": true\r\n\t\t}\r\n\t]\r\n}\r\n");
                var r = LinkPluginInstaller.Uninstall(dir, uproject);
                Assert.True(r.Ok, r.Detail);
                Assert.True(r.UProjectChanged);
                Assert.False(Directory.Exists(dir));
                string text = File.ReadAllText(uproject);
                Assert.DoesNotContain("UELink", text);
                Assert.Contains("ModelingToolsEditorMode", text);
                Assert.Contains("\r\n\t\"Plugins\"", text);
                Assert.Equal("notInstalled", LinkPluginInstaller.Uninstall(dir, uproject).Code);
            }
        }

        [Fact]
        public void UninstallRefusesWhileAFileIsInUse()
        {
            using (var t = new Temp())
            {
                string dir = System.IO.Path.GetDirectoryName(t.File("P/Plugins/UELink/UELink.uplugin", UPlugin(1, "0.1.0")));
                string dll = t.File("P/Plugins/UELink/Binaries/Win64/UnrealEditor-UELink.dll", "dll");
                using (new FileStream(dll, FileMode.Open, FileAccess.Read, FileShare.Read))
                {
                    if (LinkPluginInstaller.FindLockedFile(dir) == null) return; // this OS does not enforce share modes
                    var r = LinkPluginInstaller.Uninstall(dir, null);
                    Assert.Equal("locked", r.Code);
                    Assert.True(File.Exists(dll));
                    Assert.True(File.Exists(System.IO.Path.Combine(dir, "UELink.uplugin")));
                }
            }
        }

        [Fact]
        public void RemovePluginEntryLeavesOtherProjectsAlone()
        {
            Assert.Null(LinkPluginInstaller.RemovePluginEntry("{\"Plugins\":[{\"Name\":\"Other\"}]}"));
            Assert.Null(LinkPluginInstaller.RemovePluginEntry("{\"FileVersion\":3}"));
            Assert.Null(LinkPluginInstaller.RemovePluginEntry("not json"));
            string updated = LinkPluginInstaller.RemovePluginEntry("{\"Plugins\":[{\"Name\":\"uelink\",\"Enabled\":true}]}");
            Assert.Empty((JArray)JObject.Parse(updated)["Plugins"]);
        }
    }

    public class LinkCommandAndSettingsTests
    {
        [Fact]
        public void LinkCommandsAreInTheirRangeWithDefaults()
        {
            foreach (var id in new[] { CommandCatalog.UeLogWindow, CommandCatalog.UeTestRunner })
            {
                var c = CommandCatalog.ById(id);
                Assert.NotNull(c);
                Assert.InRange(c.Id, 0x0370, 0x037F);
                Assert.Equal(CommandOwner.Features, c.Owner);
            }
            Assert.Equal("Ctrl+Shift+Alt+U, J", CommandCatalog.ById(CommandCatalog.UeLogWindow).VaKeys);
            Assert.Equal("Ctrl+Shift+Alt+U, Z", CommandCatalog.ById(CommandCatalog.UeTestRunner).VaKeys);
        }

        [Fact]
        public void LinkSettingsRoundTripAndDefaults()
        {
            var schema = HostSettingsSchema.Build(8);
            var defaults = HostSettingsSchema.Codec.ToValues(new HostSettings());
            Assert.Equal("20000", defaults["link.logMaxLines"]);
            Assert.Equal("true", defaults["link.logKeepReceiving"]);
            Assert.Equal("true", defaults["link.liveCodingErrors"]);
            foreach (var key in new[] { "link.logMaxLines", "link.logKeepReceiving", "link.liveCodingErrors" })
                Assert.Equal(schema.Single(d => d.Key == key).Default, defaults[key]);
            var s = HostSettingsSchema.Codec.FromValues(new Dictionary<string, string> { ["link.logMaxLines"] = "5000", ["link.liveCodingErrors"] = "false" }, schema, new HostSettings());
            Assert.Equal(5000, s.LinkLogMaxLines);
            Assert.False(s.LinkLiveCodingErrors);
            var clamped = HostSettingsSchema.Codec.FromValues(new Dictionary<string, string> { ["link.logMaxLines"] = "10" }, schema, new HostSettings());
            Assert.True(clamped.LinkLogMaxLines >= 1000);
            Assert.Contains(schema, d => d.ActionId == HostSettingsSchema.WindowActions.LinkPluginUninstall);
        }
    }
}
