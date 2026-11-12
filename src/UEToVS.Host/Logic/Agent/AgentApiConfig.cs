using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using Newtonsoft.Json;

namespace UEToVS.Host.Logic.Agent
{
    /// <summary>How the API key is sent upstream (AgentPanel's "密钥的发送方式").</summary>
    public enum AgentKeyMode
    {
        /// <summary>Authorization: Bearer &lt;key&gt; (OpenAI and most relays).</summary>
        Bearer = 0,
        /// <summary>x-api-key: &lt;key&gt; (Anthropic style relays).</summary>
        XApiKey = 1,
    }

    /// <summary>One model of an API. <see cref="Enabled"/> = registered in VS chat (through the local gateway).</summary>
    public sealed class AgentModel
    {
        public string Id { get; set; }
        public bool Enabled { get; set; } = true;
        public bool ToolCalling { get; set; } = true;
        public bool Vision { get; set; }
        public int ContextTokens { get; set; } = AgentApiConfig.DefaultContextTokens;
        public int MaxOutputTokens { get; set; } = AgentApiConfig.DefaultMaxOutputTokens;

        public AgentModel Clone() => (AgentModel)MemberwiseClone();
    }

    /// <summary>
    /// One configured API (an OpenAI Chat Completions compatible endpoint). The key is NOT part of this object: it is
    /// kept encrypted per user (DPAPI) by the host and looked up by <see cref="Id"/>, so this file never holds secrets.
    /// </summary>
    public sealed class AgentApi
    {
        public string Id { get; set; } = Guid.NewGuid().ToString("N");
        public string Name { get; set; }
        public string BaseUrl { get; set; }
        public AgentKeyMode KeyMode { get; set; } = AgentKeyMode.Bearer;
        /// <summary>Requests in flight to this API at once; 0 = unlimited. Extra requests wait their turn.</summary>
        public int MaxConcurrency { get; set; }
        public List<AgentModel> Models { get; set; } = new List<AgentModel>();

        public AgentApi Clone()
        {
            var c = (AgentApi)MemberwiseClone();
            c.Models = (Models ?? new List<AgentModel>()).Select(m => m.Clone()).ToList();
            return c;
        }

        /// <summary>Display name, falling back to the host of the base URL.</summary>
        public string DisplayName
        {
            get
            {
                if (!string.IsNullOrWhiteSpace(Name)) return Name.Trim();
                return Uri.TryCreate(AgentApiConfig.ResolveApiRoot(BaseUrl) ?? string.Empty, UriKind.Absolute, out var u) ? u.Host : "API";
            }
        }
    }

    /// <summary>
    /// All APIs + the local gateway port (%LocalAppData%\UEToVS\agent-apis.json). Pure data + helpers; the host owns the
    /// file location, the keys and the gateway.
    /// </summary>
    public sealed class AgentApiConfig
    {
        /// <summary>Ollama's own port: VS's "add Ollama provider" dialog pre-fills http://localhost:11434, so the gateway sits there.</summary>
        public const int DefaultPort = 11434;
        /// <summary>Used when a real Ollama (or anything else) already listens on 11434; the default before v0.4.5.</summary>
        public const int FallbackPort = 47811;
        /// <summary>File format: 2 = default port 11434 (older files with the then-default 47811 are moved to 11434).</summary>
        public const int CurrentFormat = 2;
        public const int DefaultContextTokens = 128000;
        public const int DefaultMaxOutputTokens = 16384;

        public int Format { get; set; } = CurrentFormat;
        public int GatewayPort { get; set; } = DefaultPort;
        public List<AgentApi> Apis { get; set; } = new List<AgentApi>();

        public AgentApiConfig Clone() => new AgentApiConfig { Format = Format, GatewayPort = GatewayPort, Apis = (Apis ?? new List<AgentApi>()).Select(a => a.Clone()).ToList() };

        /// <summary>Base URL the gateway is reachable at (what VS gets as the Ollama endpoint).</summary>
        public string GatewayUrl => GatewayUrlFor(GatewayPort);

        public static string GatewayUrlFor(int port) => "http://127.0.0.1:" + port;

        public static bool IsValidPort(int port) => port >= 1024 && port <= 65535;

        /// <summary>
        /// Port the gateway actually uses: the configured one, except that the default 11434 moves to 47811 when another
        /// program (a real Ollama) already listens there. <paramref name="takenByOther"/> answers "is this port in use by
        /// someone else". A port the user picked is never changed.
        /// </summary>
        public static int ChoosePort(int configured, Func<int, bool> takenByOther, out bool fellBack)
        {
            fellBack = false;
            if (configured == DefaultPort && takenByOther != null && takenByOther(DefaultPort))
            {
                fellBack = true;
                return FallbackPort;
            }
            return configured;
        }

        // ------------------------------------------------------------------ persistence

        private static readonly JsonSerializerSettings Json = new JsonSerializerSettings
        {
            Formatting = Formatting.Indented,
            NullValueHandling = NullValueHandling.Ignore,
            MissingMemberHandling = MissingMemberHandling.Ignore,
        };

        public static AgentApiConfig Parse(string json)
        {
            if (string.IsNullOrWhiteSpace(json)) return new AgentApiConfig();
            var c = JsonConvert.DeserializeObject<AgentApiConfig>(json, Json) ?? new AgentApiConfig();
            // Files written before v0.4.5 have no Format: their port 47811 was the old default, not a user choice.
            if (!System.Text.RegularExpressions.Regex.IsMatch(json, "\"Format\"\\s*:")) c.Format = 1;
            c.Normalize();
            return c;
        }

        public string Serialize() => JsonConvert.SerializeObject(this, Json);

        public static AgentApiConfig Load(string path) => File.Exists(path) ? Parse(File.ReadAllText(path, Encoding.UTF8)) : new AgentApiConfig();

        public void Save(string path)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path));
            string tmp = path + ".tmp";
            File.WriteAllText(tmp, Serialize(), new UTF8Encoding(false));
            if (File.Exists(path)) File.Replace(tmp, path, null);
            else File.Move(tmp, path);
        }

        /// <summary>Drops empty/duplicate models, fixes out-of-range numbers (hand-edited files, old versions).</summary>
        public void Normalize()
        {
            if (Format < 2 && GatewayPort == FallbackPort) GatewayPort = DefaultPort; // old default → new default
            Format = CurrentFormat;
            if (!IsValidPort(GatewayPort)) GatewayPort = DefaultPort;
            Apis = (Apis ?? new List<AgentApi>()).Where(a => a != null).ToList();
            foreach (var a in Apis)
            {
                if (string.IsNullOrWhiteSpace(a.Id)) a.Id = Guid.NewGuid().ToString("N");
                if (a.MaxConcurrency < 0) a.MaxConcurrency = 0;
                var seen = new HashSet<string>(StringComparer.Ordinal);
                a.Models = (a.Models ?? new List<AgentModel>())
                    .Where(m => m != null && !string.IsNullOrWhiteSpace(m.Id))
                    .Select(m => { m.Id = m.Id.Trim(); return m; })
                    .Where(m => seen.Add(m.Id))
                    .ToList();
                foreach (var m in a.Models)
                {
                    if (m.ContextTokens <= 0) m.ContextTokens = DefaultContextTokens;
                    if (m.MaxOutputTokens <= 0) m.MaxOutputTokens = DefaultMaxOutputTokens;
                }
            }
        }

        // ------------------------------------------------------------------ URLs

        private static readonly Regex VersionSegment = new Regex(@"^v\d+[a-z0-9]*$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

        /// <summary>
        /// The API root that "/models" and "/chat/completions" hang off. Users paste either the host
        /// ("https://api.deepseek.com"), the versioned root ("https://x.com/v1", "https://open.bigmodel.cn/api/paas/v4") or a
        /// full endpoint ("…/v1/chat/completions"): a known endpoint suffix is cut, a path ending in a version segment is
        /// kept as is, anything else gets "/v1". Null when the text is not an http(s) URL.
        /// </summary>
        public static string ResolveApiRoot(string baseUrl)
        {
            if (string.IsNullOrWhiteSpace(baseUrl)) return null;
            string s = baseUrl.Trim();
            if (!Uri.TryCreate(s, UriKind.Absolute, out var uri) || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps)) return null;
            string path = uri.AbsolutePath.TrimEnd('/');
            foreach (var suffix in new[] { "/chat/completions", "/completions", "/models", "/responses", "/messages" })
            {
                if (path.EndsWith(suffix, StringComparison.OrdinalIgnoreCase)) { path = path.Substring(0, path.Length - suffix.Length); break; }
            }
            string last = path.Length == 0 ? string.Empty : path.Substring(path.LastIndexOf('/') + 1);
            if (!VersionSegment.IsMatch(last)) path += "/v1";
            var b = new UriBuilder(uri) { Path = path, Query = string.Empty, Fragment = string.Empty };
            return b.Uri.GetLeftPart(UriPartial.Path).TrimEnd('/');
        }

        // ------------------------------------------------------------------ routing

        /// <summary>
        /// Enabled models as VS will see them. The VS model id is the upstream id when it is unique across all APIs,
        /// otherwise "&lt;API name&gt;/&lt;id&gt;" so two APIs serving the same model both stay selectable.
        /// </summary>
        public IReadOnlyList<AgentRoute> Routes()
        {
            var enabled = Apis.SelectMany(a => a.Models.Where(m => m.Enabled).Select(m => (Api: a, Model: m))).ToList();
            var counts = enabled.GroupBy(x => x.Model.Id, StringComparer.OrdinalIgnoreCase).ToDictionary(g => g.Key, g => g.Count(), StringComparer.OrdinalIgnoreCase);
            var used = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var list = new List<AgentRoute>();
            foreach (var (api, model) in enabled)
            {
                string id = counts[model.Id] > 1 ? api.DisplayName + "/" + model.Id : model.Id;
                string unique = id;
                for (int i = 2; !used.Add(unique); i++) unique = id + "#" + i;
                list.Add(new AgentRoute(unique, api, model));
            }
            return list;
        }
    }

    /// <summary>A model as exposed to VS: <see cref="VsId"/> is what VS sends as "model"; the gateway maps it back.</summary>
    public sealed class AgentRoute
    {
        public AgentRoute(string vsId, AgentApi api, AgentModel model)
        {
            VsId = vsId;
            Api = api;
            Model = model;
        }

        public string VsId { get; }
        public AgentApi Api { get; }
        public AgentModel Model { get; }

        public string DisplayName => Model.Id + " (" + Api.DisplayName + ")";
    }
}
