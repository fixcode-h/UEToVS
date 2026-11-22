using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Text;
using UEToVS.Contracts;
using UEToVS.Core.Diagnostics;
using UEToVS.Host.Editor;
using UEToVS.Host.Engine;
using UEToVS.Host.Logic.Actions;
using UEToVS.Host.Logic.Editing;
using UEToVS.Host.Logic.Localization;
using UEToVS.Host.Logic.Settings;
using UEToVS.Host.Logic.Unreal;
using UEToVS.Host.Navigation;
using UEToVS.Host.Options;
using UEToVS.Host.Shell;

namespace UEToVS.Host.Editing
{
    /// <summary>
    /// v0.9.4 引擎代码只读（参考 IDEA）：UE 项目里打开引擎 Engine\ 下的文件时编辑器不让改，顶部提示条（<see cref="EngineReadOnlyMargin"/>）
    /// 点“启用编辑”或按 Ctrl+Shift+Alt+U, E 后这个文件才能改。只在编辑器层锁：视图设 ViewProhibitUserInput，
    /// 再由 <see cref="EngineReadOnlyCommandFilter"/> 在命令链最前面挡住打字、粘贴、删除等编辑命令（语言服务自己插字符的路径也挡住），
    /// 不改磁盘文件的只读属性。解锁记在本次 VS 会话里，关掉文件再打开不会重新锁。
    /// 判断规则在 <see cref="EngineReadOnlyPolicy"/>（有单测），这里只接 VS：引擎根、设置、日志、命令。
    /// </summary>
    internal static class EngineReadOnlyFeature
    {
        private static readonly Logger L = Log.For("EngineReadOnly");
        private static readonly HashSet<string> Unlocked = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        private static SettingsService _hookedSettings;
        private static EngineClient _hookedEngine;
        private static string _lastRoot;

        /// <summary>设置、引擎根、解决方案变了：每个打开的视图重新判断要不要锁（UI 线程上触发）。</summary>
        public static event EventHandler Reevaluate;

        public static void Register(ActionRegistry registry)
        {
            registry.Register(new DelegateAction(EditorFeatureIds.ToggleEngineReadOnly, EditorFeatureStrings.CmdToggleEngineReadOnly, ActionCategory.Settings,
                ctx => ToggleActiveAsync(), ctx => ctx.ActiveDocumentPath != null, null, EditorFeatureStrings.KwEngineReadOnly));
        }

        private static bool Enabled => NavigationContext.Settings.EngineReadOnly;

        /// <summary>引擎根：解决方案布局里看出来的，否则代码引擎报的，否则设置里填的（和“生成项目文件”同一个来源）。</summary>
        private static string EngineRoot
        {
            get
            {
                var p = HostPackage.Instance;
                return p == null ? null : Unreal.UnrealTools.EngineRoot(p);
            }
        }

        private static bool IsEngineSolution => HostPackage.Instance?.Unreal?.Kind == UnrealSolutionKind.Engine;

        /// <summary>这个文件现在该不该锁（UI 线程；只有字符串比较，打开每个文件时调用一次、获得焦点时再调用）。</summary>
        public static bool ShouldLock(string filePath)
        {
            EnsureHooked();
            if (string.IsNullOrEmpty(filePath)) return false;
            return EngineReadOnlyPolicy.ShouldLock(Enabled, NavigationContext.IsUnrealSolution, IsEngineSolution, filePath, EngineRoot, Unlocked);
        }

        public static bool IsEngineFile(string filePath) => EngineReadOnlyPolicy.IsEngineFile(filePath, EngineRoot);

        public static void Unlock(EngineReadOnlyState state, string source)
        {
            if (state == null || !state.Locked) return;
            Unlocked.Add(EngineReadOnlyPolicy.Key(state.FilePath));
            state.SetLocked(false);
            // v0.9.13: 磁盘上也是只读的引擎文件多半在 P4 里没签出，改的时候 P4VS 会把它签进你的变更列表，先说清楚
            bool diskReadOnly = false;
            try { diskReadOnly = System.IO.File.Exists(state.FilePath) && (System.IO.File.GetAttributes(state.FilePath) & System.IO.FileAttributes.ReadOnly) != 0; }
            catch (Exception) { } // 读不到属性：按不是只读提示
            VsStatusBar.SetText(diskReadOnly
                ? UEToVS.Core.Localization.Loc.T("已启用编辑。这个引擎文件在磁盘上是只读的（P4 未签出），开始修改时版本管理会把它签出到你的变更列表",
                    "Editing enabled. This engine file is read-only on disk (not checked out in P4); source control checks it out to your changelist when you change it")
                : EditorFeatureStrings.EngineReadOnlyUnlocked);
            L.Info("engineReadOnly.unlocked", null, ("file", EngineReadOnlyPolicy.RelativeToEngine(state.FilePath, EngineRoot)), ("source", source),
                   ("unlockedThisSession", Unlocked.Count));
        }

        public static void Lock(EngineReadOnlyState state, string source)
        {
            if (state == null || state.Locked) return;
            Unlocked.Remove(EngineReadOnlyPolicy.Key(state.FilePath));
            state.SetLocked(true);
            VsStatusBar.SetText(EditorFeatureStrings.EngineReadOnlyLocked);
            L.Info("engineReadOnly.relocked", null, ("file", EngineReadOnlyPolicy.RelativeToEngine(state.FilePath, EngineRoot)), ("source", source));
        }

        /// <summary>用户在锁着的文件里想改（打字、粘贴、删除…）：状态栏说明怎么解锁。日志按文件只记第一次，免得一直打字刷屏。</summary>
        public static void NoteBlockedEdit(EngineReadOnlyState state, string command)
        {
            VsStatusBar.SetText(EditorFeatureStrings.EngineReadOnlyTyped);
            if (state != null && !state.BlockedLogged)
            {
                state.BlockedLogged = true;
                L.Info("engineReadOnly.editBlocked", null, ("file", EngineReadOnlyPolicy.RelativeToEngine(state.FilePath, EngineRoot)), ("command", command));
            }
        }

        public static void LogLocked(EngineReadOnlyState state) =>
            L.Info("engineReadOnly.locked", null, ("file", EngineReadOnlyPolicy.RelativeToEngine(state.FilePath, EngineRoot)));

        public static Task OpenSettingsAsync() => HostPackage.Instance?.ShowSettingsWindowAsync(HostSettingsSchema.Editing) ?? Task.CompletedTask;

        private static async Task ToggleActiveAsync()
        {
            await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
            var view = EditorServices.ActiveView();
            if (view == null || view.IsClosed) return;
            var state = EngineReadOnlyState.For(view.TextBuffer);
            if (state.Locked) { Unlock(state, "command"); return; }
            if (!Enabled) { VsStatusBar.SetText(EditorFeatureStrings.EngineReadOnlyOff); return; }
            if (!IsEngineFile(state.FilePath)) { VsStatusBar.SetText(EditorFeatureStrings.EngineReadOnlyNotEngine); return; }
            Lock(state, "command");
        }

        /// <summary>挂一次设置和代码引擎的事件（编辑器部件可能比包先创建，所以访问时才挂；包重启引擎后换新的客户端也会重新挂）。</summary>
        private static void EnsureHooked()
        {
            var package = HostPackage.Instance;
            if (package == null) return;
            var settings = package.Settings;
            if (settings != null && !ReferenceEquals(settings, _hookedSettings))
            {
                _hookedSettings = settings;
                settings.Changed += (s, e) =>
                {
                    if (e.Old.EngineReadOnly == e.New.EngineReadOnly && e.Old.UnrealEngineRootOverride == e.New.UnrealEngineRootOverride) return;
                    L.Info("engineReadOnly.settings", null, ("enabled", e.New.EngineReadOnly));
                    Raise("settings");
                };
            }
            var engine = package.Engine;
            if (engine != null && !ReferenceEquals(engine, _hookedEngine))
            {
                _hookedEngine = engine;
                engine.StatusChanged += OnEngineStatus;
            }
        }

        // 游戏项目的引擎根要等代码引擎握手后才知道：根一出现（或变了）就让已经打开的引擎文件补上锁。
        private static void OnEngineStatus(object sender, EngineStatus status)
        {
            if (!ReferenceEquals(sender, _hookedEngine)) return;
            string root = status?.EngineRoot;
            if (string.IsNullOrEmpty(root) || string.Equals(root, _lastRoot, StringComparison.OrdinalIgnoreCase)) return;
            _lastRoot = root;
            Raise("engineRoot");
        }

        private static void Raise(string reason)
        {
            UEToVS.Host.Diagnostics.Safe.RunAsync(L, "engineReadOnly.reevaluate", async () =>
            {
                await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
                L.Debug("engineReadOnly.reevaluate", null, ("reason", reason));
                Reevaluate?.Invoke(null, EventArgs.Empty);
            });
        }
    }

    /// <summary>
    /// 一个文档（缓冲区）的锁状态，同一文件拆分窗口、多个视图共用一份。<see cref="Changed"/> 时各视图更新提示条和禁止输入；
    /// <see cref="FlashRequested"/> 让提示条闪一下。UI 线程使用。
    /// </summary>
    internal sealed class EngineReadOnlyState
    {
        private readonly ITextBuffer _buffer;

        private EngineReadOnlyState(ITextBuffer buffer) => _buffer = buffer;

        public static EngineReadOnlyState For(ITextBuffer buffer) =>
            buffer.Properties.GetOrCreateSingletonProperty(typeof(EngineReadOnlyState), () => new EngineReadOnlyState(buffer));

        /// <summary>当前路径（另存为 / 改名后跟着变）。</summary>
        public string FilePath => EditorServices.FilePath(_buffer);

        public bool Locked { get; private set; }

        /// <summary>这个文件被挡住的编辑已经记过日志。</summary>
        public bool BlockedLogged { get; set; }

        public event EventHandler Changed;

        public event EventHandler FlashRequested;

        /// <summary>按当前设置和引擎根重新判断（打开、获得焦点、设置变化时）。</summary>
        public void Evaluate()
        {
            bool want = EngineReadOnlyFeature.ShouldLock(FilePath);
            if (want == Locked) return;
            SetLocked(want);
            if (want) EngineReadOnlyFeature.LogLocked(this);
        }

        internal void SetLocked(bool locked)
        {
            if (Locked == locked) return;
            Locked = locked;
            Changed?.Invoke(this, EventArgs.Empty);
        }

        public void Flash() => FlashRequested?.Invoke(this, EventArgs.Empty);
    }
}
