using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using UEToVS.Core.Navigation;

namespace UEToVS.Core.Bookmarks
{
    public sealed class Bookmark
    {
        public Bookmark(CodeLocation location, char? mnemonic, string description)
        {
            Location = location;
            Mnemonic = mnemonic;
            Description = description;
        }

        public CodeLocation Location { get; set; }

        /// <summary>'0'-'9' or 'A'-'Z', or null for an anonymous bookmark.</summary>
        public char? Mnemonic { get; set; }

        public string Description { get; set; }
    }

    /// <summary>Rider-style bookmarks with mnemonics (Ctrl+Shift+digit sets, Ctrl+digit jumps).</summary>
    public sealed class BookmarkStore
    {
        private readonly List<Bookmark> _items = new List<Bookmark>();

        public IReadOnlyList<Bookmark> Items => _items;

        public event EventHandler Changed;

        /// <summary>Adds an anonymous bookmark on the line, or removes the bookmark already there.</summary>
        public bool Toggle(CodeLocation loc)
        {
            var existing = AtLine(loc);
            if (existing != null)
            {
                _items.Remove(existing);
                Changed?.Invoke(this, EventArgs.Empty);
                return false;
            }
            _items.Add(new Bookmark(new CodeLocation(loc.FilePath, loc.Line, 0), null, null));
            Changed?.Invoke(this, EventArgs.Empty);
            return true;
        }

        /// <summary>
        /// Sets mnemonic bookmark <paramref name="mnemonic"/> on the line. Setting it on the line that already
        /// has it removes it (Rider behaviour); a mnemonic lives on one line only.
        /// </summary>
        public bool SetMnemonic(CodeLocation loc, char mnemonic)
        {
            mnemonic = char.ToUpperInvariant(mnemonic);
            var same = _items.FirstOrDefault(b => b.Mnemonic == mnemonic);
            var onLine = AtLine(loc);
            if (same != null && onLine == same)
            {
                _items.Remove(same);
                Changed?.Invoke(this, EventArgs.Empty);
                return false;
            }
            if (same != null) _items.Remove(same);
            if (onLine != null) onLine.Mnemonic = mnemonic;
            else _items.Add(new Bookmark(new CodeLocation(loc.FilePath, loc.Line, 0), mnemonic, null));
            Changed?.Invoke(this, EventArgs.Empty);
            return true;
        }

        public Bookmark Get(char mnemonic)
        {
            mnemonic = char.ToUpperInvariant(mnemonic);
            return _items.FirstOrDefault(b => b.Mnemonic == mnemonic);
        }

        public Bookmark AtLine(CodeLocation loc) =>
            _items.FirstOrDefault(b => b.Location.SameFile(loc) && b.Location.Line == loc.Line);

        public IEnumerable<Bookmark> InFile(string path) =>
            _items.Where(b => string.Equals(b.Location.FilePath, path, StringComparison.OrdinalIgnoreCase));

        /// <summary>Keeps bookmarks attached to their text after edits (host reports the new line).</summary>
        public void Move(Bookmark b, int newLine)
        {
            b.Location = new CodeLocation(b.Location.FilePath, newLine, 0);
        }

        public void Remove(Bookmark b)
        {
            if (_items.Remove(b)) Changed?.Invoke(this, EventArgs.Empty);
        }

        public void Save(TextWriter w)
        {
            foreach (var b in _items)
                w.WriteLine(string.Join("\t", b.Location.FilePath, b.Location.Line, b.Mnemonic?.ToString() ?? "", (b.Description ?? "").Replace('\t', ' ')));
        }

        public void Load(TextReader r)
        {
            _items.Clear();
            string line;
            while ((line = r.ReadLine()) != null)
            {
                var p = line.Split('\t');
                if (p.Length < 4 || !int.TryParse(p[1], out int l)) continue;
                _items.Add(new Bookmark(new CodeLocation(p[0], l, 0), p[2].Length == 1 ? p[2][0] : (char?)null, p[3].Length == 0 ? null : p[3]));
            }
        }
    }
}
