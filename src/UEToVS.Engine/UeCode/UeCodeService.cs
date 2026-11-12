using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using UEToVS.Contracts;
using UEToVS.Core.Diagnostics;
using UEToVS.Engine.Indexing;
using UEToVS.Engine.Unreal;

namespace UEToVS.Engine.UeCode
{
    /// <summary>
    /// v0.4.5 editor link queries over the source files the index lists: automation tests and log categories / UE_LOG
    /// call sites (<see cref="UeMacroScanner"/>). Nothing runs until the first query (test runner opened, a log line
    /// double-clicked, related items on a log category). Then each project C++ file is scanned once and the result kept
    /// per file with its size + write time; later queries only re-read files whose stamp changed, so a warm query is a
    /// pass over the file table. Memory: one small entry per scanned file, hits only for files that have them.
    /// Engine sources are scanned only when a request asks for them.
    /// </summary>
    public sealed class UeCodeService
    {
        private static readonly Logger L = Log.For("UeCode");
        private const long MaxFileBytes = 4L * 1024 * 1024;

        private readonly Func<FileEntry[]> _files;
        private readonly Func<CancellationToken, Task<bool>> _waitListed;
        private readonly ConcurrentDictionary<string, Entry> _cache = new ConcurrentDictionary<string, Entry>(StringComparer.OrdinalIgnoreCase);

        private sealed class Entry
        {
            public long Size;
            public long Ticks;
            public UeMacroScanResult Result;
            public ItemOrigin Origin;
        }

        /// <param name="files">Snapshot of the listed files (the index file table).</param>
        /// <param name="waitListed">Completes when the workspace listing is done (true) or failed (false).</param>
        public UeCodeService(Func<FileEntry[]> files, Func<CancellationToken, Task<bool>> waitListed)
        {
            _files = files ?? (() => Array.Empty<FileEntry>());
            _waitListed = waitListed;
        }

        public UeCodeService(IndexOrchestrator index) : this(() => index.Files.Snapshot(), ct => index.WaitForListingAsync(ct)) { }

        /// <summary>Number of files with a cached scan result (diagnostics).</summary>
        public int CachedFiles => _cache.Count;

        public async Task<AutomationTestsResponse> FindAutomationTestsAsync(AutomationTestsRequest request, CancellationToken ct)
        {
            request ??= new AutomationTestsRequest();
            var sw = Stopwatch.StartNew();
            var resp = new AutomationTestsResponse { IndexListed = await WaitListedAsync(ct).ConfigureAwait(false) };
            var stats = Refresh(request.IncludeEngine, ct);
            resp.FilesScanned = stats.Files;
            resp.FilesRead = stats.Read;
            int max = request.MaxResults <= 0 ? 5000 : request.MaxResults;
            foreach (var kv in _cache.OrderBy(k => k.Key, StringComparer.OrdinalIgnoreCase))
            {
                var e = kv.Value;
                if (e.Result.Tests.Count == 0 || (!request.IncludeEngine && IsEngine(e.Origin))) continue;
                foreach (var t in e.Result.Tests)
                {
                    if (resp.Tests.Count >= max)
                    {
                        resp.Truncated = true;
                        break;
                    }
                    resp.Tests.Add(new AutomationTestInfo
                    {
                        Name = t.Name, ClassName = t.ClassName, Kind = t.Kind, Macro = t.Macro, Origin = e.Origin,
                        Location = new SourceLocation { FilePath = kv.Key, Line = t.Line, Column = t.Column },
                    });
                }
            }
            resp.ElapsedMs = Math.Round(sw.Elapsed.TotalMilliseconds, 1);
            L.Info("uecode.tests", null, ("tests", resp.Tests.Count), ("files", resp.FilesScanned), ("read", resp.FilesRead), ("listed", resp.IndexListed),
                ("includeEngine", request.IncludeEngine), ("truncated", resp.Truncated), ("ms", resp.ElapsedMs));
            return resp;
        }

        public async Task<LogSitesResponse> FindLogSitesAsync(LogSitesRequest request, CancellationToken ct)
        {
            var sw = Stopwatch.StartNew();
            var resp = new LogSitesResponse();
            if (string.IsNullOrWhiteSpace(request?.Category)) return resp;
            string category = request.Category.Trim();
            resp.IndexListed = await WaitListedAsync(ct).ConfigureAwait(false);
            var stats = Refresh(request.IncludeEngine, ct);
            resp.FilesScanned = stats.Files;
            resp.FilesRead = stats.Read;
            int max = request.MaxResults <= 0 ? 2000 : request.MaxResults;
            foreach (var kv in _cache.OrderBy(k => k.Key, StringComparer.OrdinalIgnoreCase))
            {
                var e = kv.Value;
                if (e.Result.LogSites.Count == 0 || (!request.IncludeEngine && IsEngine(e.Origin))) continue;
                foreach (var s in e.Result.LogSites)
                {
                    if (!string.Equals(s.Category, category, StringComparison.Ordinal)) continue;
                    bool usage = s.Kind == LogSiteKind.Usage;
                    if ((usage && !request.IncludeUsages) || (!usage && !request.IncludeDeclarations)) continue;
                    if (resp.Sites.Count >= max)
                    {
                        resp.Truncated = true;
                        break;
                    }
                    resp.Sites.Add(new LogSiteInfo
                    {
                        Kind = s.Kind, Category = s.Category, Macro = s.Macro, Verbosity = s.Verbosity, Format = s.Format, Origin = e.Origin,
                        Location = new SourceLocation { FilePath = kv.Key, Line = s.Line, Column = s.Column },
                    });
                }
            }
            resp.ElapsedMs = Math.Round(sw.Elapsed.TotalMilliseconds, 1);
            L.Info("uecode.logSites", null, ("category", category), ("sites", resp.Sites.Count), ("files", resp.FilesScanned), ("read", resp.FilesRead),
                ("listed", resp.IndexListed), ("includeEngine", request.IncludeEngine), ("ms", resp.ElapsedMs));
            return resp;
        }

        private async Task<bool> WaitListedAsync(CancellationToken ct)
        {
            if (_waitListed == null) return true;
            try
            {
                // Do not hold the user hostage while the engine discovers a huge workspace: answer with what is there.
                var wait = _waitListed(ct);
                var done = await Task.WhenAny(wait, Task.Delay(TimeSpan.FromSeconds(10), ct)).ConfigureAwait(false);
                return done == wait && wait.Status == TaskStatus.RanToCompletion && wait.Result;
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                L.Warn("uecode.listing.wait.failed", "Waiting for the workspace listing failed", ex);
                return false;
            }
        }

        private static bool IsEngine(ItemOrigin o) => o == ItemOrigin.Engine || o == ItemOrigin.EnginePlugin || o == ItemOrigin.ThirdParty;

        private static bool Wanted(ListedFile f, bool includeEngine) =>
            f != null && f.IsCpp && (includeEngine ? f.Origin != ItemOrigin.ThirdParty : !IsEngine(f.Origin));

        /// <summary>Brings the cache up to date for the wanted files; returns (files considered, files read).</summary>
        private (int Files, int Read) Refresh(bool includeEngine, CancellationToken ct)
        {
            var sw = Stopwatch.StartNew();
            var files = _files().Where(e => Wanted(e?.File, includeEngine)).Select(e => e.File).ToList();
            var present = new HashSet<string>(files.Select(f => f.Path), StringComparer.OrdinalIgnoreCase);
            foreach (var key in _cache.Keys)
            {
                if (present.Contains(key)) continue;
                if (_cache.TryGetValue(key, out var stale) && (includeEngine || !IsEngine(stale.Origin))) _cache.TryRemove(key, out _);
            }

            var toRead = new List<ListedFile>();
            foreach (var f in files)
            {
                if (_cache.TryGetValue(f.Path, out var e) && e.Size == f.Size && e.Ticks == f.LastWriteUtcTicks)
                {
                    // Files with hits are the jump targets: re-check their stamp on disk (a missed watcher event must not
                    // send the user to a stale line). Files without hits trust the listing.
                    if (e.Result.IsEmpty || StampMatches(f.Path, e)) continue;
                }
                toRead.Add(f);
            }

            int read = 0, failed = 0;
            if (toRead.Count > 0)
            {
                var options = new ParallelOptions { CancellationToken = ct, MaxDegreeOfParallelism = Math.Max(1, Math.Min(4, Environment.ProcessorCount / 2)) };
                Parallel.ForEach(toRead, options, f =>
                {
                    var entry = ScanFile(f);
                    if (entry == null)
                    {
                        Interlocked.Increment(ref failed);
                        _cache.TryRemove(f.Path, out _);
                        return;
                    }
                    Interlocked.Increment(ref read);
                    _cache[f.Path] = entry;
                });
            }
            if (toRead.Count > 0 || sw.ElapsedMilliseconds > 200)
                L.Info("uecode.refresh", null, ("files", files.Count), ("read", read), ("failed", failed), ("cached", _cache.Count), ("includeEngine", includeEngine),
                    ("ms", sw.ElapsedMilliseconds));
            return (files.Count, read);
        }

        private static bool StampMatches(string path, Entry e)
        {
            try
            {
                var fi = new FileInfo(path);
                return fi.Exists && fi.Length == e.Size && fi.LastWriteTimeUtc.Ticks == e.Ticks;
            }
            catch (Exception)
            {
                return false;
            }
        }

        private static Entry ScanFile(ListedFile f)
        {
            try
            {
                var fi = new FileInfo(f.Path);
                if (!fi.Exists) return null;
                var entry = new Entry { Size = fi.Length, Ticks = fi.LastWriteTimeUtc.Ticks, Origin = f.Origin, Result = UeMacroScanResult.Empty };
                // Keep the listing's stamp when it agrees, so the next refresh does not re-read the file.
                if (f.Size == fi.Length && f.LastWriteUtcTicks == fi.LastWriteTimeUtc.Ticks)
                {
                    entry.Size = f.Size;
                    entry.Ticks = f.LastWriteUtcTicks;
                }
                if (fi.Length == 0 || fi.Length > MaxFileBytes) return entry;
                string text = File.ReadAllText(f.Path, Encoding.UTF8);
                entry.Result = UeMacroScanner.Scan(text);
                return entry;
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
            {
                L.Debug("uecode.read.failed", null, ("file", f.Path), ("error", ex.GetType().Name + ": " + ex.Message));
                return null;
            }
        }
    }
}
