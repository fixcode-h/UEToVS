using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using UEToVS.Contracts;
using UEToVS.Host.Logic.Generate;
using UEToVS.Host.Logic.Localization;
using UEToVS.Host.Logic.Refactoring;

namespace UEToVS.Host.Logic.Editing
{
    /// <summary>灯泡 / Alt+Enter 里 UE 规则波浪线（UE0002–UE0006）的修复参数：光标下的那条诊断（UE0006 合并同一位置的全部属性）。</summary>
    public sealed class UeRuleFix
    {
        public string Code { get; set; }
        public List<string> Args { get; set; } = new List<string>();
        public int Line { get; set; }
        public int Column { get; set; }
        public int Length { get; set; }

        /// <summary>UE0006：同一个 GetLifetimeReplicatedProps 上所有未注册的属性。</summary>
        public List<string> Properties { get; set; } = new List<string>();

        /// <summary>UE0002：加到 PublicDependencyModuleNames（否则 Private）。</summary>
        public bool IsPublic { get; set; }

        public string Arg(int i) => Args != null && i < Args.Count ? Args[i] : null;

        public static UeRuleFix From(CodeDiagnostic d) => new UeRuleFix
        {
            Code = d.Code, Args = d.Args ?? new List<string>(), Line = d.Line, Column = d.Column, Length = d.Length,
        };
    }

    /// <summary>
    /// 波浪线上的 UE 规则修复（灯泡和 Alt+Enter 同一套）。只给结果确定的修复：
    /// UE0002 加 Build.cs 依赖；UE0003 插入 GENERATED_BODY()；UE0004 把手写的 RPC / BlueprintNativeEvent 函数体改名为 _Implementation；
    /// UE0005 生成缺失的 OnRep 函数；UE0006 用 DOREPLIFETIME 注册。菜单打开时只做光标附近的文本检查（不发引擎请求），
    /// 真正的修改在点击时按当时的文本重新计算。
    /// </summary>
    public sealed class UeRuleFixProvider : IContextActionProvider
    {
        public string Id => "editor.ueRuleFixes";

        public IEnumerable<ContextActionItem> GetActions(RefactorContext c)
        {
            if (c?.Diagnostics == null || !c.IsProjectFile) yield break;
            var replicated = new List<string>();
            CodeDiagnostic registration = null;
            foreach (var d in c.Diagnostics)
            {
                if (d?.Args == null || d.Args.Count == 0 || string.IsNullOrEmpty(d.Args[0])) continue;
                switch (d.Code)
                {
                    case CodeDiagnosticCodes.MissingModuleDependency:
                        // Args: include, module, owner module, owner Build.cs
                        if (d.Args.Count < 4 || string.IsNullOrEmpty(d.Args[1]) || string.IsNullOrEmpty(d.Args[3])) break;
                        // 头文件里的 include 通常要 Public 依赖（别的模块包含这个头时也要能找到），源文件里 Private 就够了
                        foreach (bool isPublic in c.IsHeader ? new[] { true, false } : new[] { false, true })
                        {
                            var fix = UeRuleFix.From(d);
                            fix.IsPublic = isPublic;
                            yield return new ContextActionItem(UeRuleFixIds.Fix, LightBulbStrings.AddModuleDependency(d.Args[1], d.Args[2], isPublic),
                                isPublic == c.IsHeader ? 125 : 115, fix) { Group = EditorFeatureStrings.GroupFix };
                        }
                        break;
                    case CodeDiagnosticCodes.MissingGeneratedBody:
                        yield return new ContextActionItem(UeRuleFixIds.Fix, LightBulbStrings.InsertGeneratedBody(d.Args.Count > 1 ? d.Args[1] : d.Args[0]), 130,
                            UeRuleFix.From(d)) { Group = EditorFeatureStrings.GroupFix };
                        break;
                    case CodeDiagnosticCodes.GeneratedFunctionBody:
                        if (d.Args.Count < 2 || !UeRuleFixPlanner.RenameSpecifiers.Contains(d.Args[1])) break;
                        if (!UeRuleFixPlanner.IsOutOfLineName(LineAt(c, d.Line), d.Column)) break; // 类体里的内联定义：改名会把 UFUNCTION 本身改掉
                        yield return new ContextActionItem(UeRuleFixIds.Fix, LightBulbStrings.RenameToImplementation(d.Args[0]), 130, UeRuleFix.From(d))
                            { Group = EditorFeatureStrings.GroupFix };
                        break;
                    case CodeDiagnosticCodes.MissingRepNotify:
                        // Args: property, function, class, "missing" | "not-ufunction"；不是 UFUNCTION 的那种要改别处的声明，不自动修
                        if (d.Args.Count < 4 || d.Args[3] != "missing" || !EditorFixProvider.IsIdentifier(d.Args[1]) || !c.IsHeader) break;
                        yield return new ContextActionItem(UeRuleFixIds.Fix, LightBulbStrings.CreateRepNotify(d.Args[1]), 130, UeRuleFix.From(d))
                            { Group = EditorFeatureStrings.GroupFix };
                        break;
                    case CodeDiagnosticCodes.UnregisteredReplicatedProperty:
                        if (d.Args.Count < 2 || !EditorFixProvider.IsIdentifier(d.Args[0]) || !EditorFixProvider.IsIdentifier(d.Args[1])) break;
                        if (!replicated.Contains(d.Args[0])) replicated.Add(d.Args[0]);
                        registration = registration ?? d;
                        break;
                }
            }
            if (registration != null)
            {
                var fix = UeRuleFix.From(registration);
                fix.Properties = replicated;
                yield return new ContextActionItem(UeRuleFixIds.Fix, LightBulbStrings.RegisterReplicated(replicated), 130, fix) { Group = EditorFeatureStrings.GroupFix };
            }
        }

        private static string LineAt(RefactorContext c, int line)
        {
            string text = c.Text;
            if (text == null || line < 0) return null;
            int start = 0;
            for (int i = 0; i < line; i++)
            {
                start = text.IndexOf('\n', start);
                if (start < 0) return null;
                start++;
            }
            int end = text.IndexOf('\n', start);
            return (end < 0 ? text.Substring(start) : text.Substring(start, end - start)).TrimEnd('\r');
        }
    }

    public static class UeRuleFixIds
    {
        /// <summary>一个 action 处理全部 UE 规则修复（参数是 <see cref="UeRuleFix"/>）。</summary>
        public const string Fix = "uetovs.edit.fixUeRule";
    }

    /// <summary>UE 规则修复的文本计算（纯逻辑，可测）。结果是带 OldText 的 <see cref="TextEdit"/>，文件在计算后又被改过时整条跳过而不是改坏。</summary>
    public static class UeRuleFixPlanner
    {
        /// <summary>函数体由 UHT 生成、手写实现要叫 _Implementation 的说明符（BlueprintImplementableEvent 根本不能有函数体，不在其列）。</summary>
        public static readonly HashSet<string> RenameSpecifiers = new HashSet<string>(StringComparer.Ordinal) { "Server", "Client", "NetMulticast", "BlueprintNativeEvent" };

        /// <summary>列 <paramref name="column"/> 处的名字前面是 "Owner::"（类外定义）。</summary>
        public static bool IsOutOfLineName(string line, int column)
        {
            if (line == null || column < 2 || column > line.Length) return false;
            int k = column - 1;
            while (k >= 0 && (line[k] == ' ' || line[k] == '\t')) k--;
            return k >= 1 && line[k] == ':' && line[k - 1] == ':';
        }

        public static string NewLine(string text) => text != null && text.Contains("\r\n") ? "\r\n" : "\n";

        /// <summary>偏移量对应的 (行, 列)。</summary>
        public static (int Line, int Column) PositionOf(int[] starts, int offset)
        {
            int i = Array.BinarySearch(starts, offset);
            if (i < 0) i = ~i - 1;
            i = Math.Max(0, i);
            return (i, offset - starts[i]);
        }

        private static TextEdit Insert(string file, int[] starts, int offset, string text)
        {
            var (line, col) = PositionOf(starts, offset);
            return new TextEdit { FilePath = file, Line = line, Column = col, Length = 0, OldText = string.Empty, NewText = text };
        }

        private static string IndentOf(string text, int offset)
        {
            int ls = offset <= 0 ? 0 : text.LastIndexOf('\n', offset - 1) + 1;
            int e = ls;
            while (e < text.Length && (text[e] == ' ' || text[e] == '\t')) e++;
            return text.Substring(ls, e - ls);
        }

        // ============================================================ UE0003

        /// <summary>在类体的 '{' 下一行插入 GENERATED_BODY()（'{' 必须是那一行的最后一个字符）。</summary>
        public static List<TextEdit> GeneratedBody(string file, string text, int line, int column, out string problem)
        {
            problem = null;
            var starts = TextEditApplier.LineStarts(text ?? string.Empty);
            int off = TextEditApplier.OffsetOf(text ?? string.Empty, starts, line, column);
            if (off < 0) { problem = "stale"; return null; }
            string b = UeCodeGenerator.Blank(text);
            int open = -1;
            for (int i = off; i < b.Length; i++)
            {
                if (b[i] == '{') { open = i; break; }
                if (b[i] == ';' || b[i] == '}') break;
            }
            if (open < 0) { problem = "no-body"; return null; }
            int eol = b.IndexOf('\n', open);
            if (eol < 0) { problem = "no-newline"; return null; }
            if (b.Substring(open + 1, eol - open - 1).Trim().Length != 0) { problem = "brace-not-at-line-end"; return null; }
            int close = UeCodeGenerator.MatchBrace(b, open);
            if (close > 0 && Regex.IsMatch(b.Substring(open, close - open), @"\bGENERATED_\w*BODY\w*\s*\(")) { problem = "exists"; return null; }
            string nl = NewLine(text);
            return new List<TextEdit> { Insert(file, starts, eol + 1, IndentOf(text, open) + "\tGENERATED_BODY()" + nl) };
        }

        // ============================================================ UE0004

        /// <summary>把类外定义 AFoo::ServerDo 改名为 ServerDo_Implementation（已有 _Implementation 时不改，免得重复定义）。</summary>
        public static List<TextEdit> RenameToImplementation(string file, string text, int line, int column, string name, string specifier, out string problem)
        {
            problem = null;
            if (!RenameSpecifiers.Contains(specifier ?? string.Empty)) { problem = "specifier"; return null; }
            var starts = TextEditApplier.LineStarts(text ?? string.Empty);
            int off = TextEditApplier.OffsetOf(text ?? string.Empty, starts, line, column);
            if (off < 0 || string.IsNullOrEmpty(name) || off + name.Length > text.Length || string.CompareOrdinal(text, off, name, 0, name.Length) != 0) { problem = "stale"; return null; }
            int lineEnd = text.IndexOf('\n', off);
            string lineText = text.Substring(starts[line], (lineEnd < 0 ? text.Length : lineEnd) - starts[line]);
            if (!IsOutOfLineName(lineText, column)) { problem = "in-class"; return null; }
            string impl = name + "_Implementation";
            if (Regex.IsMatch(UeCodeGenerator.Blank(text), @"::\s*" + Regex.Escape(impl) + @"\s*\(")) { problem = "exists"; return null; }
            return new List<TextEdit> { new TextEdit { FilePath = file, Line = line, Column = column, Length = name.Length, OldText = name, NewText = impl } };
        }

        // ============================================================ UE0006

        /// <summary>
        /// 在光标处的 GetLifetimeReplicatedProps 定义（诊断在函数名上）末尾加 DOREPLIFETIME(Class, Prop)，没有包含
        /// Net/UnrealNetwork.h 时一并加上。
        /// </summary>
        public static List<TextEdit> RegisterReplicated(string file, string text, int line, int column, string cls, IList<string> props, out string problem)
        {
            problem = null;
            if (props == null || props.Count == 0 || string.IsNullOrEmpty(cls)) { problem = "nothing"; return null; }
            var starts = TextEditApplier.LineStarts(text ?? string.Empty);
            int off = TextEditApplier.OffsetOf(text ?? string.Empty, starts, line, column);
            if (off < 0) { problem = "stale"; return null; }
            string b = UeCodeGenerator.Blank(text);
            int paren = b.IndexOf('(', off);
            if (paren < 0) { problem = "no-body"; return null; }
            int pclose = UeCodeGenerator.MatchParen(b, paren);
            if (pclose < 0) { problem = "no-body"; return null; }
            int open = -1;
            for (int i = pclose + 1; i < b.Length; i++)
            {
                if (b[i] == '{') { open = i; break; }
                if (b[i] == ';' || b[i] == '}') break;
            }
            if (open < 0) { problem = "no-body"; return null; }
            int close = UeCodeGenerator.MatchBrace(b, open);
            if (close < 0) { problem = "no-body"; return null; }
            string body = b.Substring(open, close - open);
            var missing = props.Where(p => !Regex.IsMatch(body, @"\b(?:DOREPLIFETIME\w*|DISABLE_REPLICATED_\w*)\s*\(\s*\w+\s*,\s*" + Regex.Escape(p) + @"\b")).ToList();
            if (missing.Count == 0) { problem = "exists"; return null; }
            string nl = NewLine(text);
            string indent = IndentOf(text, open) + "\t";
            var sb = new StringBuilder();
            int lineStart = text.LastIndexOf('\n', Math.Max(0, close - 1)) + 1;
            int at;
            if (lineStart > open && text.Substring(lineStart, close - lineStart).Trim().Length == 0)
            {
                at = lineStart; // '}' 独占一行：插在它前面
                foreach (var p in missing) sb.Append(indent).Append("DOREPLIFETIME(").Append(cls).Append(", ").Append(p).Append(");").Append(nl);
            }
            else
            {
                at = close; // "{ Super::…; }" 写在一行里：在 '}' 前换行插入
                foreach (var p in missing) sb.Append(nl).Append(indent).Append("DOREPLIFETIME(").Append(cls).Append(", ").Append(p).Append(");");
                sb.Append(nl).Append(IndentOf(text, open));
            }
            var edits = new List<TextEdit> { Insert(file, starts, at, sb.ToString()) };
            var inc = UeCodeGenerator.EnsureInclude(text, "Net/UnrealNetwork.h", nl);
            if (inc != null) edits.Add(Insert(file, starts, inc.Offset, inc.Text));
            return edits;
        }

        // ============================================================ UE0005

        /// <summary>
        /// ReplicatedUsing 指向的 OnRep 函数不存在：在属性声明下面加 <c>UFUNCTION() void OnRep_X();</c>，在对应的 .cpp
        /// （有的话）末尾加空实现。诊断在头文件的属性名上。
        /// </summary>
        public static List<TextEdit> RepNotify(string header, string headerText, int line, int column, string function, string cls,
                                               string source, string sourceText, out string problem)
        {
            problem = null;
            if (!EditorFixProvider.IsIdentifier(function) || !EditorFixProvider.IsIdentifier(cls)) { problem = "name"; return null; }
            var starts = TextEditApplier.LineStarts(headerText ?? string.Empty);
            int off = TextEditApplier.OffsetOf(headerText ?? string.Empty, starts, line, column);
            if (off < 0) { problem = "stale"; return null; }
            string b = UeCodeGenerator.Blank(headerText);
            // 属性声明的结尾 ';'（跳过 "= {…}" / "{0}" 这类初始化里的括号）
            int semi = -1, depth = 0;
            for (int i = off; i < b.Length; i++)
            {
                char ch = b[i];
                if (ch == '(' || ch == '{' || ch == '[') depth++;
                else if (ch == ')' || ch == ']') depth--;
                else if (ch == '}') { if (--depth < 0) break; }
                else if (ch == ';' && depth == 0) { semi = i; break; }
            }
            if (semi < 0) { problem = "no-declaration-end"; return null; }
            if (Regex.IsMatch(b, @"(?<![\w:~])" + Regex.Escape(function) + @"\s*\(")) { problem = "exists"; return null; }
            string nl = NewLine(headerText);
            string indent = IndentOf(headerText, off);
            int eol = b.IndexOf('\n', semi);
            var edits = new List<TextEdit>();
            string decl = nl + indent + "UFUNCTION()" + nl + indent + "void " + function + "();" + nl;
            if (eol < 0) edits.Add(Insert(header, starts, headerText.Length, nl + decl));
            else edits.Add(Insert(header, starts, eol + 1, decl));
            if (source != null && sourceText != null &&
                !Regex.IsMatch(UeCodeGenerator.Blank(sourceText), @"\b" + Regex.Escape(cls) + @"\s*::\s*" + Regex.Escape(function) + @"\s*\("))
            {
                string snl = NewLine(sourceText);
                var app = UeCodeGenerator.AppendToSource(sourceText, snl, "void " + cls + "::" + function + "()" + snl + "{" + snl + "}");
                edits.Add(Insert(source, TextEditApplier.LineStarts(sourceText), app.Offset, app.Text));
            }
            return edits;
        }
    }
}
