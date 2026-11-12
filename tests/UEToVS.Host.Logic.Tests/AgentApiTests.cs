using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;
using UEToVS.Host.Logic.Agent;
using Xunit;

namespace UEToVS.Host.Logic.Tests
{
    public class AgentApiConfigTests
    {
        [Theory]
        [InlineData("https://api.deepseek.com", "https://api.deepseek.com/v1")]
        [InlineData("https://api.deepseek.com/", "https://api.deepseek.com/v1")]
        [InlineData("https://relay.example.com/v1", "https://relay.example.com/v1")]
        [InlineData("https://relay.example.com/v1/chat/completions", "https://relay.example.com/v1")]
        [InlineData("https://open.bigmodel.cn/api/paas/v4", "https://open.bigmodel.cn/api/paas/v4")]
        [InlineData("https://x.com/openai", "https://x.com/openai/v1")]
        [InlineData("http://127.0.0.1:8080/v1/models", "http://127.0.0.1:8080/v1")]
        public void ResolveApiRoot_AddsOrKeepsVersion(string input, string expected)
        {
            Assert.Equal(expected, AgentApiConfig.ResolveApiRoot(input));
        }

        [Theory]
        [InlineData(null)]
        [InlineData("")]
        [InlineData("not a url")]
        [InlineData("ftp://x.com")]
        public void ResolveApiRoot_RejectsNonHttp(string input)
        {
            Assert.Null(AgentApiConfig.ResolveApiRoot(input));
        }

        [Fact]
        public void DefaultPortIsOllamasAndOldDefaultMigrates()
        {
            Assert.Equal(11434, new AgentApiConfig().GatewayPort);
            Assert.Equal(11434, AgentApiConfig.Parse(@"{ ""GatewayPort"": 47811, ""Apis"": [] }").GatewayPort); // pre-v0.4.5 default
            Assert.Equal(47811, AgentApiConfig.Parse(@"{ ""Format"": 2, ""GatewayPort"": 47811, ""Apis"": [] }").GatewayPort); // chosen by the user
            Assert.Equal(50000, AgentApiConfig.Parse(@"{ ""GatewayPort"": 50000 }").GatewayPort);
        }

        [Fact]
        public void DefaultContextIsOneMillionAndOldDefaultMigrates()
        {
            Assert.Equal(1000000, new AgentModel().ContextTokens);
            var old = AgentApiConfig.Parse(@"{ ""Apis"": [ { ""Name"": ""a"", ""Models"": [ { ""Id"": ""m1"", ""ContextTokens"": 128000 }, { ""Id"": ""m2"", ""ContextTokens"": 64000 } ] } ] }");
            Assert.Equal(1000000, old.Apis[0].Models[0].ContextTokens);
            Assert.Equal(64000, old.Apis[0].Models[1].ContextTokens);
            var chosen = AgentApiConfig.Parse(@"{ ""Format"": 2, ""Apis"": [ { ""Name"": ""a"", ""Models"": [ { ""Id"": ""m1"", ""ContextTokens"": 128000 } ] } ] }");
            Assert.Equal(128000, chosen.Apis[0].Models[0].ContextTokens);
        }

        [Fact]
        public void TakenDefaultPortFallsBackButAUserPortNeverMoves()
        {
            Assert.Equal(11434, AgentApiConfig.ChoosePort(11434, p => false, out bool f1));
            Assert.False(f1);
            Assert.Equal(47811, AgentApiConfig.ChoosePort(11434, p => p == 11434, out bool f2));
            Assert.True(f2);
            Assert.Equal(50000, AgentApiConfig.ChoosePort(50000, p => true, out bool f3));
            Assert.False(f3);
        }

        [Fact]
        public void Routes_PrefixOnlyDuplicateIds_AndSkipDisabled()
        {
            var c = new AgentApiConfig();
            c.Apis.Add(new AgentApi { Name = "A", BaseUrl = "https://a.com", Models = { new AgentModel { Id = "gpt-x" }, new AgentModel { Id = "only-a" }, new AgentModel { Id = "off", Enabled = false } } });
            c.Apis.Add(new AgentApi { Name = "B", BaseUrl = "https://b.com", Models = { new AgentModel { Id = "gpt-x" } } });
            var ids = c.Routes().Select(r => r.VsId).ToList();
            Assert.Equal(new[] { "A/gpt-x", "only-a", "B/gpt-x" }, ids);
            Assert.Equal("only-a (A)", c.Routes()[1].DisplayName);
        }

        [Fact]
        public void Serialize_RoundTrips_WithoutSecrets_AndNormalizes()
        {
            var c = new AgentApiConfig { GatewayPort = 5 }; // invalid → default
            c.Apis.Add(new AgentApi { Name = "A", BaseUrl = "https://a.com", MaxConcurrency = -3, Models = { new AgentModel { Id = " m " }, new AgentModel { Id = "m" }, new AgentModel { Id = "" } } });
            var back = AgentApiConfig.Parse(c.Serialize());
            Assert.Equal(AgentApiConfig.DefaultPort, back.GatewayPort);
            Assert.Equal(0, back.Apis[0].MaxConcurrency);
            Assert.Equal(new[] { "m" }, back.Apis[0].Models.Select(m => m.Id));
            Assert.DoesNotContain("\"Key\"", c.Serialize());
            Assert.DoesNotContain("ApiKey", c.Serialize());
        }

        [Fact]
        public void SaveAndLoad_File()
        {
            using (var dir = new TempDir())
            {
                string path = Path.Combine(dir.Path, "sub", "agent-apis.json");
                var c = new AgentApiConfig { GatewayPort = 50000 };
                c.Apis.Add(new AgentApi { Name = "A", BaseUrl = "https://a.com", Models = { new AgentModel { Id = "m", Vision = true } } });
                c.Save(path);
                c.Save(path); // replace path
                var back = AgentApiConfig.Load(path);
                Assert.Equal(50000, back.GatewayPort);
                Assert.True(back.Apis[0].Models[0].Vision);
            }
        }

        [Fact]
        public void ParseModelIds_KnownShapes()
        {
            Assert.Equal(new[] { "a", "b" }, AgentApiClient.ParseModelIds("{\"object\":\"list\",\"data\":[{\"id\":\"b\"},{\"id\":\"a\"},{\"id\":\"a\"}]}"));
            Assert.Equal(new[] { "llama3:8b" }, AgentApiClient.ParseModelIds("{\"models\":[{\"name\":\"llama3:8b\"}]}"));
            Assert.Equal(new[] { "x" }, AgentApiClient.ParseModelIds("[\"x\"]"));
            Assert.Empty(AgentApiClient.ParseModelIds("<html>"));
        }

        [Fact]
        public void ErrorDetail_PrefersMessage()
        {
            Assert.Equal(": bad key", AgentApiClient.ErrorDetail("{\"error\":{\"message\":\"bad key\"}}"));
            Assert.Equal(": oops", AgentApiClient.ErrorDetail("{\"message\":\"oops\"}"));
            Assert.Equal(": plain", AgentApiClient.ErrorDetail("plain"));
            Assert.Equal(string.Empty, AgentApiClient.ErrorDetail(""));
        }

        [Fact]
        public void ApplyKey_BearerOrXApiKey()
        {
            using (var r = new HttpRequestMessage(HttpMethod.Get, "http://x/"))
            {
                AgentApiClient.ApplyKey(r, AgentKeyMode.Bearer, "sk-xxxx");
                Assert.Equal("Bearer sk-xxxx", r.Headers.GetValues("Authorization").Single());
            }
            using (var r = new HttpRequestMessage(HttpMethod.Get, "http://x/"))
            {
                AgentApiClient.ApplyKey(r, AgentKeyMode.XApiKey, "sk-xxxx");
                Assert.Equal("sk-xxxx", r.Headers.GetValues("x-api-key").Single());
                Assert.False(r.Headers.Contains("Authorization"));
            }
        }
    }

    public class VsByokConfigTests
    {
        private const string Gw = "http://127.0.0.1:47811";

        private static IReadOnlyList<AgentRoute> Routes(params string[] ids)
        {
            var c = new AgentApiConfig();
            var api = new AgentApi { Name = "R", BaseUrl = "https://r.com" };
            foreach (var id in ids) api.Models.Add(new AgentModel { Id = id, ContextTokens = 64000, Vision = id.Contains("vl") });
            c.Apis.Add(api);
            return c.Routes();
        }

        [Fact]
        public void Merge_CreatesOllamaProvider()
        {
            var arr = JArray.Parse(VsByokConfig.Merge(null, Gw, Routes("m1", "m-vl")));
            var p = (JObject)arr.Single();
            Assert.Equal("ollama", (string)p["Name"]);
            Assert.Equal(10, (int)p["Endpoint"]);
            Assert.True((bool)p["IsApiKeyAvailable"]);
            var m = (JObject)p["Models"][1];
            Assert.Equal("m-vl", (string)m["Id"]);
            Assert.Equal(Gw, (string)m["CustomURL"]);
            Assert.True((bool)m["IsVisionEnabled"]);
            Assert.True((bool)m["IsSelected"]);
            Assert.Equal(64000, (int)m["MaxInputTokens"]);
        }

        [Fact]
        public void Merge_KeepsOtherProvidersAndUsersOllamaModels()
        {
            string existing = @"[
              {""Name"":""anthropic"",""IsApiKeyAvailable"":true,""Models"":[{""Id"":""claude"",""IsSelected"":true}],""Endpoint"":3},
              {""Name"":""ollama"",""IsApiKeyAvailable"":true,""Endpoint"":10,""Models"":[
                 {""Id"":""local"",""CustomURL"":""http://localhost:11434""},
                 {""Id"":""stale"",""CustomURL"":""http://127.0.0.1:47811/""},
                 {""Id"":""old-port"",""CustomURL"":""http://127.0.0.1:40000""}]}
            ]";
            var arr = JArray.Parse(VsByokConfig.Merge(existing, Gw, Routes("new"), new[] { "http://127.0.0.1:40000" }));
            Assert.Equal(2, arr.Count);
            Assert.Equal("claude", (string)arr[0]["Models"][0]["Id"]);
            Assert.Equal(new[] { "local", "new" }, arr[1]["Models"].Select(m => (string)m["Id"]));
        }

        [Fact]
        public void Merge_RemovesProviderThatOnlyHeldOurModels()
        {
            string withOurs = VsByokConfig.Merge(null, Gw, Routes("a"));
            Assert.Empty(JArray.Parse(VsByokConfig.Merge(withOurs, Gw, Routes())));
            Assert.Equal(new[] { "a" }, VsByokConfig.RegisteredIds(withOurs, Gw));
        }

        [Fact]
        public void Merge_RefusesNonArrayFile()
        {
            Assert.Throws<InvalidDataException>(() => VsByokConfig.Merge("{}", Gw, Routes("a")));
        }

        [Fact]
        public void Apply_WritesBackupOnce_AndReportsUnchanged()
        {
            using (var dir = new TempDir())
            {
                string path = VsByokConfig.DefaultPath(dir.Path);
                Assert.Equal(VsByokOutcome.Unchanged, VsByokConfig.Apply(path, Gw, Routes(), null, out _));
                Assert.False(File.Exists(path));
                Assert.Equal(VsByokOutcome.Written, VsByokConfig.Apply(path, Gw, Routes("a"), null, out _));
                Assert.Equal(VsByokOutcome.Unchanged, VsByokConfig.Apply(path, Gw, Routes("a"), null, out _));
                Assert.Equal(VsByokOutcome.Written, VsByokConfig.Apply(path, Gw, Routes("a", "b"), null, out _));
                Assert.True(File.Exists(path + ".uetovs-backup"));
                Assert.Single(JArray.Parse(File.ReadAllText(path + ".uetovs-backup"))[0]["Models"]);
                File.WriteAllText(path, "not json");
                Assert.Equal(VsByokOutcome.Failed, VsByokConfig.Apply(path, Gw, Routes("a"), null, out string error));
                Assert.NotNull(error);
                Assert.Equal("not json", File.ReadAllText(path));
            }
        }
    }

    public class AgentGatewayTests
    {
        /// <summary>Upstream stub: records the request, answers with a canned SSE stream.</summary>
        private sealed class Upstream : HttpMessageHandler
        {
            public HttpRequestMessage Last;
            public string LastBody;
            public HttpStatusCode Status = HttpStatusCode.OK;
            public string Response = "data: {\"choices\":[{\"delta\":{\"content\":\"hi\"}}]}\n\ndata: [DONE]\n\n";

            protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            {
                Last = request;
                LastBody = await request.Content.ReadAsStringAsync();
                var resp = new HttpResponseMessage(Status) { Content = new StringContent(Response, Encoding.UTF8, "text/event-stream") };
                return resp;
            }
        }

        private static int FreePort()
        {
            var l = new TcpListener(IPAddress.Loopback, 0);
            l.Start();
            int p = ((IPEndPoint)l.LocalEndpoint).Port;
            l.Stop();
            return p;
        }

        private static (AgentGateway Gateway, Upstream Up, HttpClient Client) Start(Action<AgentApiConfig> configure = null, Func<string, string> keys = null)
        {
            var c = new AgentApiConfig();
            c.Apis.Add(new AgentApi { Id = "api1", Name = "Relay", BaseUrl = "https://relay.example.com", KeyMode = AgentKeyMode.Bearer, Models = { new AgentModel { Id = "deepseek-v4", Vision = true } } });
            configure?.Invoke(c);
            var up = new Upstream();
            var gw = new AgentGateway(keys ?? (id => id == "api1" ? "sk-xxxx" : null), up);
            gw.Update(c);
            int port = FreePort();
            Assert.True(gw.Start(port), gw.LastError);
            var client = new HttpClient { BaseAddress = new Uri("http://127.0.0.1:" + port) };
            return (gw, up, client);
        }

        [Fact]
        public async Task Chat_IsForwardedWithKeyAndUpstreamModel_AndStreamedBack()
        {
            var (gw, up, client) = Start();
            using (gw)
            using (client)
            {
                var resp = await client.PostAsync("/v1/chat/completions", new StringContent("{\"model\":\"deepseek-v4\",\"stream\":true,\"messages\":[]}", Encoding.UTF8, "application/json"));
                Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
                Assert.Equal("text/event-stream", resp.Content.Headers.ContentType.MediaType);
                Assert.Contains("[DONE]", await resp.Content.ReadAsStringAsync());
                Assert.Equal("https://relay.example.com/v1/chat/completions", up.Last.RequestUri.ToString());
                Assert.Equal("Bearer sk-xxxx", up.Last.Headers.GetValues("Authorization").Single());
                Assert.Equal("deepseek-v4", (string)JObject.Parse(up.LastBody)["model"]);
                Assert.Equal(1, gw.RequestCount);
            }
        }

        [Fact]
        public async Task Chat_PrefixedIdMapsBackToUpstreamId()
        {
            var (gw, up, client) = Start(c => c.Apis.Add(new AgentApi { Id = "api2", Name = "Other", BaseUrl = "https://o.com/v1", Models = { new AgentModel { Id = "deepseek-v4" } } }),
                                         id => "sk-" + id);
            using (gw)
            using (client)
            {
                var resp = await client.PostAsync("/v1/chat/completions", new StringContent("{\"model\":\"Other/deepseek-v4\"}", Encoding.UTF8, "application/json"));
                Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
                Assert.Equal("https://o.com/v1/chat/completions", up.Last.RequestUri.ToString());
                Assert.Equal("deepseek-v4", (string)JObject.Parse(up.LastBody)["model"]);
                Assert.Equal("Bearer sk-api2", up.Last.Headers.GetValues("Authorization").Single());
            }
        }

        [Fact]
        public async Task Chat_UpstreamErrorPassesThrough()
        {
            var (gw, up, client) = Start();
            up.Status = HttpStatusCode.Unauthorized;
            up.Response = "{\"error\":{\"message\":\"invalid key\"}}";
            using (gw)
            using (client)
            {
                var resp = await client.PostAsync("/v1/chat/completions", new StringContent("{\"model\":\"deepseek-v4\"}", Encoding.UTF8, "application/json"));
                Assert.Equal(HttpStatusCode.Unauthorized, resp.StatusCode);
                Assert.Contains("invalid key", await resp.Content.ReadAsStringAsync());
            }
        }

        [Fact]
        public async Task Chat_UnknownModelOrMissingKey_AreExplained()
        {
            var (gw, up, client) = Start(keys: _ => null);
            using (gw)
            using (client)
            {
                var unknown = await client.PostAsync("/v1/chat/completions", new StringContent("{\"model\":\"nope\"}", Encoding.UTF8, "application/json"));
                Assert.Equal(HttpStatusCode.NotFound, unknown.StatusCode);
                Assert.Contains("nope", await unknown.Content.ReadAsStringAsync());
                var noKey = await client.PostAsync("/v1/chat/completions", new StringContent("{\"model\":\"deepseek-v4\"}", Encoding.UTF8, "application/json"));
                Assert.Equal(HttpStatusCode.Unauthorized, noKey.StatusCode);
                Assert.Contains("no API key", await noKey.Content.ReadAsStringAsync());
                Assert.Null(up.Last);
            }
        }

        [Fact]
        public async Task OllamaDiscovery_TagsShowAndModels()
        {
            var (gw, _, client) = Start();
            using (gw)
            using (client)
            {
                var tags = JObject.Parse(await client.GetStringAsync("/api/tags"));
                Assert.Equal("deepseek-v4", (string)tags["models"][0]["name"]);
                var show = await client.PostAsync("/api/show", new StringContent("{\"model\":\"deepseek-v4:latest\"}", Encoding.UTF8, "application/json"));
                var o = JObject.Parse(await show.Content.ReadAsStringAsync());
                Assert.Contains("vision", o["capabilities"].Select(t => (string)t));
                Assert.Contains("tools", o["capabilities"].Select(t => (string)t));
                Assert.Equal(AgentApiConfig.DefaultContextTokens, (int)o["model_info"]["uetovs.context_length"]);
                var models = JObject.Parse(await client.GetStringAsync("/v1/models"));
                Assert.Equal("deepseek-v4", (string)models["data"][0]["id"]);
                Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync("/v1/embeddings")).StatusCode);
            }
        }

        [Fact]
        public async Task BrowserRequestsAreRefused()
        {
            var (gw, up, client) = Start();
            using (gw)
            using (client)
            {
                var req = new HttpRequestMessage(HttpMethod.Post, "/v1/chat/completions") { Content = new StringContent("{\"model\":\"deepseek-v4\"}") };
                req.Headers.Add("Origin", "https://evil.example");
                Assert.Equal(HttpStatusCode.Forbidden, (await client.SendAsync(req)).StatusCode);
                Assert.Null(up.Last);
            }
        }

        [Fact]
        public async Task ChunkedRequestBody_IsDecoded()
        {
            var (gw, up, client) = Start();
            using (gw)
            using (client)
            {
                var content = new StreamContent(new MemoryStream(Encoding.UTF8.GetBytes("{\"model\":\"deepseek-v4\",\"messages\":[{\"role\":\"user\",\"content\":\"" + new string('x', 20000) + "\"}]}")));
                content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/json");
                var req = new HttpRequestMessage(HttpMethod.Post, "/v1/chat/completions") { Content = content };
                req.Headers.TransferEncodingChunked = true;
                var resp = await client.SendAsync(req);
                Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
                Assert.Equal(20000, ((string)JObject.Parse(up.LastBody)["messages"][0]["content"]).Length);
            }
        }

        [Fact]
        public void Start_FailsWhenPortTaken()
        {
            var l = new TcpListener(IPAddress.Loopback, 0);
            l.Start();
            try
            {
                using (var gw = new AgentGateway(_ => null, new Upstream()))
                {
                    Assert.False(gw.Start(((IPEndPoint)l.LocalEndpoint).Port));
                    Assert.False(gw.IsRunning);
                    Assert.NotNull(gw.LastError);
                }
            }
            finally { l.Stop(); }
        }
    }
}
