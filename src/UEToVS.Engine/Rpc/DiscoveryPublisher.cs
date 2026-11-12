using System;
using System.Threading.Tasks;
using UEToVS.Contracts;
using UEToVS.Core.Diagnostics;
using UEToVS.Core.Storage;

namespace UEToVS.Engine.Rpc
{
    /// <summary>
    /// Writes &lt;solution&gt;/.vs/UEToVS/engine.json once the pipe accepts connections and removes it on exit, so local
    /// clients other than the VS host (UEToVS.Mcp for AI agents) can find this engine. See <see cref="EngineDiscoveryFile"/>.
    /// </summary>
    internal static class DiscoveryPublisher
    {
        private static readonly Logger L = Log.For("Discovery");

        public static async Task PublishWhenListeningAsync(PipeServer server, EngineHost host)
        {
            try
            {
                await server.Listening.ConfigureAwait(false);
            }
            catch (Exception)
            {
                return; // accept failed; logged by the pipe server
            }
            var file = new EngineDiscoveryFile
            {
                Pipe = host.Options.PipeName,
                Pid = Environment.ProcessId,
                ProtocolVersion = ProtocolInfo.Version,
                EngineVersion = EngineHost.Version,
                SolutionDir = host.Options.SolutionDir,
                StartedUtc = DateTime.UtcNow,
            };
            string path = file.Write(host.Paths);
            if (path != null) L.Info("discovery.written", null, ("path", path), ("pipe", file.Pipe));
        }

        public static void Remove(EngineHost host)
        {
            if (host == null) return;
            EngineDiscoveryFile.DeleteIfOwned(host.Paths, Environment.ProcessId);
        }
    }
}
