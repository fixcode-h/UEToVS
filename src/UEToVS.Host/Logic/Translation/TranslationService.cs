using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using UEToVS.Core.Diagnostics;

namespace UEToVS.Host.Logic.Translation
{
    /// <summary>Effective translation settings (from HostSettings; the target language already resolved).</summary>
    public sealed class TranslationOptions
    {
        public bool Enabled { get; set; } = true;
        public TranslationDisplay Display { get; set; } = TranslationDisplay.Bilingual;
        public TranslationProviderKind Provider { get; set; } = TranslationProviderKind.Google;
        public string Target { get; set; } = "zh-CN";
    }

    public sealed class TranslationResult
    {
        private TranslationResult(string text, string error, bool fromCache)
        {
            Text = text;
            Error = error;
            FromCache = fromCache;
        }

        public bool Ok => Text != null;
        public string Text { get; }
        public string Error { get; }
        public bool FromCache { get; }

        public static TranslationResult Success(string text, bool fromCache) => new TranslationResult(text, null, fromCache);
        public static TranslationResult Failure(string error) => new TranslationResult(null, error ?? "error", false);
    }

    /// <summary>
    /// Translates comment units off the UI thread: cache first (<see cref="TranslationCache"/>), then the configured
    /// provider. Identical requests in flight share one task; at most <see cref="MaxConcurrent"/> HTTP requests run at once,
    /// each limited to <see cref="RequestTimeout"/>. After a failure the provider is left alone for <see cref="Cooldown"/>
    /// (hovers then fail fast instead of each waiting for a timeout). Logs translate.* events.
    /// </summary>
    public sealed class TranslationService
    {
        public const int MaxConcurrent = 4;
        public static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(8);
        public static readonly TimeSpan Cooldown = TimeSpan.FromSeconds(30);

        private static readonly Logger L = Log.For("Translation");
        private readonly Func<TranslationOptions> _options;
        private readonly TranslationCache _cache;
        private readonly IReadOnlyDictionary<TranslationProviderKind, ITranslationProvider> _providers;
        private readonly HttpClient _http;
        private readonly SemaphoreSlim _slots = new SemaphoreSlim(MaxConcurrent, MaxConcurrent);
        private readonly ConcurrentDictionary<string, Task<TranslationResult>> _inFlight = new ConcurrentDictionary<string, Task<TranslationResult>>(StringComparer.Ordinal);
        private readonly ConcurrentDictionary<TranslationProviderKind, (DateTime Until, string Error)> _cooldown = new ConcurrentDictionary<TranslationProviderKind, (DateTime, string)>();

        public TranslationService(Func<TranslationOptions> options, TranslationCache cache,
                                  IReadOnlyDictionary<TranslationProviderKind, ITranslationProvider> providers, HttpClient http = null)
        {
            _options = options ?? (() => new TranslationOptions());
            _cache = cache ?? new TranslationCache(null);
            _providers = providers;
            _http = http ?? CreateHttpClient();
        }

        public TranslationOptions Options => _options() ?? new TranslationOptions();

        public TranslationCache Cache => _cache;

        public static HttpClient CreateHttpClient()
        {
            // the system proxy (IE / Windows settings) is used by default, so a configured proxy reaches Google as well
            var http = new HttpClient { Timeout = Timeout.InfiniteTimeSpan };
            http.DefaultRequestHeaders.TryAddWithoutValidation("User-Agent", "Mozilla/5.0 (Windows NT 10.0; Win64; x64) UEToVS");
            return http;
        }

        /// <summary>Cached translation of <paramref name="text"/> with the current settings; never blocks (UI thread).</summary>
        public bool TryGetCached(string text, out string translation)
        {
            var o = Options;
            return _cache.TryGet(TranslationCache.Key(o.Provider, o.Target, text), out translation, allowLoad: false);
        }

        public Task<TranslationResult> TranslateAsync(string text)
        {
            var o = Options;
            if (string.IsNullOrWhiteSpace(text)) return Task.FromResult(TranslationResult.Failure("empty"));
            string key = TranslationCache.Key(o.Provider, o.Target, text);
            if (_inFlight.TryGetValue(key, out var running)) return running;
            var tcs = new TaskCompletionSource<TranslationResult>(TaskCreationOptions.RunContinuationsAsynchronously);
            if (!_inFlight.TryAdd(key, tcs.Task)) return _inFlight.TryGetValue(key, out running) ? running : TranslateAsync(text);
            Task.Run(async () =>
            {
                TranslationResult r;
                try
                {
                    r = await RunAsync(o, key, text).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    r = TranslationResult.Failure(ex.Message);
                }
                _inFlight.TryRemove(key, out _);
                tcs.TrySetResult(r);
            });
            return tcs.Task;
        }

        private async Task<TranslationResult> RunAsync(TranslationOptions o, string key, string text)
        {
            if (_cache.TryGet(key, out var cached)) return TranslationResult.Success(cached, true);
            if (!_providers.TryGetValue(o.Provider, out var provider)) return TranslationResult.Failure("unknown provider " + o.Provider);
            if (_cooldown.TryGetValue(o.Provider, out var cd) && cd.Until > DateTime.UtcNow) return TranslationResult.Failure(cd.Error);

            var sw = Stopwatch.StartNew();
            await _slots.WaitAsync().ConfigureAwait(false);
            try
            {
                var chunks = TranslationText.Chunk(text, provider.MaxChunkChars);
                var outputs = new List<string>(chunks.Count);
                foreach (var chunk in chunks)
                {
                    using (var cts = new CancellationTokenSource(RequestTimeout))
                    {
                        try
                        {
                            outputs.Add(await provider.TranslateAsync(_http, chunk, o.Target, cts.Token).ConfigureAwait(false));
                        }
                        catch (OperationCanceledException) when (cts.IsCancellationRequested)
                        {
                            throw new TranslationException("timeout " + (int)RequestTimeout.TotalSeconds + "s");
                        }
                    }
                }
                string result = TranslationText.JoinChunks(outputs);
                _cache.Put(key, result);
                _cooldown.TryRemove(o.Provider, out _);
                L.Debug("translate.ok", null, ("provider", o.Provider.ToString()), ("target", o.Target), ("chars", text.Length), ("chunks", chunks.Count),
                    ("ms", sw.ElapsedMilliseconds));
                return TranslationResult.Success(result, false);
            }
            catch (Exception ex)
            {
                string error = ex is TranslationException ? ex.Message : ex.GetType().Name + ": " + (ex.InnerException?.Message ?? ex.Message);
                _cooldown[o.Provider] = (DateTime.UtcNow + Cooldown, error);
                L.Warn("translate.failed", "Translation failed", ("provider", o.Provider.ToString()), ("target", o.Target), ("chars", text.Length),
                    ("ms", sw.ElapsedMilliseconds), ("error", error));
                return TranslationResult.Failure(error);
            }
            finally
            {
                _slots.Release();
            }
        }

        /// <summary>Forget a failure so the next request tries the provider again (settings changed, "clear cache").</summary>
        public void ResetCooldown() => _cooldown.Clear();
    }
}
