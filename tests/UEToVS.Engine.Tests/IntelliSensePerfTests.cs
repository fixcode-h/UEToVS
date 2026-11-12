using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using UEToVS.Contracts;
using UEToVS.Engine.Indexing;
using UEToVS.Engine.IntelliSense;
using UEToVS.Engine.Unreal;
using Xunit;
using Xunit.Abstractions;

namespace UEToVS.Engine.Tests
{
    /// <summary>
    /// A synthetic large project: 2 000 UCLASSes in 400 headers (inheritance chains 10 deep, 8 UPROPERTY + 8 UFUNCTION each,
    /// overloaded virtuals, namespaces with helpers) and a ~3 000-line .cpp that is "typed into".
    /// </summary>
    public sealed class SyntheticProjectFixture : IDisposable
    {
        public const int Files = 400, ClassesPerFile = 5, Members = 8, Functions = 300;

        public SyntheticProjectFixture()
        {
            Root = Path.Combine(Path.GetTempPath(), "uetovs-is-synth", Guid.NewGuid().ToString("N"));
            string pub = Path.Combine(Root, "Game", "Source", "Big", "Public", "Gen");
            string priv = Path.Combine(Root, "Game", "Source", "Big", "Private");
            Directory.CreateDirectory(pub);
            Directory.CreateDirectory(priv);
            Backend = new SegmentedIndexBackend();
            Backend.TryLoad(Path.Combine(Root, ".index"), "synth");
            var parser = new SyntaxFileParser();
            var sw = Stopwatch.StartNew();
            for (int f = 0; f < Files; f++) Add(parser, Path.Combine(pub, $"Gen{f}.h"), Header(f));
            // as after a full index (the orchestrator saves = compacts); the .cpp stays a delta like a recently edited file
            Backend.Save(Path.Combine(Root, ".index"), "synth");
            CppPath = Path.Combine(priv, "BigGame.cpp");
            CppText = Source();
            Add(parser, CppPath, CppText.Replace(CaretToken, string.Empty));
            IndexMs = sw.ElapsedMilliseconds;
            Service = new IntelliSenseService(Backend, FileTable);
        }

        public const string CaretToken = "/*CARET*/";
        public string Root { get; }
        public SegmentedIndexBackend Backend { get; }
        public FileTable FileTable { get; } = new FileTable();
        public IntelliSenseService Service { get; }
        public string CppPath { get; }
        /// <summary>The .cpp text with one <see cref="CaretToken"/> line inside the last function (class UGen1499, 9 bases deep).</summary>
        public string CppText { get; }
        public long IndexMs { get; }
        public int SymbolCount => Files * ClassesPerFile * (Members * 2 + 4);

        public string HeaderPath(int f) => Path.Combine(Root, "Game", "Source", "Big", "Public", "Gen", $"Gen{f}.h");

        private void Add(SyntaxFileParser parser, string path, string text)
        {
            File.WriteAllText(path, text);
            var listed = new ListedFile
            {
                Path = path, Name = Path.GetFileName(path), Module = "Big", Origin = ItemOrigin.Project,
                Kind = path.EndsWith(".h") ? SourceFileKind.Header : SourceFileKind.Source,
            };
            FileTable.Upsert(listed);
            var ctx = new FileContext { Path = path, Module = "Big", Origin = ItemOrigin.Project, Kind = listed.Kind };
            Backend.UpdateParsed(path, parser.Parse(ctx, Encoding.UTF8.GetBytes(text), CancellationToken.None));
        }

        public static string Header(int f)
        {
            var sb = new StringBuilder();
            sb.Append("#pragma once\n#include \"CoreMinimal.h\"\n");
            if (f > 0) sb.Append($"#include \"Gen/Gen{f - 1}.h\"\n");
            sb.Append($"#include \"Gen{f}.generated.h\"\n\n");
            sb.Append($"namespace GenUtil{f}\n{{\n\t/** Helper {f}. */\n\tfloat Helper{f}(float X, float Scale = 1.f);\n\tstruct FGenInfo{f} {{ int32 Id; float Weight; }};\n}}\n\n");
            for (int c = 0; c < ClassesPerFile; c++)
            {
                int n = f * ClassesPerFile + c;
                string baseName = n % 10 == 0 ? "UObject" : $"UGen{n - 1}";
                sb.Append($"/** Generated class {n}. */\nUCLASS(BlueprintType)\nclass BIG_API UGen{n} : public {baseName}\n{{\n\tGENERATED_BODY()\npublic:\n");
                for (int j = 0; j < Members; j++)
                {
                    sb.Append($"\t/** Value {j} of {n}. */\n\tUPROPERTY(EditAnywhere, BlueprintReadWrite, Category = \"Gen\")\n\tint32 Value{n}_{j} = {j};\n\n");
                    sb.Append($"\t/** Computes {j}. */\n\tUFUNCTION(BlueprintCallable, Category = \"Gen\")\n\tfloat Compute{n}_{j}(int32 A, float B = 1.f) const;\n\n");
                }
                sb.Append("\tvirtual int32 GetValue() const;\n");
                sb.Append("\tvirtual void Process(float DeltaTime);\n");
                sb.Append("\tvirtual void Process(float DeltaTime, int32 Flags);\n");
                sb.Append($"\t/** Step hook of {n}. */\n\tvirtual void OnStep{n}(float Alpha, bool bFinal = false);\n");
                sb.Append($"\tstatic UGen{n}* Make{n}();\n");
                sb.Append("protected:\n\tUPROPERTY()\n\tTArray<UObject*> Children;\n");
                sb.Append("};\n\n");
            }
            return sb.ToString();
        }

        private static string Source()
        {
            var sb = new StringBuilder();
            sb.Append("#include \"Gen/Gen399.h\"\n\n");
            for (int i = 0; i < Functions; i++)
            {
                int n = i * 5 + 4; // UGen..4 / ..9: 4 or 9 bases deep
                int f = n / ClassesPerFile;
                sb.Append($"float UGen{n}::Compute{n}_0(int32 A, float B) const\n{{\n");
                sb.Append($"\tUGen{n}* Other = Make{n}();\n");
                sb.Append("\tconst float Local = A * B;\n");
                sb.Append($"\tOther->Value{n}_1 = static_cast<int32>(Local);\n");
                if (i == Functions - 1) sb.Append("\t" + CaretToken + "\n");
                sb.Append($"\tif (Other->GetValue() > 0) {{ return Other->Compute{n}_2(A, Local); }}\n");
                sb.Append($"\tfor (UObject* Child : Children) {{ UE_LOG(LogTemp, Log, TEXT(\"%s\"), *GetNameSafe(Child)); }}\n");
                sb.Append($"\treturn GenUtil{f}::Helper{f}(Local);\n}}\n\n");
            }
            return sb.ToString();
        }

        public void Dispose()
        {
            Backend.Dispose();
            try { Directory.Delete(Root, true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }
    }

    /// <summary>
    /// M3 performance: completion p95 &lt; 50 ms, classify (~100 lines) p95 &lt; 15 ms. Every sample is a new buffer version
    /// (as when typing), so per-version caches never hit; thresholds here are loose (CI noise), the numbers are printed.
    /// </summary>
    public sealed class IntelliSensePerfTests : IClassFixture<IntelliSenseFixture>, IClassFixture<SyntheticProjectFixture>
    {
        private const int Samples = 40;
        private readonly IntelliSenseFixture _corpus;
        private readonly SyntheticProjectFixture _big;
        private readonly ITestOutputHelper _out;

        public IntelliSensePerfTests(IntelliSenseFixture corpus, SyntheticProjectFixture big, ITestOutputHelper output)
        {
            _corpus = corpus;
            _big = big;
            _out = output;
        }

        private static int _version = 1000;

        private static (double P50, double P95, double Max) Stats(List<double> times)
        {
            times.Sort();
            return (times[times.Count / 2], times[Math.Min(times.Count - 1, (int)(times.Count * 0.95))], times[times.Count - 1]);
        }

        private string Report(string what, List<double> times)
        {
            var (p50, p95, max) = Stats(times);
            string line = $"{what,-34} p50={p50,6:F2} p95={p95,6:F2} max={max,6:F2} ms (n={times.Count})";
            _out.WriteLine(line);
            return line;
        }

        /// <summary>Every marker of the IntelliSense corpus, each sample on a freshly edited buffer.</summary>
        [Fact]
        public void Completion_Corpus_P95()
        {
            var all = new List<double>();
            var byFile = _corpus.Carets.Where(kv => kv.Key.Contains('.') || kv.Key == "override")
                .Select(kv => kv.Value).Where(c => File.Exists(c.FilePath)).ToList();
            var texts = byFile.Select(c => c.FilePath).Distinct().ToDictionary(p => p, File.ReadAllText);
            for (int round = 0; round < 6; round++)
            {
                foreach (var c in byFile)
                {
                    var req = new CompletionRequest
                    {
                        Caret = new CaretRequest { FilePath = c.FilePath, Line = c.Line, Column = c.Column, Text = texts[c.FilePath] + "\n// " + round, TextVersion = Interlocked.Increment(ref _version) },
                    };
                    var sw = Stopwatch.StartNew();
                    _corpus.Service.GetCompletions(req, CancellationToken.None);
                    if (round > 0) all.Add(sw.Elapsed.TotalMilliseconds); // round 0 = JIT warm-up
                }
            }
            Report("completion corpus (all markers)", all);
            Assert.True(Stats(all).P95 < 150, "p95 " + Stats(all).P95);
        }

        public static IEnumerable<object[]> Scenarios() => new[]
        {
            new object[] { "member Other->", "Other->" },
            new object[] { "member prefix Other->Comp", "Other->Comp" },
            new object[] { "member chain Make()->Val", "Make1499()->Val" },
            new object[] { "general Val", "Val" },
            new object[] { "general UGe (2000 classes)", "UGe" },
            new object[] { "general GenU (namespaces)", "GenU" },
            new object[] { "scope UGen1499::", "UGen1499::" },
            new object[] { "scope GenUtil7::", "GenUtil7::" },
            new object[] { "array Children.", "Children." },
        };

        [Theory]
        [MemberData(nameof(Scenarios))]
        public void Completion_SyntheticLargeProject_P95(string name, string fragment)
        {
            var times = new List<double>();
            int count = 0;
            for (int i = 0; i < Samples + 5; i++)
            {
                var req = Typed(fragment, i);
                var sw = Stopwatch.StartNew();
                var resp = _big.Service.GetCompletions(req, CancellationToken.None);
                if (i >= 5) times.Add(sw.Elapsed.TotalMilliseconds);
                count = resp.Items.Count;
            }
            Assert.True(count > 0, name + ": no items");
            Report($"{name} [{count}]", times);
            Assert.True(Stats(times).P95 < 150, name + " p95 " + Stats(times).P95);
        }

        [Fact]
        public void Completion_SyntheticOverride_P95()
        {
            // 'virtual ' in the body of UGen1499 (9 bases deep): every overridable base method with its full signature
            string path = _big.HeaderPath(299);
            string text = File.ReadAllText(path);
            int at = text.IndexOf("class BIG_API UGen1499", StringComparison.Ordinal);
            at = text.IndexOf("public:\n", at, StringComparison.Ordinal) + "public:\n".Length;
            text = text.Insert(at, "\tvirtual \n");
            int line = text.Take(at).Count(ch => ch == '\n');
            var times = new List<double>();
            int count = 0;
            for (int i = 0; i < Samples + 5; i++)
            {
                var req = new CompletionRequest { Caret = new CaretRequest { FilePath = path, Text = text + "\n// " + i, TextVersion = Interlocked.Increment(ref _version), Line = line, Column = "\tvirtual ".Length } };
                var sw = Stopwatch.StartNew();
                var resp = _big.Service.GetCompletions(req, CancellationToken.None);
                if (i >= 5) times.Add(sw.Elapsed.TotalMilliseconds);
                Assert.Equal(CompletionContextKind.Override, resp.Context);
                count = resp.Items.Count;
            }
            Assert.True(count >= 9, "override items " + count); // OnStep of the 9 bases
            Report($"override in UGen1499 [{count}]", times);
            Assert.True(Stats(times).P95 < 150);
        }

        [Fact]
        public void Classify_SyntheticLargeProject_P95()
        {
            var typing = new List<double>();
            var firstView = new List<double>();
            var revisit = new List<double>();
            int lines = _big.CppText.Count(c => c == '\n');
            int caretLine = _big.CppText.Take(_big.CppText.IndexOf(SyntheticProjectFixture.CaretToken, StringComparison.Ordinal)).Count(ch => ch == '\n');
            int visible = Math.Max(0, caretLine - 50);
            CaretRequest doc = null;
            // typing: a new version per keystroke, the visible 100 lines around the caret are re-coloured
            for (int i = 0; i < Samples + 5; i++)
            {
                doc = new CaretRequest { FilePath = _big.CppPath, Text = _big.CppText.Replace(SyntheticProjectFixture.CaretToken, "int32 Typed" + i + " = Value1499_" + (i % 8) + ";"), TextVersion = Interlocked.Increment(ref _version) };
                var sw = Stopwatch.StartNew();
                var resp = _big.Service.Classify(new ClassifyRequest { Document = doc, StartLine = visible, EndLine = visible + 99 }, CancellationToken.None);
                if (i >= 5) typing.Add(sw.Elapsed.TotalMilliseconds);
                Assert.NotEmpty(resp.Spans);
            }
            // scrolling one version through regions never seen (every name new to the memo), then back
            for (int i = 0; i < Samples && (i + 1) * 100 <= lines; i++)
            {
                var sw = Stopwatch.StartNew();
                _big.Service.Classify(new ClassifyRequest { Document = doc, StartLine = i * 70, EndLine = i * 70 + 99 }, CancellationToken.None);
                firstView.Add(sw.Elapsed.TotalMilliseconds);
            }
            for (int i = 0; i < Samples; i++)
            {
                var sw = Stopwatch.StartNew();
                _big.Service.Classify(new ClassifyRequest { Document = doc, StartLine = i * 70 + 35, EndLine = i * 70 + 134 }, CancellationToken.None);
                revisit.Add(sw.Elapsed.TotalMilliseconds);
            }
            Report("classify 100 lines while typing", typing);
            Report("classify 100 lines, first view", firstView);
            Report("classify 100 lines, scroll back", revisit);
            _out.WriteLine($"synthetic project: {SyntheticProjectFixture.Files} headers, {SyntheticProjectFixture.Files * SyntheticProjectFixture.ClassesPerFile} classes, ~{_big.SymbolCount} symbols, {lines} lines .cpp, indexed in {_big.IndexMs} ms");
            Assert.True(Stats(typing).P95 < 45, "typing p95 " + Stats(typing).P95);
            Assert.True(Stats(firstView).P95 < 90, "first view p95 " + Stats(firstView).P95);
        }

        [Fact]
        public void Diagnostics_SyntheticLargeProject_P95()
        {
            var times = new List<double>();
            int caretLine = _big.CppText.Take(_big.CppText.IndexOf(SyntheticProjectFixture.CaretToken, StringComparison.Ordinal)).Count(ch => ch == '\n');
            int from = Math.Max(0, caretLine - 80);
            int falsePositives = 0, uncertain = 0, checkedSites = 0;
            // after each edit pause: a new version, the viewport (~60 lines) ± 50 lines
            for (int i = 0; i < Samples + 5; i++)
            {
                var doc = new CaretRequest { FilePath = _big.CppPath, Text = _big.CppText.Replace(SyntheticProjectFixture.CaretToken, "int32 Typed" + i + " = 0;"), TextVersion = Interlocked.Increment(ref _version) };
                var sw = Stopwatch.StartNew();
                var resp = _big.Service.GetDiagnostics(new CodeDiagnosticsRequest { Document = doc, StartLine = from, EndLine = from + 160 }, CancellationToken.None);
                if (i >= 5) times.Add(sw.Elapsed.TotalMilliseconds);
                falsePositives = resp.Items.Count;
                uncertain = resp.Uncertain;
                checkedSites = resp.Checked;
            }
            Report($"diagnostics 160 lines after an edit [checked {checkedSites}, uncertain {uncertain}]", times);
            Assert.Equal(0, falsePositives); // generated code is valid
            Assert.True(Stats(times).P95 < 60, "diagnostics p95 " + Stats(times).P95);
        }

        [Fact]
        public void QuickInfoAndSignature_SyntheticLargeProject_P95()
        {
            var quick = new List<double>();
            var sig = new List<double>();
            for (int i = 0; i < Samples + 5; i++)
            {
                var q = Typed("Other->Compute1499_3", i);
                q.Caret.Column -= 3;
                var sw = Stopwatch.StartNew();
                var qi = _big.Service.GetQuickInfo(q.Caret, CancellationToken.None);
                if (i >= 5) quick.Add(sw.Elapsed.TotalMilliseconds);
                Assert.Contains("Compute1499_3", qi.Declaration);
                var s = Typed("Other->Process(1.f, ", i);
                sw.Restart();
                var sh = _big.Service.GetSignatureHelp(s.Caret, CancellationToken.None);
                if (i >= 5) sig.Add(sw.Elapsed.TotalMilliseconds);
                Assert.Equal(2, sh.Signatures.Count);
            }
            Report("quick info member", quick);
            Report("signature help overloads", sig);
            Assert.True(Stats(quick).P95 < 150 && Stats(sig).P95 < 150);
        }

        /// <summary>The big .cpp with <paramref name="fragment"/> typed at the caret line; a new version per sample.</summary>
        private CompletionRequest Typed(string fragment, int sample)
        {
            string text = _big.CppText.Replace(SyntheticProjectFixture.CaretToken, fragment);
            int at = _big.CppText.IndexOf(SyntheticProjectFixture.CaretToken, StringComparison.Ordinal);
            int line = _big.CppText.Take(at).Count(ch => ch == '\n');
            int column = 1 + fragment.Length;
            return new CompletionRequest
            {
                Caret = new CaretRequest { FilePath = _big.CppPath, Text = text + "\n// " + sample, TextVersion = Interlocked.Increment(ref _version), Line = line, Column = column },
            };
        }
    }
}
