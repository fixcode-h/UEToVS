using System;
using System.IO;
using System.Net.Sockets;
using System.Text;
using System.Threading.Tasks;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using UEToVS.UI;

namespace UEToVS.Link
{
    /// <summary>
    /// Client for the UEToVSLink editor plugin: a localhost TCP server inside the running Unreal Editor that
    /// speaks one JSON object per line. The plugin writes its port to &lt;Project&gt;/Saved/UEToVSLink/port.txt.
    /// </summary>
    internal sealed class UeEditorLink
    {
        private readonly Func<string> _projectRoot;
        private readonly Func<int> _portOverride;

        public UeEditorLink(Func<string> projectRoot, Func<int> portOverride)
        {
            _projectRoot = projectRoot;
            _portOverride = portOverride;
        }

        private int ResolvePort()
        {
            int port = _portOverride();
            if (port > 0) return port;
            string root = _projectRoot();
            if (root == null) return 0;
            string file = Path.Combine(root, "Saved", "UEToVSLink", "port.txt");
            try
            {
                return File.Exists(file) && int.TryParse(File.ReadAllText(file).Trim(), out port) ? port : 0;
            }
            catch (IOException)
            {
                return 0;
            }
        }

        /// <summary>Sends a command; returns the reply object, or null when the editor is not reachable.</summary>
        public async Task<JObject> SendAsync(JObject command, int timeoutMs = 3000)
        {
            int port = ResolvePort();
            if (port <= 0) return null;
            try
            {
                using (var client = new TcpClient())
                {
                    var connect = client.ConnectAsync("127.0.0.1", port);
                    if (await Task.WhenAny(connect, Task.Delay(timeoutMs)).ConfigureAwait(false) != connect || !client.Connected) return null;
                    using (var stream = client.GetStream())
                    using (var writer = new StreamWriter(stream, new UTF8Encoding(false)) { NewLine = "\n", AutoFlush = true })
                    using (var reader = new StreamReader(stream, Encoding.UTF8))
                    {
                        await writer.WriteLineAsync(command.ToString(Formatting.None)).ConfigureAwait(false);
                        var read = reader.ReadLineAsync();
                        // Live Coding compiles can take a while; the plugin answers once the compile was started.
                        if (await Task.WhenAny(read, Task.Delay(timeoutMs)).ConfigureAwait(false) != read) return null;
                        string line = await read.ConfigureAwait(false);
                        return string.IsNullOrEmpty(line) ? null : JObject.Parse(line);
                    }
                }
            }
            catch (Exception ex)
            {
                OutputLog.Write("Editor link: " + ex.Message);
                return null;
            }
        }

        public async Task<bool> PingAsync()
        {
            var r = await SendAsync(new JObject { ["cmd"] = "ping" }, 1000).ConfigureAwait(false);
            return r?["ok"]?.Value<bool>() == true;
        }

        public async Task<(bool reached, string message)> OpenAssetAsync(string objectPath)
        {
            var r = await SendAsync(new JObject { ["cmd"] = "openAsset", ["path"] = objectPath }).ConfigureAwait(false);
            return (r != null && r["ok"]?.Value<bool>() == true, r?["message"]?.ToString());
        }

        public async Task<(bool reached, string message)> BrowseToAssetAsync(string objectPath)
        {
            var r = await SendAsync(new JObject { ["cmd"] = "browseAsset", ["path"] = objectPath }).ConfigureAwait(false);
            return (r != null && r["ok"]?.Value<bool>() == true, r?["message"]?.ToString());
        }

        public async Task<(bool reached, string message)> LiveCodingAsync()
        {
            var r = await SendAsync(new JObject { ["cmd"] = "liveCoding" }, 5000).ConfigureAwait(false);
            if (r == null) return (false, null);
            return (r["ok"]?.Value<bool>() == true, r["message"]?.ToString());
        }
    }
}
