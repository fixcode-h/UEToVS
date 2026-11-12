using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading;
using UEToVS.Contracts;
using UEToVS.Engine.IntelliSense;
using Xunit;
using Xunit.Abstractions;

namespace UEToVS.Engine.Tests
{
    /// <summary>
    /// v0.4.5 generic template instantiation: pure text helpers, the LRU cache, and the <c>tpl.*</c> carets of the IntelliSense
    /// corpus (GameTemplates.cpp) with the switch on, plus what the rule-based fallback still answers with it off.
    /// </summary>
    public sealed class TemplateInstantiationTests : IClassFixture<IntelliSenseFixture>
    {
        private readonly IntelliSenseFixture _f;
        private readonly ITestOutputHelper _out;

        public TemplateInstantiationTests(IntelliSenseFixture f, ITestOutputHelper output)
        {
            _f = f;
            _out = output;
        }

        // ------------------------------------------------------------------ pure helpers

        [Fact]
        public void ParseParameters_NamesDefaultsAndPacks()
        {
            var ps = TemplateText.ParseParameters("class ObjectType, ESPMode InMode = ESPMode::ThreadSafe, typename... Ts, int32 N = 4, typename");
            Assert.Equal(new[] { "ObjectType", "InMode", "Ts", "N", null }, ps.Select(p => p.Name).ToArray());
            Assert.Equal("ESPMode::ThreadSafe", ps[1].Default);
            Assert.True(ps[2].IsPack);
            Assert.Equal("4", ps[3].Default);
            Assert.Equal("Deleter", TemplateText.ParseParameters("typename T, typename Deleter = TDefaultDelete<T>")[1].Name);
            Assert.Equal("TDefaultDelete<T>", TemplateText.ParseParameters("typename T, typename Deleter = TDefaultDelete<T>")[1].Default);
            Assert.Equal("TT", TemplateText.ParseParameters("template<class> class TT")[0].Name);
            Assert.Empty(TemplateText.ParseParameters("  "));
        }

        [Fact]
        public void Substitute_ReplacesWholeIdentifiersOnly()
        {
            var map = new Dictionary<string, string> { ["T"] = "AGadget*", ["K"] = "FName" };
            Assert.Equal("AGadget*", TemplateText.Substitute("T", map));
            Assert.Equal("const AGadget*&", TemplateText.Substitute("const T&", map));
            Assert.Equal("TMap<FName, AGadget*>", TemplateText.Substitute("TMap<K, T>", map));
            Assert.Equal("TArrayType", TemplateText.Substitute("TArrayType", map)); // not a whole identifier
            Assert.Equal("Outer::T", TemplateText.Substitute("Outer::T", map)); // member name after ::
            Assert.Equal("AGadget**", TemplateText.Substitute("T *", map));
            Assert.True(TemplateText.Mentions("const TArray<T>&", new[] { "T" }));
            Assert.False(TemplateText.Mentions("const TArray<int32>&", new[] { "T" }));
        }

        [Fact]
        public void SplitDependentMember_FindsOwnerAndMember()
        {
            Assert.True(TemplateText.SplitDependentMember("typename TBox<UFoo>::ValueType*", out var owner, out var member, out var suffix));
            Assert.Equal("TBox<UFoo>", owner);
            Assert.Equal("ValueType", member);
            Assert.Equal("*", suffix);
            Assert.False(TemplateText.SplitDependentMember("Game::FFoo", out _, out _, out _));
            Assert.False(TemplateText.SplitDependentMember("TArray<int32>", out _, out _, out _));
        }

        [Fact]
        public void MapParameters_AppliesDefaultsInTermsOfEarlierArguments()
        {
            var map = TemplateInstantiator.MapParameters("typename T, typename Deleter = TDefaultDelete<T>", new[] { "FFoo" });
            Assert.Equal("FFoo", map["T"]);
            Assert.Equal("TDefaultDelete<FFoo>", map["Deleter"]);
            var pack = TemplateInstantiator.MapParameters("typename R, typename... Args", new[] { "void", "int32", "float" });
            Assert.Equal("int32, float", pack["Args"]);
        }

        [Fact]
        public void Cache_IsBoundedLruAndCountsHits()
        {
            var cache = new TemplateInstanceCache(TemplateInstanceCache.MinCapacity);
            int created = 0;
            TemplateBinding Make() { created++; return TemplateBinding.Empty; }
            for (int i = 0; i < TemplateInstanceCache.MinCapacity; i++) cache.Get("k" + i, (d, p) => true, Make);
            cache.Get("k0", (d, p) => true, Make); // hit, k0 becomes most recent
            cache.Get("extra", (d, p) => true, Make); // evicts k1 (least recent)
            Assert.Equal(TemplateInstanceCache.MinCapacity, cache.Count);
            Assert.Equal(1, cache.Hits);
            Assert.Equal(1, cache.Evictions);
            cache.Get("k1", (d, p) => true, Make);
            Assert.Equal(TemplateInstanceCache.MinCapacity + 2, created);
            cache.Capacity = 10; // below the minimum: clamped
            Assert.Equal(TemplateInstanceCache.MinCapacity, cache.Capacity);
            Assert.True(cache.HitRate > 0);
        }

        // ------------------------------------------------------------------ corpus

        private sealed class Case
        {
            public string Id;
            public string[] Top;
            public int N;
            /// <summary>Also answered by the rule tables with the switch off (UE templates, Cast/NewObject…).</summary>
            public bool RulesToo;
        }

        private static Case C(string id, bool rulesToo, int n, params string[] top) => new Case { Id = id, Top = top, N = n, RulesToo = rulesToo };

        private static readonly Case[] Cases =
        {
            C("tpl.box.alias.return", false, 3, "Charges"),
            C("tpl.box.field", false, 3, "Charges"),
            C("tpl.box.typedef", false, 3, "Charges"),
            C("tpl.derived.ref", false, 5, "Heal", "Health"),
            C("tpl.base.member", false, 5, "Heal"),
            C("tpl.base.field", false, 5, "Heal"),
            C("tpl.return.instance", false, 5, "Heal"),
            C("tpl.nested", false, 3, "Charges"),
            C("tpl.nested.field", false, 3, "Charges"),
            C("tpl.pair", false, 3, "Charges"),
            C("tpl.arrow.op", false, 3, "Charges"),
            C("tpl.arrow.get", false, 3, "Cooldown"),
            C("tpl.alias.arrow", false, 3, "Charges"),
            C("tpl.alias.box", false, 3, "Charges"),
            C("tpl.alias.template", false, 5, "Heal"),
            C("tpl.alias.shared", true, 3, "Damage"),
            C("tpl.dependent", false, 3, "Charges"),
            C("tpl.optional", false, 3, "Size"),
            C("tpl.optional.ptr", false, 3, "Charges"),
            C("tpl.optional.ptrornull", false, 40, "Charges", "Use"),
            C("tpl.shared.get", true, 3, "Damage"),
            C("tpl.shared.arrow", true, 3, "Damage"),
            C("tpl.sharedref.arrow", true, 3, "Damage"),
            C("tpl.sharedref.get", true, 3, "Damage"),
            C("tpl.shared.toref", true, 3, "Damage"),
            C("tpl.weakptr.pin", true, 3, "Damage"),
            C("tpl.unique.arrow", true, 3, "Damage"),
            C("tpl.unique.get", true, 3, "Damage"),
            C("tpl.weak.get", true, 3, "Charges"),
            C("tpl.weak.arrow", true, 3, "Charges"),
            C("tpl.soft.load", true, 3, "Charges"),
            C("tpl.soft.get", true, 3, "Charges"),
            C("tpl.subclass.cdo", true, 3, "Charges"),
            C("tpl.iface.arrow", true, 3, "Ping", "PingCount"),
            C("tpl.iface.get", true, 3, "Ping"),
            C("tpl.func.explicit", false, 3, "Charges"),
            C("tpl.func.instance", false, 3, "Charges"),
            C("tpl.cast", true, 3, "Charges"),
            C("tpl.newobject", true, 5, "Heal"),
            C("tpl.findcomp", true, 5, "Heal"),
            C("tpl.member.explicit", false, 5, "Heal"),
            C("tpl.auto", false, 5, "Heal"),
            C("tpl.auto.instance", false, 5, "Heal"),
            C("tpl.rangefor", true, 3, "Charges"),
        };

        private (int Ok, string Failures) RunCases(bool rulesOnly)
        {
            var failures = new StringBuilder();
            int ok = 0;
            foreach (var c in Cases)
            {
                if (rulesOnly && !c.RulesToo) continue;
                var resp = _f.Service.GetCompletions(new CompletionRequest { Caret = _f.Caret(c.Id) }, CancellationToken.None);
                var labels = resp.Items.Select(i => i.Label).ToList();
                var top = labels.Take(c.N).ToList();
                var missing = c.Top.Where(e => !top.Contains(e)).ToList();
                if (resp.Context == CompletionContextKind.Member && missing.Count == 0) ok++;
                else failures.AppendLine($"FAIL {c.Id}: missing [{string.Join(", ", missing)}] got [{resp.Context}/{resp.ResolvedType}] {string.Join(", ", labels.Take(10))}");
            }
            return (ok, failures.ToString());
        }

        [Fact]
        public void Corpus_WithInstantiation()
        {
            _f.Service.ConfigureTemplates(true, TemplateInstanceCache.DefaultCapacity);
            var (ok, failures) = RunCases(false);
            _out.WriteLine($"template corpus (on): {ok}/{Cases.Length}  cache: {_f.Service.TemplateCache}");
            _out.WriteLine(failures);
            Assert.True(failures.Length == 0, failures);
            Assert.True(_f.Service.TemplateCache.Hits > 0, "repeated instances must hit the cache");
        }

        [Fact]
        public void Corpus_SwitchOff_FallsBackToRules()
        {
            try
            {
                _f.Service.ConfigureTemplates(false, 0);
                Assert.False(_f.Service.TemplateInstantiationEnabled);
                Assert.Equal(0, _f.Service.TemplateCache.Count);
                var (ok, failures) = RunCases(true);
                int rules = Cases.Count(c => c.RulesToo);
                _out.WriteLine($"template corpus (off, rule cases): {ok}/{rules}");
                _out.WriteLine(failures);
                Assert.True(failures.Length == 0, failures);
                // a user template needs the generic path: off → no answer
                var resp = _f.Service.GetCompletions(new CompletionRequest { Caret = _f.Caret("tpl.alias.template") }, CancellationToken.None);
                Assert.DoesNotContain(resp.Items, i => i.Label == "Heal");
                Assert.Equal(0, _f.Service.TemplateCache.Count);
            }
            finally
            {
                _f.Service.ConfigureTemplates(true, TemplateInstanceCache.DefaultCapacity);
            }
        }
    }
}
