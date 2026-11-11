using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace UEToVS.Core.Navigation
{
    /// <summary>
    /// Rider-style back/forward history. A position is recorded only when the caret "jumps":
    /// go-to-definition, opening a search result, switching files, or moving more than
    /// <see cref="LineThreshold"/> lines at once. Small moves are not recorded.
    /// </summary>
    public sealed class NavigationHistory
    {
        private readonly LinkedList<CodeLocation> _back = new LinkedList<CodeLocation>();
        private readonly Stack<CodeLocation> _forward = new Stack<CodeLocation>();

        public NavigationHistory(int capacity = 100, int lineThreshold = 15)
        {
            Capacity = capacity;
            LineThreshold = lineThreshold;
        }

        public int Capacity { get; set; }

        public int LineThreshold { get; set; }

        public bool CanGoBack => _back.Count > 0;

        public bool CanGoForward => _forward.Count > 0;

        public IEnumerable<CodeLocation> BackEntries => _back.Reverse();

        public IEnumerable<CodeLocation> ForwardEntries => _forward;

        public event EventHandler Changed;

        /// <summary>Record the position the caret is leaving because of an explicit jump.</summary>
        public void RecordJump(CodeLocation from, CodeLocation to = null)
        {
            if (from == null) return;
            if (to != null && from.IsNear(to, LineThreshold))
                return; // a jump within the threshold is not worth a slot

            var top = _back.Last?.Value;
            if (top != null && top.IsNear(from, LineThreshold))
                _back.RemoveLast();

            _back.AddLast(from);
            while (_back.Count > Capacity) _back.RemoveFirst();
            _forward.Clear();
            Changed?.Invoke(this, EventArgs.Empty);
        }

        /// <summary>Called for every caret move; records only "big" moves.</summary>
        public void OnCaretMoved(CodeLocation from, CodeLocation to)
        {
            if (from == null || to == null) return;
            if (!from.SameFile(to) || Math.Abs(from.Line - to.Line) > LineThreshold)
                RecordJump(from, to);
        }

        public CodeLocation GoBack(CodeLocation current)
        {
            while (_back.Count > 0)
            {
                var target = _back.Last.Value;
                _back.RemoveLast();
                if (current != null && target.SameFile(current) && target.Line == current.Line)
                    continue; // already here
                if (current != null) PushForward(current);
                Changed?.Invoke(this, EventArgs.Empty);
                return target;
            }
            return null;
        }

        public CodeLocation GoForward(CodeLocation current)
        {
            while (_forward.Count > 0)
            {
                var target = _forward.Pop();
                if (current != null && target.SameFile(current) && target.Line == current.Line)
                    continue;
                if (current != null)
                {
                    var top = _back.Last?.Value;
                    if (top == null || !top.Equals(current)) _back.AddLast(current);
                }
                Changed?.Invoke(this, EventArgs.Empty);
                return target;
            }
            return null;
        }

        private void PushForward(CodeLocation loc)
        {
            if (_forward.Count == 0 || !_forward.Peek().Equals(loc)) _forward.Push(loc);
        }

        /// <summary>Drop every entry for a deleted file.</summary>
        public void RemoveFile(string path)
        {
            var node = _back.First;
            while (node != null)
            {
                var next = node.Next;
                if (string.Equals(node.Value.FilePath, path, StringComparison.OrdinalIgnoreCase)) _back.Remove(node);
                node = next;
            }
            var keep = _forward.Reverse().Where(l => !string.Equals(l.FilePath, path, StringComparison.OrdinalIgnoreCase)).ToList();
            _forward.Clear();
            foreach (var l in keep) _forward.Push(l);
        }

        public void RenameFile(string oldPath, string newPath)
        {
            var back = _back.Select(l => Rename(l, oldPath, newPath)).ToList();
            _back.Clear();
            foreach (var l in back) _back.AddLast(l);
            var fwd = _forward.Reverse().Select(l => Rename(l, oldPath, newPath)).ToList();
            _forward.Clear();
            foreach (var l in fwd) _forward.Push(l);
        }

        private static CodeLocation Rename(CodeLocation l, string oldPath, string newPath) =>
            string.Equals(l.FilePath, oldPath, StringComparison.OrdinalIgnoreCase) ? new CodeLocation(newPath, l.Line, l.Column) : l;

        public void Clear()
        {
            _back.Clear();
            _forward.Clear();
            Changed?.Invoke(this, EventArgs.Empty);
        }

        public void Save(TextWriter writer)
        {
            writer.WriteLine("[back]");
            foreach (var l in _back) writer.WriteLine(l.Serialize());
            writer.WriteLine("[forward]");
            foreach (var l in _forward.Reverse()) writer.WriteLine(l.Serialize());
        }

        public void Load(TextReader reader)
        {
            _back.Clear();
            _forward.Clear();
            string section = null;
            string line;
            var fwd = new List<CodeLocation>();
            while ((line = reader.ReadLine()) != null)
            {
                if (line.StartsWith("[", StringComparison.Ordinal)) { section = line; continue; }
                var loc = CodeLocation.Deserialize(line);
                if (loc == null) continue;
                if (section == "[back]") _back.AddLast(loc);
                else if (section == "[forward]") fwd.Add(loc);
            }
            foreach (var l in fwd) _forward.Push(l);
            while (_back.Count > Capacity) _back.RemoveFirst();
        }
    }
}
