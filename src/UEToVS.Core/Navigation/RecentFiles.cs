using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace UEToVS.Core.Navigation
{
    public sealed class RecentFileEntry
    {
        public RecentFileEntry(string path, DateTime lastOpened, bool edited)
        {
            Path = path;
            LastOpened = lastOpened;
            Edited = edited;
        }

        public string Path { get; }
        public DateTime LastOpened { get; set; }
        public bool Edited { get; set; }
    }

    /// <summary>Most-recently-used files, newest first (Rider "Recent Files", Ctrl+E).</summary>
    public sealed class RecentFiles
    {
        private readonly List<RecentFileEntry> _items = new List<RecentFileEntry>();

        public RecentFiles(int capacity = 50) => Capacity = capacity;

        public int Capacity { get; set; }

        public IReadOnlyList<RecentFileEntry> Items => _items;

        public IEnumerable<RecentFileEntry> EditedOnly => _items.Where(i => i.Edited);

        public void Touch(string path, DateTime now)
        {
            int idx = IndexOf(path);
            bool edited = false;
            if (idx >= 0)
            {
                edited = _items[idx].Edited;
                _items.RemoveAt(idx);
            }
            _items.Insert(0, new RecentFileEntry(path, now, edited));
            if (_items.Count > Capacity) _items.RemoveAt(_items.Count - 1);
        }

        public void MarkEdited(string path, DateTime now)
        {
            int idx = IndexOf(path);
            if (idx < 0)
            {
                Touch(path, now);
                idx = 0;
            }
            _items[idx].Edited = true;
        }

        /// <summary>Rank of the file in the MRU list (0 = most recent), or -1.</summary>
        public int IndexOf(string path)
        {
            for (int i = 0; i < _items.Count; i++)
                if (string.Equals(_items[i].Path, path, StringComparison.OrdinalIgnoreCase)) return i;
            return -1;
        }

        public void Remove(string path)
        {
            int idx = IndexOf(path);
            if (idx >= 0) _items.RemoveAt(idx);
        }

        public void Save(TextWriter w)
        {
            foreach (var i in _items)
                w.WriteLine(string.Join("\t", i.Path, i.LastOpened.ToUniversalTime().Ticks, i.Edited ? "1" : "0"));
        }

        public void Load(TextReader r)
        {
            _items.Clear();
            string line;
            while ((line = r.ReadLine()) != null)
            {
                var p = line.Split('\t');
                if (p.Length < 3 || !long.TryParse(p[1], out long ticks)) continue;
                _items.Add(new RecentFileEntry(p[0], new DateTime(ticks, DateTimeKind.Utc).ToLocalTime(), p[2] == "1"));
            }
        }
    }

    public sealed class RecentLocationEntry
    {
        public RecentLocationEntry(CodeLocation location, DateTime time, bool isEdit, string context)
        {
            Location = location;
            Time = time;
            IsEdit = isEdit;
            Context = context;
        }

        public CodeLocation Location { get; }
        public DateTime Time { get; }
        public bool IsEdit { get; }

        /// <summary>Name of the enclosing symbol, shown as the title of the entry.</summary>
        public string Context { get; }
    }

    /// <summary>Recently visited and edited places (Rider "Recent Locations", Ctrl+Shift+E).</summary>
    public sealed class RecentLocations
    {
        private readonly List<RecentLocationEntry> _items = new List<RecentLocationEntry>();

        public RecentLocations(int capacity = 50, int mergeLines = 5)
        {
            Capacity = capacity;
            MergeLines = mergeLines;
        }

        public int Capacity { get; set; }
        public int MergeLines { get; set; }
        public IReadOnlyList<RecentLocationEntry> Items => _items;

        public void Add(CodeLocation loc, DateTime now, bool isEdit, string context = null)
        {
            if (loc == null) return;
            bool wasEdit = _items.Any(i => i.IsEdit && i.Location.IsNear(loc, MergeLines));
            _items.RemoveAll(i => i.Location.IsNear(loc, MergeLines));
            _items.Insert(0, new RecentLocationEntry(loc, now, isEdit || wasEdit, context));
            if (_items.Count > Capacity) _items.RemoveAt(_items.Count - 1);
        }

        public void RemoveFile(string path) =>
            _items.RemoveAll(i => string.Equals(i.Location.FilePath, path, StringComparison.OrdinalIgnoreCase));
    }
}
