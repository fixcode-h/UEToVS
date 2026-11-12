using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;

namespace UEToVS.Host.Logic.Agent
{
    /// <summary>Result of "测试连接" / "从接口获取".</summary>
    public sealed class AgentModelsResult
    {
        public bool Ok { get; set; }
        public int StatusCode { get; set; }
        /// <summary>Error text to show as is (HTTP status + upstream message, or the exception message).</summary>
        public string Error { get; set; }
        public IReadOnlyList<string> Models { get; set; } = Array.Empty<string>();
        public long ElapsedMs { get; set; }
        /// <summary>The URL that was called (shown so a wrong base URL is obvious).</summary>
        public string Url { get; set; }
    }

    /// <summary>Talks to a configured API directly (not through the gateway): list models, test the key.</summary>
    public static class AgentApiClient
    {
        /// <summary>Adds the key the way the API wants it. A key is never logged.</summary>
        public static void ApplyKey(HttpRequestMessage request, AgentKeyMode mode, string key)
        {
            if (string.IsNullOrEmpty(key)) return;
            request.Headers.Remove("Authorization");
            request.Headers.Remove("x-api-key");
            if (mode == AgentKeyMode.XApiKey)
            {
                request.Headers.TryAddWithoutValidation("x-api-key", key);
                // Anthropic-style relays also want a version header on every call.
                if (!request.Headers.Contains("anthropic-version")) request.Headers.TryAddWithoutValidation("anthropic-version", "2023-06-01");
            }
            else request.Headers.TryAddWithoutValidation("Authorization", "Bearer " + key);
        }

        /// <summary>GET &lt;root&gt;/models. Errors come back in the result (status + upstream message), never thrown.</summary>
        public static async Task<AgentModelsResult> ListModelsAsync(HttpClient http, AgentApi api, string key, CancellationToken ct)
        {
            var sw = Stopwatch.StartNew();
            string root = AgentApiConfig.ResolveApiRoot(api.BaseUrl);
            var result = new AgentModelsResult();
            if (root == null)
            {
                result.Error = "invalid-url";
                return result;
            }
            result.Url = root + "/models";
            try
            {
                using (var req = new HttpRequestMessage(HttpMethod.Get, result.Url))
                {
                    ApplyKey(req, api.KeyMode, key);
                    using (var resp = await http.SendAsync(req, ct).ConfigureAwait(false))
                    {
                        string body = resp.Content == null ? string.Empty : await resp.Content.ReadAsStringAsync().ConfigureAwait(false);
                        result.StatusCode = (int)resp.StatusCode;
                        if (!resp.IsSuccessStatusCode)
                        {
                            result.Error = "HTTP " + (int)resp.StatusCode + " " + resp.ReasonPhrase + ErrorDetail(body);
                            return result;
                        }
                        result.Models = ParseModelIds(body);
                        result.Ok = true;
                        return result;
                    }
                }
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                result.Error = "timeout";
                return result;
            }
            catch (Exception ex)
            {
                result.Error = Innermost(ex).Message;
                return result;
            }
            finally
            {
                result.ElapsedMs = sw.ElapsedMilliseconds;
            }
        }

        /// <summary>
        /// Model ids from a models listing: OpenAI/Anthropic {"data":[{"id":..}]}, Ollama {"models":[{"name":..}]}, or a
        /// bare array of objects/strings. Sorted, de-duplicated. Unknown shapes give an empty list.
        /// </summary>
        public static IReadOnlyList<string> ParseModelIds(string json)
        {
            JToken root;
            try { root = JToken.Parse(json ?? string.Empty); }
            catch (Exception) { return Array.Empty<string>(); }
            JToken items = root;
            if (root is JObject o) items = o["data"] ?? o["models"] ?? o["result"];
            if (!(items is JArray arr)) return Array.Empty<string>();
            var ids = new List<string>();
            foreach (var it in arr)
            {
                string id = it.Type == JTokenType.String ? (string)it
                    : it is JObject m ? (string)(m["id"] ?? m["name"] ?? m["model"]) : null;
                if (!string.IsNullOrWhiteSpace(id)) ids.Add(id.Trim());
            }
            return ids.Distinct(StringComparer.Ordinal).OrderBy(x => x, StringComparer.OrdinalIgnoreCase).ToList();
        }

        /// <summary>": message" from an OpenAI-style error body ({"error":{"message":..}} / {"message":..}), else a short raw snippet.</summary>
        public static string ErrorDetail(string body)
        {
            if (string.IsNullOrWhiteSpace(body)) return string.Empty;
            try
            {
                var t = JToken.Parse(body);
                if (t is JObject o)
                {
                    var err = o["error"];
                    string msg = err is JObject eo ? (string)(eo["message"] ?? eo["type"]) : err?.Type == JTokenType.String ? (string)err : (string)o["message"];
                    if (!string.IsNullOrWhiteSpace(msg)) return ": " + msg.Trim();
                }
            }
            catch (Exception)
            {
                // not JSON: fall through to the raw snippet
            }
            string s = body.Trim().Replace("\r", " ").Replace("\n", " ");
            return ": " + (s.Length > 200 ? s.Substring(0, 200) + "…" : s);
        }

        private static Exception Innermost(Exception ex)
        {
            while (ex.InnerException != null) ex = ex.InnerException;
            return ex;
        }
    }
}
