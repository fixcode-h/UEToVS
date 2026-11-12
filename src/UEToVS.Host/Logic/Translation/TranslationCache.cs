using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using UEToVS.Core.Diagnostics;

namespace UEToVS.Host.Logic.Translation
{
    /// <summary>
    /// Translations already fetched: an LRU in memory (at most <see cref="Capacity"/> entries) backed by an append-only JSON
    /// lines file (%LocalAppData%\UEToVS\Translation\cache.jsonl, shared by all solutions: engine comments are the same
    /// everywhere). The file is read once, lazily, on the first lookup; when it holds more than twice the capacity it is
    /// rewritten with the newest entries. Thread safe.
    /// </summary>
    public sealed class TranslationCache
    {
        private static readonly Logger L = Log.For("Translation");
        private readonly object _gate = new object();
        private readonly Dictionary<string, LinkedListNode<KeyValuePair<string, string>>> _map = new Dictionary<string, LinkedListNode<KeyValuePair<string, string>>>(StringComparer.Ordinal);
        private readonly LinkedList<KeyValuePair<string, string>> _lru = new LinkedList<KeyValuePair<string, string>>();
        private readonly string _file;
        private bool _loaded;

        /// <param name="file">Cache file; null = memory only (tests).</param>
        public TranslationCache(string file, int capacity = 5000)
        {
            _file = file;
            Capacity = Math.Max(16, capacity);
            _loaded = file == null;
        }

        public int Capacity { get; }

        public int Count
        {
            get { lock (_gate) return _map.Count; }
        }

        public static string DefaultFile() =>
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "UEToVS", "Translation", "cache.jsonl");

        public static string Key(TranslationProviderKind provider, string target, string text) => provider + "|" + target + "|" + text;

        /// <param name="allowLoad">False on the UI thread: a cache not read yet is loaded in the background and this lookup misses.</param>
        public bool TryGet(string key, out string value, bool allowLoad = true)
        {
            if (!_loaded)
            {
                if (allowLoad) EnsureLoaded();
                else
                {
                    value = null;
                    System.Threading.Tasks.Task.Run(() => EnsureLoaded());
                    return false;
                }
            }
            lock (_gate)
            {
                if (_map.TryGetValue(key, out var node))
                {
                    _lru.Remove(node);
                    _lru.AddFirst(node);
                    value = node.Value.Value;
                    return true;
                }
            }
            value = null;
            return false;
        }

        public void Put(string key, string value)
        {
            if (key == null || value == null) return;
            EnsureLoaded();
            lock (_gate)
            {
                if (!AddMemory(key, value)) return;
                if (_file == null) return;
                try
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(_file));
                    File.AppendAllText(_file, Line(key, value) + "\n", Utf8);
                }
                catch (Exception ex)
                {
                    L.Warn("translate.cache.writeFailed", "Writing the translation cache failed", ex, ("file", _file));
                }
            }
        }

        public void Clear()
        {
            lock (_gate)
            {
                _map.Clear();
                _lru.Clear();
                _loaded = true;
                if (_file == null) return;
                try
                {
                    if (File.Exists(_file)) File.Delete(_file);
                    L.Info("translate.cache.cleared", null, ("file", _file));
                }
                catch (Exception ex)
                {
                    L.Warn("translate.cache.clearFailed", "Deleting the translation cache failed", ex, ("file", _file));
                }
            }
        }

        /// <summary>Adds / refreshes in memory; false when the same value was already there (nothing to persist).</summary>
        private bool AddMemory(string key, string value)
        {
            if (_map.TryGetValue(key, out var existing))
            {
                _lru.Remove(existing);
                bool same = existing.Value.Value == value;
                existing.Value = new KeyValuePair<string, string>(key, value);
                _lru.AddFirst(existing);
                return !same;
            }
            var node = _lru.AddFirst(new KeyValuePair<string, string>(key, value));
            _map[key] = node;
            while (_map.Count > Capacity)
            {
                var last = _lru.Last;
                _lru.RemoveLast();
                _map.Remove(last.Value.Key);
            }
            return true;
        }

        /// <summary>Reads the cache file now (call from a background thread).</summary>
        public void EnsureLoaded()
        {
            if (_loaded) return;
            lock (_gate)
            {
                if (_loaded) return;
                _loaded = true;
                int lines = 0;
                try
                {
                    if (!File.Exists(_file)) return;
                    foreach (var line in File.ReadLines(_file, Utf8))
                    {
                        lines++;
                        if (TryParse(line, out var k, out var v)) AddMemory(k, v);
                    }
                    if (lines > Capacity * 2) Compact();
                    L.Info("translate.cache.loaded", null, ("entries", _map.Count), ("lines", lines));
                }
                catch (Exception ex)
                {
                    L.Warn("translate.cache.loadFailed", "Reading the translation cache failed", ex, ("file", _file), ("lines", lines));
                }
            }
        }

        private void Compact()
        {
            var sb = new StringBuilder();
            for (var n = _lru.Last; n != null; n = n.Previous) sb.Append(Line(n.Value.Key, n.Value.Value)).Append('\n');
            string tmp = _file + ".tmp";
            File.WriteAllText(tmp, sb.ToString(), Utf8);
            File.Copy(tmp, _file, true);
            File.Delete(tmp);
            L.Info("translate.cache.compacted", null, ("entries", _map.Count));
        }

        private static readonly Encoding Utf8 = new UTF8Encoding(false);

        public static string Line(string key, string value) =>
            new JObject { ["k"] = key, ["v"] = value }.ToString(Formatting.None);

        public static bool TryParse(string line, out string key, out string value)
        {
            key = value = null;
            if (string.IsNullOrWhiteSpace(line)) return false;
            try
            {
                var o = JObject.Parse(line);
                key = (string)o["k"];
                value = (string)o["v"];
                return key != null && value != null;
            }
            catch (JsonException)
            {
                return false;
            }
        }
    }
}
