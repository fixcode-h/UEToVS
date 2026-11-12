using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace UEToVS.Host.Logic.Unreal
{
    /// <summary>
    /// Shared cache for the engine lookups of <see cref="IncludeDependencyChecker"/>. Without it every check (each file
    /// open / include edit) re-searched the Build.cs of every visible module — 175–214 fuzzy searches per check, ~4200
    /// in a 28-minute session, up to 1.5 s each right after indexing. Module → Build.cs paths are kept until the file
    /// disappears (unknown modules are retried after <see cref="NegativeTtl"/>); include file-name lookups expire after
    /// <see cref="NameTtl"/> so new headers show up. <see cref="Reset"/> drops everything (engine restart, index rebuild).
    /// </summary>
    public sealed class IncludeLookupCache
    {
        public static readonly TimeSpan NameTtl = TimeSpan.FromSeconds(60);
        public static readonly TimeSpan NegativeTtl = TimeSpan.FromSeconds(60);
        public const int MaxNameEntries = 4096;

        private readonly Func<DateTime> _now;
        private readonly ConcurrentDictionary<string, (string Path, DateTime At)> _modules = new ConcurrentDictionary<string, (string, DateTime)>(StringComparer.OrdinalIgnoreCase);
        private readonly ConcurrentDictionary<string, (IReadOnlyList<string> Files, DateTime At)> _names = new ConcurrentDictionary<string, (IReadOnlyList<string>, DateTime)>(StringComparer.OrdinalIgnoreCase);
        private object _owner;
        private long _hits, _misses;

        public IncludeLookupCache(Func<DateTime> now = null)
        {
            _now = now ?? (() => DateTime.UtcNow);
        }

        public long Hits => Interlocked.Read(ref _hits);
        public long Misses => Interlocked.Read(ref _misses);
        public int ModuleCount => _modules.Count;

        /// <summary>Clears the cache when the lookups now come from a different source (e.g. a new engine client).</summary>
        public void BindTo(object owner)
        {
            if (ReferenceEquals(Volatile.Read(ref _owner), owner)) return;
            Reset();
            Volatile.Write(ref _owner, owner);
        }

        public void Reset()
        {
            _modules.Clear();
            _names.Clear();
        }

        public async Task<string> GetModuleBuildCsAsync(string module, Func<string, CancellationToken, Task<string>> lookup, Func<string, bool> exists, CancellationToken ct)
        {
            if (string.IsNullOrEmpty(module)) return null;
            if (_modules.TryGetValue(module, out var e))
            {
                bool fresh = e.Path == null ? _now() - e.At < NegativeTtl : exists == null || exists(e.Path);
                if (fresh)
                {
                    Interlocked.Increment(ref _hits);
                    return e.Path;
                }
            }
            Interlocked.Increment(ref _misses);
            string path = await lookup(module, ct).ConfigureAwait(false);
            _modules[module] = (path, _now());
            return path;
        }

        public async Task<IReadOnlyList<string>> GetFilesByNameAsync(string fileName, Func<string, CancellationToken, Task<IReadOnlyList<string>>> lookup, CancellationToken ct)
        {
            if (string.IsNullOrEmpty(fileName)) return Array.Empty<string>();
            if (_names.TryGetValue(fileName, out var e) && _now() - e.At < NameTtl)
            {
                Interlocked.Increment(ref _hits);
                return e.Files;
            }
            Interlocked.Increment(ref _misses);
            var files = await lookup(fileName, ct).ConfigureAwait(false) ?? Array.Empty<string>();
            if (_names.Count >= MaxNameEntries) _names.Clear();
            _names[fileName] = (files, _now());
            return files;
        }
    }

    /// <summary><see cref="IIncludeEnvironment"/> decorator that answers index lookups from an <see cref="IncludeLookupCache"/>.</summary>
    public sealed class CachingIncludeEnvironment : IIncludeEnvironment
    {
        private readonly IIncludeEnvironment _inner;
        private readonly IncludeLookupCache _cache;

        public CachingIncludeEnvironment(IIncludeEnvironment inner, IncludeLookupCache cache)
        {
            _inner = inner ?? throw new ArgumentNullException(nameof(inner));
            _cache = cache ?? throw new ArgumentNullException(nameof(cache));
        }

        public Task<IReadOnlyList<string>> FindFilesByNameAsync(string fileName, CancellationToken ct) =>
            _cache.GetFilesByNameAsync(fileName, _inner.FindFilesByNameAsync, ct);

        public string FindBuildCsForFile(string filePath) => _inner.FindBuildCsForFile(filePath);

        public Task<string> FindBuildCsForModuleAsync(string module, CancellationToken ct) =>
            _cache.GetModuleBuildCsAsync(module, _inner.FindBuildCsForModuleAsync, _inner.FileExists, ct);

        public string ReadText(string path) => _inner.ReadText(path);

        public bool FileExists(string path) => _inner.FileExists(path);
    }
}
