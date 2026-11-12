using System;
using System.IO;
using UEToVS.Core.Diagnostics;
using UEToVS.Core.Navigation;

namespace UEToVS.Host.Logic.Navigation
{
    /// <summary>Why a jump happened. Logged with every recorded jump so the history can be analysed from the logs.</summary>
    public static class JumpReasons
    {
        public const string Search = "search";
        public const string Bookmark = "bookmark";
        public const string Related = "related";
        public const string RecentFile = "recentFile";
        public const string RecentLocation = "recentLocation";
        public const string FileStructure = "fileStructure";
        public const string BuildCs = "buildCs";
        public const string Definition = "definition";
        public const string References = "references";
        public const string Other = "other";
    }

    /// <summary>
    /// Navigation state of the open solution (pure, no VS types): Back/Forward history, edit locations, recent files and
    /// recent locations. The host feeds caret moves, edits and explicit jumps; it asks for Back/Forward targets.
    /// Persisted as text files in &lt;solution&gt;/.vs/UEToVS/Navigation. Not thread-safe: use from the UI thread.
    /// </summary>
    public sealed class NavigationState
    {
        private static readonly Logger L = Log.For("Navigation");
        private CodeLocation _last;
        private int _suppress;

        public NavigationHistory History { get; } = new NavigationHistory();
        public EditLocations Edits { get; } = new EditLocations();
        public RecentFiles Recent { get; } = new RecentFiles();
        public RecentLocations Locations { get; } = new RecentLocations();

        /// <summary>Last caret position seen (any document).</summary>
        public CodeLocation LastLocation => _last;

        /// <summary>True while a jump of our own is in progress (its caret moves must not be recorded again).</summary>
        public bool IsSuppressed => _suppress > 0;

        public void Configure(int capacity, int lineThreshold)
        {
            History.Capacity = Math.Max(10, capacity);
            History.LineThreshold = Math.Max(1, lineThreshold);
        }

        /// <summary>The caret is now at <paramref name="loc"/> (click, keyboard, VS's own F12, our jumps).</summary>
        public void OnCaret(CodeLocation loc, DateTime now)
        {
            if (loc == null) return;
            var prev = _last;
            _last = loc;
            if (prev == null)
            {
                Recent.Touch(loc.FilePath, now);
                return;
            }
            if (!prev.SameFile(loc))
            {
                Recent.Touch(loc.FilePath, now);
                if (_suppress == 0) History.RecordJump(prev, loc);
                Locations.Add(prev, now, false);
            }
            else if (_suppress == 0)
            {
                bool far = !prev.IsNear(loc, History.LineThreshold);
                History.OnCaretMoved(prev, loc);
                if (far) Locations.Add(prev, now, false);
            }
        }

        public void OnEdit(CodeLocation loc, string lineText, DateTime now)
        {
            if (loc == null) return;
            Edits.RecordEdit(loc);
            Recent.MarkEdited(loc.FilePath, now);
            Locations.Add(loc, now, true, lineText?.Trim());
        }

        /// <summary>
        /// Records an explicit jump from <paramref name="from"/> (null = last known caret) to <paramref name="to"/>.
        /// Call <see cref="Suppress"/> around the actual open so the caret move it causes is not recorded twice.
        /// </summary>
        public void RecordJump(CodeLocation from, CodeLocation to, string reason, DateTime now)
        {
            from = from ?? _last;
            if (from != null) History.RecordJump(from, to);
            if (to != null) Recent.Touch(to.FilePath, now);
            L.Debug("nav.jump", null, ("reason", reason ?? JumpReasons.Other), ("from", from?.ToString()), ("to", to?.ToString()),
                    ("back", History.CanGoBack), ("fwd", History.CanGoForward));
        }

        /// <summary>Marks the caret position after a jump without touching the history.</summary>
        public void SetLast(CodeLocation loc)
        {
            if (loc != null) _last = loc;
        }

        public IDisposable Suppress()
        {
            _suppress++;
            return new Releaser(this);
        }

        /// <summary>
        /// Next target for Back / Forward / previous edit / next edit; entries whose file no longer exists are dropped.
        /// Returns null when there is nowhere to go.
        /// </summary>
        public CodeLocation Pick(NavigationStep step, CodeLocation current, Func<string, bool> fileExists)
        {
            current = current ?? _last;
            fileExists = fileExists ?? File.Exists;
            for (int guard = 0; guard < 50; guard++)
            {
                CodeLocation target;
                switch (step)
                {
                    case NavigationStep.Back: target = History.GoBack(current); break;
                    case NavigationStep.Forward: target = History.GoForward(current); break;
                    case NavigationStep.PreviousEdit: target = Edits.Previous(current); break;
                    default: target = Edits.Next(current); break;
                }
                if (target == null) return null;
                if (fileExists(target.FilePath)) return target;
                L.Debug("nav.target.missing", null, ("file", target.FilePath), ("step", step.ToString()));
                History.RemoveFile(target.FilePath);
                Edits.RemoveFile(target.FilePath);
                Locations.RemoveFile(target.FilePath);
                Recent.Remove(target.FilePath);
            }
            return null;
        }

        public void Clear()
        {
            History.Clear();
            Recent.Clear();
            Locations.Clear();
            Edits.Clear();
            _last = null;
        }

        // ------------------------------------------------------------------ persistence

        public const string HistoryFile = "history.txt";
        public const string EditsFile = "edits.txt";
        public const string RecentFile = "recent.txt";

        /// <summary>Loads the state from <paramref name="dir"/> (missing files = empty). Never throws.</summary>
        public void Load(string dir)
        {
            Clear();
            if (string.IsNullOrEmpty(dir)) return;
            Read(Path.Combine(dir, HistoryFile), History.Load);
            Read(Path.Combine(dir, EditsFile), Edits.Load);
            Read(Path.Combine(dir, RecentFile), Recent.Load);
            L.Info("nav.state.loaded", null, ("dir", dir), ("recent", Recent.Items.Count), ("edits", Edits.Items.Count), ("canGoBack", History.CanGoBack));
        }

        /// <summary>Saves the state into <paramref name="dir"/> (created). Never throws.</summary>
        public void Save(string dir)
        {
            if (string.IsNullOrEmpty(dir)) return;
            try
            {
                Directory.CreateDirectory(dir);
            }
            catch (Exception ex)
            {
                L.Error("nav.state.save.failed", "Cannot create the navigation state folder", ex, ("dir", dir));
                return;
            }
            Write(Path.Combine(dir, HistoryFile), History.Save);
            Write(Path.Combine(dir, EditsFile), Edits.Save);
            Write(Path.Combine(dir, RecentFile), Recent.Save);
        }

        private static void Read(string path, Action<TextReader> load)
        {
            try
            {
                if (!File.Exists(path)) return;
                using (var r = new StreamReader(path)) load(r);
            }
            catch (Exception ex)
            {
                L.Error("nav.state.read.failed", "Reading navigation state failed", ex, ("file", path));
            }
        }

        private static void Write(string path, Action<TextWriter> save)
        {
            try
            {
                string tmp = path + ".tmp";
                using (var w = new StreamWriter(tmp)) save(w);
                if (File.Exists(path)) File.Delete(path);
                File.Move(tmp, path);
            }
            catch (Exception ex)
            {
                L.Error("nav.state.write.failed", "Writing navigation state failed", ex, ("file", path));
            }
        }

        private sealed class Releaser : IDisposable
        {
            private NavigationState _owner;

            public Releaser(NavigationState owner) => _owner = owner;

            public void Dispose()
            {
                if (_owner == null) return;
                _owner._suppress--;
                _owner = null;
            }
        }
    }

    public enum NavigationStep
    {
        Back,
        Forward,
        PreviousEdit,
        NextEdit,
    }
}
