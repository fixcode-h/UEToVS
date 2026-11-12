using System;
using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.VisualStudio.Shell;
using StreamJsonRpc;
using UEToVS.Core.Protocol;
using UEToVS.UI;

namespace UEToVS.Indexing
{
    /// <summary>
    /// Starts UEToVS.Indexer for the open solution and talks to it over a named pipe (JSON-RPC).
    /// One indexer process per solution; it exits with Visual Studio.
    /// </summary>
    internal sealed class IndexerClient : IDisposable
    {
        private readonly string _indexerPath;
        private Process _process;
        private JsonRpc _rpc;
        private IIndexService _service;
        private string _pipeName;
        private int _restarts;
        private readonly SemaphoreSlim _connectLock = new SemaphoreSlim(1, 1);

        public IndexerClient(string extensionDirectory)
        {
            _indexerPath = Path.Combine(extensionDirectory, "Indexer", "UEToVS.Indexer.exe");
        }

        public string ProjectFile { get; private set; }
        public string ProjectRoot => ProjectFile == null ? null : Path.GetDirectoryName(ProjectFile);
        public string EngineRoot { get; private set; }
        public bool IsRunning => _process != null && !_process.HasExited && _service != null;
        public IndexStatus LastStatus { get; private set; }

        public event EventHandler StatusChanged;

        /// <summary>Finds the .uproject for a solution directory (UE writes the .sln next to it).</summary>
        public static string FindProjectFile(string solutionDir)
        {
            if (string.IsNullOrEmpty(solutionDir) || !Directory.Exists(solutionDir)) return null;
            var direct = Directory.GetFiles(solutionDir, "*.uproject");
            if (direct.Length > 0) return direct[0];
            // Engine solutions (UE5.sln) have no .uproject; a single project folder next to the sln is common.
            foreach (var sub in Directory.GetDirectories(solutionDir))
            {
                var p = Directory.GetFiles(sub, "*.uproject");
                if (p.Length == 1) return p[0];
            }
            return null;
        }

        public async Task StartAsync(string solutionDir, string engineOverride, string extraArgs)
        {
            Stop();
            ProjectFile = FindProjectFile(solutionDir);
            bool isEngineSolution = ProjectFile == null && Directory.Exists(Path.Combine(solutionDir ?? "", "Engine", "Source"));
            if (ProjectFile == null && !isEngineSolution)
            {
                OutputLog.Write("No .uproject next to the solution; UEToVS index is off for this solution.");
                return;
            }
            if (!File.Exists(_indexerPath))
            {
                OutputLog.Write("Indexer not found: " + _indexerPath);
                return;
            }

            _pipeName = "uetovs-" + Process.GetCurrentProcess().Id + "-" + Guid.NewGuid().ToString("N").Substring(0, 8);
            string args = (ProjectFile != null ? $"--project \"{ProjectFile}\" " : "") +
                          (!string.IsNullOrWhiteSpace(engineOverride) ? $"--engine \"{engineOverride.Trim()}\" " : isEngineSolution ? $"--engine \"{solutionDir}\" " : "") +
                          $"--pipe {_pipeName} --parent {Process.GetCurrentProcess().Id} {extraArgs}";
            var psi = new ProcessStartInfo(_indexerPath, args)
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardError = true,
                RedirectStandardOutput = true,
                WorkingDirectory = Path.GetDirectoryName(_indexerPath),
            };
            try
            {
                _process = Process.Start(psi);
            }
            catch (Exception ex)
            {
                OutputLog.Write("Cannot start indexer: " + ex.Message + " (需要 .NET 8 Desktop/Runtime: https://dotnet.microsoft.com/download/dotnet/8.0)");
                return;
            }
            _process.EnableRaisingEvents = true;
            _process.ErrorDataReceived += (_, e) => { if (e.Data != null) OutputLog.Write(e.Data); };
            _process.OutputDataReceived += (_, e) => { if (e.Data != null) OutputLog.Write(e.Data); };
            _process.BeginErrorReadLine();
            _process.BeginOutputReadLine();
            _process.Exited += OnProcessExited;
            OutputLog.Write("Indexer started: " + args);

            await ConnectAsync().ConfigureAwait(false);
            _ = PollStatusAsync();
        }

        private void OnProcessExited(object sender, EventArgs e)
        {
            int code = -1;
            try { code = _process?.ExitCode ?? -1; } catch (InvalidOperationException) { }
            OutputLog.Write("Indexer exited with code " + code);
            _service = null;
            if (code == -2147450749 || code == unchecked((int)0x80008096))
                OutputLog.Write("看起来缺少 .NET 8 运行时，请安装：https://dotnet.microsoft.com/download/dotnet/8.0");
        }

        private async Task ConnectAsync()
        {
            await _connectLock.WaitAsync().ConfigureAwait(false);
            try
            {
                for (int attempt = 0; attempt < 50 && _process != null && !_process.HasExited; attempt++)
                {
                    var stream = new NamedPipeClientStream(".", _pipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
                    try
                    {
                        await stream.ConnectAsync(200).ConfigureAwait(false);
                        _rpc = JsonRpc.Attach(stream);
                        _service = _rpc.Attach<IIndexService>();
                        _rpc.Disconnected += (_, __) => _service = null;
                        return;
                    }
                    catch (TimeoutException)
                    {
                        stream.Dispose();
                    }
                    catch (IOException)
                    {
                        stream.Dispose();
                        await Task.Delay(100).ConfigureAwait(false);
                    }
                }
            }
            finally
            {
                _connectLock.Release();
            }
        }

        private async Task PollStatusAsync()
        {
            while (_process != null && !_process.HasExited)
            {
                var svc = _service;
                if (svc != null)
                {
                    try
                    {
                        LastStatus = await svc.GetStatusAsync(CancellationToken.None).ConfigureAwait(false);
                        EngineRoot = LastStatus.EngineRoot;
                        StatusChanged?.Invoke(this, EventArgs.Empty);
                        if (LastStatus.SymbolsReady)
                        {
                            OutputLog.Status($"UEToVS: 索引完成，{LastStatus.SymbolCount:N0} 个符号 / {LastStatus.FileCount:N0} 个文件");
                            return;
                        }
                        if (LastStatus.FilesToParse > 0)
                            OutputLog.Status($"UEToVS: 正在索引 {LastStatus.FilesParsed:N0}/{LastStatus.FilesToParse:N0}");
                    }
                    catch (Exception)
                    {
                    }
                }
                await Task.Delay(1000).ConfigureAwait(false);
            }
        }

        /// <summary>Returns the RPC proxy, restarting the indexer once if it died. Null when unavailable.</summary>
        public IIndexService Service
        {
            get
            {
                if (_service != null) return _service;
                return null;
            }
        }

        public async Task<IIndexService> GetServiceAsync(string solutionDir, string engineOverride, string extraArgs)
        {
            if (_service != null) return _service;
            if (_process != null && !_process.HasExited)
            {
                await ConnectAsync().ConfigureAwait(false);
                return _service;
            }
            if (_restarts++ < 3 && solutionDir != null)
            {
                await StartAsync(solutionDir, engineOverride, extraArgs).ConfigureAwait(false);
                return _service;
            }
            return null;
        }

        public void Stop()
        {
            try
            {
                if (_service != null)
                {
                    var svc = _service;
                    _ = Task.Run(async () => { try { await svc.ShutdownAsync(); } catch { } });
                }
                _rpc?.Dispose();
                if (_process != null && !_process.HasExited)
                {
                    if (!_process.WaitForExit(300)) _process.Kill();
                }
            }
            catch (Exception)
            {
            }
            _rpc = null;
            _service = null;
            _process = null;
            LastStatus = null;
        }

        public void Dispose() => Stop();
    }
}
