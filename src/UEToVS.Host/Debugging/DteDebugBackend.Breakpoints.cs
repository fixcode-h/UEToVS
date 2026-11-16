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
    // Breakpoints: VS's own breakpoints (the Breakpoints window shows the same list; the user's breakpoints are listed too).
    internal sealed partial class DteDebugBackend
    {
        private List<Breakpoint> AllBreakpoints()
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            return _dte.Debugger.Breakpoints.Cast<Breakpoint>().Where(b => b.Tag != RunToTag).ToList();
        }

        private static JObject BreakpointJson(Breakpoint b, int index)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            var o = new JObject { ["i"] = index };
            if (!string.IsNullOrEmpty(b.File))
            {
                o["file"] = b.File;
                o["line"] = b.FileLine;
            }
            if (!string.IsNullOrEmpty(b.FunctionName) && string.IsNullOrEmpty(b.File)) o["function"] = b.FunctionName;
            o["enabled"] = b.Enabled;
            if (!string.IsNullOrEmpty(b.Condition)) o["condition"] = b.Condition;
            if (b.HitCountType != dbgHitCountType.dbgHitCountTypeNone) o["hitCount"] = b.HitCountTarget;
            o["hits"] = b.CurrentHits;
            if (b is Breakpoint2 b2 && !b2.BreakWhenHit && !string.IsNullOrEmpty(b2.Message)) o["message"] = b2.Message;
            if (b.Tag == AgentTag) o["byAgent"] = true;
            try
            {
                // Children = bound locations in the running process; none while debugging = symbols not loaded / wrong line.
                if (b.Children != null) o["bound"] = b.Children.Count;
            }
            catch (Exception) { } // binding state unavailable outside a debug session
            return o;
        }

        private static string BreakpointLabel(Breakpoint b)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            if (b.Parent != null && !ReferenceEquals(b.Parent, b)) b = b.Parent;
            return !string.IsNullOrEmpty(b.File) ? Path.GetFileName(b.File) + ":" + b.FileLine : b.FunctionName ?? "";
        }

        private JObject ListBreakpoints(DebugArgs a)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            int offset = a.Int("offset", 0, 0, 10000), limit = a.Int("limit", 50, 1, 200);
            var all = AllBreakpoints();
            var arr = new JArray();
            for (int i = offset; i < all.Count && arr.Count < limit; i++) arr.Add(BreakpointJson(all[i], i + 1));
            var o = new JObject { ["total"] = all.Count, ["items"] = arr };
            if (all.Count > offset + limit) o["nextOffset"] = offset + limit;
            return o;
        }

        private JObject AddBreakpoint(DebugArgs a)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            if (a.Str("data") != null) return AddDataBreakpoint(a);
            string function = a.Str("function");
            string file = a.Str("file");
            int line = a.Int("line", 0, 0, 1000000);
            if (function == null && file == null) throw new DebugControlException("给 file + line，或者 function（如 AMyCharacter::Jump）");
            if (file != null)
            {
                if (line <= 0) throw new DebugControlException("给了 file 还要给 line（从 1 开始）");
                file = DebugHelpers.ResolveSourceFile(file, BaseDirs, File.Exists);
            }
            string condition = a.Str("condition") ?? "";
            int hitCount = a.Int("hitCount", 0, 0, 1000000);
            string message = a.Str("message");

            // Same location already has a breakpoint (the user's or an earlier call): update it instead of stacking a second one.
            var existing = file != null
                ? AllBreakpoints().FirstOrDefault(b => string.Equals(b.File, file, StringComparison.OrdinalIgnoreCase) && b.FileLine == line)
                : AllBreakpoints().FirstOrDefault(b => string.IsNullOrEmpty(b.File) && string.Equals(b.FunctionName, function, StringComparison.Ordinal));
            if (existing != null) existing.Delete();

            var added = _dte.Debugger.Breakpoints.Add(file == null ? function : "", file ?? "", file == null ? 1 : line, 1, condition,
                dbgBreakpointConditionType.dbgBreakpointConditionTypeWhenTrue, "", "", 1, "", hitCount,
                hitCount > 0 ? dbgHitCountType.dbgHitCountTypeEqual : dbgHitCountType.dbgHitCountTypeNone);
            var created = added.Cast<Breakpoint>().ToList();
            foreach (var b in created)
            {
                b.Tag = AgentTag;
                if (message != null && b is Breakpoint2 b2)
                {
                    // Tracepoint ("日志断点"): prints {expressions} to the Debug output and keeps running; read it with output.
                    b2.Message = message;
                    b2.BreakWhenHit = false;
                }
            }
            var all = AllBreakpoints();
            var items = new JArray(created.Select(b => BreakpointJson(b, all.IndexOf(b) + 1)));
            var o = new JObject { ["added"] = items, ["replaced"] = existing != null, ["total"] = all.Count };
            if (CurrentMode != DebugMode.Design && created.All(b => { try { return b.Children == null || b.Children.Count == 0; } catch (Exception) { return false; } }))
                o["hint"] = "断点还没绑定：模块的符号可能还没加载（DLL 没载入），或者这一行没有代码；加载后会自动生效";
            return o;
        }

        /// <summary>
        /// Data breakpoint ("这个值是谁改的"): stops when the bytes at an address change. Native data breakpoints live in the
        /// CPU's debug registers (x64: 4 at most, 1/2/4/8 bytes) and only while the process runs, so it needs a debug session and
        /// an address that is valid now: "this->Health" is taken as "&amp;(this->Health)".
        /// </summary>
        private JObject AddDataBreakpoint(DebugArgs a)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            if (CurrentMode != DebugMode.Break) throw new DebugControlException("数据断点要在停下时设（地址要能算出来）：先在用到这个值的地方停下");
            SelectContext(a);
            string data = a.Str("data");
            int size = a.Int("size", 4, 1, 8);
            if (size != 1 && size != 2 && size != 4 && size != 8) throw new DebugControlException("size 只能是 1、2、4、8（字节）");
            string address = data.StartsWith("&") || data.StartsWith("0x", StringComparison.OrdinalIgnoreCase) ? data : "&(" + data + ")";
            var probe = _dte.Debugger.GetExpression(address, false, 3000);
            if (probe == null || !probe.IsValidValue) throw new DebugControlException($"算不出 {address} 的地址：{probe?.Value}");
            var added = _dte.Debugger.Breakpoints.Add("", "", 1, 1, a.Str("condition") ?? "", dbgBreakpointConditionType.dbgBreakpointConditionTypeWhenTrue,
                                                      "", address, size, "", 0, dbgHitCountType.dbgHitCountTypeNone);
            var created = added.Cast<Breakpoint>().ToList();
            foreach (var b in created) b.Tag = AgentTag;
            var all = AllBreakpoints();
            return new JObject
            {
                ["added"] = new JArray(created.Select(b => BreakpointJson(b, all.IndexOf(b) + 1))),
                ["data"] = address,
                ["address"] = probe.Value,
                ["size"] = size,
                ["hint"] = "值一变就会停下（停在改它的那行之后）：continue 后用 wait 等；调试结束后数据断点会失效",
            };
        }

        /// <summary>Selected by index (from list), by file + line, or by function; several indexes allowed ("indexes").</summary>
        private List<Breakpoint> Select(DebugArgs a)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            var all = AllBreakpoints();
            var picked = new List<Breakpoint>();
            if (a.Has("indexes") && a.Raw["indexes"] is JArray idx)
            {
                foreach (var t in idx)
                {
                    int i = t.Type == JTokenType.Integer ? (int)t : 0;
                    if (i < 1 || i > all.Count) throw new DebugControlException($"没有第 {t} 个断点（共 {all.Count} 个，先 list 看编号）");
                    picked.Add(all[i - 1]);
                }
                return picked;
            }
            int? index = a.IntOrNull("index");
            if (index != null)
            {
                if (index < 1 || index > all.Count) throw new DebugControlException($"没有第 {index} 个断点（共 {all.Count} 个，先 list 看编号）");
                return new List<Breakpoint> { all[index.Value - 1] };
            }
            string file = a.Str("file");
            if (file != null)
            {
                int line = a.Int("line", 0, 0, 1000000);
                string name = Path.GetFileName(file);
                bool rooted = Path.IsPathRooted(file);
                picked = all.Where(b => !string.IsNullOrEmpty(b.File)
                                        && (rooted ? string.Equals(b.File, Path.GetFullPath(file), StringComparison.OrdinalIgnoreCase)
                                                   : b.File.EndsWith(file.Replace('/', '\\'), StringComparison.OrdinalIgnoreCase) || string.Equals(Path.GetFileName(b.File), name, StringComparison.OrdinalIgnoreCase))
                                        && (line <= 0 || b.FileLine == line)).ToList();
            }
            else if (a.Str("function") != null)
            {
                string fn = a.Str("function");
                picked = all.Where(b => string.IsNullOrEmpty(b.File) && string.Equals(b.FunctionName, fn, StringComparison.Ordinal)).ToList();
            }
            else throw new DebugControlException("用 index / indexes（list 里的编号）、file + line 或 function 指定断点");
            if (picked.Count == 0) throw new DebugControlException("没有对上的断点，先 list 看看");
            return picked;
        }

        private JObject RemoveBreakpoints(DebugArgs a)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            var picked = Select(a);
            var labels = picked.Select(BreakpointLabel).ToList();
            foreach (var b in picked) b.Delete();
            return new JObject { ["removed"] = new JArray(labels), ["total"] = AllBreakpoints().Count };
        }

        private JObject EnableBreakpoints(DebugArgs a)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            bool enabled = a.Bool("enabled", true);
            var picked = Select(a);
            foreach (var b in picked) b.Enabled = enabled;
            return new JObject { [enabled ? "enabled" : "disabled"] = new JArray(picked.Select(BreakpointLabel)) };
        }

        private JObject ClearBreakpoints(DebugArgs a)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            string scope = a.OneOf("scope", "agent", "agent", "all");
            var picked = AllBreakpoints().Where(b => scope == "all" || b.Tag == AgentTag).ToList();
            foreach (var b in picked) b.Delete();
            var o = new JObject { ["removed"] = picked.Count, ["total"] = AllBreakpoints().Count };
            if (scope == "agent" && picked.Count == 0 && AllBreakpoints().Count > 0)
                o["hint"] = "剩下的是用户自己下的断点；确实要全删用 scope=all";
            return o;
        }
    }
}
