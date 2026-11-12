using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using UEToVS.Core.Diagnostics;
using UEToVS.Host.Logic.Agent;

namespace UEToVS.Host.Agent
{
    /// <summary>
    /// Agent / API: owns the API list (%LocalAppData%\UEToVS\agent-apis.json), the keys (<see cref="AgentSecrets"/>), the
    /// local gateway VS chat talks to (<see cref="AgentGateway"/>) and the registration of the enabled models in VS's
    /// custom model file (<see cref="VsByokConfig"/>). Runs while the package is loaded (C++ solutions / any UEToVS
    /// command); file and network work stays off the UI thread.
    /// </summary>
    internal sealed class AgentService : IDisposable
    {
        private static readonly Logger L = Log.For("Agent");
        private readonly AgentSecrets _secrets;
        private readonly AgentGateway _gateway;
        private readonly HttpClient _probe = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
        private readonly object _gate = new object();
        private AgentApiConfig _config = new AgentApiConfig();

        public AgentService()
        {
            string dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "UEToVS");
            ConfigPath = Path.Combine(dir, "agent-apis.json");
            _secrets = new AgentSecrets(Path.Combine(dir, "agent-keys.dat"));
            VsConfigPath = VsByokConfig.DefaultPath(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData));
            _gateway = new AgentGateway(id => _secrets.Get(id));
        }

        public string ConfigPath { get; }
        public string VsConfigPath { get; }

        /// <summary>Raised (any thread) after the configuration was applied or the gateway / VS registration state changed.</summary>
        public event EventHandler StateChanged;

        /// <summary>Copy of the committed configuration (the settings page edits the copy).</summary>
        public AgentApiConfig Snapshot() { lock (_gate) return _config.Clone(); }

        public bool HasKey(string apiId) => _secrets.Has(apiId);

        public bool GatewayRunning => _gateway.IsRunning;
        public string GatewayError => _gateway.LastError;
        public long GatewayRequests => _gateway.RequestCount;
        public VsByokOutcome LastVsOutcome { get; private set; } = VsByokOutcome.Unchanged;
        public string LastVsError { get; private set; }
        public int RegisteredCount { get; private set; }

        /// <summary>Port the gateway runs on (the configured one, or 47811 when Ollama already has 11434).</summary>
        public int ActivePort { get; private set; } = AgentApiConfig.DefaultPort;

        /// <summary>True when the default port 11434 was taken by another program and the gateway moved to 47811.</summary>
        public bool PortFellBack { get; private set; }

        /// <summary>Package load: read the files, start the gateway when models are enabled, refresh VS's registration.</summary>
        public Task InitializeAsync() => Task.Run(() =>
        {
            AgentApiConfig loaded;
            try { loaded = AgentApiConfig.Load(ConfigPath); }
            catch (Exception ex)
            {
                L.Error("agent.load", "agent-apis.json unreadable; Agent / API starts empty", ex, ("file", ConfigPath));
                loaded = new AgentApiConfig();
            }
            lock (_gate) _config = loaded;
            Activate(loaded, null);
            L.Info("agent.init", null, ("apis", loaded.Apis.Count), ("routes", loaded.Routes().Count), ("gateway", _gateway.IsRunning));
        });

        /// <summary>
        /// Settings page Apply: saves the config and changed keys (apiId → new key; empty = delete), restarts the gateway
        /// on a port change and rewrites VS's model list.
        /// </summary>
        public Task ApplyAsync(AgentApiConfig next, IReadOnlyDictionary<string, string> keyChanges) => Task.Run(() =>
        {
            next = next.Clone();
            next.Normalize();
            AgentApiConfig previous;
            lock (_gate) previous = _config;
            var removed = previous.Apis.Select(a => a.Id).Except(next.Apis.Select(a => a.Id)).ToList();
            if (keyChanges.Count > 0 || removed.Count > 0) _secrets.Update(keyChanges, removed);
            next.Save(ConfigPath);
            lock (_gate) _config = next;
            Activate(next, previous.GatewayPort != next.GatewayPort ? previous.GatewayUrl : null);
            L.Info("agent.apply", null, ("apis", next.Apis.Count), ("routes", next.Routes().Count), ("port", next.GatewayPort), ("keysChanged", keyChanges.Count));
        });

        private void Activate(AgentApiConfig config, string previousGatewayUrl)
        {
            var routes = config.Routes();
            _gateway.Update(config);
            int port = config.GatewayPort;
            bool fellBack = false;
            if (routes.Count > 0)
            {
                // 11434 is Ollama's port: if a real Ollama already listens there, move to 47811 instead of failing.
                port = AgentApiConfig.ChoosePort(config.GatewayPort, p => !(_gateway.IsRunning && _gateway.Port == p) && PortInUse(p), out fellBack);
                if (!_gateway.Start(port) && port == AgentApiConfig.DefaultPort)
                {
                    fellBack = true;
                    port = AgentApiConfig.FallbackPort;
                    _gateway.Start(port);
                }
            }
            else _gateway.Stop();
            if (fellBack) L.Warn("agent.port.fallback", "Port 11434 is used by another program (probably Ollama); the gateway uses 47811", ("port", port));
            ActivePort = port;
            PortFellBack = fellBack;
            // Every URL our models may have been registered under before (port change, fallback) is cleaned up.
            var previous = new List<string> { AgentApiConfig.GatewayUrlFor(AgentApiConfig.DefaultPort), AgentApiConfig.GatewayUrlFor(AgentApiConfig.FallbackPort) };
            if (previousGatewayUrl != null) previous.Add(previousGatewayUrl);
            string url = AgentApiConfig.GatewayUrlFor(port);
            previous.RemoveAll(u => string.Equals(u, url, StringComparison.OrdinalIgnoreCase));
            LastVsOutcome = VsByokConfig.Apply(VsConfigPath, url, routes, previous, out string error);
            LastVsError = error;
            RegisteredCount = routes.Count;
            if (LastVsOutcome == VsByokOutcome.Failed) L.Warn("agent.vs.register.failed", error, ("file", VsConfigPath));
            else L.Info("agent.vs.register", null, ("outcome", LastVsOutcome.ToString()), ("models", routes.Count), ("file", VsConfigPath));
            StateChanged?.Invoke(this, EventArgs.Empty);
        }

        /// <summary>"测试连接" / "从接口获取": lists the models with the key typed on the page (or the stored one).</summary>
        public Task<AgentModelsResult> ListModelsAsync(AgentApi api, string typedKey, CancellationToken ct)
        {
            string key = string.IsNullOrEmpty(typedKey) ? _secrets.Get(api.Id) : typedKey;
            return AgentApiClient.ListModelsAsync(_probe, api, key, ct);
        }

        private static bool PortInUse(int port)
        {
            try
            {
                foreach (var ep in System.Net.NetworkInformation.IPGlobalProperties.GetIPGlobalProperties().GetActiveTcpListeners())
                    if (ep.Port == port) return true;
            }
            catch (System.Net.NetworkInformation.NetworkInformationException ex)
            {
                L.Debug("agent.port.probe.failed", ex.Message, ("port", port));
            }
            return false;
        }

        public void Dispose()
        {
            _gateway.Dispose();
            _probe.Dispose();
        }
    }
}
