using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;
using UEToVS.Contracts;
using UEToVS.Host.Logic.Generate;
using UEToVS.Host.Logic.Localization;
using UEToVS.Host.Logic.Refactoring;

namespace UEToVS.Host.Logic.Editing
{
    /// <summary>UE0009 / UE0010 修复的几种改法（<see cref="UeRuleFix.Variant"/>）。</summary>
    public static class UhtTypeFixVariants
    {
        /// <summary>UE0009 结构体 / 基础类型指针属性：去掉 '*' 改成按值存。</summary>
        public const string ByValue = "by-value";

        /// <summary>UE0009：去掉 UPROPERTY()，成员留着但不再参与反射（TSharedPtr / std:: 成员的常见做法）。</summary>
        public const string RemoveUProperty = "remove-uproperty";

        /// <summary>UE0010 指针参数：改成 const T&amp;（输入）。</summary>
        public const string ConstRef = "const-ref";

        /// <summary>UE0010 指针参数：改成 T&amp;（蓝图里是输出引脚）。</summary>
        public const string Ref = "ref";

        /// <summary>UE0010 蓝图不支持的整数：换成 int32 / int64（<see cref="UeRuleFix.Arg"/> 里带着目标类型）。</summary>
        public const string BlueprintNumber = "blueprint-number";

        /// <summary>UE0010 UHT 根本不认的参数类型（TSharedPtr、TFunction、std::…）：去掉 UFUNCTION()。</summary>
        public const string RemoveUFunction = "remove-ufunction";
    }

    /// <summary>
    /// v0.9.12 灯泡 B 项：UE0009（UPROPERTY 类型 UHT 不支持）和 UE0010（UFUNCTION 参数类型 UHT / 蓝图不支持）的修复。
    /// 只给改法明确的几种：指针改按值 / 引用、蓝图不支持的整数换成 int32 / int64、去掉反射宏。菜单里只看诊断参数，
    /// 不读文本；真正的改动在点击时按当时的文本重算（<see cref="UhtTypeFixPlanner"/>）。
    /// </summary>
    public sealed class UhtTypeFixProvider : IContextActionProvider
    {
        public string Id => "editor.uhtTypeFixes";

        public IEnumerable<ContextActionItem> GetActions(RefactorContext c)
        {
            if (c?.Diagnostics == null || !c.IsProjectFile) yield break;
            foreach (var d in c.Diagnostics)
            {
                if (d?.Args == null || d.Args.Count < 2 || string.IsNullOrEmpty(d.Args[0])) continue;
                string type = d.Args[1] ?? string.Empty;
                if (d.Code == CodeDiagnosticCodes.UnsupportedPropertyType)
                {
                    // Args: property, normalized type
                    if (UhtTypeFixPlanner.IsSimplePointer(type))
                        yield return Item(d, UhtTypeFixVariants.ByValue, CodingAssistStrings.FixByValue(d.Args[0], UhtTypeFixPlanner.Pointee(type)), 128);
                    yield return Item(d, UhtTypeFixVariants.RemoveUProperty, CodingAssistStrings.FixRemoveUProperty(d.Args[0]), 110);
                }
                else if (d.Code == CodeDiagnosticCodes.UnsupportedParameterType)
                {
                    // Args: parameter, normalized type, function, kind ("pointer" | "unsupported" | "blueprint")
                    string kind = d.Args.Count > 3 ? d.Args[3] : null;
                    string function = d.Args.Count > 2 ? d.Args[2] : null;
                    if (kind == "blueprint")
                    {
                        string to = UhtTypeFixPlanner.BlueprintNumberFor(type);
                        if (to != null)
                            yield return Item(d, UhtTypeFixVariants.BlueprintNumber, CodingAssistStrings.FixNumber(d.Args[0], type.TrimEnd('&'), to), 128, to);
                    }
                    else if (kind == "pointer" && UhtTypeFixPlanner.IsSimplePointer(type))
                    {
                        string pointee = UhtTypeFixPlanner.Pointee(type);
                        yield return Item(d, UhtTypeFixVariants.ConstRef, CodingAssistStrings.FixToReference(d.Args[0], "const " + pointee + "&"), 128);
                        yield return Item(d, UhtTypeFixVariants.Ref, CodingAssistStrings.FixToReference(d.Args[0], pointee + "&"), 120);
                    }
                    else if (kind == "unsupported" && EditorFixProvider.IsIdentifier(function))
                        yield return Item(d, UhtTypeFixVariants.RemoveUFunction, CodingAssistStrings.FixRemoveUFunction(function), 105);
                }
            }
        }

        private static ContextActionItem Item(CodeDiagnostic d, string variant, string title, int priority, string extra = null)
        {
            var fix = UeRuleFix.From(d);
            fix.Variant = variant;
            fix.Target = extra;
            return new ContextActionItem(UeRuleFixIds.Fix, title, priority, fix) { Group = EditorFeatureStrings.GroupFix };
        }
    }

    /// <summary>UE0009 / UE0010 修复的文本计算（纯逻辑）。诊断在属性名 / 参数名上；改动带 OldText，文件改过就整条跳过。</summary>
    public static class UhtTypeFixPlanner
    {
        private static readonly Regex SimplePointer = new Regex(@"^[A-Za-z_][A-Za-z0-9_]*\*$", RegexOptions.Compiled | RegexOptions.CultureInvariant);

        /// <summary>"FFoo*" / "int32*" 这种单层指针（不含引用、模板、多级指针）。</summary>
        public static bool IsSimplePointer(string type) => type != null && SimplePointer.IsMatch(type);

        public static string Pointee(string type) => type?.TrimEnd('*');

        /// <summary>蓝图不支持的整数换成什么：uint32 / uint64 换 int64（不丢范围），其余换 int32；不认识的返回 null。</summary>
        public static string BlueprintNumberFor(string type)
        {
            switch ((type ?? string.Empty).TrimEnd('&'))
            {
                case "int8":
                case "int16":
                case "uint16": return "int32";
                case "uint32":
                case "uint64": return "int64";
                default: return null;
            }
        }

        public static List<TextEdit> Plan(string file, string text, UeRuleFix fix, out string problem)
        {
            problem = null;
            if (fix == null || string.IsNullOrEmpty(text)) { problem = "stale"; return null; }
            string name = fix.Arg(0);
            string type = fix.Arg(1) ?? string.Empty;
            var starts = TextEditApplier.LineStarts(text);
            int off = TextEditApplier.OffsetOf(text, starts, fix.Line, fix.Column);
            bool atName = off >= 0 && !string.IsNullOrEmpty(name) && off + name.Length <= text.Length && string.CompareOrdinal(text, off, name, 0, name.Length) == 0;
            switch (fix.Variant)
            {
                case UhtTypeFixVariants.RemoveUProperty:
                case UhtTypeFixVariants.RemoveUFunction:
                {
                    if (off < 0) { problem = "stale"; return null; }
                    string macro = fix.Variant == UhtTypeFixVariants.RemoveUProperty ? "UPROPERTY" : "UFUNCTION";
                    var call = ReflectionMacroText.FindBefore(text, off, macro);
                    if (call == null) { problem = "no-macro"; return null; }
                    var (start, length) = ReflectionMacroText.RemovalSpan(text, call);
                    return new List<TextEdit> { Edit(file, starts, start, text.Substring(start, length), string.Empty) };
                }
                case UhtTypeFixVariants.ByValue:
                case UhtTypeFixVariants.ConstRef:
                case UhtTypeFixVariants.Ref:
                {
                    if (!atName) { problem = "stale"; return null; }
                    if (!IsSimplePointer(type)) { problem = "type"; return null; }
                    int regionStart = RegionStart(text, off, fix.Code == CodeDiagnosticCodes.UnsupportedPropertyType);
                    string region = UeCodeGenerator.Blank(text.Substring(regionStart, off - regionStart));
                    int star = region.LastIndexOf('*');
                    if (star < 0 || region.IndexOf('*') != star || region.IndexOf('&') >= 0) { problem = "type"; return null; }
                    string pointee = Pointee(type);
                    var head = Regex.Match(region.Substring(0, star), @"\b" + Regex.Escape(pointee) + @"\s*$");
                    if (!head.Success) { problem = "type"; return null; }
                    int starAt = regionStart + star;
                    var edits = new List<TextEdit>();
                    if (fix.Variant == UhtTypeFixVariants.ByValue)
                    {
                        // "FFoo* Ptr" → "FFoo Ptr"；"FFoo *Ptr" → "FFoo Ptr"；"FFoo*Ptr" → "FFoo Ptr"
                        bool spaceBefore = star > 0 && char.IsWhiteSpace(region[star - 1]);
                        if (starAt + 1 == off) edits.Add(Edit(file, starts, starAt, "*", spaceBefore ? string.Empty : " "));
                        else if (spaceBefore && text[starAt + 1] == ' ') edits.Add(Edit(file, starts, starAt, "* ", string.Empty));
                        else edits.Add(Edit(file, starts, starAt, "*", string.Empty));
                        return edits;
                    }
                    edits.Add(Edit(file, starts, starAt, "*", "&"));
                    if (fix.Variant == UhtTypeFixVariants.ConstRef && !Regex.IsMatch(region.Substring(0, star), @"\bconst\b"))
                        edits.Add(Edit(file, starts, regionStart + head.Index, string.Empty, "const "));
                    return edits;
                }
                case UhtTypeFixVariants.BlueprintNumber:
                {
                    if (!atName) { problem = "stale"; return null; }
                    string from = type.TrimEnd('&');
                    string to = fix.Target ?? BlueprintNumberFor(type);
                    if (to == null) { problem = "type"; return null; }
                    int regionStart = RegionStart(text, off, false);
                    string region = UeCodeGenerator.Blank(text.Substring(regionStart, off - regionStart));
                    var matches = Regex.Matches(region, @"\b" + Regex.Escape(from) + @"\b");
                    if (matches.Count != 1) { problem = "type"; return null; }
                    return new List<TextEdit> { Edit(file, starts, regionStart + matches[0].Index, from, to) };
                }
                default:
                    problem = "unknown-rule";
                    return null;
            }
        }

        /// <summary>
        /// 类型文本从哪开始：参数是同一层的 '(' 或 ',' 之后；属性是 UPROPERTY(…) 之后（找不到就从上一个 ';' '{' '}' 之后）。
        /// </summary>
        private static int RegionStart(string text, int nameOffset, bool property)
        {
            if (property)
            {
                var call = ReflectionMacroText.FindBefore(text, nameOffset, "UPROPERTY");
                if (call != null) return call.End;
            }
            int from = Math.Max(0, nameOffset - 2000);
            string b = UeCodeGenerator.Blank(text.Substring(from, nameOffset - from));
            int depth = 0;
            for (int i = b.Length - 1; i >= 0; i--)
            {
                char ch = b[i];
                if (ch == '>' || ch == ')') depth++;
                else if (ch == '<' || ch == '(')
                {
                    if (depth == 0) return from + i + 1;
                    depth--;
                }
                else if (depth == 0 && (ch == ',' || ch == ';' || ch == '{' || ch == '}')) return from + i + 1;
            }
            return from;
        }

        private static TextEdit Edit(string file, int[] starts, int offset, string oldText, string newText)
        {
            var (line, col) = UeRuleFixPlanner.PositionOf(starts, offset);
            return new TextEdit { FilePath = file, Line = line, Column = col, Length = oldText.Length, OldText = oldText, NewText = newText };
        }
    }
}
