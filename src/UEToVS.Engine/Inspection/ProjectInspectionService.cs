using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Threading;
using UEToVS.Contracts;
using UEToVS.Core.Diagnostics;
using UEToVS.Engine.Indexing;
using UEToVS.Engine.IntelliSense;
using UEToVS.Engine.Navigation;
using UEToVS.Engine.Unreal;

namespace UEToVS.Engine.Inspection
{
    /// <summary>
    /// v0.4.5 whole-project UE rule inspection: the squiggle rules (<see cref="DiagnosticsProvider"/>, UE0001–UE0010) over every
    /// project source file, on demand only. Runs on dedicated BelowNormal threads (as many as C++ indexing uses), streams
    /// findings to a JSON-lines file through one writer thread and keeps only counters in memory. One run at a time;
    /// cancellable; switched off (<see cref="Enabled"/> = false) it refuses to start and cancels a running job.
    /// </summary>
    public sealed class ProjectInspectionService : IDisposable
    {
        private static readonly Logger L = Log.For("Inspection");

        /// <summary>Files larger than this are skipped (generated tables, amalgamations): rules are about hand-written UE code.</summary>
        public const int MaxFileBytes = 4 * 1024 * 1024;

        private readonly IntelliSenseService _intelliSense;
        private readonly Func<bool, IReadOnlyList<string>> _listFiles;
        private readonly Func<int> _threads;
        private readonly Func<bool> _indexReady;
        private readonly string _outputDir;
        private readonly string _solutionDir;
        private readonly object _gate = new object();
        private Job _job;
        private volatile bool _enabled = true;

        public ProjectInspectionService(IntelliSenseService intelliSense, Func<bool, IReadOnlyList<string>> listFiles, Func<int> threads, Func<bool> indexReady,
                                        string outputDir, string solutionDir)
        {
            _intelliSense = intelliSense ?? throw new ArgumentNullException(nameof(intelliSense));
            _listFiles = listFiles ?? throw new ArgumentNullException(nameof(listFiles));
            _threads = threads ?? (() => 1);
            _indexReady = indexReady ?? (() => true);
            _outputDir = outputDir;
            _solutionDir = solutionDir;
        }

        /// <summary>While true, workers wait between files (user pause, build in progress with the "pause" policy).</summary>
        public Func<bool> ShouldPause { get; set; }

        /// <summary>The plugin switch. Turning it off cancels a running inspection.</summary>
        public bool Enabled
        {
            get => _enabled;
            set
            {
                if (_enabled == value) return;
                _enabled = value;
                L.Info("inspection.config", null, ("enabled", value));
                if (!value) Cancel("disabled");
            }
        }

        /// <summary>The folder the .jsonl files go to (&lt;solution&gt;\.vs\UEToVS\inspections).</summary>
        public string OutputDirectory => _outputDir;

        /// <summary>Project (and optionally project plugin) headers and sources from the file table.</summary>
        public static IReadOnlyList<string> ProjectFiles(FileTable files, bool includePlugins)
        {
            var list = new List<string>();
            foreach (var e in files.Snapshot())
            {
                var f = e.File;
                if (f == null || !(f.Kind == SourceFileKind.Header || f.Kind == SourceFileKind.Source)) continue;
                if (f.Origin == ItemOrigin.Project || (includePlugins && f.Origin == ItemOrigin.ProjectPlugin)) list.Add(f.Path);
            }
            list.Sort(StringComparer.OrdinalIgnoreCase);
            return list;
        }

        public ProjectInspectionResponse Handle(ProjectInspectionRequest request, CancellationToken ct)
        {
            request = request ?? new ProjectInspectionRequest();
            if (!_enabled)
            {
                L.Info("inspection.disabled", null, ("action", request.Action.ToString()));
                return new ProjectInspectionResponse { State = HeavyJobState.Disabled, Error = "Project inspection is switched off in the UEToVS settings." };
            }
            switch (request.Action)
            {
                case HeavyJobAction.Start: return Start(request.IncludePlugins);
                case HeavyJobAction.Cancel:
                    Cancel("request");
                    return Status();
                case HeavyJobAction.Results: return Results(request, ct);
                default: return Status();
            }
        }

        public ProjectInspectionResponse Start(bool includePlugins)
        {
            if (!_enabled) return new ProjectInspectionResponse { State = HeavyJobState.Disabled };
            lock (_gate)
            {
                if (_job != null && _job.State == HeavyJobState.Running) return _job.Snapshot(); // one run at a time
                if (!_indexReady())
                {
                    L.Info("inspection.refused", null, ("reason", "index-not-ready"));
                    return new ProjectInspectionResponse { State = HeavyJobState.Failed, Error = "index-not-ready" };
                }
                var files = _listFiles(includePlugins) ?? Array.Empty<string>();
                int threads = Math.Max(1, Math.Min(IndexingConfig.MaxThreads, _threads()));
                string path = null;
                try
                {
                    Directory.CreateDirectory(_outputDir);
                    InspectionResultsFile.Prune(_outputDir, InspectionResultsFile.KeepFiles - 1);
                    path = InspectionResultsFile.NewPath(_outputDir, DateTime.Now);
                }
                catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
                {
                    L.Warn("inspection.failed", "Cannot create the results folder", ex, ("dir", _outputDir));
                    return new ProjectInspectionResponse { State = HeavyJobState.Failed, Error = ex.Message };
                }
                _job?.Dispose();
                _job = new Job(this, files.ToArray(), includePlugins, threads, path);
                _job.Begin();
                return _job.Snapshot();
            }
        }

        public void Cancel(string reason)
        {
            Job job;
            lock (_gate) job = _job;
            job?.Cancel(reason);
        }

        public ProjectInspectionResponse Status()
        {
            Job job;
            lock (_gate) job = _job;
            return job?.Snapshot() ?? new ProjectInspectionResponse { State = HeavyJobState.Idle };
        }

        public ProjectInspectionResponse Results(ProjectInspectionRequest request, CancellationToken ct)
        {
            var resp = Status();
            if (resp.ResultsPath == null) return resp;
            int limit = Math.Max(1, Math.Min(2000, request.Limit <= 0 ? 200 : request.Limit));
            int offset = Math.Max(0, request.Offset);
            var sw = Stopwatch.StartNew();
            resp.Items = InspectionResultsFile.ReadPage(resp.ResultsPath, request.Rule, request.File, request.FixableOnly, offset, limit, out int matched, ct);
            resp.Matched = matched;
            L.Debug("inspection.page", null, ("rule", request.Rule), ("file", request.File), ("fixableOnly", request.FixableOnly), ("offset", offset), ("limit", limit),
                ("matched", matched), ("items", resp.Items.Count), ("ms", Math.Round(sw.Elapsed.TotalMilliseconds, 1)));
            return resp;
        }

        /// <summary>Blocks until the current run ends (tests, shutdown). True when it ended within the timeout.</summary>
        public bool Wait(TimeSpan timeout)
        {
            Job job;
            lock (_gate) job = _job;
            return job == null || job.Done.Wait(timeout);
        }

        public void Dispose()
        {
            Job job;
            lock (_gate) job = _job;
            job?.Cancel("shutdown");
            job?.Done.Wait(TimeSpan.FromSeconds(5));
            job?.Dispose();
        }

        // ================================================================== one run

        private sealed class Job : IDisposable
        {
            private readonly ProjectInspectionService _owner;
            private readonly string[] _files;
            private readonly bool _includePlugins;
            private readonly int _threads;
            private readonly string _path;
            private readonly CancellationTokenSource _cts = new CancellationTokenSource();
            private readonly BlockingCollection<InspectionFinding> _queue = new BlockingCollection<InspectionFinding>(8192);
            private readonly Stopwatch _sw = new Stopwatch();
            private readonly object _countGate = new object();
            private readonly Dictionary<string, int[]> _rules = new Dictionary<string, int[]>(StringComparer.Ordinal); // code → [count, fixable]
            private readonly Dictionary<string, int> _perFile = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            private readonly DocumentCache _documents = new DocumentCache(256, 128L * 1024 * 1024);
            private readonly MemberTableCache _members = new MemberTableCache(2048);
            private int _next;
            private int _filesDone;
            private int _findings;
            private int _errors;
            private int _skipped;
            private long _peakWorkingSet;
            private volatile HeavyJobState _state = HeavyJobState.Running;
            private string _error;
            private string _cancelReason;

            public Job(ProjectInspectionService owner, string[] files, bool includePlugins, int threads, string path)
            {
                _owner = owner;
                _files = files;
                _includePlugins = includePlugins;
                _threads = threads;
                _path = path;
            }

            public HeavyJobState State => _state;
            public ManualResetEventSlim Done { get; } = new ManualResetEventSlim(false);

            public void Begin()
            {
                _sw.Start();
                SamplePeak();
                L.Info("inspection.start", null, ("files", _files.Length), ("threads", _threads), ("includePlugins", _includePlugins), ("path", _path),
                    ("templates", _owner._intelliSense.TemplateInstantiationEnabled));
                var coordinator = new Thread(Run) { IsBackground = true, Priority = ThreadPriority.BelowNormal, Name = "UEToVS inspection" };
                coordinator.Start();
            }

            public void Cancel(string reason)
            {
                if (_state != HeavyJobState.Running) return;
                _cancelReason = reason;
                try { _cts.Cancel(); } catch (ObjectDisposedException) { }
            }

            private void Run()
            {
                Thread writer = null;
                Exception writeError = null;
                try
                {
                    var stream = new FileStream(_path, FileMode.CreateNew, FileAccess.Write, FileShare.Read | FileShare.Delete, 1 << 16);
                    writer = new Thread(() => writeError = WriteAll(stream)) { IsBackground = true, Priority = ThreadPriority.BelowNormal, Name = "UEToVS inspection writer" };
                    writer.Start();
                    var workers = new Thread[_threads];
                    for (int i = 0; i < workers.Length; i++)
                    {
                        workers[i] = new Thread(Work) { IsBackground = true, Priority = ThreadPriority.BelowNormal, Name = "UEToVS inspection " + i };
                        workers[i].Start();
                    }
                    long lastLog = 0;
                    while (!workers.All(w => w.Join(250)))
                    {
                        SamplePeak();
                        if (_sw.ElapsedMilliseconds - lastLog >= 10_000)
                        {
                            lastLog = _sw.ElapsedMilliseconds;
                            L.Info("inspection.progress", null, ("done", Volatile.Read(ref _filesDone)), ("total", _files.Length), ("findings", Volatile.Read(ref _findings)),
                                ("filesPerSec", Math.Round(Volatile.Read(ref _filesDone) / Math.Max(0.001, _sw.Elapsed.TotalSeconds), 1)), ("workingSetMB", Environment.WorkingSet >> 20));
                        }
                    }
                    _queue.CompleteAdding();
                    writer.Join();
                    if (writeError != null) throw new IOException("Writing inspection results failed: " + writeError.Message, writeError);
                    _state = _cts.IsCancellationRequested ? HeavyJobState.Cancelled : HeavyJobState.Completed;
                }
                catch (Exception ex)
                {
                    _error = ex.Message;
                    _state = HeavyJobState.Failed;
                    if (!_queue.IsAddingCompleted) _queue.CompleteAdding();
                    writer?.Join(5000);
                    L.Error("inspection.failed", "Project inspection failed", ex, ("path", _path), ("done", _filesDone), ("total", _files.Length));
                }
                finally
                {
                    _sw.Stop();
                    SamplePeak();
                    AppendSummary();
                    var rules = RuleCounts();
                    var data = new (string, object)[]
                    {
                        ("files", _files.Length), ("done", _filesDone), ("findings", _findings), ("errors", _errors), ("skipped", _skipped), ("threads", _threads),
                        ("ms", Math.Round(_sw.Elapsed.TotalMilliseconds, 1)), ("peakWorkingSetMB", _peakWorkingSet >> 20), ("managedMB", GC.GetTotalMemory(false) >> 20),
                        ("rules", string.Join(",", rules.Select(r => r.Code + "=" + r.Count))), ("fixable", rules.Sum(r => r.Fixable)),
                        ("includePlugins", _includePlugins), ("path", _path), ("reason", _cancelReason),
                    };
                    if (_state == HeavyJobState.Cancelled) L.Info("inspection.cancelled", null, data);
                    else if (_state == HeavyJobState.Completed) L.Info("inspection.done", null, data);
                    _documents.Clear();
                    _members.Clear();
                    Done.Set();
                }
            }

            private void Work()
            {
                var ct = _cts.Token;
                var provider = _owner._intelliSense.CreateDiagnosticsProvider();
                int i;
                while (!ct.IsCancellationRequested && (i = Interlocked.Increment(ref _next) - 1) < _files.Length)
                {
                    while (_owner.ShouldPause != null && _owner.ShouldPause() && !ct.IsCancellationRequested) Thread.Sleep(200);
                    string path = _files[i];
                    try
                    {
                        InspectFile(provider, path, ct);
                    }
                    catch (OperationCanceledException) when (ct.IsCancellationRequested)
                    {
                        break;
                    }
                    catch (Exception ex)
                    {
                        if (Interlocked.Increment(ref _errors) <= 20) L.Warn("inspection.file_failed", "Inspecting a file failed", ex, ("file", path));
                    }
                    if ((Interlocked.Increment(ref _filesDone) & 63) == 0) SamplePeak();
                }
            }

            private void InspectFile(DiagnosticsProvider provider, string path, CancellationToken ct)
            {
                byte[] raw;
                try
                {
                    var fi = new FileInfo(path);
                    if (!fi.Exists || fi.Length > MaxFileBytes) { Interlocked.Increment(ref _skipped); return; }
                    raw = File.ReadAllBytes(path);
                }
                catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
                {
                    Interlocked.Increment(ref _skipped);
                    return;
                }
                var doc = DocumentModel.Parse(path, 0, raw);
                int lines = doc.Lines.LineCount;
                if (lines == 0) return;
                var s = _owner._intelliSense.CreateSession(doc, _documents, _members, ct);
                var items = provider.Run(s, 0, lines - 1, -1, new DiagnosticsStats());
                if (items.Count == 0) return;
                foreach (var d in items)
                {
                    var f = new InspectionFinding
                    {
                        FilePath = path, Line = d.Line, Column = d.Column, Length = d.Length, Severity = d.Severity, Code = d.Code,
                        Args = d.Args ?? new List<string>(), Message = d.Message, LineText = InspectionFixes.LinePreview(doc, d.Line), Fix = InspectionFixes.For(doc, d),
                    };
                    Count(f);
                    _queue.Add(f, ct);
                }
            }

            private void Count(InspectionFinding f)
            {
                Interlocked.Increment(ref _findings);
                lock (_countGate)
                {
                    if (!_rules.TryGetValue(f.Code ?? "?", out var c)) _rules[f.Code ?? "?"] = c = new int[2];
                    c[0]++;
                    if (f.Fix != null) c[1]++;
                    _perFile.TryGetValue(f.FilePath, out int n);
                    _perFile[f.FilePath] = n + 1;
                }
            }

            private Exception WriteAll(FileStream stream)
            {
                var options = new JsonWriterOptions { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };
                try
                {
                    using (stream)
                    using (var w = new Utf8JsonWriter(stream, options))
                    {
                        InspectionResultsFile.WriteHeader(w, _owner._solutionDir, _includePlugins, _files.Length, DateTime.UtcNow);
                        EndLine(w, stream);
                        var flushed = Stopwatch.StartNew();
                        foreach (var f in _queue.GetConsumingEnumerable())
                        {
                            InspectionResultsFile.WriteFinding(w, f);
                            EndLine(w, stream);
                            if (flushed.ElapsedMilliseconds >= 1000 || _queue.Count == 0) { stream.Flush(); flushed.Restart(); }
                        }
                        stream.Flush();
                    }
                    return null;
                }
                catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
                {
                    _cts.Cancel();
                    foreach (var _ in _queue.GetConsumingEnumerable()) { } // unblock producers
                    return ex;
                }
            }

            private static void EndLine(Utf8JsonWriter w, Stream s)
            {
                w.Flush();
                s.WriteByte((byte)'\n');
                w.Reset(s);
            }

            private void AppendSummary()
            {
                try
                {
                    using (var stream = new FileStream(_path, FileMode.Append, FileAccess.Write, FileShare.Read | FileShare.Delete))
                    using (var w = new Utf8JsonWriter(stream))
                    {
                        InspectionResultsFile.WriteSummary(w, _state.ToString(), _filesDone, _findings, _sw.Elapsed.TotalMilliseconds, _peakWorkingSet, RuleCounts());
                        EndLine(w, stream);
                    }
                }
                catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
                {
                    L.Warn("inspection.summary_failed", "Cannot append the summary", ex, ("path", _path));
                }
            }

            private void SamplePeak()
            {
                long ws = Environment.WorkingSet;
                long cur;
                while (ws > (cur = Interlocked.Read(ref _peakWorkingSet)) && Interlocked.CompareExchange(ref _peakWorkingSet, ws, cur) != cur) { }
            }

            private List<InspectionRuleCount> RuleCounts()
            {
                lock (_countGate)
                    return _rules.Select(p => new InspectionRuleCount { Code = p.Key, Count = p.Value[0], Fixable = p.Value[1] })
                                 .OrderByDescending(r => r.Count).ThenBy(r => r.Code, StringComparer.Ordinal).ToList();
            }

            public ProjectInspectionResponse Snapshot()
            {
                var r = new ProjectInspectionResponse
                {
                    State = _state,
                    Error = _error,
                    ResultsPath = _path,
                    IncludePlugins = _includePlugins,
                    FilesTotal = _files.Length,
                    FilesDone = Math.Min(_files.Length, Volatile.Read(ref _filesDone)),
                    Findings = Volatile.Read(ref _findings),
                    Threads = _threads,
                    ElapsedMs = Math.Round(_sw.Elapsed.TotalMilliseconds, 1),
                    PeakWorkingSetBytes = Interlocked.Read(ref _peakWorkingSet),
                    Rules = RuleCounts(),
                };
                lock (_countGate)
                    r.TopFiles = _perFile.OrderByDescending(p => p.Value).ThenBy(p => p.Key, StringComparer.OrdinalIgnoreCase).Take(200)
                                         .Select(p => new InspectionFileCount { FilePath = p.Key, Count = p.Value }).ToList();
                return r;
            }

            public void Dispose()
            {
                if (!Done.IsSet) return; // still running: the thread owns the resources until it ends
                _cts.Dispose();
                _queue.Dispose();
            }
        }
    }
}
