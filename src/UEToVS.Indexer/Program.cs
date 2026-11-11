using System;
using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using StreamJsonRpc;
using UEToVS.Core.Protocol;
using UEToVS.Core.Query;
using UEToVS.Indexer.Index;
using UEToVS.Indexer.Rpc;
using UEToVS.Indexer.Text;
using UEToVS.Indexer.Unreal;

namespace UEToVS.Indexer
{
    /// <summary>
    /// UEToVS.Indexer --project &lt;path.uproject&gt; [--engine &lt;root&gt;] [--cache &lt;dir&gt;] [--pipe &lt;name&gt;] [--parent &lt;pid&gt;]
    ///                 [--query &lt;text&gt;] [--text &lt;text&gt;] [--all] [--verbose]
    /// With --pipe it serves JSON-RPC (IIndexService) over a named pipe; with --query/--text it runs once and prints.
    /// </summary>
    public static class Program
    {
        public static async Task<int> Main(string[] args)
        {
            string project = Arg(args, "--project");
            string engine = Arg(args, "--engine");
            string cache = Arg(args, "--cache") ?? DefaultCacheDir();
            string pipe = Arg(args, "--pipe");
            string parent = Arg(args, "--parent");
            string query = Arg(args, "--query");
            string text = Arg(args, "--text");
            bool all = args.Contains("--all");
            Log.Verbose = args.Contains("--verbose");

            if (project == null && engine == null)
            {
                Console.Error.WriteLine("usage: UEToVS.Indexer --project <file.uproject> [--engine <root>] [--pipe <name>] [--query <text>]");
                return 2;
            }

            var shutdown = new CancellationTokenSource();
            if (parent != null && int.TryParse(parent, out int pid)) WatchParent(pid, shutdown);

            var layout = ProjectLayout.Discover(project, engine);
            Log.Info($"Project: {layout.ProjectRoot ?? "-"}  Engine: {layout.EngineRoot ?? "(not found)"} {layout.EngineVersion}");
            using var store = new IndexStore(layout, cache) { IgnoreCache = args.Contains("--rebuild") };
            var textSearcher = new TextSearcher(store);
            var service = new IndexService(store, textSearcher, shutdown);

            if (pipe == null)
            {
                await store.BuildAsync(shutdown.Token);
                if (query != null) PrintQuery(service, query, all);
                if (text != null) PrintText(service, text, all);
                if (args.Contains("--stats")) PrintStats(store);
                return 0;
            }

            // Serve immediately; the index fills in the background.
            var build = Task.Run(() => store.BuildAsync(shutdown.Token));
            _ = build.ContinueWith(t => Log.Warn("Index build failed: " + t.Exception?.GetBaseException().Message), TaskContinuationOptions.OnlyOnFaulted);
            await ServeAsync(pipe, service, shutdown.Token);
            return 0;
        }

        private static async Task ServeAsync(string pipeName, IIndexService service, CancellationToken ct)
        {
            Log.Info("Listening on pipe " + pipeName);
            while (!ct.IsCancellationRequested)
            {
                var server = new NamedPipeServerStream(pipeName, PipeDirection.InOut, NamedPipeServerStream.MaxAllowedServerInstances,
                    PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
                try
                {
                    await server.WaitForConnectionAsync(ct);
                }
                catch (OperationCanceledException)
                {
                    server.Dispose();
                    break;
                }
                _ = Task.Run(async () =>
                {
                    using (server)
                    {
                        var rpc = JsonRpc.Attach(server, service);
                        await rpc.Completion.ContinueWith(_ => { });
                    }
                });
            }
        }

        private static void WatchParent(int pid, CancellationTokenSource shutdown)
        {
            try
            {
                var p = Process.GetProcessById(pid);
                p.EnableRaisingEvents = true;
                p.Exited += (_, __) =>
                {
                    Log.Info("Parent exited, shutting down");
                    shutdown.Cancel();
                    Environment.Exit(0);
                };
            }
            catch (Exception)
            {
                Environment.Exit(0);
            }
        }

        private static void PrintQuery(IIndexService service, string text, bool all)
        {
            var q = SearchQuery.Parse(text);
            var sw = Stopwatch.StartNew();
            var resp = service.SearchAsync(new SearchRequest
            {
                Pattern = q.Pattern,
                Container = q.Container,
                PathFragments = q.PathFragments.ToArray(),
                IncludeAssets = true,
                IncludeNonProject = all,
                MaxResultsPerCategory = 15,
            }, CancellationToken.None).GetAwaiter().GetResult();
            Console.WriteLine($"{resp.Hits.Count} hits in {sw.ElapsedMilliseconds} ms (non-project: {resp.NonProjectHitCount})");
            foreach (var h in resp.Hits)
            {
                switch (h.Category)
                {
                    case ResultCategory.File:
                        Console.WriteLine($"  [file  ] {h.File.Name,-40} {h.File.RelativePath} ({h.File.Module}, {h.File.Origin}) {h.Score}");
                        break;
                    case ResultCategory.Asset:
                        Console.WriteLine($"  [asset ] {h.Asset.Name,-40} {h.Asset.ObjectPath} {h.Score}");
                        break;
                    default:
                        var s = h.Symbol;
                        Console.WriteLine($"  [{s.Kind,-6}] {(s.Container == null ? "" : s.Container + "::")}{s.Name,-30} {Path.GetFileName(s.FilePath)}:{s.Line + 1} {s.Ue} {h.Score}");
                        break;
                }
            }
        }

        private static void PrintText(IIndexService service, string text, bool all)
        {
            var sw = Stopwatch.StartNew();
            var hits = service.SearchTextAsync(new TextSearchRequest { Query = text, IncludeNonProject = all, MaxResults = 30 }, CancellationToken.None).GetAwaiter().GetResult();
            Console.WriteLine($"{hits.Count} text hits in {sw.ElapsedMilliseconds} ms");
            foreach (var h in hits) Console.WriteLine($"  {h.FilePath}:{h.Line + 1}:{h.Column + 1}  {h.LineText.Trim()}");
        }

        private static void PrintStats(IndexStore store)
        {
            var snap = store.Current;
            Console.WriteLine("Symbols by kind:");
            foreach (var g in snap.Symbols.GroupBy(s => s.Kind).OrderByDescending(g => g.Count()))
                Console.WriteLine($"  {g.Key,-10} {g.Count()}");
            Console.WriteLine("Suspicious names (keyword-like or ALL_CAPS methods/fields):");
            var keywords = new System.Collections.Generic.HashSet<string> { "const", "override", "virtual", "static", "inline", "return", "if", "else", "void", "int", "float", "bool", "public", "private", "protected", "class", "struct", "typename", "template", "auto" };
            foreach (var g in snap.Symbols
                         .Where(s => keywords.Contains(s.Name) || ((s.Kind == UEToVS.Core.Protocol.SymbolKind.Method || s.Kind == UEToVS.Core.Protocol.SymbolKind.Field || s.Kind == UEToVS.Core.Protocol.SymbolKind.Function) && Parsing.CppSymbolScanner.IsMacroName(s.Name)))
                         .GroupBy(s => s.Kind + " " + s.Name).OrderByDescending(g => g.Count()).Take(25))
            {
                int idx = Array.IndexOf(snap.Symbols, g.First());
                Console.WriteLine($"  {g.Count(),6}  {g.Key}   e.g. {snap.SymbolFiles[idx].Path}:{g.First().Line + 1}");
            }
        }

        private static string Arg(string[] args, string name)
        {
            int i = Array.IndexOf(args, name);
            return i >= 0 && i + 1 < args.Length ? args[i + 1] : null;
        }

        private static string DefaultCacheDir() =>
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "UEToVS", "Index");
    }
}
