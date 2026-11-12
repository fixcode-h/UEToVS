using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using UEToVS.Host.Logic.Settings;
using UEToVS.Host.Logic.Translation;
using Xunit;

namespace UEToVS.Host.Logic.Tests
{
    public class TranslationTextTests
    {
        private const string TArrayDoc =
            "Templated dynamic array\n\n" +
            "A dynamically sized array of typed elements.  Makes the assumption that your elements are trivially relocatable;\n" +
            "i.e. that they can be transparently moved to new memory without a copy constructor.  The main implication\n" +
            "is that pointers to elements in the TArray may be invalidated by adding or removing other elements to the array.\n" +
            "Removal of elements is O(N) and invalidates the indices of subsequent elements.\n\n" +
            "Caution: as noted below some methods are not safe for element types that require constructors.";

        [Fact]
        public void Split_reflows_hard_wrapped_paragraphs()
        {
            var units = TranslationText.Split(TArrayDoc, "zh-CN");
            Assert.Equal(3, units.Count);
            Assert.Equal("Templated dynamic array", units[0].Text);
            Assert.False(units[0].GapBefore);
            Assert.StartsWith("A dynamically sized array", units[1].Text);
            Assert.Contains("copy constructor.  The main implication is that pointers", units[1].Text);
            Assert.DoesNotContain("\n", units[1].Text);
            Assert.True(units[1].GapBefore);
            Assert.True(units[2].GapBefore);
            Assert.All(units, u => Assert.True(u.Translate));
        }

        [Fact]
        public void Split_keeps_list_items_tags_and_code_apart()
        {
            var units = TranslationText.Split(
                "Adds an element.\n@param Item The item to add\n@return Index of the new element\n- first point\n- second point\n" +
                "    TArray<int32> A;\nArray.Add(5);", "zh-CN");
            Assert.Equal(new[] { "Adds an element.", "@param Item The item to add", "@return Index of the new element", "- first point", "- second point", "TArray<int32> A;", "Array.Add(5);" },
                units.Select(u => u.Text).ToArray());
            Assert.False(units[5].Translate);
            Assert.False(units[6].Translate);
            Assert.True(units[1].Translate);
        }

        [Theory]
        [InlineData("Templated dynamic array", "zh-CN", true)]
        [InlineData("模板化的动态数组", "zh-CN", false)]
        [InlineData("TArray", "zh-CN", false)]
        [InlineData("1024", "zh-CN", false)]
        [InlineData("Templated dynamic array", "en", false)]
        [InlineData("模板化的动态数组", "en", true)]
        [InlineData("返回 TArray 的元素个数", "zh-CN", false)]
        public void NeedsTranslation(string text, string target, bool expected) => Assert.Equal(expected, TranslationText.NeedsTranslation(text, target));

        [Fact]
        public void ResolveTarget_follows_ui_language_for_auto()
        {
            Assert.Equal("zh-CN", TranslationText.ResolveTarget("auto", englishUi: false));
            Assert.Equal("en", TranslationText.ResolveTarget("auto", englishUi: true));
            Assert.Equal("ja", TranslationText.ResolveTarget("JA", englishUi: false));
            Assert.Equal("zh-CN", TranslationText.ResolveTarget("xx", englishUi: false));
            Assert.Equal("zh-Hans", TranslationText.ProviderCode(TranslationProviderKind.Microsoft, "zh-CN"));
            Assert.Equal("zh-Hant", TranslationText.ProviderCode(TranslationProviderKind.Microsoft, "zh-TW"));
            Assert.Equal("zh-CN", TranslationText.ProviderCode(TranslationProviderKind.Google, "zh-CN"));
        }

        [Fact]
        public void Chunk_cuts_at_sentences_and_respects_the_limit()
        {
            string text = string.Join(" ", Enumerable.Range(0, 40).Select(i => "Sentence number " + i + " is here."));
            var chunks = TranslationText.Chunk(text, 120);
            Assert.True(chunks.Count > 1);
            Assert.All(chunks, c => Assert.True(c.Length <= 120, c));
            Assert.All(chunks, c => Assert.EndsWith(".", c));
            Assert.Equal(text, string.Join(" ", chunks));
            Assert.Single(TranslationText.Chunk("short", 100));
            var words = TranslationText.Chunk(new string('x', 250), 100);
            Assert.Equal(new[] { 100, 100, 50 }, words.Select(w => w.Length).ToArray());
        }

        [Fact]
        public void JoinChunks_spaces_only_between_latin_pieces()
        {
            Assert.Equal("移除元素。会使索引失效。", TranslationText.JoinChunks(new[] { "移除元素。", "会使索引失效。" }));
            Assert.Equal("Hello world. Bye.", TranslationText.JoinChunks(new[] { "Hello world.", "Bye." }));
        }
    }

    public class TranslationProviderParseTests
    {
        [Fact]
        public void Google_response_sentences_are_joined()
        {
            string json = "[[[\"模板化动态数组。\",\"Templated dynamic array. \",null,null,3],[\"移除的时间复杂度为 O(N)。\",\"Removal is O(N).\",null,null,3]],null,\"en\"]";
            Assert.Equal("模板化动态数组。移除的时间复杂度为 O(N)。", GoogleTranslateProvider.Parse(json));
            Assert.Throws<TranslationException>(() => GoogleTranslateProvider.Parse("[null]"));
        }

        [Fact]
        public void Google_url_uses_the_configured_mirror()
        {
            Assert.StartsWith("https://translate.googleapis.com/translate_a/single?client=gtx&sl=auto&tl=zh-CN&dt=t&q=a%20b",
                GoogleTranslateProvider.RequestUrl(null, "a b", "zh-CN"));
            Assert.StartsWith("https://mirror.example.com/translate_a/single?", GoogleTranslateProvider.RequestUrl(" mirror.example.com/ ", "a", "en"));
        }

        [Fact]
        public void Microsoft_response_and_error()
        {
            Assert.Equal("你好", MicrosoftTranslateProvider.Parse("[{\"detectedLanguage\":{\"language\":\"en\"},\"translations\":[{\"text\":\"你好\",\"to\":\"zh-Hans\"}]}]"));
            var ex = Assert.Throws<TranslationException>(() => MicrosoftTranslateProvider.Parse("{\"error\":{\"code\":401000,\"message\":\"token expired\"}}"));
            Assert.Equal("token expired", ex.Message);
            Assert.Equal("[{\"Text\":\"a \\\"b\\\"\"}]", MicrosoftTranslateProvider.RequestBody("a \"b\""));
        }

        [Fact]
        public void MyMemory_response_and_quota_error()
        {
            Assert.Equal("数组 & 元素", MyMemoryProvider.Parse("{\"responseData\":{\"translatedText\":\"数组 &amp; 元素\"},\"responseStatus\":200}"));
            var ex = Assert.Throws<TranslationException>(() => MyMemoryProvider.Parse("{\"responseData\":{\"translatedText\":\"MYMEMORY WARNING\"},\"responseStatus\":429,\"responseDetails\":\"quota\"}"));
            Assert.Equal("quota", ex.Message);
            Assert.Contains("langpair=en%7Czh-CN", MyMemoryProvider.RequestUrl("a", "zh-CN"));
        }
    }

    public class TranslationCacheTests
    {
        [Fact]
        public void Lru_evicts_the_least_recently_used()
        {
            var c = new TranslationCache(null, capacity: 16);
            for (int i = 0; i < 16; i++) c.Put("k" + i, "v" + i);
            Assert.True(c.TryGet("k0", out _)); // k0 is now the most recent
            c.Put("k16", "v16");
            Assert.True(c.TryGet("k0", out var v0));
            Assert.Equal("v0", v0);
            Assert.False(c.TryGet("k1", out _));
            Assert.Equal(16, c.Count);
        }

        [Fact]
        public void Persists_and_reloads_from_the_file()
        {
            string dir = Path.Combine(Path.GetTempPath(), "uetovs-tr-" + Guid.NewGuid().ToString("N"));
            string file = Path.Combine(dir, "cache.jsonl");
            try
            {
                var c = new TranslationCache(file);
                string key = TranslationCache.Key(TranslationProviderKind.Google, "zh-CN", "Templated \"dynamic\"\narray");
                c.Put(key, "模板化\n动态数组");
                c.Put(key, "模板化\n动态数组"); // same value: not appended again
                Assert.Single(File.ReadAllLines(file));
                File.AppendAllText(file, "not json\n");

                var reloaded = new TranslationCache(file);
                Assert.False(reloaded.TryGet(key, out _, allowLoad: false) && false);
                reloaded.EnsureLoaded();
                Assert.True(reloaded.TryGet(key, out var v));
                Assert.Equal("模板化\n动态数组", v);

                reloaded.Clear();
                Assert.False(File.Exists(file));
                Assert.Equal(0, reloaded.Count);
            }
            finally
            {
                if (Directory.Exists(dir)) Directory.Delete(dir, true);
            }
        }
    }

    public class TranslationServiceTests
    {
        private sealed class FakeProvider : ITranslationProvider
        {
            public int Calls;
            public bool Fail;
            public TaskCompletionSource<bool> Gate;

            public TranslationProviderKind Kind => TranslationProviderKind.Google;
            public int MaxChunkChars => 20;

            public async Task<string> TranslateAsync(HttpClient http, string text, string target, CancellationToken ct)
            {
                Interlocked.Increment(ref Calls);
                if (Gate != null) await Gate.Task;
                if (Fail) throw new TranslationException("offline");
                return "[" + target + "]" + text;
            }
        }

        private static TranslationService Create(FakeProvider p, TranslationCache cache = null) =>
            new TranslationService(() => new TranslationOptions { Target = "zh-CN" }, cache ?? new TranslationCache(null),
                new Dictionary<TranslationProviderKind, ITranslationProvider> { [TranslationProviderKind.Google] = p }, new HttpClient());

        [Fact]
        public async Task Translates_in_chunks_then_serves_from_cache()
        {
            var p = new FakeProvider();
            var svc = Create(p);
            var r = await svc.TranslateAsync("First sentence. Second one.");
            Assert.True(r.Ok);
            Assert.False(r.FromCache);
            Assert.Equal(2, p.Calls); // 27 chars, 20 per chunk
            Assert.Equal("[zh-CN]First sentence. [zh-CN]Second one.", r.Text);
            Assert.True(svc.TryGetCached("First sentence. Second one.", out var cached));
            Assert.Equal(r.Text, cached);
            var again = await svc.TranslateAsync("First sentence. Second one.");
            Assert.True(again.FromCache);
            Assert.Equal(2, p.Calls);
        }

        [Fact]
        public async Task Identical_requests_in_flight_share_one_call()
        {
            var p = new FakeProvider { Gate = new TaskCompletionSource<bool>() };
            var svc = Create(p);
            var a = svc.TranslateAsync("Same text");
            var b = svc.TranslateAsync("Same text");
            Assert.Same(a, b);
            p.Gate.SetResult(true);
            await Task.WhenAll(a, b);
            Assert.Equal(1, p.Calls);
        }

        [Fact]
        public async Task A_failure_puts_the_provider_on_cooldown()
        {
            var p = new FakeProvider { Fail = true };
            var svc = Create(p);
            var r = await svc.TranslateAsync("Some text");
            Assert.False(r.Ok);
            Assert.Equal("offline", r.Error);
            var r2 = await svc.TranslateAsync("Other text");
            Assert.False(r2.Ok);
            Assert.Equal(1, p.Calls); // failed fast, no second request
            p.Fail = false;
            svc.ResetCooldown();
            Assert.True((await svc.TranslateAsync("Other text")).Ok);
        }
    }

    public class TranslationSettingsTests
    {
        [Fact]
        public void Defaults_are_google_bilingual_on()
        {
            var s = new HostSettings();
            Assert.True(s.CommentTranslation);
            Assert.Equal(TranslationDisplay.Bilingual, s.TranslationDisplay);
            Assert.Equal(TranslationProviderKind.Google, s.TranslationProvider);
            Assert.Equal("auto", s.TranslationTarget);
        }

        [Fact]
        public void Schema_rows_round_trip_through_the_codec()
        {
            var schema = HostSettingsSchema.Build(8);
            var rows = schema.Where(d => d.Key.StartsWith("translate.", StringComparison.Ordinal)).ToList();
            Assert.Equal(6, rows.Count);
            Assert.All(rows, d => Assert.Equal(HostSettingsSchema.IntelliSense, d.Category));

            var values = HostSettingsSchema.Codec.ToValues(new HostSettings());
            foreach (var d in rows.Where(r => r.HasValue)) Assert.Equal(d.Default, values[d.Key]);

            values[TranslationSettingsSchema.DisplayKey] = "TranslationOnly";
            values[TranslationSettingsSchema.ProviderKey] = "Microsoft";
            values[TranslationSettingsSchema.TargetKey] = "ja";
            values[TranslationSettingsSchema.GoogleUrlKey] = "https://mirror.example.com";
            values[TranslationSettingsSchema.EnabledKey] = "false";
            var s = HostSettingsSchema.Codec.FromValues(values, schema, new HostSettings());
            Assert.False(s.CommentTranslation);
            Assert.Equal(TranslationDisplay.TranslationOnly, s.TranslationDisplay);
            Assert.Equal(TranslationProviderKind.Microsoft, s.TranslationProvider);
            Assert.Equal("ja", s.TranslationTarget);
            Assert.Equal("https://mirror.example.com", s.TranslationGoogleUrl);
            Assert.Equal("ja", TranslationSettingsSchema.OptionsOf(s).Target);
        }
    }
}
