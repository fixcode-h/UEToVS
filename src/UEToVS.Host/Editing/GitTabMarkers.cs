using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Threading;
using Microsoft.VisualStudio;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Threading;
using Microsoft.VisualStudio.Shell.Interop;
using UEToVS.Core.Diagnostics;
using UEToVS.Host.Diagnostics;
using UEToVS.Host.IntelliSense;
using UEToVS.Host.Logic.Editing;
using UEToVS.Host.Logic.Mcp;
using UEToVS.Host.Logic.Vcs;
using UEToVS.Host.Navigation;

namespace UEToVS.Host.Editing
{
    /// <summary>
    /// v0.7.5: with auto-save VS's * vanishes within seconds, so, like IDEA's tab colors, document tabs of files that differ
    /// from Git (modified, added, untracked) get " [Git]" after the name (<c>VSFPROPID_EditorCaption</c>, the same place VS puts
    /// " [Design]"). Git is asked (<c>git status --porcelain</c>, off the UI thread) when the solution opens, after a save and
    /// when VS is activated; a newly opened document only re-applies the known state. Setting editor.gitTabMarker.
    /// 版本信息（2026-11）修正：git status 带 --no-optional-locks（不和用户自己的 git 抢 index.lock）、--untracked-files=normal
    /// （未跟踪的文件夹只报一行，打开的文件在不在里面由 <see cref="GitStatusParser.IsChanged"/> 判断，标记和以前一样）；
    /// VS 激活触发的刷新最多 30 秒一次（保存、打开解决方案照常刷新）。找 git 和编辑器内版本信息共用 <see cref="Vcs.GitLocator"/>。
    /// </summary>
    internal static class GitTabMarkers
    {
        // v0.9.11: 以前是 " ●"，VS 18 的未保存标记也是圆点，用户以为文件被改了还没保存（Ctrl+S 也去不掉）。改成文字，一眼能区分
        private const string Marker = " [Git]";
        private const string OldMarker = " ●";
        private static readonly Logger L = Log.For("Editing");
        private static readonly TimeSpan RefreshDelay = TimeSpan.FromMilliseconds(800);

        private static HashSet<string> _changed = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        private static List<string> _untrackedDirs = new List<string>();
        private static readonly RefreshThrottle ActivationThrottle = new RefreshThrottle(TimeSpan.FromSeconds(30));
        private static string _root;
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
            _untrackedDirs = new List<string>();
            L.Debug("editor.gitTabs.root", null, ("root", _root));
            ActivationThrottle.Mark(DateTime.UtcNow);
            ScheduleRefresh();
        }

        public static void OnSolutionClosing()
        {
            _root = null;
            _changed = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            _untrackedDirs = new List<string>();
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

        /// <summary>VS 被激活（外面可能 commit / checkout 了）：最多 30 秒刷新一次，频繁切窗口时不反复跑 git status。</summary>
        private static void OnActivated(object sender, EventArgs e)
        {
            Safe.Execute(L, "vcs.activated", Vcs.VcsService.Instance.OnActivated);
            if (!ActivationThrottle.TryEnter(DateTime.UtcNow)) return;
            ScheduleRefresh();
        }

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
                    (HashSet<string> Files, List<string> Dirs)? changed = null;
                    if (Enabled && root != null)
                    {
                        changed = await QueryAsync(root);
                        await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
                    }
                    if (changed != null || !Enabled)
                    {
                        _changed = changed?.Files ?? new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                        _untrackedDirs = changed?.Dirs ?? new List<string>();
                    }
                    Apply();
                } while (_again);
            }
            finally
            {
                _running = false;
            }
        }

        /// <summary>Runs git status (background thread, 10 s limit); null when git is missing or fails (the markers then keep the last known state).</summary>
        private static async Task<(HashSet<string> Files, List<string> Dirs)?> QueryAsync(string root)
        {
            await TaskScheduler.Default;
            try
            {
                var r = await Vcs.GitLocator.RunAsync(GitStatusParser.Arguments, root, TimeSpan.FromSeconds(10), CancellationToken.None, "git status").ConfigureAwait(false);
                if (r.ExitCode != 0)
                {
                    L.Debug("editor.gitTabs.failed", r.Stderr, ("exit", r.ExitCode));
                    return null;
                }
                var dirs = new List<string>();
                var set = GitStatusParser.Parse(r.Text, root, dirs);
                L.Debug("editor.gitTabs.status", null, ("changed", set.Count), ("untrackedDirs", dirs.Count), ("ms", Math.Round(r.ElapsedMs, 1)));
                return (set, dirs);
            }
            catch (VcsException ex)
            {
                L.Debug("editor.gitTabs.unavailable", ex.Message, ("root", root));
                return null;
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
                    bool want = enabled && GitStatusParser.IsChanged(path, _changed, _untrackedDirs);
                    // 升级前打开的标签页可能还带旧标记：先去掉再按新标记判断
                    if (caption.EndsWith(OldMarker, StringComparison.Ordinal))
                    {
                        caption = caption.Substring(0, caption.Length - OldMarker.Length);
                        frame.SetProperty((int)__VSFPROPID.VSFPROPID_EditorCaption, caption);
                    }
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
