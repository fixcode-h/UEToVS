using System.IO;
using System.Linq;
using System.Threading;
using UEToVS.Contracts;
using Xunit;
using Xunit.Abstractions;

namespace UEToVS.Engine.Tests
{
    /// <summary>The completion / navigation corpora are valid code (apart from unfinished expressions at completion markers): no false positives there.</summary>
    public sealed class DiagnosticsCorpusTests : IClassFixture<IntelliSenseFixture>
    {
        private readonly IntelliSenseFixture _f;
        private readonly ITestOutputHelper _out;

        public DiagnosticsCorpusTests(IntelliSenseFixture f, ITestOutputHelper output)
        {
            _f = f;
            _out = output;
        }

        [Fact]
        public void Corpus_NoFalsePositives()
        {
            int total = 0, uncertain = 0, items = 0;
            foreach (var path in Directory.GetFiles(_f.Root, "*.*", SearchOption.AllDirectories).Where(p => p.EndsWith(".h") || p.EndsWith(".cpp")).OrderBy(p => p))
            {
                var resp = _f.Service.GetDiagnostics(new CodeDiagnosticsRequest { Document = new CaretRequest { FilePath = path }, StartLine = 0, EndLine = 100000 }, CancellationToken.None);
                total += resp.Checked;
                uncertain += resp.Uncertain;
                var lines = File.ReadAllLines(path);
                foreach (var d in resp.Items)
                {
                    items++;
                    string line = lines[d.Line];
                    string word = line.Substring(d.Column, d.Length);
                    _out.WriteLine($"{Path.GetFileName(path)}:{d.Line + 1}:{d.Column + 1} {d.Code} {d.Message}");
                    // true positives only: unfinished words at completion markers, and a member the engine stand-in FVector lacks
                    // and AGadget::ServerUse, a Server RPC the corpus defines by hand (UE0004 is right: UHT generates that body)
                    Assert.True(line.Contains("/*@complete:") || word == "IsNearlyZero" || d.Code == CodeDiagnosticCodes.GeneratedFunctionBody && word == "ServerUse", $"false positive {d.Code} '{word}' {Path.GetFileName(path)}:{d.Line + 1}");
                }
            }
            _out.WriteLine($"checked {total}, uncertain {uncertain}, reported {items}");
            Assert.True(total > 20);
            Assert.Equal(14, items); // v0.4.5: +5 unfinished words at GameTemplates.cpp completion markers
        }
    }
}
