using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Threading.Tasks;
using System.Windows.Threading;
using Microsoft.VisualStudio;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Shell.Interop;
using UEToVS.Core.Diagnostics;
using UEToVS.Host.Diagnostics;
using UEToVS.Host.IntelliSense;
using UEToVS.Host.Logic.Editing;
using UEToVS.Host.Logic.Mcp;
using UEToVS.Host.Navigation;

namespace UEToVS.Host.Editing
{
    /// <summary>
    /// v0.7.5: with auto-save VS's * vanishes within seconds, so, like IDEA's tab colors, document tabs of files that differ
    /// from Git (modified, added, untracked) get a ● after the name (<c>VSFPROPID_EditorCaption</c>, the same place VS puts
    /// " [Design]"). Git is asked (<c>git status --porcelain</c>, off the UI thread) when the solution opens, after a save and
    /// when VS is activated; a newly opened document only re-applies the known state. Setting editor.gitTabMarker.
    /// </summary>
    internal static class GitTabMarkers
    {
        private const string Marker = " ●";
        private static readonly Logger L = Log.For("Editing");
        private static readonly TimeSpan RefreshDelay = TimeSpan.FromMilliseconds(800);

        private static HashSet<string> _changed = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        private static string _root;
        private static string _git;
        private static DispatcherTimer _refresh, _apply;
        private static System.Windows.Window _hooked;
        private static bool _running, _again;

        private static bool Enabled
        {
            get
            {
                var s = IntelliSenseContext.Settings;
                return s != null && s.GitTabMarker && IntelliSenseContext.IsUnrealSolution;
            }
        }

        public static void Initialize()
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            if (_refresh != null) return;
            _refresh = new DispatcherTimer(DispatcherPriority.Background) { Interval = RefreshDelay };
            _refresh.Tick += (s, e) =>
            {
                _refresh.Stop();
                _ = Safe.RunAsync(L, "editor.gitTabs.refresh", RefreshAsync);
            };
            _apply = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(200) };
            _apply.Tick += (s, e) =>
            {
                _apply.Stop();
                Safe.Execute(L, "editor.gitTabs.apply", Apply);
            };
            NavigationService.Instance.FileSaved += _ => ScheduleRefresh();
            NavigationContext.SettingsChanged += (s, e) =>
            {
                if (e.Old.GitTabMarker != e.New.GitTabMarker) ScheduleRefresh();
            };
            HookMainWindow();
        }

        public static void OnSolutionOpened(string solutionDir)
        {
            string root = VcsRoot.Find(solutionDir);
            _root = root != null && (Directory.Exists(Path.Combine(root, ".git")) || File.Exists(Path.Combine(root, ".git"))) ? root : null;
            _changed = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            L.Debug("editor.gitTabs.root", null, ("root", _root));
            ScheduleRefresh();
        }

        public static void OnSolutionClosing()
        {
            _root = null;
            _changed = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        }

        /// <summary>A document view was opened: put the marker on its tab if its file is known to be changed.</summary>
        public static void OnDocumentOpened()
        {
            HookMainWindow();
            if (_apply == null) return;
            _apply.Stop();
            _apply.Start();
        }

        private static void HookMainWindow()
        {
            var main = System.Windows.Application.Current?.MainWindow;
            if (main == null || ReferenceEquals(main, _hooked)) return;
            if (_hooked != null) _hooked.Activated -= OnActivated;
            _hooked = main;
            main.Activated += OnActivated;
        }

        private static void OnActivated(object sender, EventArgs e) => ScheduleRefresh(); // a commit / checkout outside VS

        private static void ScheduleRefresh()
        {
            if (_refresh == null) return;
            _refresh.Stop();
            _refresh.Start();
        }

        private static async Task RefreshAsync()
        {
            if (_running)
            {
                _again = true;
                return;
            }
            _running = true;
            try
            {
                do
                {
                    _again = false;
                    string root = _root;
                    HashSet<string> changed = null;
                    if (Enabled && root != null)
                    {
                        changed = await Task.Run(() => Query(root));
                        await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
                    }
                    if (changed != null || !Enabled) _changed = changed ?? new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                    Apply();
                } while (_again);
            }
            finally
            {
                _running = false;
            }
        }

        /// <summary>Runs git status; null when git is missing or fails (the markers then keep the last known state).</summary>
        private static HashSet<string> Query(string root)
        {
            foreach (string git in Candidates())
            {
                try
                {
                    var psi = new ProcessStartInfo(git, "-C \"" + root + "\" " + GitStatusParser.Arguments)
                    {
                        UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true,
                        StandardOutputEncoding = System.Text.Encoding.UTF8,
                    };
                    using (var p = Process.Start(psi))
                    {
                        if (p == null) continue;
                        var output = p.StandardOutput.ReadToEndAsync();
                        var error = p.StandardError.ReadToEndAsync();
                        if (!p.WaitForExit(10000))
                        {
                            try { p.Kill(); } catch (Exception) { }
                            L.Debug("editor.gitTabs.timeout", null, ("root", root));
                            return null;
                        }
                        // 进程已退出；无参 WaitForExit 再等输出管道读完，下面取 Result 不会再阻塞。
                        // 这段跑在后台线程，不碰 UI 线程，VSTHRD002 说的死锁不会发生
                        p.WaitForExit();
#pragma warning disable VSTHRD002
                        var stdout = output.Result;
                        var stderr = error.Result;
#pragma warning restore VSTHRD002
                        if (p.ExitCode != 0)
                        {
                            L.Debug("editor.gitTabs.failed", stderr, ("exit", p.ExitCode));
                            return null;
                        }
                        _git = git;
                        var set = GitStatusParser.Parse(stdout, root);
                        L.Debug("editor.gitTabs.status", null, ("changed", set.Count));
                        return set;
                    }
                }
                catch (System.ComponentModel.Win32Exception)
                {
                    // not found: try the next candidate
                }
            }
            return null;
        }

        /// <summary>git on PATH, then the copy that ships with Visual Studio.</summary>
        private static IEnumerable<string> Candidates()
        {
            if (_git != null) yield return _git;
            yield return "git";
            string ide = null;
            try { ide = Path.GetDirectoryName(Process.GetCurrentProcess().MainModule?.FileName); }
            catch (Exception) { } // no process path: only the git on PATH
            if (ide != null)
            {
                string bundled = Path.Combine(ide, "CommonExtensions", "Microsoft", "TeamFoundation", "Team Explorer", "Git", "cmd", "git.exe");
                if (File.Exists(bundled)) yield return bundled;
            }
        }

        private static void Apply()
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            if (!(Package.GetGlobalService(typeof(SVsUIShell)) is IVsUIShell shell)) return;
            if (shell.GetDocumentWindowEnum(out IEnumWindowFrames frames) != VSConstants.S_OK || frames == null) return;
            bool enabled = Enabled;
            var one = new IVsWindowFrame[1];
            int marked = 0;
            while (frames.Next(1, one, out uint fetched) == VSConstants.S_OK && fetched == 1)
            {
                var frame = one[0];
                if (frame == null) continue;
                try
                {
                    frame.GetProperty((int)__VSFPROPID.VSFPROPID_pszMkDocument, out object mk);
                    frame.GetProperty((int)__VSFPROPID.VSFPROPID_EditorCaption, out object cap);
                    string path = mk as string, caption = cap as string ?? string.Empty;
                    bool want = enabled && !string.IsNullOrEmpty(path) && _changed.Contains(path);
                    bool has = caption.EndsWith(Marker, StringComparison.Ordinal);
                    if (want && !has) frame.SetProperty((int)__VSFPROPID.VSFPROPID_EditorCaption, caption + Marker);
                    else if (!want && has) frame.SetProperty((int)__VSFPROPID.VSFPROPID_EditorCaption, caption.Substring(0, caption.Length - Marker.Length));
                    if (want) marked++;
                }
                catch (Exception ex)
                {
                    L.Debug("editor.gitTabs.frameFailed", ex.Message);
                }
            }
            L.Debug("editor.gitTabs.applied", null, ("marked", marked), ("known", _changed.Count));
        }
    }
}
