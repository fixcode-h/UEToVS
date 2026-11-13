using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using UEToVS.Contracts;
using UEToVS.Host.Logic.Commands;
using UEToVS.Host.Logic.Generate;
using UEToVS.Host.Logic.Localization;
using UEToVS.Host.Logic.Refactoring;

namespace UEToVS.Host.Logic.Editing
{
    /// <summary>灯泡里新增的非修复类 action（参数见各 provider）。</summary>
    public static class LightBulbActionIds
    {
        /// <summary>跳到一个已知位置（参数 <see cref="SourceLocation"/>）。</summary>
        public const string GoToLocation = "uetovs.edit.goToLocation";

        /// <summary>打开 X.generated.h（参数是文件名，点击时才去索引里找）。</summary>
        public const string OpenGeneratedHeader = "uetovs.edit.openGeneratedHeader";
    }

    /// <summary>
    /// 灯泡 / Alt+Enter 的其余 provider：把原来只能走命令的生成、导航、翻译、包围按光标上下文放进同一个菜单。
    /// 都只读 <see cref="RefactorContext"/>（头文件文本是懒加载的那一份），不发引擎请求。
    /// </summary>
    public static class LightBulbProviders
    {
        /// <summary>
        /// 注册时传入的开关（设置里关掉的功能不出现在菜单里）。默认都开，单测不用管。
        /// </summary>
        public static IEnumerable<IContextActionProvider> Create(Func<bool> translationEnabled = null, Func<bool> surroundEnabled = null) => new IContextActionProvider[]
        {
            new GenerateProvider(),
            new NavigateProvider(),
            new TranslateProvider(translationEnabled ?? (() => true)),
            new SurroundProvider(surroundEnabled ?? (() => true)),
        };

        /// <summary>头文件里光标所在类的生成项：构造函数、GetLifetimeReplicatedProps、把普通函数包成 Server RPC。</summary>
        public sealed class GenerateProvider : IContextActionProvider
        {
            public string Id => "lightbulb.generate";

            public IEnumerable<ContextActionItem> GetActions(RefactorContext c)
            {
                if (c == null || !c.IsHeader || !c.IsProjectFile || c.CaretOffset < 0) yield break;
                string text = c.Text;
                if (string.IsNullOrEmpty(text)) yield break;
                var cls = UeCodeGenerator.FindClass(text, c.CaretOffset);
                if (cls == null) yield break;
                if (!UeCodeGenerator.DeclaresFunction(text, cls, cls.Name))
                    yield return new ContextActionItem(CommandCatalog.ActionIds.GenerateConstructor, LightBulbStrings.GenerateConstructor(cls.Name), 32)
                        { Group = RefactorStrings.GroupGenerate };
                var props = UeCodeGenerator.ReplicatedProperties(text, cls);
                if (props.Count > 0)
                    yield return new ContextActionItem(CommandCatalog.ActionIds.GenerateReplication, LightBulbStrings.GenerateReplication(props.Count), 34)
                        { Group = RefactorStrings.GroupGenerate };
                // 已经是 RPC 的声明由“生成 _Implementation / _Validate”负责，这里只给普通成员函数
                var f = UeCodeGenerator.FunctionAt(text, cls, c.CaretOffset);
                // 只在光标就在这个声明行上时给（FunctionAt 会往后找到下一个声明，光标在空白处也会命中）
                if (f != null && !string.IsNullOrEmpty(f.Name) && Regex.IsMatch(CaretLine(c) ?? string.Empty, @"\b" + Regex.Escape(f.Name) + @"\s*\(") && f.Name != cls.Name && !f.Name.StartsWith("~", StringComparison.Ordinal) && !RefactorProviders.IsUeGenerated(f.Name))
                {
                    string spec = f.Specifiers ?? string.Empty;
                    bool isRpc = UeCodeGenerator.HasSpecifier(spec, "Server") || UeCodeGenerator.HasSpecifier(spec, "Client") || UeCodeGenerator.HasSpecifier(spec, "NetMulticast");
                    string rpc = f.Name.StartsWith("Server", StringComparison.Ordinal) ? f.Name + "Rpc" : "Server" + f.Name;
                    if (!isRpc && !UeCodeGenerator.DeclaresFunction(text, cls, rpc))
                        yield return new ContextActionItem(CommandCatalog.ActionIds.GenerateRpc, LightBulbStrings.GenerateServerRpc(f.Name, rpc), 20)
                            { Group = RefactorStrings.GroupGenerate };
                }
            }
        }

        /// <summary>
        /// 光标在 RPC / BlueprintNativeEvent 声明上：直接跳到已有的 _Implementation / _Validate；
        /// 光标在 #include "X.generated.h" 或 GENERATED_BODY 行上：打开 X.generated.h。
        /// </summary>
        public sealed class NavigateProvider : IContextActionProvider
        {
            public string Id => "lightbulb.navigate";

            private static readonly Regex GeneratedInclude = new Regex(@"^\s*#\s*include\s*[""<]([^"">]*?([\w.-]+\.generated\.h))["">]", RegexOptions.Compiled);
            private static readonly Regex GeneratedBodyLine = new Regex(@"^\s*GENERATED_\w*BODY\w*\s*\(", RegexOptions.Compiled);

            public IEnumerable<ContextActionItem> GetActions(RefactorContext c)
            {
                if (c == null) yield break;
                var f = c.Function;
                var defs = c.Engine?.UeCounterpartDefinitions;
                if (f != null && !f.IsDefinition && defs != null)
                {
                    foreach (var d in defs.Where(d => d?.Location?.FilePath != null).OrderBy(d => d.Name, StringComparer.Ordinal).Take(2))
                        yield return new ContextActionItem(LightBulbActionIds.GoToLocation, LightBulbStrings.GoTo(d.Name), 28, d.Location)
                            { Group = LightBulbStrings.GroupNavigate };
                }
                if (!c.IsHeader) yield break;
                string line = CaretLine(c);
                if (line == null) yield break;
                var m = GeneratedInclude.Match(line);
                string gen = m.Success ? m.Groups[2].Value
                    : GeneratedBodyLine.IsMatch(line) ? System.IO.Path.GetFileNameWithoutExtension(c.FilePath ?? string.Empty) + ".generated.h" : null;
                if (!string.IsNullOrEmpty(gen) && gen != ".generated.h")
                    yield return new ContextActionItem(LightBulbActionIds.OpenGeneratedHeader, LightBulbStrings.OpenFile(gen), 27, gen)
                        { Group = LightBulbStrings.GroupNavigate };
            }
        }

        /// <summary>有选区时翻译选区；光标在注释行上时翻译这段注释。真正联网只在点击之后。</summary>
        public sealed class TranslateProvider : IContextActionProvider
        {
            private readonly Func<bool> _enabled;

            public TranslateProvider(Func<bool> enabled) => _enabled = enabled;

            public string Id => "lightbulb.translate";

            public IEnumerable<ContextActionItem> GetActions(RefactorContext c)
            {
                if (c == null || !_enabled()) yield break;
                if (c.HasSelection)
                {
                    yield return new ContextActionItem(CommandCatalog.ActionIds.TranslateSelection, LightBulbStrings.TranslateSelection, 10)
                        { Group = LightBulbStrings.GroupEdit };
                    yield break;
                }
                if (IsCommentLine(CaretLine(c)))
                    yield return new ContextActionItem(CommandCatalog.ActionIds.TranslateSelection, LightBulbStrings.TranslateComment, 10)
                        { Group = LightBulbStrings.GroupEdit };
            }
        }

        /// <summary>选中了多行时：Surround With（if / for / {} / #if / SCOPE_CYCLE_COUNTER…）。</summary>
        public sealed class SurroundProvider : IContextActionProvider
        {
            private readonly Func<bool> _enabled;

            public SurroundProvider(Func<bool> enabled) => _enabled = enabled;

            public string Id => "lightbulb.surround";

            public IEnumerable<ContextActionItem> GetActions(RefactorContext c)
            {
                if (c == null || !c.HasSelection || !c.IsProjectFile || !_enabled()) yield break;
                string text = c.Text;
                if (text == null || c.SelectionEnd > text.Length) yield break;
                if (text.IndexOf('\n', c.SelectionStart, c.SelectionEnd - c.SelectionStart) < 0) yield break; // 单行内的选区：包围没有意义
                yield return new ContextActionItem(EditorFeatureIds.SurroundWith, LightBulbStrings.SurroundWith, 15) { Group = LightBulbStrings.GroupEdit };
            }
        }

        /// <summary>// /// /* * 开头的行。</summary>
        public static bool IsCommentLine(string line)
        {
            if (line == null) return false;
            string t = line.TrimStart();
            return t.StartsWith("//", StringComparison.Ordinal) || t.StartsWith("/*", StringComparison.Ordinal) ||
                   t == "*" || t.StartsWith("* ", StringComparison.Ordinal) || t.StartsWith("*\t", StringComparison.Ordinal) || t.StartsWith("*/", StringComparison.Ordinal);
        }

        /// <summary>光标所在行的文本（不含换行）；文本未知时 null。</summary>
        public static string CaretLine(RefactorContext c)
        {
            string text = c?.Text;
            if (text == null) return null;
            int caret = c.CaretOffset;
            if (caret < 0 || caret > text.Length) return null;
            int start = caret == 0 ? 0 : text.LastIndexOf('\n', caret - 1) + 1;
            int end = text.IndexOf('\n', caret);
            if (end < 0) end = text.Length;
            return text.Substring(start, end - start).TrimEnd('\r');
        }
    }
}
