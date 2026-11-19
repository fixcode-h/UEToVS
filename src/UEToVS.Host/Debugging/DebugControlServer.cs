using System;
using System.IO;
using System.IO.Pipes;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using UEToVS.Core.Diagnostics;
using UEToVS.Core.Storage;
using UEToVS.Host.Logic.Debugging;

namespace UEToVS.Host.Debugging
{
    /// <summary>
    /// v0.8.0 VS debug control: a named pipe in the VS process through which agents drive VS's own debugger
    /// (docs/design/VS-调试控制.md). Lives while a UE solution is open and the setting is on.
    /// <list type="bullet">
    /// <item>Only the current Windows user may connect (pipe ACL); every request carries the random token of
    /// &lt;solution&gt;\.vs\UEToVS\Debug\endpoint.json, which is how clients (UEToVS.Mcp, the Agent panel's UE plugin) find the pipe.</item>
    /// <item>One JSON line in, one JSON line out; several requests per connection; connections are served concurrently
    /// (a long <c>wait</c> does not block a <c>status</c>).</item>
    /// <item>Every debugger call runs on the UI thread inside the backend; this class never touches DTE.</item>
    /// </list>
    /// </summary>
    internal sealed class DebugControlServer : IDisposable
    {
        private static readonly Logger L = Log.For("Debug");
        private const int MaxLineChars = 64 * 1024;

        private readonly CancellationTokenSource _cts = new CancellationTokenSource();
        private readonly DteDebugBackend _backend;
        private readonly DebugDispatcher _dispatcher;
        private readonly string _endpointFile;
        private readonly string _pipeName;
        private readonly string _instanceFile;
        private int _connections;

        private DebugControlServer(DteDebugBackend backend, string endpointFile)
        {
            _backend = backend;
            _endpointFile = endpointFile;
            int pid = System.Diagnostics.Process.GetCurrentProcess().Id;
            _pipeName = DebugProtocol.PipePrefix + pid;
            _instanceFile = Path.Combine(StoragePaths.DebugInstancesDir(), pid + ".json");
            Token = DebugDispatcher.NewToken();
            _dispatcher = new DebugDispatcher(Token, backend);
        }

        public string Token { get; }

        /// <summary>Starts serving for this solution (UI thread: the backend subscribes to DTE events). Never throws.</summary>
        public static async Task<DebugControlServer> StartAsync(HostPackage package, string solutionDir, string solutionFile, string projectFile, string vsVersion)
        {
            try
            {
                var backend = await DteDebugBackend.CreateAsync(package, solutionDir, projectFile);
                var server = new DebugControlServer(backend, StoragePaths.ForSolution(solutionDir).DebugEndpoint);
                server.WriteEndpoint(solutionDir, solutionFile, projectFile, vsVersion);
                _ = Task.Run(() => server.AcceptLoopAsync(server._cts.Token));
                L.Info("debug.control.start", null, ("pipe", server._pipeName), ("endpoint", server._endpointFile));
                return server;
            }
            catch (Exception ex)
            {
                L.Error("debug.control.start.failed", "VS debug control unavailable; agents cannot drive the debugger", ex);
                return null;
            }
        }

        private void WriteEndpoint(string solutionDir, string solutionFile, string projectFile, string vsVersion)
        {
            var o = new JObject
            {
                ["protocol"] = DebugProtocol.Version,
                ["pipe"] = _pipeName,
                ["pid"] = System.Diagnostics.Process.GetCurrentProcess().Id,
                ["token"] = Token,
                ["solution"] = solutionFile,
                ["solutionDir"] = solutionDir,
                ["project"] = projectFile,
                ["uetovs"] = HostPackage.HostVersion,
                ["vs"] = vsVersion,
                ["startedUtc"] = DateTime.UtcNow.ToString("o"),
            };
            string text = o.ToString(Formatting.Indented);
            WriteAtomic(_endpointFile, text);
            // 用户级的这一份写不了也不影响本解决方案的调试控制，只是面板找不到「开着别的项目的 VS」
            try
            {
                DeleteStaleInstances();
                WriteAtomic(_instanceFile, text);
            }
            catch (Exception ex) { L.Warn("debug.control.instance.write.failed", null, ex, ("file", _instanceFile)); }
        }

        private static void WriteAtomic(string file, string text)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(file));
            string tmp = file + ".tmp";
            File.WriteAllText(tmp, text, new UTF8Encoding(false));
            if (File.Exists(file)) File.Delete(file);
            File.Move(tmp, file);
        }

        /// <summary>VS 崩了没删的 &lt;PID&gt;.json：进程已经不在的删掉（面板那边也按 PID 判断，这里只是顺手清理）。</summary>
        private static void DeleteStaleInstances()
        {
            string dir = StoragePaths.DebugInstancesDir();
            if (!Directory.Exists(dir)) return;
            foreach (string f in Directory.GetFiles(dir, "*.json"))
            {
                if (!int.TryParse(Path.GetFileNameWithoutExtension(f), out int pid)) continue;
                bool alive;
                try { using (var p = System.Diagnostics.Process.GetProcessById(pid)) alive = !p.HasExited; }
                catch (ArgumentException) { alive = false; }
                catch (InvalidOperationException) { alive = false; }
                if (alive) continue;
                try { File.Delete(f); } catch (IOException) { } catch (UnauthorizedAccessException) { }
            }
        }

        private NamedPipeServerStream CreatePipe()
        {
            var security = new PipeSecurity();
            var me = WindowsIdentity.GetCurrent().User;
            security.AddAccessRule(new PipeAccessRule(me, PipeAccessRights.FullControl, AccessControlType.Allow));
            return new NamedPipeServerStream(_pipeName, PipeDirection.InOut, NamedPipeServerStream.MaxAllowedServerInstances,
                                              PipeTransmissionMode.Byte, PipeOptions.Asynchronous, 0, 0, security);
        }

        private async Task AcceptLoopAsync(CancellationToken ct)
        {
            while (!ct.IsCancellationRequested)
            {
                NamedPipeServerStream pipe = null;
                try
                {
                    pipe = CreatePipe();
                    await pipe.WaitForConnectionAsync(ct).ConfigureAwait(false);
                    var connected = pipe;
                    pipe = null;
                    _ = Task.Run(() => ServeAsync(connected, ct));
                }
                catch (OperationCanceledException) { pipe?.Dispose(); return; }
                catch (ObjectDisposedException) { pipe?.Dispose(); return; }
                catch (Exception ex)
                {
                    pipe?.Dispose();
                    L.Warn("debug.control.accept.failed", null, ex);
                    try { await Task.Delay(1000, ct).ConfigureAwait(false); } catch (OperationCanceledException) { return; }
                }
            }
        }

        private async Task ServeAsync(NamedPipeServerStream pipe, CancellationToken ct)
        {
            int id = Interlocked.Increment(ref _connections);
            var utf8 = new UTF8Encoding(false);
            try
            {
                using (pipe)
                using (var reader = new StreamReader(pipe, utf8, false, 4096, leaveOpen: true))
                using (var writer = new StreamWriter(pipe, utf8, 4096, leaveOpen: true) { NewLine = "\n", AutoFlush = true })
                {
                    while (!ct.IsCancellationRequested && pipe.IsConnected)
                    {
                        string line = await reader.ReadLineAsync().ConfigureAwait(false);
                        if (line == null) break;
                        if (line.Length == 0) continue;
                        if (line.Length <= MaxLineChars && line.Contains("\"" + DebugProtocol.Subscribe + "\""))
                        {
                            await SubscribeAsync(line, writer, pipe, ct).ConfigureAwait(false);
                            break;
                        }
                        var started = DateTime.UtcNow;
                        string response = line.Length > MaxLineChars
                            ? new JObject { ["ok"] = false, ["error"] = "请求太长" }.ToString(Formatting.None)
                            : await _dispatcher.HandleLineAsync(line, ct).ConfigureAwait(false);
                        await writer.WriteLineAsync(response).ConfigureAwait(false);
                        L.Debug("debug.control.request", null, ("conn", id), ("ms", (long)(DateTime.UtcNow - started).TotalMilliseconds), ("bytes", response.Length));
                    }
                }
            }
            catch (IOException) { /* client went away */ }
            catch (ObjectDisposedException) { }
            catch (Exception ex) { L.Warn("debug.control.serve.failed", null, ex, ("conn", id)); }
        }

        /// <summary>
        /// subscribe: pushes the status now and after every debugger / build change (bursts like step = run + break are
        /// merged), plus every 30 s so a dead client is noticed by the failing write. Ends when the client disconnects.
        /// </summary>
        private async Task SubscribeAsync(string line, StreamWriter writer, NamedPipeServerStream pipe, CancellationToken ct)
        {
            string denied = _dispatcher.Authorize(line, out var req);
            if (denied != null)
            {
                await writer.WriteLineAsync(denied).ConfigureAwait(false);
                return;
            }
            var id = req["id"];
            using (var signal = new SemaphoreSlim(0))
            {
                EventHandler onChanged = (s, e) =>
                {
                    if (signal.CurrentCount == 0) signal.Release();
                };
                _backend.Tracker.Changed += onChanged;
                try
                {
                    while (!ct.IsCancellationRequested && pipe.IsConnected)
                    {
                        string text;
                        try
                        {
                            var status = await _backend.ExecuteAsync(DebugProtocol.Status, new DebugArgs(new JObject { ["frames"] = 5 }), ct).ConfigureAwait(false);
                            text = new JObject { ["id"] = id, ["event"] = "state", ["result"] = status }.ToString(Formatting.None);
                        }
                        catch (OperationCanceledException) { return; }
                        catch (Exception ex)
                        {
                            text = new JObject { ["id"] = id, ["event"] = "error", ["error"] = ex.Message }.ToString(Formatting.None);
                        }
                        await writer.WriteLineAsync(text).ConfigureAwait(false);
                        await signal.WaitAsync(30000, ct).ConfigureAwait(false);
                        await Task.Delay(150, ct).ConfigureAwait(false);
                        while (signal.CurrentCount > 0) await signal.WaitAsync(0).ConfigureAwait(false);
                    }
                }
                catch (OperationCanceledException) { }
                finally
                {
                    _backend.Tracker.Changed -= onChanged;
                }
            }
        }

        public void Dispose()
        {
            _cts.Cancel();
            _backend.Dispose();
            try
            {
                // Only our own file: a second VS on the same solution may have written its endpoint after us.
                if (File.Exists(_endpointFile) && JObject.Parse(File.ReadAllText(_endpointFile)).Value<string>("pipe") == _pipeName)
                    File.Delete(_endpointFile);
            }
            catch (Exception ex) { L.Debug("debug.control.endpoint.delete.failed", ex.Message); }
            try { if (File.Exists(_instanceFile)) File.Delete(_instanceFile); }
            catch (Exception ex) { L.Debug("debug.control.instance.delete.failed", ex.Message); }
            L.Info("debug.control.stop", null, ("pipe", _pipeName));
        }
    }
}
