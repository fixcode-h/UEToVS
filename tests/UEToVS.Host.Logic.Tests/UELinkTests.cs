using System;
using System.IO;
using System.IO.Compression;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;
using UEToVS.Host.Logic.Link;
using Xunit;

namespace UEToVS.Host.Logic.Tests
{
    /// <summary>UELink (the editor plugin shared with Agent Panel): old plugins, never downgrade, releases, cards and tokens.</summary>
    public class UELinkTests
    {
        private sealed class Temp : IDisposable
        {
            public readonly string Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "uetovs-uelink-" + Guid.NewGuid().ToString("N"));

            public string File(string rel, string text)
            {
                string f = System.IO.Path.Combine(Path, rel.Replace('/', System.IO.Path.DirectorySeparatorChar));
                Directory.CreateDirectory(System.IO.Path.GetDirectoryName(f));
                System.IO.File.WriteAllText(f, text);
                return f;
            }

            public void Dispose()
            {
                try { Directory.Delete(Path, true); } catch (IOException) { }
            }
        }

        private static string UPlugin(int v, string name) => "{\n\t\"FileVersion\": 3,\n\t\"Version\": " + v + ",\n\t\"VersionName\": \"" + name + "\"\n}\n";

        private static string Bundle(Temp t, string version)
        {
            t.File("bundle/UELink.uplugin", UPlugin(2, version));
            t.File("bundle/Source/UELinkCore/Private/A.cpp", "// a");
            t.File("bundle/Config/DefaultUELink.ini", "[CoreRedirects]");
            return System.IO.Path.Combine(t.Path, "bundle");
        }

        [Fact]
        public void OldPluginsOnlyMeanAnUpdateAndInstallRemovesThem()
        {
            using (var t = new Temp())
            {
                string bundled = Bundle(t, "2.0.0");
                string project = System.IO.Path.Combine(t.Path, "Proj");
                t.File("Proj/Plugins/UEToVSLink/UEToVSLink.uplugin", UPlugin(2, "0.2.0"));
                t.File("Proj/Plugins/Tools/AgentPanelBridge/AgentPanelBridge.uplugin", UPlugin(1, "1.7.0"));
                string uproject = t.File("Proj/Proj.uproject",
                    "{\n\t\"FileVersion\": 3,\n\t\"Plugins\": [\n\t\t{ \"Name\": \"UEToVSLink\", \"Enabled\": true },\n\t\t{ \"Name\": \"AgentPanelBridge\", \"Enabled\": true },\n\t\t{ \"Name\": \"Other\", \"Enabled\": true }\n\t]\n}\n");

                var st = LinkPluginInstaller.GetStatus(project, null, bundled);
                Assert.Null(st.Installed);
                Assert.Equal(2, st.Legacy.Count);
                Assert.Equal(LinkPluginStatusKind.UpdateAvailable, st.Kind);
                Assert.True(st.CanInstall);
                Assert.False(st.CanUninstall);

                var r = LinkPluginInstaller.Install(bundled, st.InstallTarget,
                    new LinkPluginInstaller.InstallOptions { ProjectRoot = project, UProjectFile = uproject, Source = "bundled" });
                Assert.True(r.Ok, r.Detail);
                Assert.Equal(2, r.RemovedLegacy.Count);
                Assert.True(r.UProjectChanged);
                Assert.False(Directory.Exists(System.IO.Path.Combine(project, "Plugins", "UEToVSLink")));
                Assert.False(Directory.Exists(System.IO.Path.Combine(project, "Plugins", "Tools", "AgentPanelBridge")));
                string text = File.ReadAllText(uproject);
                Assert.DoesNotContain("UEToVSLink", text);
                Assert.DoesNotContain("AgentPanelBridge", text);
                Assert.Contains("Other", text);

                var after = LinkPluginInstaller.GetStatus(project, null, bundled);
                Assert.Equal(LinkPluginStatusKind.UpToDate, after.Kind);
                Assert.Empty(after.Legacy);
                Assert.Equal("uetovs", after.Installed.InstalledBy);
            }
        }

        [Fact]
        public void NeverDowngradesAndKeepsTheOtherClientsMode()
        {
            using (var t = new Temp())
            {
                string bundled = Bundle(t, "2.0.0");
                string project = System.IO.Path.Combine(t.Path, "Proj");
                string target = LinkPluginInstaller.ProjectTarget(project);
                t.File("Proj/Plugins/UELink/UELink.uplugin", UPlugin(3, "2.1.0"));
                t.File("Proj/Plugins/UELink/.install.json", "{\"version\":\"2.1.0\",\"by\":\"agent-panel\",\"source\":\"github\",\"mode\":\"local\"}");

                var st = LinkPluginInstaller.GetStatus(project, null, bundled);
                Assert.Equal(LinkPluginStatusKind.Newer, st.Kind);
                Assert.Equal("agent-panel", st.Installed.InstalledBy);
                Assert.False(st.CanInstall);
                var refused = LinkPluginInstaller.Install(bundled, target);
                Assert.Equal("newer", refused.Code);
                Assert.Equal("2.1.0", LinkPluginInstaller.ReadVersion(System.IO.Path.Combine(target, "UELink.uplugin"))?.VersionName);

                // A newer release than the installed one: update, keep Agent Panel's "local" mode.
                t.File("rel/UELink.uplugin", UPlugin(4, "2.2.0"));
                t.File("rel/Source/UELinkCore/Private/A.cpp", "// a");
                var r = LinkPluginInstaller.Install(System.IO.Path.Combine(t.Path, "rel"), target,
                    new LinkPluginInstaller.InstallOptions { ProjectRoot = project, Source = "github" });
                Assert.True(r.Ok, r.Detail);
                var record = LinkPluginInstaller.ReadInstallRecord(target);
                Assert.Equal("local", record["mode"].ToString());
                Assert.Equal("github", record["source"].ToString());
                Assert.Equal("2.2.0", record["version"].ToString());
            }
        }

        [Fact]
        public void ApplyRemoteOffersNewerReleasesOnly()
        {
            using (var t = new Temp())
            {
                string bundled = Bundle(t, "2.0.0");
                string project = System.IO.Path.Combine(t.Path, "Proj");
                t.File("Proj/Plugins/UELink/UELink.uplugin", UPlugin(2, "2.0.0"));

                var st = LinkPluginInstaller.GetStatus(project, null, bundled);
                LinkPluginInstaller.ApplyRemote(st, "2.0.0", null);
                Assert.Null(st.RemoteVersionName);
                Assert.Equal(LinkPluginStatusKind.UpToDate, st.Kind);

                LinkPluginInstaller.ApplyRemote(st, "2.1.0", null);
                Assert.Equal("2.1.0", st.RemoteVersionName);
                Assert.Equal(LinkPluginStatusKind.UpdateAvailable, st.Kind);

                var failed = LinkPluginInstaller.GetStatus(project, null, bundled);
                LinkPluginInstaller.ApplyRemote(failed, null, "timeout");
                Assert.Equal("timeout", failed.RemoteError);
                Assert.Equal(LinkPluginStatusKind.UpToDate, failed.Kind);
            }
        }

        private static byte[] Zip(params (string Name, string Text)[] files)
        {
            using (var ms = new MemoryStream())
            {
                using (var z = new ZipArchive(ms, ZipArchiveMode.Create, true))
                    foreach (var (name, text) in files)
                        using (var w = new StreamWriter(z.CreateEntry(name).Open()))
                            w.Write(text);
                return ms.ToArray();
            }
        }

        [Fact]
        public void ReleaseZipIsCheckedAndUnpacked()
        {
            using (var t = new Temp())
            {
                byte[] zip = Zip(("UELink/UELink.uplugin", UPlugin(3, "2.1.0")), ("UELink/Source/UELinkCore/Private/A.cpp", "// a"));
                string json = "{\"name\":\"UELink\",\"version\":\"2.1.0\",\"asset\":\"UELink-2.1.0-src.zip\",\"url\":\"https://example.invalid/z.zip\",\"sha256\":\"" +
                              UELinkRelease.Sha256Hex(zip).ToUpperInvariant() + "\"}";
                var m = UELinkRelease.ParseManifest(json);
                Assert.NotNull(m);
                string cache = System.IO.Path.Combine(t.Path, "cache");
                Assert.Null(UELinkRelease.Cached(cache, "2.1.0"));
                string dir = UELinkRelease.Unpack(zip, m, cache);
                Assert.True(File.Exists(System.IO.Path.Combine(dir, "UELink.uplugin")));
                Assert.Equal(dir, UELinkRelease.Cached(cache, "2.1.0"));

                var bad = UELinkRelease.ParseManifest(json.Replace(m.Sha256.ToUpperInvariant(), new string('0', 64)));
                Assert.Throws<InvalidDataException>(() => UELinkRelease.Unpack(zip, bad, System.IO.Path.Combine(t.Path, "cache2")));
                Assert.False(Directory.Exists(System.IO.Path.Combine(t.Path, "cache2", "2.1.0")));

                byte[] evil = Zip(("../evil.txt", "x"));
                var em = UELinkRelease.ParseManifest(json.Replace(m.Sha256.ToUpperInvariant(), UELinkRelease.Sha256Hex(evil)));
                Assert.Throws<InvalidDataException>(() => UELinkRelease.Unpack(evil, em, System.IO.Path.Combine(t.Path, "cache3")));
                Assert.False(File.Exists(System.IO.Path.Combine(t.Path, "evil.txt")));
            }
        }

        [Theory]
        [InlineData("{\"version\":\"1.0.0\",\"asset\":\"a.zip\"}")]
        [InlineData("{\"version\":\"1.0.0\",\"asset\":\"../a.zip\",\"sha256\":\"ab\"}")]
        [InlineData("not json")]
        public void RejectsBadManifests(string json) => Assert.Null(UELinkRelease.ParseManifest(json));

        [Fact]
        public void ResolvesTheUpdateSource()
        {
            var gh = UELinkRelease.ResolveSource("");
            Assert.Equal(UELinkRelease.DefaultManifestUrl, gh.ManifestUrl);
            Assert.Equal("github", gh.Kind);
            var m = new UELinkManifest { Asset = "UELink-2.1.0-src.zip", Url = "https://github.com/x/z.zip" };
            Assert.Equal("https://github.com/x/z.zip", gh.ZipUrl(m));
            var mirror = UELinkRelease.ResolveSource("https://mirror.example/uelink/");
            Assert.Equal("https://mirror.example/uelink/manifest.json", mirror.ManifestUrl);
            Assert.Equal("https://mirror.example/uelink/UELink-2.1.0-src.zip", mirror.ZipUrl(m));
            var folder = UELinkRelease.ResolveSource(@"D:\share\uelink");
            Assert.Equal(@"D:\share\uelink", folder.Folder);
            Assert.Null(folder.ZipUrl(m));
        }

        [Fact]
        public void ReadsLiveCardsNewestFirst()
        {
            using (var t = new Temp())
            {
                t.File("Saved/UELink/instances/100.json", "{\"pid\":100,\"kind\":\"editor\",\"port\":5100,\"stream\":6100.0,\"token\":\"old\",\"startedAt\":\"2026-11-14T01:00:00.000Z\"}");
                t.File("Saved/UELink/instances/200.json", "{\"pid\":200,\"kind\":\"editor\",\"port\":5200,\"stream\":6200,\"token\":\"new\",\"startedAt\":\"2026-11-14T02:00:00.000Z\"}");
                t.File("Saved/UELink/instances/300.json", "{\"pid\":300,\"kind\":\"editor\",\"stream\":6300,\"token\":\"dead\",\"startedAt\":\"2026-11-14T03:00:00.000Z\"}");
                t.File("Saved/UELink/instances/400.json", "{\"pid\":400,\"kind\":\"commandlet\",\"port\":5400,\"token\":\"nostream\"}");
                t.File("Saved/UELink/instances/500.json", "{ broken");
                var cards = LinkCard.ReadLive(t.Path, pid => pid != 300);
                Assert.Equal(2, cards.Count);
                Assert.Equal(6200, cards[0].StreamPort);
                Assert.Equal("new", cards[0].Token);
                Assert.Equal(6100, cards[1].StreamPort);
                Assert.Equal(5100, cards[1].HttpPort);
            }
        }

        [Fact]
        public async Task ClientUsesTheCardAndSendsTheToken()
        {
            var listener = new TcpListener(IPAddress.Loopback, 0);
            listener.Start();
            int port = ((IPEndPoint)listener.LocalEndpoint).Port;
            using (var t = new Temp())
            {
                t.File("Saved/UELink/instances/42.json", "{\"pid\":42,\"kind\":\"editor\",\"stream\":" + port + ",\"token\":\"tok-1\",\"startedAt\":\"2026-11-14T02:00:00Z\"}");
                t.File("Saved/UEToVSLink/port.txt", "1\n"); // stale compat file: the card wins
                var server = Task.Run(async () =>
                {
                    using (var c = await listener.AcceptTcpClientAsync())
                    using (var s = c.GetStream())
                    using (var r = new StreamReader(s, Encoding.UTF8))
                    using (var w = new StreamWriter(s, new UTF8Encoding(false)) { NewLine = "\n", AutoFlush = true })
                    {
                        string line = await r.ReadLineAsync();
                        await w.WriteLineAsync("{\"ok\":true,\"message\":\"pong\"}");
                        return line;
                    }
                });
                var client = new UeEditorLinkClient(() => t.Path, () => 0) { ProcessAlive = pid => pid == 42 };
                var reply = await client.PingAsync();
                Assert.True(reply.Reached);
                var sent = JObject.Parse(await server);
                Assert.Equal("ping", sent["cmd"].ToString());
                Assert.Equal("tok-1", sent["token"].ToString());

                // Card's editor gone: back to port.txt (old UEToVSLink) and no token.
                var fallback = new UeEditorLinkClient(() => t.Path, () => 0) { ProcessAlive = _ => false };
                Assert.Equal(1, fallback.ResolvePort());
                Assert.Null(fallback.Token);
            }
            listener.Stop();
        }
    }
}
