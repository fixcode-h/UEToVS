using System;
using System.IO;
using UEToVS.Core.Diagnostics;
using UEToVS.Core.Storage;
using UEToVS.Engine.Indexing;

namespace UEToVS.Mcp.Data
{
    /// <summary>
    /// The engine's offline Blueprint index (.vs\UEToVS\Blueprints\blueprints.jsonl.gz), reloaded when the file changes.
    /// Always read from disk, so it answers whether or not Visual Studio is running.
    /// </summary>
    public sealed class BlueprintIndexReader
    {
        private static readonly Logger L = Log.For("Mcp.Blueprints");
        private static readonly TimeSpan CheckInterval = TimeSpan.FromSeconds(2);

        private readonly object _gate = new object();
        private BlueprintIndex _index;
        private DateTime _fileTimeUtc = DateTime.MinValue;
        private long _fileLength = -1;
        private DateTime _lastCheckUtc = DateTime.MinValue;

        public BlueprintIndexReader(string solutionDir)
        {
            IndexFile = BlueprintIndex.DefaultPath(StoragePaths.ForSolution(solutionDir).Root);
        }

        public string IndexFile { get; }

        public string LoadError { get; private set; }

        /// <summary>The index, or null when the engine has not written one yet.</summary>
        public BlueprintIndex Index
        {
            get
            {
                lock (_gate)
                {
                    var now = DateTime.UtcNow;
                    if (now - _lastCheckUtc < CheckInterval) return _index;
                    _lastCheckUtc = now;
                    var info = new FileInfo(IndexFile);
                    if (!info.Exists)
                    {
                        _index = null;
                        return null;
                    }
                    if (_index != null && info.LastWriteTimeUtc == _fileTimeUtc && info.Length == _fileLength) return _index;
                    var next = new BlueprintIndex();
                    var sw = System.Diagnostics.Stopwatch.StartNew();
                    if (next.Load(IndexFile))
                    {
                        _index = next;
                        _fileTimeUtc = info.LastWriteTimeUtc;
                        _fileLength = info.Length;
                        LoadError = null;
                        L.Info("mcp.blueprints.loaded", null, ("assets", next.Count), ("blueprints", next.SearchableCount), ("ms", sw.ElapsedMilliseconds));
                    }
                    else
                    {
                        // keep the previous data: the engine may be replacing the file right now
                        LoadError = "unreadable or from another version: " + IndexFile;
                        L.Warn("mcp.blueprints.load_failed", LoadError);
                    }
                    return _index;
                }
            }
        }

        /// <summary>Forces the next <see cref="Index"/> read to check the file (tests).</summary>
        public void Invalidate()
        {
            lock (_gate) _lastCheckUtc = DateTime.MinValue;
        }
    }
}
