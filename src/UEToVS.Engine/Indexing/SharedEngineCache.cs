using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Hashing;
using System.Linq;
using System.Text;
using MessagePack;
using MessagePack.Resolvers;
using UEToVS.Core.Diagnostics;
using UEToVS.Engine.Index;

namespace UEToVS.Engine.Indexing
{
    /// <summary>
    /// Engine part of the index shared between projects using the same engine install:
    /// <c>%LocalAppData%\UEToVS\EngineIndex\&lt;engine id&gt;\v&lt;ticks&gt;\</c> holds the engine segment files and
    /// <c>state.bin</c> (per-file size / mtime / hash as stored in those segments; written last, so its presence marks a
    /// complete version). The engine id is the engine build id (version + build/changelist + install path hash) plus a
    /// hash of the index cache key (parser and format versions), so any change of those starts a new cache.
    /// <para>Import attaches the segment files in place (read-only memory maps, referenced from the project's manifest by
    /// absolute path; several VS instances can map them at once); the usual per-file size/mtime check then re-parses only
    /// engine files that differ on disk (those go to the project's delta / project segments, never into the shared files).
    /// Export runs under a lock file, writes a new version folder (segments hard-linked or copied, state.bin last = atomic
    /// publish) and keeps the two newest versions; deleting older ones is best effort (a project still referencing one
    /// re-attaches the newest version on its next start).</para>
    /// Project data never goes here; the project index stays in &lt;solution&gt;\.vs\UEToVS\Index\project.
    /// </summary>
    public sealed class SharedEngineCache
    {
        private static readonly Logger L = Log.For("Index");
        private const string StateFile = "state.bin";
        private const string LockFile = ".lock";

        private static readonly MessagePackSerializerOptions Options =
            MessagePackSerializerOptions.Standard.WithResolver(ContractlessStandardResolver.Instance).WithCompression(MessagePackCompression.Lz4BlockArray);

        public SharedEngineCache(string rootDirectory = null)
        {
            Root = rootDirectory ?? DefaultRoot;
        }

        public string Root { get; }

        /// <summary>%LocalAppData%\UEToVS\EngineIndex (on Linux/macOS: ~/.local/share/UEToVS/EngineIndex).</summary>
        public static string DefaultRoot =>
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData, Environment.SpecialFolderOption.DoNotVerify), "UEToVS", "EngineIndex");

        public sealed class State
        {
            public int Format { get; set; } = 1;
            public string Key { get; set; }
            public string EngineRoot { get; set; }
            public DateTime SavedUtc { get; set; }
            public List<string> Segments { get; set; } = new List<string>();
            public List<IndexStateStore.FileState> Files { get; set; } = new List<IndexStateStore.FileState>();
        }

        public static string EngineId(string engineBuildId, string key)
        {
            var sb = new StringBuilder();
            foreach (char c in engineBuildId ?? "unknown") sb.Append(char.IsLetterOrDigit(c) || c == '.' || c == '-' || c == '_' ? c : '_');
            if (sb.Length > 60) sb.Length = 60;
            ulong h = XxHash64.HashToUInt64(Encoding.UTF8.GetBytes(key ?? ""));
            return sb + "-" + (h & 0xffffffff).ToString("x8");
        }

        public string EngineDirectory(string engineBuildId, string key) => Path.Combine(Root, EngineId(engineBuildId, key));

        /// <summary>Newest complete version for this engine + key, or null.</summary>
        public string FindLatestVersion(string engineBuildId, string key)
        {
            string dir = EngineDirectory(engineBuildId, key);
            if (!Directory.Exists(dir)) return null;
            return Directory.EnumerateDirectories(dir, "v*")
                .Where(d => File.Exists(Path.Combine(d, StateFile)))
                .OrderByDescending(d => Path.GetFileName(d), StringComparer.Ordinal)
                .FirstOrDefault();
        }

        /// <summary>
        /// Adopts the shared engine segments into <paramref name="store"/> (which must not have engine segments yet) and
        /// returns the per-file states they were built from. False when there is no usable shared version.
        /// </summary>
        public bool TryImport(SymbolIndex store, string engineBuildId, string engineRoot, string key)
        {
            var sw = Stopwatch.StartNew();
            string version = null;
            try
            {
                version = FindLatestVersion(engineBuildId, key);
                if (version == null)
                {
                    L.Info("index.shared.miss", "No shared engine index for this engine yet", ("dir", EngineDirectory(engineBuildId, key)));
                    return false;
                }
                State state;
                using (var fs = File.OpenRead(Path.Combine(version, StateFile))) state = MessagePackSerializer.Deserialize<State>(fs, Options);
                if (state == null || state.Key != key || !SamePath(state.EngineRoot, engineRoot) || state.Segments.Count == 0)
                {
                    L.Info("index.shared.stale", "Shared engine index does not match this engine", ("dir", version), ("key", state?.Key), ("engineRoot", state?.EngineRoot));
                    return false;
                }
                var paths = state.Segments.Select(seg => Path.Combine(version, seg)).ToList();
                if (!store.AttachExternalSegments(SegmentKind.Engine, paths, out string detail))
                {
                    L.Warn("index.shared.attach_failed", "Shared engine segments unusable; indexing the engine locally", ("dir", version), ("detail", detail));
                    return false;
                }
                L.Info("index.shared.hit", "Engine index loaded from the shared cache", ("dir", version), ("segments", state.Segments.Count),
                    ("files", state.Files.Count), ("savedUtc", state.SavedUtc), ("ms", Math.Round(sw.Elapsed.TotalMilliseconds, 1)));
                return true;
            }
            catch (Exception ex)
            {
                L.Warn("index.shared.import_failed", "Reading the shared engine index failed; indexing the engine locally", ex, ("dir", version));
                return false;
            }
        }

        /// <summary>
        /// Publishes the store's engine segments as a new shared version, then switches the store to use the shared files
        /// (its own engine copies are deleted once unused). Never throws.
        /// </summary>
        public bool Export(SymbolIndex store, string engineBuildId, string engineRoot, string key)
        {
            var sw = Stopwatch.StartNew();
            string dir = EngineDirectory(engineBuildId, key);
            string target = null;
            try
            {
                Directory.CreateDirectory(dir);
                using var lockStream = TryLock(dir);
                if (lockStream == null)
                {
                    L.Info("index.shared.export_skipped", "Another instance is publishing the shared engine index", ("dir", dir));
                    return false;
                }
                target = Path.Combine(dir, "v" + DateTime.UtcNow.Ticks.ToString("D19"));
                Directory.CreateDirectory(target);
                var state = new State { Key = key, EngineRoot = engineRoot, SavedUtc = DateTime.UtcNow };
                long bytes = 0;
                store.WithBaseSegments(SegmentKind.Engine, paths =>
                {
                    int i = 0;
                    foreach (var p in paths)
                    {
                        string name = $"engine-{i++:D4}.seg";
                        string dest = Path.Combine(target, name);
                        if (!FileLinks.TryHardLink(p, dest)) File.Copy(p, dest, overwrite: true);
                        bytes += new FileInfo(dest).Length;
                        state.Segments.Add(name);
                    }
                });
                if (state.Segments.Count == 0)
                {
                    Directory.Delete(target, true);
                    return false;
                }
                foreach (var f in store.BaseFileStates(SegmentKind.Engine, includeDead: true))
                    state.Files.Add(new IndexStateStore.FileState { Path = f.Path, Size = f.Length, Ticks = f.MtimeTicks, Hash = f.ContentHash });
                string statePath = Path.Combine(target, StateFile);
                using (var fs = File.Create(statePath + ".tmp")) MessagePackSerializer.Serialize(fs, state, Options);
                File.Move(statePath + ".tmp", statePath, true);
                if (!store.AttachExternalSegments(SegmentKind.Engine, state.Segments.Select(n => Path.Combine(target, n)).ToList(), out string attachDetail))
                    L.Warn("index.shared.attach_failed", "Published engine segments could not be attached; keeping the local copy", ("dir", target), ("detail", attachDetail));
                int removed = 0;
                foreach (var old in Directory.EnumerateDirectories(dir, "v*").OrderByDescending(d => Path.GetFileName(d), StringComparer.Ordinal).Skip(2).ToList())
                {
                    try { Directory.Delete(old, true); removed++; }
                    catch (Exception ex) { L.Debug("index.shared.cleanup_deferred", ex.Message, ("dir", old)); }
                }
                L.Info("index.shared.exported", "Engine index published to the shared cache", ("dir", target), ("segments", state.Segments.Count),
                    ("files", state.Files.Count), ("mb", Math.Round(bytes / 1048576.0, 1)), ("removedVersions", removed), ("ms", Math.Round(sw.Elapsed.TotalMilliseconds, 1)));
                return true;
            }
            catch (Exception ex)
            {
                L.Warn("index.shared.export_failed", "Publishing the shared engine index failed", ex, ("dir", target ?? dir));
                try { if (target != null && Directory.Exists(target)) Directory.Delete(target, true); } catch (Exception) { }
                return false;
            }
        }

        private static FileStream TryLock(string dir)
        {
            try
            {
                return new FileStream(Path.Combine(dir, LockFile), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None, 1, FileOptions.DeleteOnClose);
            }
            catch (IOException)
            {
                return null;
            }
        }

        private static bool SamePath(string a, string b)
        {
            if (string.IsNullOrEmpty(a) || string.IsNullOrEmpty(b)) return false;
            try
            {
                return string.Equals(Path.GetFullPath(a).TrimEnd('/', '\\'), Path.GetFullPath(b).TrimEnd('/', '\\'), StringComparison.OrdinalIgnoreCase);
            }
            catch (Exception) { return false; }
        }
    }
}
