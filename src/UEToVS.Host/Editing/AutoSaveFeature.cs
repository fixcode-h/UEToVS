using System;
using System.IO;
using System.Windows.Threading;
using EnvDTE;
using Microsoft.VisualStudio.Shell;
using UEToVS.Core.Diagnostics;
using UEToVS.Host.Diagnostics;
using UEToVS.Host.IntelliSense;
using UEToVS.Host.Logic.Editing;
using UEToVS.Host.Shell;

namespace UEToVS.Host.Editing
{
    /// <summary>
    /// v0.7.5 auto-save, like IDEA (setting editor.autoSave, on by default; UE solutions only): modified documents are saved
    /// when VS loses focus (switching to another application) and <c>editor.autoSaveIdleSeconds</c> after the last edit.
    /// Only documents that exist on disk and are not read-only (<see cref="AutoSavePolicy.ShouldSave"/>): an untitled
    /// document never opens Save As and a read-only file never prompts. UI thread only.
    /// </summary>
    internal static class AutoSaveFeature
    {
        private static readonly Logger L = Log.For("Editing");
        private static DispatcherTimer _timer;
        private static System.Windows.Window _hooked;
        private static DateTime _lastEdit = DateTime.MinValue, _lastRound = DateTime.MinValue;
        private static bool _saving;

        public static void Initialize()
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            if (_timer != null) return;
            _timer = new DispatcherTimer(DispatcherPriority.ApplicationIdle) { Interval = TimeSpan.FromSeconds(1) };
            _timer.Tick += (s, e) => Safe.Execute(L, "editor.autoSave.tick", OnTick);
            _timer.Start();
            HookMainWindow();
            L.Debug("editor.autoSave.ready", null);
        }

        /// <summary>Called for every edit in a document view (TextViewTracker).</summary>
        public static void NoteEdit() => _lastEdit = DateTime.UtcNow;

        private static bool Active
        {
            get
            {
                var s = IntelliSenseContext.Settings;
                return s != null && s.AutoSaveEnabled && IntelliSenseContext.IsUnrealSolution;
            }
        }

        private static void HookMainWindow()
        {
            var main = System.Windows.Application.Current?.MainWindow;
            if (main == null || ReferenceEquals(main, _hooked)) return;
            if (_hooked != null) _hooked.Deactivated -= OnDeactivated;
            _hooked = main;
            main.Deactivated += OnDeactivated;
        }

        private static void OnDeactivated(object sender, EventArgs e) =>
            Safe.Execute(L, "editor.autoSave.deactivated", () => SaveAll(AutoSavePolicy.ReasonDeactivated));

        private static void OnTick()
        {
            HookMainWindow(); // the main window may not exist yet when the package loads
            if (!Active) return;
            if (AutoSavePolicy.IdleDue(_lastEdit, _lastRound, DateTime.UtcNow, IntelliSenseContext.Settings.AutoSaveIdleSeconds))
                SaveAll(AutoSavePolicy.ReasonIdle);
        }

        private static readonly System.Collections.Generic.HashSet<string> WarnedReadOnly = new System.Collections.Generic.HashSet<string>(StringComparer.OrdinalIgnoreCase);

        private static void SaveAll(string reason)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            if (!Active || _saving) return;
            _lastRound = DateTime.UtcNow;
            if (!(Package.GetGlobalService(typeof(DTE)) is DTE dte)) return;
            _saving = true;
            int saved = 0, skipped = 0;
            string readOnlyName = null;
            try
            {
                foreach (Document d in dte.Documents)
                {
                    try
                    {
                        if (d.Saved) continue;
                        string path = d.FullName;
                        bool exists = !string.IsNullOrEmpty(path) && File.Exists(path);
                        bool readOnly = d.ReadOnly || exists && (File.GetAttributes(path) & FileAttributes.ReadOnly) != 0;
                        if (!AutoSavePolicy.ShouldSave(path, true, exists, readOnly))
                        {
                            // v0.9.13: 只读多半是 P4 里还没签出：以前悄悄跳过，用户以为存了。每个文件提示一次
                            if (exists && readOnly && WarnedReadOnly.Add(path)) readOnlyName = Path.GetFileName(path);
                            skipped++;
                            continue;
                        }
                        d.Save();
                        saved++;
                    }
                    catch (Exception ex)
                    {
                        skipped++;
                        L.Debug("editor.autoSave.documentFailed", ex.Message);
                    }
                }
            }
            finally
            {
                _saving = false;
            }
            if (saved + skipped > 0) L.Info("editor.autoSave", null, ("reason", reason), ("saved", saved), ("skipped", skipped));
            if (readOnlyName != null)
                VsStatusBar.SetText(UEToVS.Core.Localization.Loc.T(readOnlyName + " 是只读的（P4 下可能还没签出），自动保存跳过了它；签出后会照常自动保存",
                    readOnlyName + " is read-only (not checked out in P4?), so auto-save skipped it; it is saved as usual once checked out"));
        }
    }
}
