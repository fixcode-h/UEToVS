using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading;
using UEToVS.Contracts;
using Xunit;
using Xunit.Abstractions;

namespace UEToVS.Engine.Tests
{
    /// <summary>
    /// Regression corpus for go to definition / declaration / implementation: every <c>@caret</c> marker must navigate to
    /// exactly the <c>@def</c> markers with the same id (none for ids without @def, e.g. GENERATED_BODY).
    /// </summary>
    public sealed class NavigationCorpusTests : IClassFixture<NavigationFixture>
    {
        private readonly NavigationFixture _f;
        private readonly ITestOutputHelper _out;

        public NavigationCorpusTests(NavigationFixture f, ITestOutputHelper output)
        {
            _f = f;
            _out = output;
        }

        [Fact]
        public void Corpus_HasEnoughCases()
        {
            Assert.True(_f.Carets.Count >= 60, "corpus cases: " + _f.Carets.Count);
            Assert.True(_f.Carets.Select(c => c.Kind).Distinct().Count() == 4, "all navigation kinds are covered");
        }

        [Fact]
        public void Corpus_Accuracy_AtLeast95Percent()
        {
            int ok = 0;
            var failures = new StringBuilder();
            foreach (var c in _f.Carets)
            {
                var resp = _f.Navigation.Navigate(new NavigationRequest
                {
                    Kind = c.Kind,
                    Caret = new CaretRequest { FilePath = c.File, Line = c.Line, Column = c.Column },
                }, CancellationToken.None);
                var expected = _f.Defs.TryGetValue(c.Id, out var defs) ? defs.Select(NavigationFixture.Key).OrderBy(x => x).ToList() : new List<string>();
                var actual = resp.Targets.Select(t => NavigationFixture.Key(t.Symbol.Location)).Distinct().OrderBy(x => x).ToList();
                if (expected.SequenceEqual(actual)) ok++;
                else
                {
                    failures.AppendLine($"FAIL {c}: word='{resp.Word}' reason={resp.Reason} conf={resp.Confidence}");
                    failures.AppendLine("     expected: " + string.Join(", ", expected));
                    failures.AppendLine("     actual:   " + string.Join(", ", resp.Targets.Select(t => NavigationFixture.Key(t.Symbol.Location) + "(" + t.Role + ")")));
                }
            }
            double accuracy = ok * 100.0 / _f.Carets.Count;
            _out.WriteLine($"navigation corpus: {ok}/{_f.Carets.Count} = {accuracy:F1}%");
            _out.WriteLine(failures.ToString());
            Console.WriteLine($"navigation corpus: {ok}/{_f.Carets.Count} = {accuracy:F1}%");
            Console.WriteLine(failures.ToString());
            Assert.True(accuracy >= 95.0, $"accuracy {accuracy:F1}% < 95%\n{failures}");
        }
    }
}
