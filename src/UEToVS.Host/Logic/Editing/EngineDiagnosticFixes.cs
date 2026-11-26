using System;
using System.Collections.Generic;
using System.Linq;
using UEToVS.Contracts;
using UEToVS.Host.Logic.Localization;
using UEToVS.Host.Logic.Refactoring;

namespace UEToVS.Host.Logic.Editing
{
    /// <summary>
    /// v0.11.0 灯泡 / Alt+Enter：引擎随诊断一起算好的修复（<see cref="CodeDiagnostic.Fixes"/>，UE0017–UE0023）。修复只带“种类 + 参数”，
    /// 位置按光标下那条诊断在当前快照里的位置重新算：引擎算的时候文本可能已经又改过了。
    /// </summary>
    public sealed class EngineDiagnosticFixProvider : IContextActionProvider
    {
        public static readonly HashSet<string> Codes = new HashSet<string>(StringComparer.Ordinal)
        {
            CodeDiagnosticCodes.IncludeNotFound, CodeDiagnosticCodes.IncludeOfPrivateHeader, CodeDiagnosticCodes.GeneratedInclude,
            CodeDiagnosticCodes.IncludeSpelling, CodeDiagnosticCodes.SyntaxError, CodeDiagnosticCodes.MissingInclude, CodeDiagnosticCodes.UnknownIdentifier,
        };

        public string Id => "editor.engineDiagnosticFixes";

        public IEnumerable<ContextActionItem> GetActions(RefactorContext c)
        {
            if (c?.Diagnostics == null || !c.IsProjectFile) yield break;
            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (var d in c.Diagnostics)
            {
                if (d?.Fixes == null || !Codes.Contains(d.Code ?? string.Empty)) continue;
                int rank = 0;
                foreach (var f in d.Fixes)
                {
                    string arg = f?.Args != null && f.Args.Count > 0 ? f.Args[0] : null;
                    if (string.IsNullOrEmpty(arg) || !seen.Add(f.Kind + "\n" + arg)) continue;
                    string title = EngineDiagnosticFixPlanner.Title(f.Kind, arg);
                    if (title == null) continue;
                    var fix = UeRuleFix.From(d);
                    fix.Variant = f.Kind;
                    fix.Target = arg;
                    // 第一条建议排最前；拼写建议最多三条
                    yield return new ContextActionItem(UeRuleFixIds.Fix, title, 135 - rank++, fix) { Group = EditorFeatureStrings.GroupFix };
                }
            }
        }
    }

    /// <summary>引擎修复的文本计算（纯逻辑）。</summary>
    public static class EngineDiagnosticFixPlanner
    {
        public const string Replace = "replace";
        public const string AddInclude = "add-include";
        public const string InsertLine = "insert-line";

        public static string Title(string kind, string arg)
        {
            switch (kind)
            {
                case Replace: return LightBulbStrings.ReplaceWith(arg);
                case AddInclude: return LightBulbStrings.AddIncludeOf(arg);
                case InsertLine: return LightBulbStrings.InsertLine(arg);
                default: return null;
            }
        }

        /// <summary>
        /// "replace"：把诊断所在的那段文字换成参数；"insert-line"：在最后一个 #include 之后（没有时在 #pragma once 之后）插一行。
        /// "add-include" 由调用方按“添加 include”的规则放置，这里返回 null。
        /// </summary>
        public static List<TextEdit> Plan(string file, string text, UeRuleFix fix, out string problem)
        {
            problem = null;
            if (text == null || fix == null) { problem = "stale"; return null; }
            var lines = text.Split('\n');
            switch (fix.Variant)
            {
                case Replace:
                {
                    if (fix.Line < 0 || fix.Line >= lines.Length) { problem = "stale"; return null; }
                    string line = lines[fix.Line].TrimEnd('\r');
                    if (fix.Column < 0 || fix.Column + fix.Length > line.Length) { problem = "stale"; return null; }
                    string old = line.Substring(fix.Column, fix.Length);
                    if (old == fix.Target) { problem = "nothing"; return null; }
                    return new List<TextEdit> { new TextEdit { FilePath = file, Line = fix.Line, Column = fix.Column, Length = fix.Length, OldText = old, NewText = fix.Target } };
                }
                case InsertLine:
                {
                    if (lines.Any(l => l.Trim() == fix.Target)) { problem = "nothing"; return null; }
                    int after = -1;
                    for (int i = 0; i < lines.Length; i++)
                    {
                        string t = lines[i].TrimStart();
                        if (t.StartsWith("#include", StringComparison.Ordinal) || after < 0 && t.StartsWith("#pragma once", StringComparison.Ordinal)) after = i;
                    }
                    if (after < 0) { problem = "no-place"; return null; }
                    return new List<TextEdit> { TextEdit.Insert(file, after + 1, fix.Target + "\n") };
                }
                default:
                    problem = "unknown-rule";
                    return null;
            }
        }
    }
}
