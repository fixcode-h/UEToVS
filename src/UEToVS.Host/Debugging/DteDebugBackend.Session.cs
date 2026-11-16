using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using EnvDTE;
using EnvDTE80;
using Microsoft.VisualStudio.Shell;
using Newtonsoft.Json.Linq;
using UEToVS.Host.Logic.Debugging;

namespace UEToVS.Host.Debugging
{
    // Session ops: start / restart / stop / attach / build / pause / continue / step.
    internal sealed partial class DteDebugBackend
    {
        private const string SolutionFolderKind = "{66A26720-8FB5-11D2-AA7E-00C04F688DDE}";

        private JObject Start(DebugArgs a)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            var mode = CurrentMode;
            if (mode != DebugMode.Design)
                throw new DebugControlException($"已经在调试（{ModeText(mode)}）：要重新开始用 restart，停在断点时用 continue 继续");
            if (Tracker.Building) throw new DebugControlException("VS 正在编译，等编译完（wait）再启动");
            bool noDebug = a.Bool("noDebug", false);
            string config = a.Str("configuration");
            string project = a.Str("project");
            if (config != null) ActivateConfiguration(config);
            if (project != null) SetStartupProject(project);
            string launchArgs = ApplyLaunchArgs(a.Str("map"), a.Bool("game", false), a.Str("args"));
            if (!noDebug) Tracker.OnLaunchRequested();
            // The same command as the toolbar's "本地 Windows 调试器" button: VS builds out-of-date projects (UBT) first.
            _dte.ExecuteCommand(noDebug ? "Debug.StartWithoutDebugging" : "Debug.Start");
            var o = Snapshot(0);
            o["started"] = true;
            if (launchArgs != null) o["commandArguments"] = launchArgs;
            o["hint"] = noDebug
                ? "已按 Ctrl+F5 启动（不挂调试器，断点不会停）"
                : "已按 F5 启动：VS 先编译再启动 UE。用 wait 等它停在断点或退出；UE 编辑器启动要一会儿，status 看进程";
            return o;
        }

        private JObject Restart()
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            if (CurrentMode == DebugMode.Design) return Start(new DebugArgs(null));
            Tracker.OnLaunchRequested();
            _dte.ExecuteCommand("Debug.Restart");
            var o = Snapshot(0);
            o["restarted"] = true;
            o["hint"] = "已重启（Ctrl+Shift+F5）：用 wait 等它停在断点或退出";
            return o;
        }

        private JObject Stop(bool detach)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            if (Tracker.Building)
            {
                try { _dte.ExecuteCommand("Build.Cancel"); } catch (Exception) { /* nothing to cancel */ }
            }
            var mode = CurrentMode;
            RestoreLaunchArgs();
            Tracker.OnStopRequested();
            if (mode == DebugMode.Design)
            {
                var idle = Snapshot(0);
                idle["hint"] = "本来就没在调试";
                return idle;
            }
            var procs = _dte.Debugger.DebuggedProcesses.Cast<EnvDTE.Process>().Select(p => Path.GetFileName(p.Name ?? "")).ToList();
            if (detach) _dte.Debugger.DetachAll();
            else _dte.Debugger.Stop(false);
            var o = Snapshot(0);
            o[detach ? "detached" : "stopped"] = new JArray(procs);
            return o;
        }

        private JObject Attach(int? pid, string name)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            string wanted = name ?? "UnrealEditor";
            var candidates = new List<Process2>();
            foreach (EnvDTE.Process p in _dte.Debugger.LocalProcesses)
            {
                if (!(p is Process2 p2)) continue;
                if (pid != null)
                {
                    if (p.ProcessID == pid.Value) candidates.Add(p2);
                    continue;
                }
                string exe = Path.GetFileNameWithoutExtension(p.Name ?? "");
                if (exe.IndexOf(wanted, StringComparison.OrdinalIgnoreCase) >= 0) candidates.Add(p2);
            }
            if (candidates.Count == 0)
                throw new DebugControlException(pid != null ? $"本机没有 PID {pid} 的进程" : $"本机没有名字含 {wanted} 的进程：UE 还没开？可以用 start 从 VS 启动");
            if (candidates.Count > 1)
                throw new DebugControlException($"有 {candidates.Count} 个 {wanted} 进程，用 pid 指定：" +
                    string.Join("、", candidates.Select(c => $"{Path.GetFileName(c.Name)}({c.ProcessID})")));
            var target = candidates[0];
            if (target.IsBeingDebugged) throw new DebugControlException($"{Path.GetFileName(target.Name)}({target.ProcessID}) 已经挂着调试器");
            try { target.Attach2("Native"); }
            catch (Exception) { target.Attach(); } // engine name differs on some VS builds: let VS pick automatically
            var o = Snapshot(0);
            o["attached"] = new JObject { ["pid"] = target.ProcessID, ["name"] = Path.GetFileName(target.Name ?? "") };
            return o;
        }

        private JObject Build(DebugArgs a)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            if (CurrentMode != DebugMode.Design) throw new DebugControlException("调试中不能编译：先 stop，或者用 UE 的 Live Coding");
            if (Tracker.Building) throw new DebugControlException("VS 已经在编译，用 wait 等它编完");
            string config = a.Str("configuration");
            if (config != null) ActivateConfiguration(config);
            var sb = _dte.Solution.SolutionBuild;
            string project = a.Str("project") != null ? FindProject(a.Str("project")).UniqueName
                : (sb.StartupProjects as object[])?.FirstOrDefault() as string;
            if (string.IsNullOrEmpty(project)) throw new DebugControlException("没有启动项目：用 project 指定要编译的项目");
            var active = sb.ActiveConfiguration as SolutionConfiguration2;
            string configName = active == null ? null : active.Name + "|" + active.PlatformName;
            sb.BuildProject(configName, project, false);
            var o = Snapshot(0);
            o["building"] = true;
            o["project"] = Path.GetFileNameWithoutExtension(project);
            o["hint"] = "已开始编译：用 wait 等结果（buildDone / buildFailed，失败时带前几条错误）";
            return o;
        }

        private JObject Pause()
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            var mode = CurrentMode;
            if (mode == DebugMode.Break) return Snapshot(5);
            if (mode == DebugMode.Design) throw new DebugControlException("没有在调试：先 start 或 attach");
            _dte.Debugger.Break(false);
            return new JObject { ["pausing"] = true };
        }

        private JObject Continue()
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            RequireBreak();
            Tracker.OnRun(); // before Go: a break raised during the call must not be taken for the old one
            _dte.Debugger.Go(false);
            return new JObject { ["mode"] = "run", ["hint"] = "已继续运行：用 wait 等下一次停下" };
        }

        private JObject Step(DebugArgs a)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            RequireBreak();
            string kind = a.OneOf("kind", "over", "into", "over", "out", "toLine");
            if (kind == "toLine")
            {
                string file = DebugHelpers.ResolveSourceFile(a.RequireStr("file"), BaseDirs, File.Exists);
                int line = a.Int("line", 0, 1, 1000000);
                if (line == 0) throw new DebugControlException("toLine 要给 line");
                var added = _dte.Debugger.Breakpoints.Add("", file, line, 1, "", dbgBreakpointConditionType.dbgBreakpointConditionTypeWhenTrue,
                                                          "", "", 1, "", 0, dbgHitCountType.dbgHitCountTypeNone);
                foreach (Breakpoint bp in added) bp.Tag = RunToTag;
                Tracker.OnRun();
                _dte.Debugger.Go(false);
                return new JObject { ["step"] = kind, ["target"] = Path.GetFileName(file) + ":" + line };
            }
            Tracker.OnRun();
            switch (kind)
            {
                case "into": _dte.Debugger.StepInto(false); break;
                case "out": _dte.Debugger.StepOut(false); break;
                default: _dte.Debugger.StepOver(false); break;
            }
            return new JObject { ["step"] = kind };
        }

        // ------------------------------------------------------------------ one-launch command arguments (第 4 项)

        private object _argsSettings;   // VCDebugSettings of the startup project's active configuration (COM, late bound)
        private string _argsOriginal;   // its CommandArguments before this launch

        /// <summary>
        /// map / game / args for this launch only: VS has no "start with these arguments" API for C++ projects, so the startup
        /// project's 调试 > 命令参数 is changed now and put back as soon as the process runs (or the build fails / is stopped).
        /// VCProjectEngine is reached through IDispatch so the extension needs no reference to it. Returns the arguments used.
        /// </summary>
        private string ApplyLaunchArgs(string map, bool game, string extra)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            RestoreLaunchArgs();
            if (string.IsNullOrWhiteSpace(map) && !game && string.IsNullOrWhiteSpace(extra)) return null;
            string unique = (_dte.Solution.SolutionBuild.StartupProjects as object[])?.FirstOrDefault() as string;
            if (string.IsNullOrEmpty(unique)) throw new DebugControlException("没有启动项目，没法带启动参数：用 project 指定");
            Project project = _dte.Solution.Item(unique);
            object settings;
            string original;
            try
            {
                object cfg = Com.Get(project.Object, "ActiveConfiguration");
                settings = Com.Get(cfg, "DebugSettings");
                original = Com.Get(settings, "CommandArguments") as string ?? "";
            }
            catch (Exception ex)
            {
                throw new DebugControlException($"启动项目 {project.Name} 不是 C++（VC）工程，没法带启动参数：{ex.Message}");
            }
            string composed = DebugHelpers.ComposeLaunchArgs(original, map, game, extra);
            Com.Set(settings, "CommandArguments", composed);
            _argsSettings = settings;
            _argsOriginal = original;
            L.Info("debug.launchArgs.set", null, ("project", project.Name), ("args", composed));
            return composed;
        }

        private void RestoreLaunchArgs()
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            var settings = _argsSettings;
            if (settings == null) return;
            _argsSettings = null;
            try
            {
                Com.Set(settings, "CommandArguments", _argsOriginal ?? "");
                L.Info("debug.launchArgs.restored", null, ("args", _argsOriginal));
            }
            catch (Exception ex)
            {
                L.Warn("debug.launchArgs.restore.failed", "Startup project's command arguments were not restored", ex, ("original", _argsOriginal));
            }
        }

        /// <summary>Late-bound property access on VS COM objects (IDispatch).</summary>
        private static class Com
        {
            public static object Get(object o, string name) =>
                o.GetType().InvokeMember(name, System.Reflection.BindingFlags.GetProperty, null, o, null);

            public static void Set(object o, string name, object value) =>
                o.GetType().InvokeMember(name, System.Reflection.BindingFlags.SetProperty, null, o, new[] { value });
        }

        private void RemoveRunToBreakpoints()
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            try
            {
                var temp = _dte.Debugger.Breakpoints.Cast<Breakpoint>().Where(b => b.Tag == RunToTag).ToList();
                foreach (var b in temp) b.Delete();
            }
            catch (Exception ex) { L.Debug("debug.runTo.cleanup.failed", ex.Message); }
        }

        private IEnumerable<string> BaseDirs => new[] { _solutionDir, _projectDir }.Where(d => !string.IsNullOrEmpty(d));

        private void ActivateConfiguration(string wanted)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            var configs = _dte.Solution.SolutionBuild.SolutionConfigurations.Cast<SolutionConfiguration2>().ToList();
            int i = DebugHelpers.FindConfiguration(configs.Select(c => (c.Name, c.PlatformName)).ToList(), wanted);
            configs[i].Activate();
        }

        private void SetStartupProject(string name)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            _dte.Solution.SolutionBuild.StartupProjects = FindProject(name).UniqueName;
        }

        /// <summary>A project by name (UE solutions nest them in "Games", "Engine", "Programs" folders).</summary>
        private Project FindProject(string name)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            var all = new List<Project>();
            foreach (Project p in _dte.Solution.Projects) Collect(p, all);
            var hit = all.Where(p => string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase)).ToList();
            if (hit.Count == 0) hit = all.Where(p => (p.Name ?? "").IndexOf(name, StringComparison.OrdinalIgnoreCase) >= 0).ToList();
            if (hit.Count == 1) return hit[0];
            string names = string.Join("、", all.Select(p => p.Name).Take(30));
            throw new DebugControlException(hit.Count == 0 ? $"解决方案里没有项目 {name}，有：{names}" : $"{name} 对上了多个项目：{string.Join("、", hit.Select(p => p.Name))}");
        }

        private static void Collect(Project p, List<Project> into)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            if (p == null) return;
            if (p.Kind == SolutionFolderKind)
            {
                if (p.ProjectItems == null) return;
                foreach (ProjectItem item in p.ProjectItems) Collect(item.SubProject, into);
                return;
            }
            into.Add(p);
        }
    }
}
