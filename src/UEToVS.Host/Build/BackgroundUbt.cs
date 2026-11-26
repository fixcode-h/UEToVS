using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Threading;
using UEToVS.Core.Diagnostics;
using UEToVS.Host.Diagnostics;
using UEToVS.Host.IntelliSense;
using UEToVS.Host.Logic.Build;
using UEToVS.Host.Logic.Localization;
using UEToVS.Host.Shell;
using UEToVS.Host.Unreal;

namespace UEToVS.Host.Build
{
    /// <summary>
    /// v0.11.0 background UnrealBuildTool runs (design 后续规划-代码诊断与提示 1.7 and stage 3 C):
    /// <list type="bullet">
    /// <item>Include-path export (build.ubtExport, on): when a Build.cs / Target.cs of the project changes — or the export is
    /// missing / older than them when the solution opens — runs <c>-Mode=JsonExport</c> once at low priority; the engine reads
    /// .vs\UEToVS\ubt\export.json for exact include folders.</item>
    /// <item>Save check (build.saveCheck, off): after a project .cpp is saved, compiles that file alone (<c>-SingleFile</c>) and
    /// merges the compiler's output into the build error marks.</item>
    /// </list>
    /// One UBT at a time, never while VS builds or project files are generated; a request that arrives meanwhile runs afterwards
    /// (the latest one per kind). Everything is logged as ubt.background.*.
    /// </summary>
    internal sealed class BackgroundUbt : IDisposable
    {
        private static readonly Logger L = Log.For("UBT");
        private static readonly TimeSpan ExportDelay = TimeSpan.FromSeconds(10);
        private static readonly TimeSpan StartupExportDelay = TimeSpan.FromSeconds(90);
        private static readonly TimeSpan SaveDelay = TimeSpan.FromSeconds(2);

        private readonly HostPackage _package;
        private readonly object _gate = new object();
        private FileSystemWatcher _watcher;
        private Timer _exportTimer, _saveTimer;
        private string _pendingSave;
        private bool _pendingExport;
        private int _running;
        private bool _disposed;

        public static BackgroundUbt Instance { get; private set; }

        private BackgroundUbt(HostPackage package) => _package = package;

        /// <summary>Solution opened (UE): watch the rules files and schedule the first export when it is out of date.</summary>
        public static void Start(HostPackage package)
        {
            Stop();
            if (package?.Unreal?.IsUnreal != true || package.Unreal.ProjectFile == null) return;
            var b = new BackgroundUbt(package);
            Instance = b;
            Safe.Execute(L, "ubt.background.start", b.Begin);
        }

        public static void Stop()
        {
            var b = Instance;
            Instance = null;
            b?.Dispose();
        }

        private string ProjectDir => Path.GetDirectoryName(_package.Unreal?.ProjectFile ?? string.Empty);

        private string ExportPath => Path.Combine(_package.SolutionDirectory ?? ProjectDir, ".vs", "UEToVS", "ubt", "export.json");

        private void Begin()
        {
            _exportTimer = new Timer(_ => Safe.Execute(L, "ubt.background.exportTimer", () => Kick()), null, Timeout.Infinite, Timeout.Infinite);
            _saveTimer = new Timer(_ => Safe.Execute(L, "ubt.background.saveTimer", () => Kick()), null, Timeout.Infinite, Timeout.Infinite);
            string dir = ProjectDir;
            if (Directory.Exists(dir))
            {
                _watcher = new FileSystemWatcher(dir, "*.cs") { IncludeSubdirectories = true, NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.FileName };
                _watcher.Changed += OnRulesChanged;
                _watcher.Created += OnRulesChanged;
                _watcher.Renamed += OnRulesChanged;
                _watcher.EnableRaisingEvents = true;
            }
            OpenCppDocuments.Saved += OnSaved;
            // first run: only when the export is missing or older than the rules (one-time cost, low priority)
            var rules = RulesFiles(dir).Select(SafeTime).ToList();
            DateTime? stamp = File.Exists(ExportPath) ? SafeTime(ExportPath) : (DateTime?)null;
            bool stale = BackgroundUbtPlan.ExportOutOfDate(stamp, rules);
            L.Info("ubt.background.started", null, ("rulesFiles", rules.Count), ("exportStale", stale), ("export", ExportPath));
            if (stale) RequestExport(StartupExportDelay, "startup");
        }

        private static IEnumerable<string> RulesFiles(string dir)
        {
            foreach (var sub in new[] { "Source", "Plugins" })
            {
                string root = Path.Combine(dir, sub);
                if (!Directory.Exists(root)) continue;
                IEnumerable<string> files;
                try { files = Directory.EnumerateFiles(root, "*.cs", SearchOption.AllDirectories).Where(IsRulesFile).ToList(); }
                catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException) { continue; }
                foreach (var f in files) yield return f;
            }
        }

        private static bool IsRulesFile(string path) =>
            path != null && (path.EndsWith(".Build.cs", StringComparison.OrdinalIgnoreCase) || path.EndsWith(".Target.cs", StringComparison.OrdinalIgnoreCase));

        private static DateTime SafeTime(string f)
        {
            try { return File.GetLastWriteTimeUtc(f); }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException) { return DateTime.MinValue; }
        }

        private void OnRulesChanged(object sender, FileSystemEventArgs e)
        {
            if (!IsRulesFile(e.FullPath) || e.FullPath.IndexOf(Path.DirectorySeparatorChar + "Intermediate" + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) >= 0) return;
            RequestExport(ExportDelay, Path.GetFileName(e.FullPath));
        }

        private void RequestExport(TimeSpan delay, string reason)
        {
            if (_package.Settings?.Effective?.UbtIncludeExport == false) return;
            lock (_gate)
            {
                if (_disposed) return;
                _pendingExport = true;
                _exportTimer?.Change(delay, Timeout.InfiniteTimeSpan);
            }
            L.Debug("ubt.background.exportRequested", null, ("reason", reason), ("delaySec", delay.TotalSeconds));
        }

        private void OnSaved(object sender, string path) => Safe.Execute(L, "ubt.background.saved", () =>
        {
            if (_package.Settings?.Effective?.SaveCompileCheck != true || !BackgroundUbtPlan.IsCompilable(path)) return;
            if (!Refactoring.RefactoringFeature.IsProjectPath(path)) return;
            lock (_gate)
            {
                if (_disposed) return;
                _pendingSave = path;
                _saveTimer?.Change(SaveDelay, Timeout.InfiniteTimeSpan);
            }
        });

        /// <summary>Starts the next pending run unless one is running, VS builds or project files are being generated (retried later).</summary>
        private void Kick()
        {
            if (_disposed) return;
            if (Interlocked.CompareExchange(ref _running, 1, 0) != 0) return; // the running one kicks again when it ends
            bool busy = BuildErrorService.Instance?.IsBuilding == true || UnrealTools.IsGeneratingProjectFiles;
            string save;
            bool export;
            lock (_gate)
            {
                save = _pendingSave;
                export = _pendingExport;
                if (busy && (save != null || export))
                {
                    // try again later instead of queueing behind the user's build
                    _exportTimer?.Change(export ? ExportDelay : Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
                    _saveTimer?.Change(save != null ? TimeSpan.FromSeconds(15) : Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
                    Interlocked.Exchange(ref _running, 0);
                    L.Debug("ubt.background.busy", null, ("save", save != null), ("export", export));
                    return;
                }
                // the save check first: the user is waiting for it; the export is background bookkeeping
                if (save != null) _pendingSave = null;
                else if (export) _pendingExport = false;
            }
            if (save == null && !export)
            {
                Interlocked.Exchange(ref _running, 0);
                return;
            }
            _ = Task.Run(async () =>
            {
                try
                {
                    if (save != null) await SaveCheckAsync(save).ConfigureAwait(false);
                    else await ExportAsync().ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    L.Warn("ubt.background.failed", "Background UnrealBuildTool run failed", ex, ("save", save), ("export", export));
                }
                finally
                {
                    Interlocked.Exchange(ref _running, 0);
                    Kick();
                }
            });
        }

        private (string Exe, string Target, string UProject) Command()
        {
            string uproject = _package.Unreal?.ProjectFile;
            string exe = UnrealTools.UbtExe(UnrealTools.EngineRoot(_package));
            string target = BackgroundUbtPlan.PickTarget(RulesFiles(ProjectDir).Where(f => f.EndsWith(".Target.cs", StringComparison.OrdinalIgnoreCase)),
                Path.GetFileNameWithoutExtension(uproject ?? string.Empty));
            return (exe, target, uproject);
        }

        private async Task ExportAsync()
        {
            if (_package.Settings?.Effective?.UbtIncludeExport == false) return;
            var (exe, target, uproject) = Command();
            if (exe == null || target == null || uproject == null)
            {
                L.Info("ubt.background.export.skipped", null, ("exe", exe != null), ("target", target), ("uproject", uproject));
                return;
            }
            string output = ExportPath;
            Directory.CreateDirectory(Path.GetDirectoryName(output));
            string temp = output + ".tmp";
            var sw = Stopwatch.StartNew();
            int lines = 0;
            int code = await UnrealTools.RunAsync(exe, BackgroundUbtPlan.ExportArgs(target, uproject, temp), Path.GetDirectoryName(uproject),
                (line, err) => Interlocked.Increment(ref lines), lowPriority: true).ConfigureAwait(false);
            bool ok = code == 0 && File.Exists(temp);
            if (ok)
            {
                // replace in one step: the engine may be reading the old file
                if (File.Exists(output)) File.Replace(temp, output, null);
                else File.Move(temp, output);
            }
            L.Info("ubt.background.export", null, ("target", target), ("exitCode", code), ("ok", ok), ("lines", lines), ("sec", Math.Round(sw.Elapsed.TotalSeconds, 1)), ("file", output));
        }

        private async Task SaveCheckAsync(string file)
        {
            var (exe, target, uproject) = Command();
            if (exe == null || target == null || uproject == null)
            {
                L.Info("ubt.background.saveCheck.skipped", null, ("exe", exe != null), ("target", target), ("uproject", uproject));
                return;
            }
            string name = Path.GetFileName(file);
            await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
            VsStatusBar.SetText(BuildStrings.SaveCheckRunning(name));
            await TaskScheduler.Default;
            var started = DateTime.UtcNow;
            var sw = Stopwatch.StartNew();
            var parser = new BuildOutputParser(_package.SolutionDirectory);
            bool liveCoding = false;
            int code = await UnrealTools.RunAsync(exe, BackgroundUbtPlan.SingleFileArgs(target, uproject, file), Path.GetDirectoryName(uproject), (line, err) =>
            {
                if (BackgroundUbtPlan.IsLiveCodingRefusal(line)) liveCoding = true;
                lock (parser) parser.AddLine(line);
            }, lowPriority: true).ConfigureAwait(false);
            BuildRecord record;
            lock (parser) record = BuildRecord.From(parser, started, DateTime.UtcNow, code == 0 ? "succeeded" : "failed", "save-check");
            L.Info("ubt.background.saveCheck", null, ("file", name), ("exitCode", code), ("errors", record.Errors), ("warnings", record.Warnings),
                ("liveCoding", liveCoding), ("sec", Math.Round(sw.Elapsed.TotalSeconds, 1)));
            await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
            if (liveCoding)
            {
                VsStatusBar.SetText(BuildStrings.SaveCheckLiveCoding);
                return;
            }
            if (code != 0 && record.Errors == 0)
            {
                VsStatusBar.SetText(BuildStrings.SaveCheckFailed(name));
                return;
            }
            BuildErrorService.Instance?.MergeSingleFile(file, record);
            VsStatusBar.SetText(BuildStrings.SaveCheckDone(name, record.Errors, record.Warnings, sw.Elapsed.TotalSeconds));
        }

        public void Dispose()
        {
            lock (_gate)
            {
                if (_disposed) return;
                _disposed = true;
            }
            OpenCppDocuments.Saved -= OnSaved;
            if (_watcher != null)
            {
                _watcher.EnableRaisingEvents = false;
                _watcher.Dispose();
            }
            _exportTimer?.Dispose();
            _saveTimer?.Dispose();
        }
    }
}
