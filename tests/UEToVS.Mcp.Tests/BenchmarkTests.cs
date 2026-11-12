using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using UEToVS.Engine.Tests;
using UEToVS.Mcp.Data;
using UEToVS.Mcp.Tools;
using Xunit;
using Xunit.Abstractions;

namespace UEToVS.Mcp.Tests
{
    /// <summary>
    /// "Is MCP search faster than an agent grepping?" — a synthetic UE project (N actor classes, each a header + a .cpp
    /// with UCLASS/UFUNCTION/UPROPERTY members and calls between classes, plus N/2 Blueprint assets), indexed by the real
    /// engine, then the same questions answered by ripgrep/find/cat (what agents run) and by the MCP tools.
    /// Opt-in (it generates files and takes a while): UETOVS_BENCH_FILES=4000 dotnet test --filter Benchmark.
    /// Results are printed and, with UETOVS_BENCH_OUT=path, written as Markdown.
    /// </summary>
    public sealed class BenchmarkTests
    {
        private readonly ITestOutputHelper _out;

        public BenchmarkTests(ITestOutputHelper output) => _out = output;

        private const int Runs = 7;

        [Fact]
        [Trait("Category", "Benchmark")]
        public async Task GrepVersusIndex()
        {
            if (!int.TryParse(Environment.GetEnvironmentVariable("UETOVS_BENCH_FILES"), out int n) || n <= 0)
            {
                _out.WriteLine("skipped: set UETOVS_BENCH_FILES (e.g. 4000) to run the benchmark");
                return;
            }
            string rg = FindTool("rg");
            using var t = new FakeUnrealTree();
            var genSw = Stopwatch.StartNew();
            long bytes = Generate(t, n);
            genSw.Stop();

            var host = Tree.StartEngine(t);
            var idxSw = Stopwatch.StartNew();
            Task indexRun = host.StartIndexing(); // completion is observed through WaitForReadyAsync
            await host.Index.WaitForReadyAsync(CancellationToken.None).WaitAsync(TimeSpan.FromMinutes(20));
            idxSw.Stop();
            await host.StopAsync(TimeSpan.FromSeconds(30));
            string src = Path.Combine(t.ProjectRoot, "Source");
            string content = Path.Combine(t.ProjectRoot, "Content");

            using var index = new CodeIndex(t.ProjectRoot, DataSourceMode.IndexOnly);
            var invoker = new ToolInvoker(new ToolCatalog(index));
            var (client, stdioFirst) = await StartStdio(t.ProjectRoot, $"AGenActor{n * 3 / 4 + 7}");
            await using var _ = client;
            int k = n * 3 / 4 + 7, r = n / 3 + 11, bp = (n / 3 + 11) & ~1;

            var rows = new List<string[]>();
            async Task Row(string question, string shell, string[] shellArgs, string tool, object args)
            {
                var (grepMs, grepBytes, grepLines) = MeasureProcess(shell, shellArgs, t.ProjectRoot);
                var (inProcMs, mcpBytes) = await MeasureTool(invoker, tool, args);
                double mcpMs = await MeasureClient(client, tool, args);
                rows.Add(new[]
                {
                    question, $"`{Path.GetFileName(shell)} {string.Join(" ", shellArgs.Select(a => a.Replace(t.ProjectRoot, ".")))}`", F(grepMs), $"{grepLines} 行 / {Kb(grepBytes)}",
                    $"`{tool}`", F(mcpMs), F(inProcMs), Kb(mcpBytes), $"{grepMs / Math.Max(mcpMs, 0.001):F0}×",
                });
            }

            await Row("类定义在哪（精确名）", rg, new[] { "-n", $@"class\s+\w+\s+AGenActor{k}\b", src }, "go_to_definition", new { symbol = $"AGenActor{k}" });
            await Row("模糊找类（前缀/片段）", rg, new[] { "-n", $@"class\s+\w+\s+\w*GenActor{k / 10}\w*", src }, "search_symbols", new { query = $"GenActor{k / 10}", kinds = new[] { "class" }, limit = 30 });
            await Row("UFUNCTION 声明（带说明符）", rg, new[] { "-n", "-B1", $@"\bDoWork{k}\(", src }, "search_symbols", new { query = $"DoWork{k}", match = "exact", ue = new[] { "UFUNCTION" } });
            await Row("谁调用了某函数", rg, new[] { "-n", "-w", $"DoWork{r}", src }, "find_references", new { symbol = $"DoWork{r}" });
            await Row("类的成员列表", "/bin/cat", new[] { Path.Combine(src, "MyGame", "Gen", $"Gen{k / 100}", $"GenActor{k}.h") }, "get_members", new { type_name = $"AGenActor{k}" });
            await Row("按文件名找文件", rg, new[] { "--files", "-g", $"*GenActor{k}*", src }, "search_files", new { query = $"GenActor{k}" });
            await Row("找蓝图资产", rg, new[] { "--files", "-g", $"*BP_Gen{bp}*", content }, "search_assets", new { query = $"BP_Gen{bp}", @class = "Blueprint" });
            await Row("子类有哪些", rg, new[] { "-n", $@":\s*public\s+AGenActor{r / 10}\b", src }, "get_type_hierarchy", new { type_name = $"AGenActor{r / 10}", direction = "derived", max_depth = 1 });


            var sb = new StringBuilder();
            sb.AppendLine($"合成工程：{n:N0} 个 Actor（{n * 2:N0} 个源文件，{bytes / 1048576.0:F1} MB），{n / 2:N0} 个蓝图资产；机器 {Environment.ProcessorCount} 核，{RuntimeOs()}；ripgrep {RgVersion(rg)}。");
            sb.AppendLine($"一次性建索引（VS 打开解决方案后台进行）：{idxSw.Elapsed.TotalSeconds:F1} s；生成文件 {genSw.Elapsed.TotalSeconds:F1} s。每项取 {Runs} 次中位数，文件已在页缓存中（对 grep 最有利）。");
            sb.AppendLine();
            sb.AppendLine("| 问题 | Agent 惯用做法 | 耗时 | 输出 | MCP 工具 | 耗时（stdio 往返） | 其中工具本身 | 输出 | 加速 |");
            sb.AppendLine("|---|---|---:|---:|---|---:|---:|---:|---:|");
            foreach (var row in rows) sb.AppendLine("| " + string.Join(" | ", row) + " |");
            sb.AppendLine();
            sb.AppendLine($"MCP 耗时为 Agent 实际付出的 JSON-RPC stdio 往返（真实 UEToVS.Mcp 进程）；该进程首次启动 + initialize + 首个调用（打开索引）共 {F(stdioFirst)}，每个会话只付一次。");
            string md = sb.ToString();
            _out.WriteLine(md);
            string outPath = Environment.GetEnvironmentVariable("UETOVS_BENCH_OUT");
            if (!string.IsNullOrEmpty(outPath)) File.WriteAllText(outPath, md);
        }

        private static string F(double ms) => ms >= 100 ? $"{ms:F0} ms" : $"{ms:F1} ms";

        private static string Kb(long bytes) => bytes >= 1024 ? $"{bytes / 1024.0:F1} KB" : $"{bytes} B";

        private static string RuntimeOs() => System.Runtime.InteropServices.RuntimeInformation.OSDescription;

        private static string RgVersion(string rg)
        {
            try
            {
                var p = Process.Start(new ProcessStartInfo(rg, "--version") { RedirectStandardOutput = true });
                string first = p.StandardOutput.ReadLine();
                p.WaitForExit();
                return first?.Replace("ripgrep ", "") ?? "?";
            }
            catch (Exception) { return "?"; }
        }

        private static string FindTool(string name)
        {
            foreach (var dir in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator))
            {
                string p = Path.Combine(dir, name);
                if (File.Exists(p)) return p;
                if (File.Exists(p + ".exe")) return p + ".exe";
            }
            return "grep";
        }

        private static (double Ms, long Bytes, int Lines) MeasureProcess(string exe, string[] args, string cwd)
        {
            var times = new List<double>();
            long bytes = 0;
            int lines = 0;
            for (int i = 0; i < Runs; i++)
            {
                var psi = new ProcessStartInfo(exe) { RedirectStandardOutput = true, RedirectStandardError = true, WorkingDirectory = cwd, UseShellExecute = false };
                foreach (var a in args) psi.ArgumentList.Add(a);
                var sw = Stopwatch.StartNew();
                using var p = Process.Start(psi);
                string output = p.StandardOutput.ReadToEnd();
                p.WaitForExit();
                sw.Stop();
                times.Add(sw.Elapsed.TotalMilliseconds);
                bytes = Encoding.UTF8.GetByteCount(output);
                lines = output.Split('\n', StringSplitOptions.RemoveEmptyEntries).Length;
            }
            return (Median(times), bytes, lines);
        }

        private static async Task<(double Ms, long Bytes)> MeasureTool(ToolInvoker invoker, string tool, object args)
        {
            var times = new List<double>();
            long bytes = 0;
            for (int i = 0; i < Runs; i++)
            {
                var sw = Stopwatch.StartNew();
                var r = await invoker.InvokeAsync(tool, Tree.Args(args), CancellationToken.None);
                sw.Stop();
                Assert.False(r.IsError, r.Text);
                Assert.True(r.ResultCount > 0, tool + " found nothing: " + r.Text);
                times.Add(sw.Elapsed.TotalMilliseconds);
                bytes = Encoding.UTF8.GetByteCount(r.Text);
            }
            return (Median(times), bytes);
        }

        private static async Task<(McpClient Client, double FirstMs)> StartStdio(string solutionDir, string symbol)
        {
            var sw = Stopwatch.StartNew();
            var transport = new StdioClientTransport(new StdioClientTransportOptions
            {
                Name = "uetovs",
                Command = "dotnet",
                Arguments = new[] { typeof(UEToVS.Mcp.Program).Assembly.Location, "--solution-dir", solutionDir, "--source", "index" },
            });
            var client = await McpClient.CreateAsync(transport);
            var r = await client.CallToolAsync("go_to_definition", new Dictionary<string, object> { ["symbol"] = symbol });
            Assert.Contains(symbol, ((TextContentBlock)r.Content[0]).Text);
            return (client, sw.Elapsed.TotalMilliseconds);
        }

        private static async Task<double> MeasureClient(McpClient client, string tool, object args)
        {
            var dict = Tree.Args(args).ToDictionary(kv => kv.Key, kv => (object)kv.Value);
            var times = new List<double>();
            for (int i = 0; i < Runs; i++)
            {
                var sw = Stopwatch.StartNew();
                var r = await client.CallToolAsync(tool, dict);
                sw.Stop();
                Assert.NotEqual(true, r.IsError);
                times.Add(sw.Elapsed.TotalMilliseconds);
            }
            return Median(times);
        }

        private static double Median(List<double> xs)
        {
            var s = xs.OrderBy(x => x).ToList();
            return s[s.Count / 2];
        }

        /// <summary>Writes the synthetic sources and assets; returns the source bytes.</summary>
        private static long Generate(FakeUnrealTree t, int n)
        {
            for (int i = 0; i < n; i++)
            {
                string folder = $"Game/Source/MyGame/Gen/Gen{i / 100}";
                string baseName = i == 0 ? "ACharacter" : $"AGenActor{i / 10}";
                var h = new StringBuilder();
                h.Append("#pragma once\n#include \"CoreMinimal.h\"\n#include \"GenActor").Append(i).Append(".generated.h\"\n\n");
                h.Append("// AGenActor").Append(i).Append(" is generated for the benchmark. It calls DoWork").Append((i + 1) % n).Append(" on a neighbour.\n");
                h.Append("UCLASS(Blueprintable, BlueprintType)\nclass MYGAME_API AGenActor").Append(i).Append(" : public ").Append(baseName).Append("\n{\n\tGENERATED_BODY()\npublic:\n");
                h.Append("\tUFUNCTION(BlueprintCallable, Category = \"Gen\")\n\tvoid DoWork").Append(i).Append("(int32 Value);\n\n");
                h.Append("\tUPROPERTY(EditAnywhere, BlueprintReadWrite)\n\tfloat Speed").Append(i).Append(" = 1.f;\n\n");
                for (int m = 0; m < 6; m++)
                {
                    h.Append("\tUFUNCTION()\n\tint32 Compute").Append(m).Append("(int32 A, int32 B) const;\n");
                    h.Append("\tUPROPERTY()\n\tint32 Counter").Append(m).Append(" = 0;\n");
                }
                h.Append("private:\n\tTArray<AGenActor").Append((i + 1) % n).Append("*> Neighbours;\n};\n");
                var c = new StringBuilder();
                c.Append("#include \"GenActor").Append(i).Append(".h\"\n\n");
                c.Append("void AGenActor").Append(i).Append("::DoWork").Append(i).Append("(int32 Value)\n{\n\tSpeed").Append(i).Append(" += Value;\n");
                c.Append("\tfor (AGenActor").Append((i + 1) % n).Append("* N : Neighbours)\n\t{\n\t\tN->DoWork").Append((i + 1) % n).Append("(Value - 1);\n\t}\n}\n\n");
                for (int m = 0; m < 6; m++)
                    c.Append("int32 AGenActor").Append(i).Append("::Compute").Append(m).Append("(int32 A, int32 B) const\n{\n\tconst int32 Sum = A + B + Counter").Append(m).Append(";\n\treturn Sum * ").Append(m + 1).Append(";\n}\n\n");
                t.Write($"{folder}/GenActor{i}.h", h.ToString());
                t.Write($"{folder}/GenActor{i}.cpp", c.ToString());
                if (i % 2 == 0) t.Write($"Game/Content/Gen/G{i / 200}/BP_Gen{i}.uasset", "");
            }
            long bytes = 0;
            foreach (var f in Directory.EnumerateFiles(Path.Combine(t.ProjectRoot, "Source"), "*", SearchOption.AllDirectories)) bytes += new FileInfo(f).Length;
            return bytes;
        }
    }
}
