using System;
using System.Collections.Generic;
using System.IO;

namespace UEToVS.Core.Navigation
{
    /// <summary>
    /// Places where text was changed, oldest first. "Last Edit Location" walks towards older
    /// entries; "Next Edit Location" walks back towards newer ones. Nearby edits merge.
    /// </summary>
    public sealed class EditLocations
    {
        private readonly List<CodeLocation> _items = new List<CodeLocation>();
        private int _cursor = -1; // index of the entry last navigated to; -1 = not navigating

        public EditLocations(int capacity = 50, int mergeLines = 3)
        {
            Capacity = capacity;
            MergeLines = mergeLines;
        }

        public int Capacity { get; set; }
        public int MergeLines { get; set; }
        public IReadOnlyList<CodeLocation> Items => _items;

        public void Clear()
        {
            _items.Clear();
            _cursor = -1;
        }

        public void RecordEdit(CodeLocation loc)
        {
            if (loc == null) return;
            _cursor = -1;
            if (_items.Count > 0 && _items[_items.Count - 1].IsNear(loc, MergeLines))
            {
                _items[_items.Count - 1] = loc;
                return;
            }
            _items.RemoveAll(i => i.IsNear(loc, MergeLines));
            _items.Add(loc);
            if (_items.Count > Capacity) _items.RemoveAt(0);
        }

        /// <summary>Move to an older edit location.</summary>
        public CodeLocation Previous(CodeLocation current)
        {
            if (_items.Count == 0) return null;
            int i = _cursor < 0 ? _items.Count - 1 : _cursor - 1;
            // Skip the entry we are standing on.
            while (i >= 0 && current != null && _items[i].IsNear(current, 0)) i--;
            if (i < 0) return null;
            _cursor = i;
            return _items[i];
        }

        /// <summary>Move to a newer edit location.</summary>
        public CodeLocation Next(CodeLocation current)
        {
            if (_cursor < 0 || _cursor >= _items.Count - 1) return null;
            _cursor++;
            return _items[_cursor];
        }

        public void RemoveFile(string path) =>
            _items.RemoveAll(i => string.Equals(i.FilePath, path, StringComparison.OrdinalIgnoreCase));

        public void Save(TextWriter w)
        {
            foreach (var l in _items) w.WriteLine(l.Serialize());
        }

        public void Load(TextReader r)
        {
            _items.Clear();
            string line;
            while ((line = r.ReadLine()) != null)
            {
                var l = CodeLocation.Deserialize(line);
                if (l != null) _items.Add(l);
            }
            _cursor = -1;
        }
    }
}
