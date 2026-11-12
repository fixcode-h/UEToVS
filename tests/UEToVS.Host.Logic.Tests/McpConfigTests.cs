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
            var shared = McpConfigTarget.SharedProject(Sln);
            Assert.Equal(Path.Combine(Sln, ".mcp.json"), shared.Path);
            Assert.Equal(new[] { "mcpServers" }, shared.RootKeys);
            Assert.Equal(new[] { "servers" }, shared.DropKeys);
            var legacy = McpConfigTarget.LegacySolutionRoot(Sln);
            Assert.Equal(new[] { "servers", "mcpServers" }, legacy.RootKeys);
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

        [Fact]
        public void SharedProject_WritesOnlyMcpServers_AndDropsTheOldServersCopy()
        {
            var shared = McpConfigTarget.SharedProject(Sln);
            var created = McpConfig.Merge(null, shared.RootKeys, Entry(), shared.DropKeys);
            var j = JObject.Parse(created.Json);
            Assert.NotNull(j["mcpServers"]["uetovs"]);
            Assert.Null(j["servers"]);

            // file written by an earlier version: both keys, plus another VS server that must stay
            var old = McpConfig.Merge("{\"servers\":{\"github\":{\"url\":\"u\"}}}", Both, Entry());
            var r = McpConfig.Merge(old.Json, shared.RootKeys, Entry(), shared.DropKeys);
            Assert.Equal(McpMergeOutcome.Updated, r.Outcome);
            j = JObject.Parse(r.Json);
            Assert.Null(j["servers"]["uetovs"]);
            Assert.NotNull(j["servers"]["github"]);
            Assert.NotNull(j["mcpServers"]["uetovs"]);
            Assert.Equal(McpMergeOutcome.Unchanged, McpConfig.Merge(r.Json, shared.RootKeys, Entry(), shared.DropKeys).Outcome);
        }

        [Fact]
        public void PortableEntry_HasNoMachinePath()
        {
            var e = McpConfig.BuildPortableEntry(McpConfig.RelativeSolutionDir(@"D:\Repo", @"D:\Repo\Games\MyGame"));
            Assert.Equal("cmd", (string)e["command"]);
            Assert.Equal(new[] { "/c", @"%LOCALAPPDATA%\UEToVS\Mcp\uetovs-mcp.cmd", "--solution-dir", "Games/MyGame" }, e["args"].ToObject<string[]>());
            Assert.True(McpConfig.IsPortableCommand((string)e["command"]));
            Assert.Equal(".", McpConfig.RelativeSolutionDir(@"D:\Repo\", @"d:\repo"));
            Assert.Equal(@"E:\Other", McpConfig.RelativeSolutionDir(@"D:\Repo", @"E:\Other")); // outside: stays absolute
            Assert.Equal(Path.Combine("L", "UEToVS", "Mcp", "uetovs-mcp.cmd"), McpConfig.ResolveLauncherPath("L"));
        }

        [Fact]
        public void PortableEntry_MigratesTheOldAbsoluteForm_KeepingUserArgs_AndIsStable()
        {
            var shared = McpConfigTarget.SharedProject(Sln);
            string old = new JObject
            {
                ["mcpServers"] = new JObject
                {
                    ["uetovs"] = new JObject
                    {
                        ["type"] = "stdio",
                        ["command"] = @"C:\Users\me\AppData\Local\Microsoft\VisualStudio\17.0_x\Extensions\abc\Engine\Mcp\UEToVS.Mcp.exe",
                        ["args"] = new JArray("--log-level", "debug", "--solution-dir", @"D:\Proj"),
                        ["env"] = new JObject { ["X"] = "1" },
                    },
                },
            }.ToString();
            var portable = McpConfig.BuildPortableEntry(".");
            var r = McpConfig.Merge(old, shared.RootKeys, portable, shared.DropKeys);
            Assert.Equal(McpMergeOutcome.Updated, r.Outcome);
            var e = JObject.Parse(r.Json)["mcpServers"]["uetovs"];
            Assert.Equal("cmd", (string)e["command"]);
            Assert.Equal(new[] { "/c", McpConfig.PortableLauncherPath, "--log-level", "debug", "--solution-dir", "." }, e["args"].ToObject<string[]>());
            Assert.Equal("1", (string)e["env"]["X"]);
            Assert.DoesNotContain(@"C:\Users", r.Json);
            Assert.Equal(McpMergeOutcome.Unchanged, McpConfig.Merge(r.Json, shared.RootKeys, portable, shared.DropKeys).Outcome);

            // back to a direct exe (VS's own file): the "/c <launcher>" prefix goes away
            var back = McpConfig.Merge(r.Json, shared.RootKeys, Entry(), shared.DropKeys);
            Assert.Equal(new[] { "--log-level", "debug", "--solution-dir", Sln }, JObject.Parse(back.Json)["mcpServers"]["uetovs"]["args"].ToObject<string[]>());
        }

        [Fact]
        public void Launcher_RunsTheRecordedExe_FallsBackToASearch_AndIsRewrittenOnlyOnChange()
        {
            string text = McpConfig.BuildLauncherScript(Exe);
            Assert.StartsWith("@echo off\r\n", text);
            Assert.Contains("set \"UETOVS_MCP=" + Exe + "\"", text);
            Assert.Contains("\"%UETOVS_MCP%\" %*", text);
            Assert.Contains(@"%LOCALAPPDATA%\Microsoft\VisualStudio\*", text);
            Assert.Contains(@"\Engine\Mcp\UEToVS.Mcp.exe", text);
            Assert.Contains("once. 1>&2\r\n", text); // the error goes to stderr: stdout belongs to the MCP protocol
            string dir = Path.Combine(Path.GetTempPath(), "uetovs-launcher-" + Guid.NewGuid().ToString("N"));
            try
            {
                string path = McpConfig.ResolveLauncherPath(dir);
                Assert.True(McpConfig.WriteLauncher(path, Exe));
                Assert.False(McpConfig.WriteLauncher(path, Exe));
                Assert.True(McpConfig.WriteLauncher(path, @"C:\New\Engine\Mcp\UEToVS.Mcp.exe"));
                Assert.Contains(@"C:\New\", File.ReadAllText(path));
            }
            finally
            {
                if (Directory.Exists(dir)) Directory.Delete(dir, true);
            }
        }

        [Fact]
        public void ApplyToFile_RemoveWithDeleteIfEmpty_DeletesOnlyAnEmptyFile()
        {
            string dir = Path.Combine(Path.GetTempPath(), "uetovs-mcpcfg-" + Guid.NewGuid().ToString("N"));
            try
            {
                Directory.CreateDirectory(dir);
                var legacy = McpConfigTarget.LegacySolutionRoot(dir);
                McpConfig.ApplyToFile(legacy, Entry());
                Assert.Equal(McpMergeOutcome.Removed, McpConfig.ApplyToFile(legacy, null, remove: true, deleteIfEmpty: true).Outcome);
                Assert.False(File.Exists(legacy.Path));

                File.WriteAllText(legacy.Path, "{\"mcpServers\":{\"other\":{\"command\":\"x\"}}}");
                McpConfig.ApplyToFile(legacy, Entry());
                Assert.Equal(McpMergeOutcome.Removed, McpConfig.ApplyToFile(legacy, null, remove: true, deleteIfEmpty: true).Outcome);
                Assert.True(File.Exists(legacy.Path));
                Assert.NotNull(JObject.Parse(File.ReadAllText(legacy.Path))["mcpServers"]["other"]);
            }
            finally
            {
                if (Directory.Exists(dir)) Directory.Delete(dir, true);
            }
        }

        [Theory]
        [InlineData(".git", true)]
        [InlineData(".git", false)] // worktree / submodule: .git is a file
        [InlineData(".svn", true)]
        [InlineData(".hg", true)]
        [InlineData(".plastic", true)]
        [InlineData(".p4config", false)]
        [InlineData("p4config.txt", false)]
        public void VcsRoot_FindsTheMarkerAboveTheSolution(string marker, bool isDir)
        {
            string top = Path.Combine(Path.GetTempPath(), "uetovs-vcs-" + Guid.NewGuid().ToString("N"));
            try
            {
                string repo = Path.Combine(top, "ws_main");
                string sln = Path.Combine(repo, "Project");
                Directory.CreateDirectory(sln);
                Assert.Null(VcsRoot.Find(sln, "none.cfg"));
                if (isDir) Directory.CreateDirectory(Path.Combine(repo, marker));
                else File.WriteAllText(Path.Combine(repo, marker), "x");
                Assert.Equal(repo, VcsRoot.Find(sln, "none.cfg"));
                Assert.Equal(repo, VcsRoot.Find(sln + Path.DirectorySeparatorChar, "none.cfg"));
                Assert.Equal(repo, VcsRoot.Find(repo, "none.cfg"));
            }
            finally
            {
                if (Directory.Exists(top)) Directory.Delete(top, true);
            }
        }

        [Fact]
        public void VcsRoot_NearestRootWins_OuterMostForSvn_AndCustomP4Config()
        {
            string top = Path.Combine(Path.GetTempPath(), "uetovs-vcs-" + Guid.NewGuid().ToString("N"));
            try
            {
                string outer = Path.Combine(top, "outer"), inner = Path.Combine(outer, "inner"), sln = Path.Combine(inner, "Game");
                Directory.CreateDirectory(sln);
                Directory.CreateDirectory(Path.Combine(outer, ".git"));
                Directory.CreateDirectory(Path.Combine(inner, ".git"));
                Assert.Equal(inner, VcsRoot.Find(sln, "none.cfg"));

                string svnTop = Path.Combine(top, "svn"), svnSln = Path.Combine(svnTop, "a", "b");
                Directory.CreateDirectory(Path.Combine(svnSln, ".svn"));
                Directory.CreateDirectory(Path.Combine(svnTop, "a", ".svn"));
                Directory.CreateDirectory(Path.Combine(svnTop, ".svn"));
                Assert.Equal(svnTop, VcsRoot.Find(svnSln, "none.cfg"));

                string p4 = Path.Combine(top, "p4"), p4Sln = Path.Combine(p4, "Project");
                Directory.CreateDirectory(p4Sln);
                File.WriteAllText(Path.Combine(p4, "my.p4"), "P4CLIENT=x");
                Assert.Equal(p4, VcsRoot.Find(p4Sln, "my.p4"));
            }
            finally
            {
                if (Directory.Exists(top)) Directory.Delete(top, true);
            }
        }

        [Fact]
        public void VcsRoot_IsSameOrUnder()
        {
            string a = Path.Combine(Path.GetTempPath(), "ws");
            Assert.True(VcsRoot.IsSameOrUnder(a, a));
            Assert.True(VcsRoot.IsSameOrUnder(Path.Combine(a, "Project"), a));
            Assert.True(VcsRoot.IsSameOrUnder(Path.Combine(a, "Project"), a + Path.DirectorySeparatorChar));
            Assert.False(VcsRoot.IsSameOrUnder(a + "2", a));
            Assert.False(VcsRoot.IsSameOrUnder(a, Path.Combine(a, "Project")));
            Assert.False(VcsRoot.IsSameOrUnder(null, a));
        }
    }
}
