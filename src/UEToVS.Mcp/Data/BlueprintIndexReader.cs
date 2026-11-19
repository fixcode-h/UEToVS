using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using UEToVS.Core.Diagnostics;
using UEToVS.Core.Storage;
using UEToVS.Engine.Indexing;

namespace UEToVS.Mcp.Data
{
    /// <summary>
    /// The engine's offline Blueprint index (.vs\UEToVS\Blueprints\blueprints.jsonl.gz), always read from disk so it
    /// answers whether or not Visual Studio is running. Queries stream the file (<see cref="BlueprintIndexScan"/>)
    /// instead of loading it: loading a big project's index overran the server's 512 MiB heap cap (OutOfMemoryException).
    /// The last few answers are cached per file version, so paging through one query reads the file once.
    /// </summary>
    public sealed class BlueprintIndexReader
    {
        private static readonly Logger L = Log.For("Mcp.Blueprints");
        private const int CacheSize = 8;

        private readonly object _gate = new object();
        private readonly LinkedList<(string Key, BlueprintScanResult Result)> _cache = new LinkedList<(string, BlueprintScanResult)>();
        private string _cacheStamp;

        public BlueprintIndexReader(string solutionDir)
        {
            IndexFile = BlueprintIndex.DefaultPath(StoragePaths.ForSolution(solutionDir).Root);
        }

        public string IndexFile { get; }

        public bool Exists => File.Exists(IndexFile);

        /// <summary>
        /// Derived Blueprints and users of <paramref name="className"/> (UE name), or users of <paramref name="member"/>;
        /// null when there is no index file. <see cref="BlueprintScanResult.Ok"/> is false when the file is unreadable.
        /// </summary>
        public BlueprintScanResult Query(string className, string member, int maxUsers, CancellationToken ct)
        {
            var info = new FileInfo(IndexFile);
            if (!info.Exists) return null;
            string stamp = info.LastWriteTimeUtc.Ticks + ":" + info.Length;
            string key = className + "\n" + member + "\n" + maxUsers;
            lock (_gate)
            {
                if (_cacheStamp != stamp)
                {
                    _cache.Clear();
                    _cacheStamp = stamp;
                }
                for (var n = _cache.First; n != null; n = n.Next)
                    if (string.Equals(n.Value.Key, key, StringComparison.OrdinalIgnoreCase))
                    {
                        _cache.Remove(n);
                        _cache.AddFirst(n);
                        return n.Value.Result;
                    }
            }
            var sw = System.Diagnostics.Stopwatch.StartNew();
            var result = BlueprintIndexScan.Scan(IndexFile, className, member, maxUsers, ct);
            if (!result.Ok)
            {
                // 引擎可能正在替换这个文件：不缓存，下次再读
                L.Warn("mcp.blueprints.scan_failed", "unreadable or from another version: " + IndexFile);
                return result;
            }
            L.Info("mcp.blueprints.scanned", null, ("class", className), ("member", member), ("users", result.Users.Count), ("ms", sw.ElapsedMilliseconds));
            lock (_gate)
            {
                if (_cacheStamp == stamp)
                {
                    _cache.AddFirst((key, result));
                    while (_cache.Count > CacheSize) _cache.RemoveLast();
                }
            }
            return result;
        }

        /// <summary>Drops cached answers (tests rewrite the file within the file-time resolution).</summary>
        public void Invalidate()
        {
            lock (_gate)
            {
                _cache.Clear();
                _cacheStamp = null;
            }
        }
    }
}
