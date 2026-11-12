using System;
using System.IO;
using Microsoft.VisualStudio.Shell;
using UEToVS.Core.Navigation;
using UEToVS.UI;

namespace UEToVS.Navigation
{
    /// <summary>
    /// Rider-style navigation state for the open solution: Back/Forward history, edit locations,
    /// recent files and recent locations. Fed by <see cref="TextViewTracker"/>; persisted in .vs/UEToVS.
    /// </summary>
    internal sealed class NavigationService
    {
        public static NavigationService Instance { get; } = new NavigationService();

        private CodeLocation _last;
        private int _suppress;

        public NavigationHistory History { get; } = new NavigationHistory();
        public EditLocations Edits { get; } = new EditLocations();
        public RecentFiles Recent { get; } = new RecentFiles();
        public RecentLocations Locations { get; } = new RecentLocations();

        /// <summary>Raised (UI thread) after a file was saved.</summary>
        public event Action<string> FileSaved;

        /// <summary>Raised (UI thread) when an edited document changes; used by bookmarks to follow lines.</summary>
        public event Action<string> FileEdited;

        public CodeLocation LastLocation => _last;

        public void Configure(int capacity, int lineThreshold)
        {
            History.Capacity = Math.Max(10, capacity);
            History.LineThreshold = Math.Max(1, lineThreshold);
        }

        /// <summary>The caret is now at <paramref name="loc"/> (any cause: click, F12, our own navigation).</summary>
        public void OnCaret(CodeLocation loc, string lineText)
        {
            if (loc == null) return;
            var prev = _last;
            _last = loc;
            if (prev == null)
            {
                Recent.Touch(loc.FilePath, DateTime.Now);
                return;
            }
            if (!prev.SameFile(loc))
            {
                Recent.Touch(loc.FilePath, DateTime.Now);
                if (_suppress == 0) History.RecordJump(prev, loc);
                Locations.Add(prev, DateTime.Now, false);
            }
            else if (_suppress == 0)
            {
                bool far = !prev.IsNear(loc, History.LineThreshold);
                History.OnCaretMoved(prev, loc);
                if (far) Locations.Add(prev, DateTime.Now, false);
            }
        }

        public void OnEdit(CodeLocation loc, string lineText)
        {
            if (loc == null) return;
            Edits.RecordEdit(loc);
            Recent.MarkEdited(loc.FilePath, DateTime.Now);
            Locations.Add(loc, DateTime.Now, true, lineText?.Trim());
            FileEdited?.Invoke(loc.FilePath);
        }

        public void OnSaved(string path) => FileSaved?.Invoke(path);

        /// <summary>Opens a location as a recorded jump (search results, related symbols...).</summary>
        public bool NavigateTo(string path, int line, int column, bool newTabGroup = false)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            var from = VsHelpers.CurrentLocation() ?? _last;
            if (from != null) History.RecordJump(from, new CodeLocation(path, Math.Max(0, line), Math.Max(0, column)));
            _suppress++;
            try
            {
                bool ok = VsHelpers.OpenAt(path, line, column, newTabGroup);
                _last = VsHelpers.CurrentLocation() ?? new CodeLocation(path, Math.Max(0, line), Math.Max(0, column));
                Recent.Touch(path, DateTime.Now);
                return ok;
            }
            finally
            {
                _suppress--;
            }
        }

        /// <summary>Opens a location without touching Back/Forward (used by Back/Forward themselves).</summary>
        private bool OpenQuiet(CodeLocation loc)
        {
            _suppress++;
            try
            {
                bool ok = VsHelpers.OpenAt(loc.FilePath, loc.Line, loc.Column);
                _last = VsHelpers.CurrentLocation() ?? loc;
                return ok;
            }
            finally
            {
                _suppress--;
            }
        }

        public void Back() => Step(cur => History.GoBack(cur), "没有可以后退的位置");

        public void Forward() => Step(cur => History.GoForward(cur), "没有可以前进的位置");

        public void LastEdit() => Step(cur => Edits.Previous(cur), "没有更早的编辑位置");

        public void NextEdit() => Step(cur => Edits.Next(cur), "没有更晚的编辑位置");

        private void Step(Func<CodeLocation, CodeLocation> pick, string emptyMessage)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            var cur = VsHelpers.CurrentLocation() ?? _last;
            for (int guard = 0; guard < 20; guard++)
            {
                var target = pick(cur);
                if (target == null)
                {
                    OutputLog.Status("UEToVS: " + emptyMessage);
                    return;
                }
                if (File.Exists(target.FilePath))
                {
                    OpenQuiet(target);
                    return;
                }
                History.RemoveFile(target.FilePath);
                Edits.RemoveFile(target.FilePath);
            }
        }

        public void Load(string stateDir)
        {
            Clear();
            if (stateDir == null) return;
            Read(Path.Combine(stateDir, "history.txt"), History.Load);
            Read(Path.Combine(stateDir, "edits.txt"), Edits.Load);
            Read(Path.Combine(stateDir, "recent.txt"), Recent.Load);
        }

        public void Save(string stateDir)
        {
            if (stateDir == null) return;
            Write(Path.Combine(stateDir, "history.txt"), History.Save);
            Write(Path.Combine(stateDir, "edits.txt"), Edits.Save);
            Write(Path.Combine(stateDir, "recent.txt"), Recent.Save);
        }

        public void Clear()
        {
            History.Clear();
            Recent.Clear();
            Locations.Clear();
            Edits.Clear();
            _last = null;
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
                OutputLog.Write("Reading " + path + " failed: " + ex.Message);
            }
        }

        private static void Write(string path, Action<TextWriter> save)
        {
            try
            {
                using (var w = new StreamWriter(path)) save(w);
            }
            catch (Exception ex)
            {
                OutputLog.Write("Writing " + path + " failed: " + ex.Message);
            }
        }
    }
}
