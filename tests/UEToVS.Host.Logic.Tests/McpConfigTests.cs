using System;
using System.IO;
using Newtonsoft.Json.Linq;
using UEToVS.Host.Logic.Mcp;
using Xunit;

namespace UEToVS.Host.Logic.Tests
{
    public class McpConfigTests
    {
        private static readonly string Exe = @"C:\Ext\Engine\Mcp\UEToVS.Mcp.exe";
        private static readonly string Sln = @"D:\Proj";
        private static JObject Entry() => McpConfig.BuildEntry(Exe, Sln);
        private static readonly string[] Both = { McpConfig.VsRootKey, McpConfig.PortableRootKey };

        [Fact]
        public void Targets_FollowTheDocumentedLocations()
        {
            var vs = McpConfigTarget.VisualStudioUser(Sln);
            Assert.Equal(Path.Combine(Sln, ".vs", "mcp.json"), vs.Path);
            Assert.Equal(new[] { "servers" }, vs.RootKeys);
            var root = McpConfigTarget.SolutionRoot(Sln);
            Assert.Equal(Path.Combine(Sln, ".mcp.json"), root.Path);
            Assert.Equal(new[] { "servers", "mcpServers" }, root.RootKeys);
            Assert.Equal(Path.Combine("X", "Engine", "Mcp", "UEToVS.Mcp.exe"), McpConfig.ResolveExecutable("X"));
        }

        [Fact]
        public void Create_WritesBothRootKeys()
        {
            var r = McpConfig.Merge(null, Both, Entry());
            Assert.Equal(McpMergeOutcome.Created, r.Outcome);
            var j = JObject.Parse(r.Json);
            foreach (var key in Both)
            {
                var e = j[key]["uetovs"];
                Assert.Equal("stdio", (string)e["type"]);
                Assert.Equal(Exe, (string)e["command"]);
                Assert.Equal(new[] { "--solution-dir", Sln }, e["args"].ToObject<string[]>());
            }
        }

        [Fact]
        public void Merge_KeepsOtherServersAndUnknownKeys_AndIgnoresComments()
        {
            string existing = "// my servers\n{ \"inputs\": [ {\"id\":\"x\"} ], \"servers\": { \"github\": { \"url\": \"https://api.githubcopilot.com/mcp/\" } } }";
            var r = McpConfig.Merge(existing, Both, Entry());
            Assert.Equal(McpMergeOutcome.Updated, r.Outcome);
            Assert.Equal(1, r.OtherServers);
            var j = JObject.Parse(r.Json);
            Assert.Equal("https://api.githubcopilot.com/mcp/", (string)j["servers"]["github"]["url"]);
            Assert.Equal("x", (string)j["inputs"][0]["id"]);
            Assert.NotNull(j["servers"]["uetovs"]);
            Assert.NotNull(j["mcpServers"]["uetovs"]);
        }

        [Fact]
        public void Merge_SecondTime_IsUnchanged_AndKeepsTheTextVerbatim()
        {
            var first = McpConfig.Merge(null, Both, Entry());
            var second = McpConfig.Merge(first.Json, Both, Entry());
            Assert.Equal(McpMergeOutcome.Unchanged, second.Outcome);
            Assert.Same(first.Json, second.Json);
        }

        [Fact]
        public void Merge_UpdatesCommandAndSolutionDir_KeepsUserArgsAndEnv()
        {
            string existing = new JObject
            {
                ["servers"] = new JObject
                {
                    ["uetovs"] = new JObject
                    {
                        ["type"] = "stdio",
                        ["command"] = @"C:\Old\Engine\Mcp\UEToVS.Mcp.exe",
                        ["args"] = new JArray("--log-level", "debug", "--solution-dir", @"D:\Old"),
                        ["env"] = new JObject { ["X"] = "1" },
                    },
                },
            }.ToString();
            var r = McpConfig.Merge(existing, new[] { "servers" }, Entry());
            Assert.Equal(McpMergeOutcome.Updated, r.Outcome);
            var e = JObject.Parse(r.Json)["servers"]["uetovs"];
            Assert.Equal(Exe, (string)e["command"]);
            Assert.Equal(new[] { "--log-level", "debug", "--solution-dir", Sln }, e["args"].ToObject<string[]>());
            Assert.Equal("1", (string)e["env"]["X"]);
        }

        [Theory]
        [InlineData("{ not json")]
        [InlineData("[1,2]")]
        [InlineData("{\"servers\": [] }")]
        public void Merge_InvalidFile_IsLeftUntouched(string existing)
        {
            var r = McpConfig.Merge(existing, Both, Entry());
            Assert.Equal(McpMergeOutcome.Invalid, r.Outcome);
            Assert.Equal(existing, r.Json);
            Assert.False(string.IsNullOrEmpty(r.Error));
        }

        [Fact]
        public void Remove_DropsOnlyOurEntry_AndEmptyRoots()
        {
            var created = McpConfig.Merge("{\"servers\":{\"other\":{\"command\":\"x\"}}}", Both, Entry());
            var r = McpConfig.Remove(created.Json, Both);
            Assert.Equal(McpMergeOutcome.Removed, r.Outcome);
            var j = JObject.Parse(r.Json);
            Assert.Null(j["servers"]["uetovs"]);
            Assert.NotNull(j["servers"]["other"]);
            Assert.Null(j["mcpServers"]); // became empty
            Assert.Equal(McpMergeOutcome.Unchanged, McpConfig.Remove(r.Json, Both).Outcome);
            Assert.Equal(McpMergeOutcome.Unchanged, McpConfig.Remove(null, Both).Outcome);
        }

        [Fact]
        public void ExistingCommand_ReadsOurEntry()
        {
            var created = McpConfig.Merge(null, Both, Entry());
            Assert.Equal(Exe, McpConfig.ExistingCommand(created.Json, Both));
            Assert.Null(McpConfig.ExistingCommand("{}", Both));
            Assert.Null(McpConfig.ExistingCommand("nope", Both));
        }

        [Fact]
        public void ApplyToFile_CreatesMergesAndRemoves()
        {
            string dir = Path.Combine(Path.GetTempPath(), "uetovs-mcpcfg-" + Guid.NewGuid().ToString("N"));
            try
            {
                var target = McpConfigTarget.VisualStudioUser(dir);
                Assert.Equal(McpMergeOutcome.Created, McpConfig.ApplyToFile(target, Entry()).Outcome);
                Assert.True(File.Exists(target.Path));
                Assert.Equal(McpMergeOutcome.Unchanged, McpConfig.ApplyToFile(target, Entry()).Outcome);
                Assert.Equal(McpMergeOutcome.Updated, McpConfig.ApplyToFile(target, McpConfig.BuildEntry("other.exe", dir)).Outcome);
                Assert.Equal("other.exe", McpConfig.ExistingCommand(File.ReadAllText(target.Path), target.RootKeys));
                Assert.Equal(McpMergeOutcome.Removed, McpConfig.ApplyToFile(target, null, remove: true).Outcome);
                Assert.Equal("{}", JObject.Parse(File.ReadAllText(target.Path)).ToString(Newtonsoft.Json.Formatting.None));
                File.WriteAllText(target.Path, "broken");
                Assert.Equal(McpMergeOutcome.Invalid, McpConfig.ApplyToFile(target, Entry()).Outcome);
                Assert.Equal("broken", File.ReadAllText(target.Path));
            }
            finally
            {
                if (Directory.Exists(dir)) Directory.Delete(dir, true);
            }
        }
    }
}
