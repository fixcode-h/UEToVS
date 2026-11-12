using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace UEToVS.Host.Logic.Translation
{
    /// <summary>A free translation service. Implementations only do the HTTP call and parse the answer.</summary>
    public interface ITranslationProvider
    {
        TranslationProviderKind Kind { get; }

        /// <summary>Longest text sent in one request (longer units are chunked by <see cref="TranslationText.Chunk"/>).</summary>
        int MaxChunkChars { get; }

        /// <summary>Translates <paramref name="text"/> into <paramref name="target"/> (UEToVS code, e.g. zh-CN). Throws on failure.</summary>
        Task<string> TranslateAsync(HttpClient http, string text, string target, CancellationToken ct);
    }

    /// <summary>Google Translate, the endpoint the Chrome extension uses (client=gtx, no key). Base URL can point to a mirror.</summary>
    public sealed class GoogleTranslateProvider : ITranslationProvider
    {
        public const string DefaultBaseUrl = "https://translate.googleapis.com";
        private readonly Func<string> _baseUrl;

        public GoogleTranslateProvider(Func<string> baseUrl = null) => _baseUrl = baseUrl ?? (() => null);

        public TranslationProviderKind Kind => TranslationProviderKind.Google;

        public int MaxChunkChars => 1800;

        public static string NormalizeBaseUrl(string url)
        {
            string u = (url ?? string.Empty).Trim().TrimEnd('/');
            if (u.Length == 0) return DefaultBaseUrl;
            if (!u.StartsWith("http://", StringComparison.OrdinalIgnoreCase) && !u.StartsWith("https://", StringComparison.OrdinalIgnoreCase)) u = "https://" + u;
            return u;
        }

        public static string RequestUrl(string baseUrl, string text, string target) =>
            NormalizeBaseUrl(baseUrl) + "/translate_a/single?client=gtx&sl=auto&tl=" + Uri.EscapeDataString(target) + "&dt=t&q=" + Uri.EscapeDataString(text);

        public async Task<string> TranslateAsync(HttpClient http, string text, string target, CancellationToken ct)
        {
            using (var resp = await http.GetAsync(RequestUrl(_baseUrl(), text, TranslationText.ProviderCode(Kind, target)), ct).ConfigureAwait(false))
            {
                string body = await resp.Content.ReadAsStringAsync().ConfigureAwait(false);
                if (!resp.IsSuccessStatusCode) throw new TranslationException("HTTP " + (int)resp.StatusCode);
                return Parse(body);
            }
        }

        /// <summary>[[["译文","原文",…],…],…,"en",…] → the translated sentences joined.</summary>
        public static string Parse(string json)
        {
            var root = JToken.Parse(json) as JArray;
            if (root == null || root.Count == 0 || !(root[0] is JArray sentences)) throw new TranslationException("unexpected response");
            var sb = new StringBuilder();
            foreach (var s in sentences)
                if (s is JArray a && a.Count > 0 && a[0].Type == JTokenType.String) sb.Append((string)a[0]);
            if (sb.Length == 0) throw new TranslationException("empty translation");
            return sb.ToString();
        }
    }

    /// <summary>
    /// Microsoft Translator through the anonymous token Edge's page translation uses (edge.microsoft.com/translate/auth,
    /// valid about 10 minutes; cached 8). Usually reachable where Google is not.
    /// </summary>
    public sealed class MicrosoftTranslateProvider : ITranslationProvider
    {
        public const string AuthUrl = "https://edge.microsoft.com/translate/auth";
        public const string ApiUrl = "https://api-edge.cognitive.microsofttranslator.com/translate";
        private static readonly TimeSpan TokenLifetime = TimeSpan.FromMinutes(8);
        private readonly SemaphoreSlim _tokenLock = new SemaphoreSlim(1, 1);
        private string _token;
        private DateTime _tokenAt;

        public TranslationProviderKind Kind => TranslationProviderKind.Microsoft;

        public int MaxChunkChars => 4000;

        public async Task<string> TranslateAsync(HttpClient http, string text, string target, CancellationToken ct)
        {
            string token = await TokenAsync(http, ct).ConfigureAwait(false);
            string url = ApiUrl + "?api-version=3.0&to=" + Uri.EscapeDataString(TranslationText.ProviderCode(Kind, target));
            using (var req = new HttpRequestMessage(HttpMethod.Post, url))
            {
                req.Headers.TryAddWithoutValidation("Authorization", "Bearer " + token);
                req.Content = new StringContent(RequestBody(text), Encoding.UTF8, "application/json");
                using (var resp = await http.SendAsync(req, ct).ConfigureAwait(false))
                {
                    string body = await resp.Content.ReadAsStringAsync().ConfigureAwait(false);
                    if ((int)resp.StatusCode == 401) _token = null;
                    if (!resp.IsSuccessStatusCode) throw new TranslationException("HTTP " + (int)resp.StatusCode);
                    return Parse(body);
                }
            }
        }

        private async Task<string> TokenAsync(HttpClient http, CancellationToken ct)
        {
            await _tokenLock.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                if (_token != null && DateTime.UtcNow - _tokenAt < TokenLifetime) return _token;
                using (var resp = await http.GetAsync(AuthUrl, ct).ConfigureAwait(false))
                {
                    string body = (await resp.Content.ReadAsStringAsync().ConfigureAwait(false)).Trim();
                    if (!resp.IsSuccessStatusCode || body.Length == 0) throw new TranslationException("auth HTTP " + (int)resp.StatusCode);
                    _token = body;
                    _tokenAt = DateTime.UtcNow;
                    return _token;
                }
            }
            finally
            {
                _tokenLock.Release();
            }
        }

        public static string RequestBody(string text) => JsonConvert.SerializeObject(new[] { new { Text = text } });

        /// <summary>[{"translations":[{"text":"…","to":"zh-Hans"}]}] → text.</summary>
        public static string Parse(string json)
        {
            var root = JToken.Parse(json);
            string t = root is JArray a && a.Count > 0 ? (string)a[0]?["translations"]?[0]?["text"] : null;
            if (string.IsNullOrEmpty(t))
            {
                string err = root is JObject o ? (string)o["error"]?["message"] : null;
                throw new TranslationException(err ?? "unexpected response");
            }
            return t;
        }
    }

    /// <summary>MyMemory (translated.net): no key; anonymous requests are limited to 500 bytes and about 5000 words a day.</summary>
    public sealed class MyMemoryProvider : ITranslationProvider
    {
        public const string ApiUrl = "https://api.mymemory.translated.net/get";

        public TranslationProviderKind Kind => TranslationProviderKind.MyMemory;

        public int MaxChunkChars => 450;

        public static string RequestUrl(string text, string target)
        {
            string source = string.Equals(target, "en", StringComparison.OrdinalIgnoreCase) ? "zh-CN" : "en";
            return ApiUrl + "?q=" + Uri.EscapeDataString(text) + "&langpair=" + Uri.EscapeDataString(source + "|" + target);
        }

        public async Task<string> TranslateAsync(HttpClient http, string text, string target, CancellationToken ct)
        {
            using (var resp = await http.GetAsync(RequestUrl(text, TranslationText.ProviderCode(Kind, target)), ct).ConfigureAwait(false))
            {
                string body = await resp.Content.ReadAsStringAsync().ConfigureAwait(false);
                if (!resp.IsSuccessStatusCode) throw new TranslationException("HTTP " + (int)resp.StatusCode);
                return Parse(body);
            }
        }

        /// <summary>{"responseData":{"translatedText":"…"},"responseStatus":200} → text; other status → error with its details.</summary>
        public static string Parse(string json)
        {
            var o = JObject.Parse(json);
            int status = o["responseStatus"]?.Type == JTokenType.Integer ? (int)o["responseStatus"] : int.TryParse((string)o["responseStatus"], out int st) ? st : 0;
            string t = (string)o["responseData"]?["translatedText"];
            if (status != 200 || string.IsNullOrEmpty(t))
                throw new TranslationException((string)o["responseDetails"] ?? ("status " + status));
            return System.Net.WebUtility.HtmlDecode(t);
        }
    }

    public sealed class TranslationException : Exception
    {
        public TranslationException(string message) : base(message)
        {
        }
    }

    public static class TranslationProviders
    {
        public static IReadOnlyDictionary<TranslationProviderKind, ITranslationProvider> Create(Func<string> googleBaseUrl) =>
            new Dictionary<TranslationProviderKind, ITranslationProvider>
            {
                [TranslationProviderKind.Google] = new GoogleTranslateProvider(googleBaseUrl),
                [TranslationProviderKind.Microsoft] = new MicrosoftTranslateProvider(),
                [TranslationProviderKind.MyMemory] = new MyMemoryProvider(),
            };
    }
}
