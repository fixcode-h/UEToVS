using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Pipelines;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using UEToVS.Mcp.Server;
using UEToVS.Mcp.Tools;
using Xunit;

namespace UEToVS.Mcp.Tests
{
    /// <summary>MCP handshake and methods through the official SDK client: in memory, and against the real exe over stdio.</summary>
    public sealed class ProtocolTests : IClassFixture<OfflineFixture>
    {
        private static readonly string[] ExpectedTools =
        {
            "find_references", "get_file_outline", "get_lua_outline", "get_members", "get_type_hierarchy", "go_to_definition", "index_status",
            "search_assets", "search_files", "search_lua", "search_lua_files", "search_symbols",
        };

        private readonly OfflineFixture _f;

        public ProtocolTests(OfflineFixture f) => _f = f;

        private sealed class InMemory : IAsyncDisposable
        {
            private readonly CancellationTokenSource _cts = new CancellationTokenSource();
            private Task _run;
            private McpServer _server;

            public McpClient Client { get; private set; }

            public static async Task<InMemory> StartAsync(ToolInvoker invoker)
            {
                var m = new InMemory();
                var toServer = new Pipe();
                var toClient = new Pipe();
                var transport = new StreamServerTransport(toServer.Reader.AsStream(), toClient.Writer.AsStream(), "uetovs-test");
                m._server = McpServer.Create(transport, McpServerFactory.CreateOptions(invoker));
                m._run = m._server.RunAsync(m._cts.Token);
                m.Client = await McpClient.CreateAsync(new StreamClientTransport(toServer.Writer.AsStream(), toClient.Reader.AsStream()));
                return m;
            }

            public async ValueTask DisposeAsync()
            {
                await Client.DisposeAsync();
                _cts.Cancel();
                try { await _run; } catch (OperationCanceledException) { }
                await _server.DisposeAsync();
            }
        }

        [Fact]
        public async Task Initialize_ListTools_CallTool_InMemory()
        {
            await using var m = await InMemory.StartAsync(_f.Invoker);
            var c = m.Client;
            Assert.Equal("uetovs", c.ServerInfo.Name);
            Assert.Contains("search_symbols", c.ServerInstructions);
            Assert.NotNull(c.ServerCapabilities.Tools);
            Assert.NotNull(c.ServerCapabilities.Resources);
            Assert.NotNull(c.ServerCapabilities.Prompts);

            var tools = await c.ListToolsAsync();
            Assert.Equal(ExpectedTools, tools.Select(t => t.Name).OrderBy(n => n, StringComparer.Ordinal).ToArray());
            foreach (var t in tools)
            {
                var schema = t.ProtocolTool.InputSchema;
                Assert.Equal("object", schema.GetProperty("type").GetString());
                Assert.False(schema.GetProperty("additionalProperties").GetBoolean());
                Assert.True(t.ProtocolTool.Annotations.ReadOnlyHint);
                Assert.False(string.IsNullOrWhiteSpace(t.Description));
                Assert.True(t.Description.Length < 1200, t.Name + " description is long: " + t.Description.Length);
            }

            var result = await c.CallToolAsync("search_symbols", new Dictionary<string, object> { ["query"] = "MyHero", ["limit"] = 5 });
            Assert.NotEqual(true, result.IsError);
            var text = Assert.IsType<TextContentBlock>(result.Content.Single()).Text;
            using var doc = JsonDocument.Parse(text);
            Assert.Equal("AMyHero", doc.RootElement.GetProperty("items")[0].GetProperty("name").GetString());

            var bad = await c.CallToolAsync("search_symbols", new Dictionary<string, object> { ["query"] = "x", ["limit"] = 9999 });
            Assert.True(bad.IsError);
        }

        [Fact]
        public async Task Resources_And_Prompts()
        {
            await using var m = await InMemory.StartAsync(_f.Invoker);
            var resources = await m.Client.ListResourcesAsync();
            Assert.Contains(resources, r => r.Uri == McpServerFactory.StatusUri);
            var status = await m.Client.ReadResourceAsync(McpServerFactory.StatusUri);
            var json = Assert.IsType<TextResourceContents>(status.Contents.Single()).Text;
            Assert.Contains("\"phase\"", json);
            var guide = await m.Client.ReadResourceAsync(McpServerFactory.GuideUri);
            Assert.Contains("search_assets", Assert.IsType<TextResourceContents>(guide.Contents.Single()).Text);

            var prompts = await m.Client.ListPromptsAsync();
            Assert.Contains(prompts, p => p.Name == "explain_ue_class");
            var p1 = await m.Client.GetPromptAsync("explain_ue_class", new Dictionary<string, object> { ["class_name"] = "AMyHero" });
            var msg = Assert.IsType<TextContentBlock>(p1.Messages.Single().Content).Text;
            Assert.Contains("get_type_hierarchy type_name=AMyHero", msg);
            Assert.Contains("search_assets query=MyHero", msg);
        }

        /// <summary>The published exe speaks MCP over stdio with nothing else on stdout (any stray byte breaks clients).</summary>
        [Fact]
        public async Task RealProcess_Stdio_Handshake()
        {
            string dll = typeof(UEToVS.Mcp.Program).Assembly.Location;
            Assert.True(File.Exists(dll), dll);
            var transport = new StdioClientTransport(new StdioClientTransportOptions
            {
                Name = "uetovs",
                Command = "dotnet",
                Arguments = new[] { dll, "--solution-dir", _f.T.ProjectRoot, "--log-level", "debug" },
                WorkingDirectory = Path.GetTempPath(),
            });
            await using var client = await McpClient.CreateAsync(transport);
            Assert.Equal("uetovs", client.ServerInfo.Name);
            var tools = await client.ListToolsAsync();
            Assert.Equal(ExpectedTools.Length, tools.Count);
            var r = await client.CallToolAsync("search_files", new Dictionary<string, object> { ["query"] = "MyHero", ["extensions"] = new[] { ".cpp" } });
            var text = Assert.IsType<TextContentBlock>(r.Content.Single()).Text;
            Assert.Contains("MyHero.cpp", text);
            await client.DisposeAsync();

            // the call was logged with the stable event name
            string logs = Path.Combine(_f.T.ProjectRoot, ".vs", "UEToVS", "Logs");
            await Task.Delay(300);
            var log = Directory.GetFiles(logs, "mcp-*.log").Select(File.ReadAllText).FirstOrDefault(t => t.Contains("\"mcp.tool.search_files\""));
            Assert.NotNull(log);
            Assert.Contains("\"results\":1", log);
        }

        [Fact]
        public void Options_SolutionDirDiscovery()
        {
            var o = McpOptions.Parse(Array.Empty<string>(), out string err, cwd: Path.Combine(_f.T.ProjectRoot, "Source", "MyGame"), env: _ => null);
            Assert.Null(err);
            Assert.Equal(_f.T.ProjectRoot, o.SolutionDir); // has .vs/UEToVS
            Assert.Equal("discovered", o.SolutionDirMethod);

            var e = McpOptions.Parse(Array.Empty<string>(), out err, cwd: "/", env: k => k == "UETOVS_SOLUTION_DIR" ? _f.T.ProjectRoot : null);
            Assert.Equal("environment", e.SolutionDirMethod);

            var a = McpOptions.Parse(new[] { "--solution-dir", _f.T.Root, "--source", "index", "--log-level", "debug" }, out err);
            Assert.Equal(DataSourceMode.IndexOnly, a.Source);
            Assert.Equal("argument", a.SolutionDirMethod);

            Assert.Null(McpOptions.Parse(new[] { "--source", "cloud" }, out err));
            Assert.Contains("--source", err);
            Assert.Null(McpOptions.Parse(new[] { "--nope" }, out err));
        }
    }
}
