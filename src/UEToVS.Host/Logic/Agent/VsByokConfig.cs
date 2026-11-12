using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace UEToVS.Host.Logic.Agent
{
    /// <summary>Outcome of writing the UEToVS models into VS's custom model file.</summary>
    public enum VsByokOutcome
    {
        Unchanged,
        Written,
        Failed,
    }

    /// <summary>
    /// Visual Studio Copilot Chat's "bring your own model" file. VS has no extension API for chat models; its "添加模型提供程序"
    /// dialog writes %LocalAppData%\Microsoft\VisualStudio\Copilot\BringYourOwnModel\ConfiguredBringYourOwnModel_v1.json
    /// (Newtonsoft, PascalCase): a list of providers {Name, IsApiKeyAvailable, Models[], Endpoint}. Found by inspecting
    /// Copilot 18.6; see docs/design/Agent-API.md. Only the "ollama" provider (Endpoint 10) honours a per-model URL
    /// (Model.CustomURL → OpenAI Chat Completions client at scheme://host:port/v1), so UEToVS registers its models there,
    /// pointing at the local gateway. Models of other providers and the user's own Ollama models are left untouched: ours
    /// are recognised by their CustomURL being the gateway URL.
    /// </summary>
    public static class VsByokConfig
    {
        public const string ProviderName = "ollama";
        /// <summary>CopilotModelEndpoint.Ollama.</summary>
        public const int OllamaEndpoint = 10;

        public static string DefaultPath(string localAppData) =>
            Path.Combine(localAppData, "Microsoft", "VisualStudio", "Copilot", "BringYourOwnModel", "ConfiguredBringYourOwnModel_v1.json");

        /// <summary>
        /// Returns the new file text with exactly <paramref name="routes"/> registered for <paramref name="gatewayUrl"/>.
        /// <paramref name="previousGatewayUrls"/>: earlier gateway URLs (port changed) whose models are removed too.
        /// </summary>
        public static string Merge(string existingJson, string gatewayUrl, IReadOnlyList<AgentRoute> routes, IEnumerable<string> previousGatewayUrls = null)
        {
            var providers = ParseProviders(existingJson);
            var ours = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { Norm(gatewayUrl) };
            foreach (var u in previousGatewayUrls ?? Enumerable.Empty<string>()) if (!string.IsNullOrEmpty(u)) ours.Add(Norm(u));

            var ollama = providers.OfType<JObject>().FirstOrDefault(p => string.Equals((string)p["Name"], ProviderName, StringComparison.OrdinalIgnoreCase))
                         ?? providers.OfType<JObject>().FirstOrDefault(p => p["Endpoint"]?.Type == JTokenType.Integer && (int)p["Endpoint"] == OllamaEndpoint);
            bool created = false;
            if (ollama == null)
            {
                if (routes.Count == 0) return Serialize(providers);
                ollama = new JObject { ["Name"] = ProviderName, ["IsApiKeyAvailable"] = true, ["Models"] = new JArray(), ["Endpoint"] = OllamaEndpoint };
                providers.Add(ollama);
                created = true;
            }
            var models = ollama["Models"] as JArray ?? new JArray();
            var keep = models.Where(m => !(m is JObject mo) || !ours.Contains(Norm((string)mo["CustomURL"]))).ToList();
            var next = new JArray(keep);
            foreach (var r in routes)
            {
                next.Add(new JObject
                {
                    ["Id"] = r.VsId,
                    ["DisplayName"] = r.DisplayName,
                    ["IsToolCallingEnabled"] = r.Model.ToolCalling,
                    ["IsVisionEnabled"] = r.Model.Vision,
                    ["MaxInputTokens"] = r.Model.ContextTokens,
                    ["MaxOutputTokens"] = r.Model.MaxOutputTokens,
                    ["ProviderName"] = ProviderName,
                    ["IsCustom"] = true,
                    ["IsSelected"] = true,
                    ["CustomURL"] = gatewayUrl,
                });
            }
            ollama["Models"] = next;
            if (routes.Count > 0) ollama["IsApiKeyAvailable"] = true;
            // An ollama provider that only ever held our models goes away with them (VS would show an empty provider).
            if (next.Count == 0 && (created || keep.Count == 0 && models.Count > 0)) providers.Remove(ollama);
            return Serialize(providers);
        }

        /// <summary>Reads, merges and writes the file (atomic replace; one-time backup of the user's original file).</summary>
        public static VsByokOutcome Apply(string path, string gatewayUrl, IReadOnlyList<AgentRoute> routes, IEnumerable<string> previousGatewayUrls, out string error)
        {
            error = null;
            try
            {
                string existing = File.Exists(path) ? File.ReadAllText(path, Encoding.UTF8) : null;
                if (existing == null && routes.Count == 0) return VsByokOutcome.Unchanged;
                string merged = Merge(existing, gatewayUrl, routes, previousGatewayUrls);
                if (existing != null && SameJson(existing, merged)) return VsByokOutcome.Unchanged;
                Directory.CreateDirectory(Path.GetDirectoryName(path));
                string backup = path + ".uetovs-backup";
                if (existing != null && !File.Exists(backup)) File.WriteAllText(backup, existing, new UTF8Encoding(false));
                string tmp = path + ".uetovs-tmp";
                File.WriteAllText(tmp, merged, new UTF8Encoding(false));
                if (File.Exists(path)) File.Replace(tmp, path, null);
                else File.Move(tmp, path);
                return VsByokOutcome.Written;
            }
            catch (Exception ex)
            {
                error = ex.Message;
                return VsByokOutcome.Failed;
            }
        }

        /// <summary>VS model ids currently registered for the gateway (for the status line).</summary>
        public static IReadOnlyList<string> RegisteredIds(string json, string gatewayUrl)
        {
            string g = Norm(gatewayUrl);
            return ParseProviders(json).OfType<JObject>()
                .SelectMany(p => (p["Models"] as JArray ?? new JArray()).OfType<JObject>())
                .Where(m => Norm((string)m["CustomURL"]) == g)
                .Select(m => (string)m["Id"]).Where(id => id != null).ToList();
        }

        private static JArray ParseProviders(string json)
        {
            if (string.IsNullOrWhiteSpace(json)) return new JArray();
            // A file VS can't read either; refusing to touch it is safer than replacing the user's providers.
            var t = JToken.Parse(json);
            if (t is JArray a) return a;
            throw new InvalidDataException("ConfiguredBringYourOwnModel_v1.json is not a JSON array");
        }

        private static string Serialize(JArray providers) => providers.ToString(Formatting.Indented);

        private static bool SameJson(string a, string b)
        {
            try { return JToken.DeepEquals(JToken.Parse(a), JToken.Parse(b)); }
            catch (Exception) { return false; }
        }

        private static string Norm(string url) => (url ?? string.Empty).Trim().TrimEnd('/').ToLowerInvariant();
    }
}
