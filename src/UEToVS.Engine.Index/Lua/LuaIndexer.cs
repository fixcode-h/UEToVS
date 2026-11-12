using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Hashing;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace UEToVS.Engine.Index.Lua
{
    /// <summary>Counts of one scan or one change batch (logged by the engine).</summary>
    public sealed class LuaScanStats
    {
        public int Files;
        public int Parsed;
        public int Reused;
        public int Unchanged;
        public int Removed;
        public int Failed;
        public int Symbols;
        public double ElapsedMs;
        public string FirstError;

        public override string ToString() =>
            $"files={Files} parsed={Parsed} reused={Reused} unchanged={Unchanged} removed={Removed} failed={Failed} symbols={Symbols} ms={ElapsedMs:0.0}";
    }

    /// <summary>
    /// Builds and incrementally updates a <see cref="LuaIndexData"/>, the same way the C++ index does: a full scan reuses
    /// every file whose size and timestamp are unchanged, re-reads touched files and re-parses only those whose content
    /// hash (xxHash64) changed; change batches from the file watcher update just the files (or folders) they name.
    /// Pure: no threads of its own, no logging; every method returns a new <see cref="LuaIndexData"/>.
    /// </summary>
    public static class LuaIndexer
    {
        public const string Extension = ".lua";

        /// <summary>Folder names never scanned (VCS, editor and build output folders).</summary>
        private static readonly HashSet<string> SkippedFolders = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "node_modules", "Intermediate", "Binaries", "Saved", "DerivedDataCache",
        };

        public static bool IsLuaFile(string path) => path != null && path.EndsWith(Extension, StringComparison.OrdinalIgnoreCase);

        /// <summary>
        /// Splits the setting (paths separated by ';', ',' or new lines) and makes each absolute: rooted paths as they are,
        /// relative ones against the project folder (where the .uproject is), else the solution folder. Duplicates and
        /// folders inside another configured folder are dropped. Missing folders are kept (they may be created later).
        /// </summary>
        public static List<string> ResolveRoots(IEnumerable<string> configured, string solutionDir, string projectRoot)
        {
            var result = new List<string>();
            if (configured == null) return result;
            foreach (var raw in configured.SelectMany(SplitSetting))
            {
                string p = raw.Trim().Trim('"').Trim();
                if (p.Length == 0) continue;
                string full;
                try
                {
                    if (Path.IsPathRooted(p)) full = Path.GetFullPath(p);
                    else
                    {
                        var candidates = new[] { projectRoot, solutionDir }.Where(b => !string.IsNullOrEmpty(b)).Select(b => Path.GetFullPath(Path.Combine(b, p))).ToList();
                        if (candidates.Count == 0) continue;
                        full = candidates.FirstOrDefault(Directory.Exists) ?? candidates[0];
                    }
                }
                catch (Exception) { continue; }
                full = full.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
                if (result.Any(r => IsUnder(full, r))) continue;
                result.RemoveAll(r => IsUnder(r, full));
                result.Add(full);
            }
            return result;
        }

        public static IEnumerable<string> SplitSetting(string value) =>
            string.IsNullOrWhiteSpace(value) ? Enumerable.Empty<string>() : value.Split(new[] { ';', '\n', '\r', '|' }, StringSplitOptions.RemoveEmptyEntries);

        /// <summary>True when <paramref name="path"/> is <paramref name="root"/> or inside it (case-insensitive).</summary>
        public static bool IsUnder(string path, string root)
        {
            if (string.IsNullOrEmpty(path) || string.IsNullOrEmpty(root)) return false;
            if (path.Length < root.Length || string.Compare(path, 0, root, 0, root.Length, StringComparison.OrdinalIgnoreCase) != 0) return false;
            return path.Length == root.Length || path[root.Length] == Path.DirectorySeparatorChar || path[root.Length] == Path.AltDirectorySeparatorChar;
        }

        public static string RootOf(string path, IReadOnlyList<string> roots) => roots?.FirstOrDefault(r => IsUnder(path, r));

        /// <summary><c>require</c> name of a file under <paramref name="root"/>: <c>UI/MainPanel.lua</c> → <c>UI.MainPanel</c>.</summary>
        public static string ModuleName(string root, string path)
        {
            string rel = Path.GetRelativePath(root, path);
            if (rel.EndsWith(Extension, StringComparison.OrdinalIgnoreCase)) rel = rel.Substring(0, rel.Length - Extension.Length);
            return rel.Replace(Path.DirectorySeparatorChar, '.').Replace(Path.AltDirectorySeparatorChar, '.');
        }

        /// <summary>All .lua files under the roots (skipping dot-folders and build output). Missing roots yield nothing.</summary>
        public static IEnumerable<string> ListFiles(IEnumerable<string> roots, CancellationToken ct = default)
        {
            foreach (var root in roots ?? Enumerable.Empty<string>())
                foreach (var f in ListUnder(root, ct))
                    yield return f;
        }

        private static IEnumerable<string> ListUnder(string dir, CancellationToken ct)
        {
            if (!Directory.Exists(dir)) yield break;
            var stack = new Stack<string>();
            stack.Push(dir);
            while (stack.Count > 0)
            {
                ct.ThrowIfCancellationRequested();
                string d = stack.Pop();
                string[] files, dirs;
                try
                {
                    files = Directory.GetFiles(d, "*" + Extension);
                    dirs = Directory.GetDirectories(d);
                }
                catch (Exception) { continue; } // access denied, vanished while listing
                foreach (var f in files)
                    if (IsLuaFile(f)) yield return Path.GetFullPath(f);
                foreach (var sub in dirs)
                {
                    string name = Path.GetFileName(sub);
                    if (name.StartsWith(".", StringComparison.Ordinal) || SkippedFolders.Contains(name)) continue;
                    stack.Push(sub);
                }
            }
        }

        /// <summary>Is <paramref name="path"/> inside a folder the scan skips (relative to its root)?</summary>
        public static bool IsInSkippedFolder(string path, string root)
        {
            if (root == null) return true;
            string rel = Path.GetRelativePath(root, path);
            var parts = rel.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            for (int k = 0; k < parts.Length - 1; k++)
                if (parts[k].StartsWith(".", StringComparison.Ordinal) || SkippedFolders.Contains(parts[k])) return true;
            return false;
        }

        /// <summary>
        /// Full scan of <paramref name="roots"/>. With <paramref name="previous"/> (the last saved index) unchanged files
        /// are reused; pass null to re-parse everything (rebuild).
        /// </summary>
        public static LuaIndexData Scan(IReadOnlyList<string> roots, LuaIndexData previous, LuaScanStats stats, int parallelism = 2, CancellationToken ct = default)
        {
            var sw = System.Diagnostics.Stopwatch.StartNew();
            var old = Map(previous);
            var paths = ListFiles(roots, ct).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            var entries = new LuaFileEntry[paths.Count];
            var options = new ParallelOptions { MaxDegreeOfParallelism = Math.Max(1, parallelism), CancellationToken = ct };
            Parallel.For(0, paths.Count, options, k =>
            {
                string path = paths[k];
                old.TryGetValue(path, out var prev);
                entries[k] = Refresh(path, RootOf(path, roots), prev, stats);
            });
            var data = new LuaIndexData { Roots = roots.ToList(), WrittenUtc = DateTime.UtcNow };
            data.Files.AddRange(entries.Where(e => e != null).OrderBy(e => e.Path, StringComparer.OrdinalIgnoreCase));
            stats.Files = data.Files.Count;
            var kept = new HashSet<string>(data.Files.Select(f => f.Path), StringComparer.OrdinalIgnoreCase);
            stats.Removed += old.Keys.Count(p => !kept.Contains(p));
            stats.Symbols = data.SymbolCount;
            stats.ElapsedMs = sw.Elapsed.TotalMilliseconds;
            return data;
        }

        /// <summary>
        /// Applies watcher / host change notifications: each path is a file (re-stamped, re-parsed when its content
        /// changed, removed when gone) or a folder (re-listed when it exists, its files dropped when it does not).
        /// </summary>
        public static LuaIndexData Apply(LuaIndexData current, IEnumerable<string> changedPaths, LuaScanStats stats, CancellationToken ct = default)
        {
            var sw = System.Diagnostics.Stopwatch.StartNew();
            current ??= new LuaIndexData();
            var roots = current.Roots;
            var files = Map(current);
            foreach (var raw in changedPaths ?? Enumerable.Empty<string>())
            {
                ct.ThrowIfCancellationRequested();
                string path;
                try { path = Path.GetFullPath(raw); }
                catch (Exception) { continue; }
                string root = RootOf(path, roots);
                if (root == null || IsInSkippedFolder(path, root)) continue;
                if (Directory.Exists(path))
                {
                    var present = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                    foreach (var f in ListUnder(path, ct))
                    {
                        present.Add(f);
                        files.TryGetValue(f, out var prev);
                        var e = Refresh(f, root, prev, stats);
                        if (e != null) files[f] = e;
                    }
                    foreach (var gone in files.Keys.Where(k => IsUnder(k, path) && !present.Contains(k)).ToList())
                    {
                        files.Remove(gone);
                        stats.Removed++;
                    }
                    continue;
                }
                if (IsLuaFile(path) && File.Exists(path))
                {
                    files.TryGetValue(path, out var prev);
                    var e = Refresh(path, root, prev, stats);
                    if (e != null) files[path] = e;
                    else if (files.Remove(path)) stats.Removed++;
                    continue;
                }
                // Gone: a deleted file, or a deleted / renamed-away folder.
                if (files.Remove(path)) stats.Removed++;
                else if (!IsLuaFile(path))
                    foreach (var gone in files.Keys.Where(k => IsUnder(k, path)).ToList())
                    {
                        files.Remove(gone);
                        stats.Removed++;
                    }
            }
            var data = new LuaIndexData { Roots = roots.ToList(), WrittenUtc = DateTime.UtcNow };
            data.Files.AddRange(files.Values.OrderBy(e => e.Path, StringComparer.OrdinalIgnoreCase));
            stats.Files = data.Files.Count;
            stats.Symbols = data.SymbolCount;
            stats.ElapsedMs = sw.Elapsed.TotalMilliseconds;
            return data;
        }

        private static Dictionary<string, LuaFileEntry> Map(LuaIndexData data)
        {
            var d = new Dictionary<string, LuaFileEntry>(StringComparer.OrdinalIgnoreCase);
            if (data?.Files != null)
                foreach (var f in data.Files)
                    if (f?.Path != null) d[f.Path] = f;
            return d;
        }

        /// <summary>Entry for one file: <paramref name="prev"/> when untouched, re-stamped when only touched, re-parsed otherwise; null when unreadable.</summary>
        private static LuaFileEntry Refresh(string path, string root, LuaFileEntry prev, LuaScanStats stats)
        {
            try
            {
                var info = new FileInfo(path);
                if (!info.Exists) return null;
                long size = info.Length, ticks = info.LastWriteTimeUtc.Ticks;
                string module = root == null ? Path.GetFileNameWithoutExtension(path) : ModuleName(root, path);
                if (prev != null && prev.Size == size && prev.LastWriteUtcTicks == ticks && prev.Module == module)
                {
                    Interlocked.Increment(ref stats.Reused);
                    return prev;
                }
                byte[] bytes = File.ReadAllBytes(path);
                ulong hash = XxHash64.HashToUInt64(bytes);
                var entry = new LuaFileEntry { Path = path, Root = root, Module = module, Size = bytes.LongLength, LastWriteUtcTicks = ticks, Hash = hash };
                if (prev != null && prev.Hash == hash)
                {
                    entry.Symbols = prev.Symbols;
                    Interlocked.Increment(ref stats.Unchanged);
                    return entry;
                }
                entry.Symbols = LuaParser.Parse(Decode(bytes));
                Interlocked.Increment(ref stats.Parsed);
                return entry;
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
            {
                Interlocked.Increment(ref stats.Failed);
                Interlocked.CompareExchange(ref stats.FirstError, path + ": " + ex.Message, null);
                return prev; // keep what we had (file locked while being saved); the next change retries
            }
        }

        /// <summary>UTF-8 (with or without BOM); invalid bytes (e.g. GBK comments) become U+FFFD and do not shift lines.</summary>
        public static string Decode(byte[] bytes)
        {
            int start = bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF ? 3 : 0;
            return Encoding.UTF8.GetString(bytes, start, bytes.Length - start);
        }
    }
}
