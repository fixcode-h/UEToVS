using System;
using System.Collections.Generic;
using System.Linq;
using EnvDTE;
using Microsoft.VisualStudio.Shell;
using Newtonsoft.Json.Linq;
using UEToVS.Host.Logic.Debugging;

namespace UEToVS.Host.Debugging
{
    // Inspection while stopped: call stack, threads, locals, expressions, Debug output.
    internal sealed partial class DteDebugBackend
    {
        private const string DebugPaneGuid = "{FC076020-078A-11D1-A7DF-00A0C9110051}";
        private const int ValueBudget = 3500;

        /// <summary>thread (ID) and frame (0 = top) arguments: switches VS's current thread / frame like a click in the Call Stack window.</summary>
        private void SelectContext(DebugArgs a)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            int? threadId = a.IntOrNull("thread");
            if (threadId != null)
            {
                var t = _dte.Debugger.CurrentProgram?.Threads.Cast<Thread>().FirstOrDefault(x => x.ID == threadId.Value)
                        ?? throw new DebugControlException($"没有 ID 为 {threadId} 的线程，用 threads 看");
                _dte.Debugger.CurrentThread = t;
            }
            int? frame = a.IntOrNull("frame");
            if (frame != null)
            {
                var frames = _dte.Debugger.CurrentThread.StackFrames;
                if (frame < 0 || frame >= frames.Count) throw new DebugControlException($"没有第 {frame} 帧（共 {frames.Count} 帧，0 是栈顶）");
                _dte.Debugger.CurrentStackFrame = frames.Item(frame.Value + 1);
            }
        }

        private JObject StackTrace(DebugArgs a)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            RequireBreak();
            SelectContext(a);
            int offset = a.Int("offset", 0, 0, 10000), limit = a.Int("limit", 20, 1, 100);
            var frames = Frames(offset, limit, out int total, out int current);
            var t = _dte.Debugger.CurrentThread;
            var o = new JObject
            {
                ["thread"] = new JObject { ["id"] = t.ID, ["name"] = t.Name ?? "" },
                ["frames"] = frames,
                ["total"] = total,
                ["current"] = current,
                ["breakCount"] = Tracker.BreakCount,
            };
            if (total > offset + limit) o["nextOffset"] = offset + limit;
            return o;
        }

        private JObject Threads(DebugArgs a)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            RequireBreak();
            SelectContext(a);
            string filter = a.Str("filter");
            int limit = a.Int("limit", 60, 1, 300);
            var cur = _dte.Debugger.CurrentThread;
            var arr = new JArray();
            int total = 0;
            foreach (Thread t in _dte.Debugger.CurrentProgram.Threads)
            {
                string name = t.Name ?? "";
                string location = t.Location ?? "";
                if (filter != null && name.IndexOf(filter, StringComparison.OrdinalIgnoreCase) < 0
                    && location.IndexOf(filter, StringComparison.OrdinalIgnoreCase) < 0) continue;
                total++;
                if (arr.Count >= limit) continue;
                var o = new JObject { ["id"] = t.ID, ["name"] = name, ["location"] = location.Length > 160 ? location.Substring(0, 160) + "…" : location };
                if (cur != null && t.ID == cur.ID) o["current"] = true;
                if (t.IsFrozen) o["frozen"] = true;
                arr.Add(o);
            }
            return new JObject { ["threads"] = arr, ["total"] = total };
        }

        private JObject Locals(DebugArgs a)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            RequireBreak();
            SelectContext(a);
            int depth = a.Int("depth", 1, 0, 3);
            string filter = a.Str("filter");
            var frame = _dte.Debugger.CurrentStackFrame ?? throw new DebugControlException("当前线程没有栈帧");
            var writer = new ValueTreeWriter(ValueBudget);
            var args = Nodes(frame.Arguments).ToList();
            var argNames = new HashSet<string>(args.Select(n => n.Name));
            var o = new JObject
            {
                ["function"] = frame.FunctionName ?? "",
                ["args"] = writer.WriteAll(args, depth, filter),
                ["locals"] = writer.WriteAll(Nodes(frame.Locals).Where(n => !argNames.Contains(n.Name)), depth, filter),
            };
            if (writer.Truncated) o["truncated"] = "内容太多只列了一部分：用 filter 只看某个变量，或用 eval 看具体成员（如 this->Health）";
            return o;
        }

        private JObject Eval(DebugArgs a)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            RequireBreak();
            SelectContext(a);
            var exprs = new List<string>();
            if (a.Raw["exprs"] is JArray list) exprs.AddRange(list.Select(t => (string)t).Where(s => !string.IsNullOrWhiteSpace(s)));
            if (a.Str("expr") != null) exprs.Insert(0, a.Str("expr"));
            if (exprs.Count == 0) throw new DebugControlException("缺少参数 expr（如 this->Health、GetName()、MyArray.Num()）");
            if (exprs.Count > 10) throw new DebugControlException("一次最多 10 个表达式");
            int depth = a.Int("depth", 1, 0, 3);
            int timeout = a.Int("timeoutMs", 5000, 100, 30000);
            var writer = new ValueTreeWriter(ValueBudget);
            var arr = new JArray();
            foreach (string e in exprs)
            {
                // UseAutoExpandRules: values formatted like the Watch window (natvis: FString, TArray, FName, UObject*).
                Expression r = _dte.Debugger.GetExpression(e, true, timeout);
                var node = writer.Write(new DteValueNode(r, e), depth);
                arr.Add(node);
            }
            var o = new JObject { ["results"] = arr };
            if (writer.Truncated) o["truncated"] = "内容太多只列了一部分：降低 depth，或者直接 eval 某个成员";
            return o;
        }

        /// <summary>dump: minidump of the stopped process into .vs\UEToVS\Debug\Dumps (full = with heap, GBs for an editor).</summary>
        private JObject Dump(DebugArgs a)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            RequireBreak();
            bool full = a.Bool("full", false);
            string dir = System.IO.Path.Combine(UEToVS.Core.Storage.StoragePaths.ForSolution(_solutionDir).Debug, "Dumps");
            System.IO.Directory.CreateDirectory(dir);
            var proc = _dte.Debugger.CurrentProcess;
            string name = System.IO.Path.GetFileNameWithoutExtension(proc?.Name ?? "process");
            string file = System.IO.Path.Combine(dir, $"{name}-{DateTime.Now:yyyyMMdd-HHmmss}{(full ? "-full" : "")}.dmp");
            ((EnvDTE80.Debugger2)_dte.Debugger).WriteMinidump(file, full ? EnvDTE80.dbgMinidumpOption.dbgMinidumpFull : EnvDTE80.dbgMinidumpOption.dbgMinidumpNormal);
            long size = System.IO.File.Exists(file) ? new System.IO.FileInfo(file).Length : 0;
            return new JObject { ["file"] = file, ["bytes"] = size, ["full"] = full, ["hint"] = "用 VS 打开 .dmp（配同一版本的 PDB）就能回到这一刻的调用栈和变量" };
        }

        private JObject Output(DebugArgs a)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            int maxChars = a.Int("maxChars", 3000, 200, 10000);
            int? cursor = a.IntOrNull("cursor");
            string filter = a.Str("filter");
            OutputWindowPane pane = null;
            foreach (OutputWindowPane p in _dte.ToolWindows.OutputWindow.OutputWindowPanes)
            {
                if (string.Equals(p.Guid, DebugPaneGuid, StringComparison.OrdinalIgnoreCase)) { pane = p; break; }
            }
            if (pane == null) return new JObject { ["text"] = "", ["next"] = 1, ["hint"] = "还没有调试输出（没启动过调试）" };
            var doc = pane.TextDocument;
            int lines = doc.EndPoint.Line;
            int start = DebugHelpers.OutputStartLine(lines, cursor, 200, out bool restarted);
            var ep = doc.CreateEditPoint(doc.StartPoint);
            ep.MoveToLineAndOffset(start, 1);
            string text = ep.GetText(doc.EndPoint) ?? "";
            if (filter != null)
                text = string.Join("\n", text.Split('\n').Where(l => l.IndexOf(filter, StringComparison.OrdinalIgnoreCase) >= 0));
            text = DebugHelpers.Tail(text.Replace("\r", ""), maxChars, out int cut);
            var o = new JObject { ["text"] = text, ["next"] = lines, ["lines"] = lines };
            if (restarted) o["restarted"] = true;
            if (cut > 0) o["cut"] = cut;
            return o;
        }

        private static IEnumerable<IValueNode> Nodes(Expressions list)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            if (list == null) yield break;
            foreach (Expression e in list) yield return new DteValueNode(e, null);
        }

        /// <summary>EnvDTE.Expression as a value node; children are read only when the writer descends.</summary>
        private sealed class DteValueNode : IValueNode
        {
            private readonly Expression _e;
            private readonly string _name;

            public DteValueNode(Expression e, string name)
            {
                _e = e;
                _name = name;
            }

            public string Name { get { ThreadHelper.ThrowIfNotOnUIThread(); return _name ?? _e.Name; } }
            public string Type { get { ThreadHelper.ThrowIfNotOnUIThread(); return _e.Type; } }
            public string Value { get { ThreadHelper.ThrowIfNotOnUIThread(); return _e.Value; } }
            public bool IsValid { get { ThreadHelper.ThrowIfNotOnUIThread(); return _e.IsValidValue; } }

            public IEnumerable<IValueNode> Children()
            {
                ThreadHelper.ThrowIfNotOnUIThread();
                return Nodes(_e.DataMembers);
            }
        }
    }
}
