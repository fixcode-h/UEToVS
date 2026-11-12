using System;
using System.IO;
using Microsoft.VisualStudio.Shell;
using UEToVS.Contracts;
using UEToVS.Core.Diagnostics;
using UEToVS.Core.Navigation;
using UEToVS.Core.Storage;
using UEToVS.Host.Diagnostics;
using UEToVS.Host.Logic.Localization;
using UEToVS.Host.Logic.Navigation;
using UEToVS.Host.Logic.Settings;
using UEToVS.Host.Shell;

namespace UEToVS.Host.Navigation
{
    /// <summary>
    /// The host's single entry point for jumping somewhere: <see cref="JumpTo(CodeLocation, string, bool)"/> records the
    /// current caret in Back/Forward history, then opens the target. Every feature that moves the user (search results,
    /// bookmarks, related files, M2 go-to-definition / references …) should call it, so Alt+← / Alt+→ work everywhere.
    /// Also owns Back/Forward, previous/next edit location, recent files and recent locations (fed by
    /// <see cref="TextViewTracker"/>), persisted in &lt;solution&gt;/.vs/UEToVS/Navigation. UI thread only.
    /// </summary>
    internal sealed class NavigationService
    {
        private static readonly Logger L = Log.For("Navigation");
        private string _stateDir;

        public static NavigationService Instance { get; } = new NavigationService();

        public NavigationState State { get; } = new NavigationState();

        /// <summary>Raised (UI thread) after a document was saved to disk.</summary>
        public event Action<string> FileSaved;

        // ------------------------------------------------------------------ jump entry point

        /// <summary>
        /// Records the current position in Back history, then opens <paramref name="location"/> (0-based line/column;
        /// line &lt; 0 only opens the file). <paramref name="reason"/> is a <see cref="JumpReasons"/> value (logged).
        /// Returns false when the file does not exist or could not be opened (status bar says why).
        /// </summary>
        public bool JumpTo(CodeLocation location, string reason, bool newTabGroup = false)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            if (location == null) return false;
            using (UiThread.Measure(L, "nav.jumpTo", ("reason", reason)))
            {
                if (!File.Exists(location.FilePath))
                {
                    L.Info("nav.jump.missing", null, ("file", location.FilePath), ("reason", reason));
                    VsStatusBar.SetText(FeatureStrings.FileNotFound(location.FilePath));
                    return false;
                }
                var from = VsEditor.CurrentLocation();
                var to = new CodeLocation(location.FilePath, Math.Max(0, location.Line), Math.Max(0, location.Column));
                State.RecordJump(from, location.Line < 0 ? null : to, reason, DateTime.Now);
                bool ok;
                using (State.Suppress())
                {
                    ok = VsEditor.OpenAt(location.FilePath, location.Line, location.Column, newTabGroup);
                    State.SetLast(VsEditor.CurrentLocation() ?? to);
                }
                if (!ok) L.Warn("nav.jump.failed", "Opening the jump target failed", ("file", location.FilePath), ("line", location.Line), ("reason", reason));
                return ok;
            }
        }

        /// <summary>Async entry for callers that may be off the UI thread (M2 navigation features).</summary>
        public async System.Threading.Tasks.Task<bool> JumpToAsync(CodeLocation location, string reason)
        {
            await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
            return JumpTo(location, reason);
        }

        /// <summary><see cref="JumpTo(CodeLocation, string, bool)"/> for an engine location (0-based, as in the contracts).</summary>
        public bool JumpTo(SourceLocation location, string reason, bool newTabGroup = false) =>
            location != null && JumpTo(new CodeLocation(location.FilePath, location.Line, location.Column), reason, newTabGroup);

        public bool JumpTo(string path, int line, int column, string reason, bool newTabGroup = false) =>
            JumpTo(new CodeLocation(path, line, column), reason, newTabGroup);

        // ------------------------------------------------------------------ Back / Forward / edits

        public void Back() => Step(NavigationStep.Back, FeatureStrings.NoBack);

        public void Forward() => Step(NavigationStep.Forward, FeatureStrings.NoForward);

        public void PreviousEdit() => Step(NavigationStep.PreviousEdit, FeatureStrings.NoPreviousEdit);

        public void NextEdit() => Step(NavigationStep.NextEdit, FeatureStrings.NoNextEdit);

        private void Step(NavigationStep step, string emptyMessage)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            var cur = VsEditor.CurrentLocation();
            var target = State.Pick(step, cur, File.Exists);
            L.Info("nav.step", null, ("step", step.ToString()), ("from", cur?.ToString()), ("to", target?.ToString()),
                   ("canGoBack", State.History.CanGoBack), ("canGoForward", State.History.CanGoForward));
            if (target == null)
            {
                VsStatusBar.SetText(emptyMessage);
                return;
            }
            using (State.Suppress())
            {
                VsEditor.OpenAt(target.FilePath, target.Line, target.Column);
                State.SetLast(VsEditor.CurrentLocation() ?? target);
            }
        }

        // ------------------------------------------------------------------ editor feed (TextViewTracker)

        internal void OnCaret(CodeLocation loc) => State.OnCaret(loc, DateTime.Now);

        internal void OnEdit(CodeLocation loc, string lineText) => State.OnEdit(loc, lineText, DateTime.Now);

        internal void OnSaved(string path)
        {
            Safe.Execute(L, "nav.saved", () => FileSaved?.Invoke(path), ("file", path));
            Save(); // so a VS crash does not lose the history
        }

        // ------------------------------------------------------------------ lifetime

        public void Configure(HostSettings s)
        {
            if (s == null) return;
            State.Configure(s.NavigationHistoryCapacity, s.NavigationLineThreshold);
        }

        /// <summary>UE solution opened: load its state.</summary>
        public void Open(string solutionDir)
        {
            _stateDir = string.IsNullOrEmpty(solutionDir) ? null : Path.Combine(StoragePaths.ForSolution(solutionDir).Root, "Navigation");
            State.Load(_stateDir);
        }

        public void Save()
        {
            if (_stateDir == null) return;
            using (L.Time("nav.state.save", 20)) State.Save(_stateDir);
        }

        /// <summary>Solution closing: save and forget.</summary>
        public void Close()
        {
            Save();
            State.Clear();
            _stateDir = null;
        }
    }
}
