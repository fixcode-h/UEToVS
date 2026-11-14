using System;
using System.Collections.Generic;
using System.Globalization;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace UEToVS.Host.Logic.Link
{
    /// <summary>
    /// UEToVSLink wire protocol, version 2 (v0.4.5). Still one JSON object per line over localhost TCP.
    /// <para>Protocol 1 (plugin 0.1.x): one-shot commands <c>ping / openAsset / browseAsset / liveCoding</c>, one reply, then
    /// the plugin closes the connection. Protocol 2 keeps all of that unchanged and adds:</para>
    /// <list type="bullet">
    /// <item><c>{"cmd":"ping","protocol":2}</c> → the reply carries <c>protocol</c>, <c>plugin</c> and <c>features</c>
    /// (a protocol-1 plugin ignores the extra field and answers without them → treated as protocol 1).</item>
    /// <item><c>{"cmd":"subscribe","protocol":2,"topics":["log","tests","liveCoding"]}</c> → <c>{"ok":true,"protocol":2,...}</c>,
    /// then the connection stays open and the plugin pushes event lines (<c>{"event":"log"|"tests"|"liveCoding"|"subscribed",...}</c>).
    /// The client may send another <c>subscribe</c> on the same connection to change the topics. A protocol-1 plugin
    /// answers <c>unknown command</c> and closes: the client remembers "plugin too old" and does not retry.</item>
    /// <item><c>{"cmd":"runTests","id":"r1","filter":"Project.Foo"}</c> → <c>{"ok":true,"id":"r1"}</c>; the results arrive as
    /// <c>tests</c> events on a subscription.</item>
    /// </list>
    /// Unknown fields and unknown events are ignored on both sides (forward compatible). Pure (no VS types), unit-tested.
    /// </summary>
    public static class LinkProtocol
    {
        /// <summary>Protocol version this extension speaks.</summary>
        public const int Version = 2;

        public const string TopicLog = "log";
        public const string TopicTests = "tests";
        public const string TopicLiveCoding = "liveCoding";

        public const string FeatureLog = "log";
        public const string FeatureTests = "tests";
        public const string FeatureLiveCoding = "liveCoding";

        public static JObject Ping() => new JObject { ["cmd"] = "ping", ["protocol"] = Version };

        /// <summary>
        /// Copy of <paramref name="command"/> with the card's token (UELink checks it when present and will require it);
        /// unchanged when there is no token (old UEToVSLink, which ignores unknown fields anyway).
        /// </summary>
        public static JObject WithToken(JObject command, string token)
        {
            if (command == null || string.IsNullOrEmpty(token)) return command;
            var copy = (JObject)command.DeepClone();
            copy["token"] = token;
            return copy;
        }

        public static JObject Subscribe(IEnumerable<string> topics)
        {
            var arr = new JArray();
            if (topics != null)
                foreach (var t in topics)
                    if (!string.IsNullOrEmpty(t)) arr.Add(t);
            return new JObject { ["cmd"] = "subscribe", ["protocol"] = Version, ["topics"] = arr };
        }

        public static JObject RunTests(string runId, string filter) =>
            new JObject { ["cmd"] = "runTests", ["protocol"] = Version, ["id"] = runId ?? string.Empty, ["filter"] = filter ?? string.Empty };

        /// <summary>True when a reply says the plugin does not know the command (protocol-1 plugin).</summary>
        public static bool IsUnknownCommand(bool ok, string message) =>
            !ok && message != null && message.StartsWith("unknown command", StringComparison.OrdinalIgnoreCase);

        /// <summary>What a ping reply says about the plugin; a reply without <c>protocol</c> is a protocol-1 plugin.</summary>
        public static LinkCapabilities ParseCapabilities(JObject reply)
        {
            var caps = new LinkCapabilities();
            if (reply == null) return caps;
            caps.Protocol = IntField(reply, "protocol", 1);
            if (caps.Protocol < 1) caps.Protocol = 1;
            caps.PluginVersion = StringField(reply, "plugin");
            caps.Project = StringField(reply, "project");
            caps.EngineVersion = StringField(reply, "engine");
            if (reply["features"] is JArray features)
                foreach (var f in features)
                    if (f.Type == JTokenType.String) caps.Features.Add((string)f);
            return caps;
        }

        /// <summary>
        /// Parses one line pushed on a subscription. Never throws: garbage, an empty line or an unknown event gives null
        /// (<paramref name="error"/> says why for the log).
        /// </summary>
        public static LinkEvent ParseEvent(string line, out string error)
        {
            error = null;
            if (string.IsNullOrWhiteSpace(line))
            {
                error = "empty";
                return null;
            }
            JObject obj;
            try
            {
                obj = JObject.Parse(line);
            }
            catch (JsonException ex)
            {
                error = "json: " + ex.Message;
                return null;
            }
            string evt = StringField(obj, "event");
            switch (evt)
            {
                case "log": return ParseLog(obj);
                case "liveCoding": return ParseLiveCoding(obj);
                case "tests": return ParseTests(obj);
                case "subscribed": return new SubscribedEvent { Topics = Strings(obj["topics"]) };
                case null:
                    // The first line of a subscription is the plain {"ok":...} reply of the subscribe command.
                    if (obj["ok"] != null) return new SubscribeReplyEvent { Ok = obj["ok"].Type == JTokenType.Boolean && (bool)obj["ok"], Message = StringField(obj, "message"), Protocol = IntField(obj, "protocol", 1) };
                    error = "no event";
                    return null;
                default:
                    error = "unknown event: " + evt;
                    return null;
            }
        }

        private static LogBatchEvent ParseLog(JObject obj)
        {
            var e = new LogBatchEvent { Dropped = IntField(obj, "dropped", 0) };
            if (obj["lines"] is JArray lines)
                foreach (var t in lines)
                {
                    if (!(t is JObject l)) continue;
                    string msg = StringField(l, "m");
                    if (msg == null) continue;
                    e.Lines.Add(new UeLogRecord(DoubleField(l, "t"), StringField(l, "c") ?? string.Empty, UeLogVerbosities.FromWire(IntField(l, "v", (int)UeLogVerbosity.Log)), msg));
                }
            return e;
        }

        private static LiveCodingEvent ParseLiveCoding(JObject obj)
        {
            var e = new LiveCodingEvent
            {
                Phase = ParsePhase(StringField(obj, "phase")),
                Result = StringField(obj, "result"),
                Message = StringField(obj, "message"),
                DurationMs = DoubleField(obj, "durationMs"),
            };
            e.Lines.AddRange(Strings(obj["lines"]));
            return e;
        }

        private static LiveCodingPhase ParsePhase(string phase)
        {
            switch (phase)
            {
                case "started": return LiveCodingPhase.Started;
                case "output": return LiveCodingPhase.Output;
                case "finished": return LiveCodingPhase.Finished;
                default: return LiveCodingPhase.Unknown;
            }
        }

        private static TestEvent ParseTests(JObject obj)
        {
            var e = new TestEvent
            {
                RunId = StringField(obj, "id"),
                Name = StringField(obj, "name"),
                Result = ParseResult(StringField(obj, "result")),
                DurationMs = DoubleField(obj, "durationMs"),
                Passed = IntField(obj, "passed", 0),
                Failed = IntField(obj, "failed", 0),
                Message = StringField(obj, "message"),
                Filter = StringField(obj, "filter"),
            };
            switch (StringField(obj, "phase"))
            {
                case "runStarted": e.Phase = TestEventPhase.RunStarted; break;
                case "testStarted": e.Phase = TestEventPhase.TestStarted; break;
                case "testFinished": e.Phase = TestEventPhase.TestFinished; break;
                case "runFinished": e.Phase = TestEventPhase.RunFinished; break;
                default: e.Phase = TestEventPhase.Unknown; break;
            }
            e.Errors.AddRange(Strings(obj["errors"]));
            e.Warnings.AddRange(Strings(obj["warnings"]));
            e.Logs.AddRange(Strings(obj["logs"]));
            return e;
        }

        private static AutomationTestOutcome ParseResult(string result)
        {
            switch ((result ?? string.Empty).ToLowerInvariant())
            {
                case "passed":
                case "success": return AutomationTestOutcome.Passed;
                case "failed":
                case "fail": return AutomationTestOutcome.Failed;
                case "skipped":
                case "notrun": return AutomationTestOutcome.Skipped;
                default: return AutomationTestOutcome.Unknown;
            }
        }

        private static List<string> Strings(JToken token)
        {
            var list = new List<string>();
            if (token is JArray arr)
                foreach (var t in arr)
                    if (t.Type == JTokenType.String) list.Add((string)t);
            return list;
        }

        private static string StringField(JObject o, string name)
        {
            var t = o[name];
            return t == null || t.Type == JTokenType.Null ? null : t.Type == JTokenType.String ? (string)t : t.ToString(Formatting.None);
        }

        private static int IntField(JObject o, string name, int fallback)
        {
            var t = o[name];
            if (t == null) return fallback;
            if (t.Type == JTokenType.Integer) return (int)Math.Max(int.MinValue, Math.Min(int.MaxValue, (long)t));
            if (t.Type == JTokenType.Float) return (int)(double)t;
            return t.Type == JTokenType.String && int.TryParse((string)t, NumberStyles.Integer, CultureInfo.InvariantCulture, out int i) ? i : fallback;
        }

        private static double DoubleField(JObject o, string name)
        {
            var t = o[name];
            if (t == null) return 0;
            if (t.Type == JTokenType.Integer || t.Type == JTokenType.Float) return (double)t;
            return t.Type == JTokenType.String && double.TryParse((string)t, NumberStyles.Float, CultureInfo.InvariantCulture, out double d) ? d : 0;
        }
    }

    /// <summary>What the running plugin supports (from the ping reply).</summary>
    public sealed class LinkCapabilities
    {
        public int Protocol { get; set; } = 1;
        public string PluginVersion { get; set; }
        public string Project { get; set; }
        public string EngineVersion { get; set; }
        public HashSet<string> Features { get; } = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        public bool Supports(string feature) => Protocol >= 2 && Features.Contains(feature);

        public override string ToString() => "protocol " + Protocol + (PluginVersion == null ? "" : ", plugin " + PluginVersion) + ", features [" + string.Join(",", Features) + "]";
    }

    public abstract class LinkEvent
    {
        public abstract string Kind { get; }
    }

    /// <summary>First line of a subscription: the reply to <c>subscribe</c>.</summary>
    public sealed class SubscribeReplyEvent : LinkEvent
    {
        public override string Kind => "reply";
        public bool Ok { get; set; }
        public string Message { get; set; }
        public int Protocol { get; set; }
    }

    public sealed class SubscribedEvent : LinkEvent
    {
        public override string Kind => "subscribed";
        public List<string> Topics { get; set; } = new List<string>();
    }

    /// <summary>One batch of editor / PIE log lines. <see cref="Dropped"/>: lines the plugin dropped since the previous batch (rate limit).</summary>
    public sealed class LogBatchEvent : LinkEvent
    {
        public override string Kind => "log";
        public List<UeLogRecord> Lines { get; } = new List<UeLogRecord>();
        public int Dropped { get; set; }
    }

    public enum LiveCodingPhase
    {
        Unknown = 0,
        Started = 1,
        Output = 2,
        Finished = 3,
    }

    public sealed class LiveCodingEvent : LinkEvent
    {
        public override string Kind => "liveCoding";
        public LiveCodingPhase Phase { get; set; }
        public List<string> Lines { get; } = new List<string>();

        /// <summary>finished: "success" | "failed" | "noChanges" | "cancelled" | "unknown".</summary>
        public string Result { get; set; }

        public string Message { get; set; }
        public double DurationMs { get; set; }
    }

    public enum TestEventPhase
    {
        Unknown = 0,
        RunStarted = 1,
        TestStarted = 2,
        TestFinished = 3,
        RunFinished = 4,
    }

    public enum AutomationTestOutcome
    {
        Unknown = 0,
        Passed = 1,
        Failed = 2,
        Skipped = 3,
    }

    public sealed class TestEvent : LinkEvent
    {
        public override string Kind => "tests";
        public TestEventPhase Phase { get; set; }
        public string RunId { get; set; }
        public string Filter { get; set; }

        /// <summary>testStarted / testFinished: full test path ("Project.Foo.Bar").</summary>
        public string Name { get; set; }

        public AutomationTestOutcome Result { get; set; }
        public double DurationMs { get; set; }
        public List<string> Errors { get; } = new List<string>();
        public List<string> Warnings { get; } = new List<string>();
        public List<string> Logs { get; } = new List<string>();

        /// <summary>runFinished: totals; <see cref="Message"/> explains an empty / failed run ("no tests matched").</summary>
        public int Passed { get; set; }
        public int Failed { get; set; }
        public string Message { get; set; }
    }
}
