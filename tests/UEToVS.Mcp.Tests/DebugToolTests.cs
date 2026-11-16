using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Pipes;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using UEToVS.Core.Storage;
using UEToVS.Mcp.Data;
using UEToVS.Mcp.Tools;
using Xunit;

namespace UEToVS.Mcp.Tests
{
    /// <summary>debug_* tools against a fake VS end of the debug control pipe (docs/design/VS-调试控制.md).</summary>
    public sealed class DebugToolTests : IDisposable
    {
        private readonly string _dir = Path.Combine(Path.GetTempPath(), "uetovs-debug-" + Guid.NewGuid().ToString("N"));
        private readonly CodeIndex _index;
        private readonly ToolInvoker _invoker;

        public DebugToolTests()
        {
            Directory.CreateDirectory(_dir);
            _index = new CodeIndex(_dir, DataSourceMode.Auto);
            _invoker = new ToolInvoker(new ToolCatalog(_index));
        }

        public void Dispose()
        {
            _index.Dispose();
            try { Directory.Delete(_dir, true); } catch (IOException) { }
        }

        private string WriteEndpoint(string pipe, int protocol = 1)
        {
            string file = StoragePaths.ForSolution(_dir).DebugEndpoint;
            Directory.CreateDirectory(Path.GetDirectoryName(file));
            File.WriteAllText(file, new JsonObject { ["protocol"] = protocol, ["pipe"] = pipe, ["token"] = "tok" }.ToJsonString());
            return file;
        }

        /// <summary>Answers one request with <paramref name="answer"/>(request) and hands the request to the test.</summary>
        private static Task<JsonObject> ServeOnce(string pipe, Func<JsonObject, JsonObject> answer)
        {
            var server = new NamedPipeServerStream(pipe, PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
            return Task.Run(async () =>
            {
                using (server)
                {
                    await server.WaitForConnectionAsync();
                    var utf8 = new UTF8Encoding(false);
                    using var reader = new StreamReader(server, utf8, false, 4096, true);
                    using var writer = new StreamWriter(server, utf8, 4096, true) { NewLine = "\n", AutoFlush = true };
                    var req = (JsonObject)JsonNode.Parse(await reader.ReadLineAsync());
                    await writer.WriteLineAsync(answer(req).ToJsonString());
                    return req;
                }
            });
        }

        private async Task<(bool IsError, JsonElement Json)> Call(string tool, object args)
        {
            var r = await _invoker.InvokeAsync(tool, Tree.Args(args), CancellationToken.None);
            using var doc = JsonDocument.Parse(r.Text);
            return (r.IsError, doc.RootElement.Clone());
        }

        [Fact]
        public async Task Breakpoint_Add_ForwardsRenamedArgsAndToken()
        {
            string pipe = "uetovs-test-" + Guid.NewGuid().ToString("N").Substring(0, 8);
            WriteEndpoint(pipe);
            var served = ServeOnce(pipe, req => new JsonObject { ["id"] = req["id"]?.DeepClone(), ["ok"] = true, ["result"] = new JsonObject { ["added"] = new JsonArray() } });
            var (isError, json) = await Call("debug_breakpoints", new { action = "add", file = "Source/A.cpp", line = 12, hit_count = 3, condition = "Health <= 0" });
            Assert.False(isError, json.ToString());
            var req = await served;
            Assert.Equal("bp.add", (string)req["op"]);
            Assert.Equal("tok", (string)req["token"]);
            Assert.Equal(1, (int)req["v"]);
            Assert.Equal(3, (int)req["args"]["hitCount"]);
            Assert.Equal(12, (int)req["args"]["line"]);
            Assert.Null(req["args"]["action"]);
        }

        [Fact]
        public async Task Session_ErrorFromVsIsShownAsIs()
        {
            string pipe = "uetovs-test-" + Guid.NewGuid().ToString("N").Substring(0, 8);
            WriteEndpoint(pipe);
            var served = ServeOnce(pipe, req => new JsonObject { ["id"] = req["id"]?.DeepClone(), ["ok"] = false, ["error"] = "没有在调试：先 start" });
            var (isError, json) = await Call("debug_session", new { action = "continue", wait_ms = 0 });
            Assert.True(isError);
            Assert.Equal("vs_error", json.GetProperty("error").GetString());
            Assert.Contains("没有在调试", json.GetProperty("message").GetString());
            Assert.Equal("continue", (string)(await served)["op"]);
        }

        [Fact]
        public async Task NoEndpoint_SaysOpenVs()
        {
            var (isError, json) = await Call("debug_inspect", new { action = "stack" });
            Assert.True(isError);
            Assert.Equal("vs_not_running", json.GetProperty("error").GetString());
            Assert.Contains(".sln", json.GetProperty("message").GetString());
        }

        [Fact]
        public async Task ProtocolMismatch_IsReported()
        {
            WriteEndpoint("whatever", protocol: 9);
            var (isError, json) = await Call("debug_session", new { action = "status" });
            Assert.True(isError);
            Assert.Equal("vs_protocol", json.GetProperty("error").GetString());
        }

        [Fact]
        public async Task DeadPipe_IsNotRunning()
        {
            WriteEndpoint("uetovs-nobody-" + Guid.NewGuid().ToString("N").Substring(0, 8));
            var (isError, json) = await Call("debug_session", new { action = "status" });
            Assert.True(isError);
            Assert.Equal("vs_not_running", json.GetProperty("error").GetString());
        }
    }
}
