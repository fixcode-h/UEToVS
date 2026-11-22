using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using Newtonsoft.Json.Linq;

namespace UEToVS.Host.Logic.Editing
{
    /// <summary>说明符知识库里的一项（UPROPERTY 的 EditAnywhere、meta 的 ClampMin…）。</summary>
    public sealed class SpecifierDef
    {
        public string Name { get; set; }
        public string Macro { get; set; }
        public bool IsMeta { get; set; }

        /// <summary>英文说明（和补全、Quick Info 用的同一句）。</summary>
        public string Doc { get; set; }

        /// <summary>中文说明。</summary>
        public string DocZh { get; set; }

        /// <summary>取值写法提示，例如 <c>="Category"</c>、<c>=(A, B)</c>；纯开关为 null。</summary>
        public string ValueHint { get; set; }

        public IReadOnlyList<string> Conflicts { get; set; } = Array.Empty<string>();

        public bool HasValue => ValueHint != null;

        public string Description(bool english) => english || string.IsNullOrEmpty(DocZh) ? Doc : DocZh;

        public override string ToString() => (IsMeta ? "meta:" : string.Empty) + Macro + "." + Name;
    }

    /// <summary>
    /// v0.9.12 说明符编辑器的知识库：读引擎同一份 UeMacros.json（宿主里作为嵌入资源链接进来，不另抄一份），
    /// 只取 UCLASS / USTRUCT / UPROPERTY / UFUNCTION 的说明符和 meta 键。JSON 里多出来的 "zh" 字段是中文说明。
    /// </summary>
    public sealed class SpecifierCatalog
    {
        private readonly Dictionary<string, List<SpecifierDef>> _specifiers = new Dictionary<string, List<SpecifierDef>>(StringComparer.Ordinal);
        private readonly Dictionary<string, List<SpecifierDef>> _meta = new Dictionary<string, List<SpecifierDef>>(StringComparer.Ordinal);

        public static SpecifierCatalog Parse(string json)
        {
            var c = new SpecifierCatalog();
            var root = JObject.Parse(json);
            Read(root["specifiers"] as JObject, false, c._specifiers);
            Read(root["metaKeys"] as JObject, true, c._meta);
            return c;
        }

        private static void Read(JObject section, bool meta, Dictionary<string, List<SpecifierDef>> target)
        {
            if (section == null) return;
            foreach (var p in section.Properties())
            {
                if (Array.IndexOf(ReflectionMacroText.EditableMacros, p.Name) < 0 || !(p.Value is JArray arr)) continue;
                var list = new List<SpecifierDef>();
                foreach (var e in arr.OfType<JObject>())
                {
                    string name = (string)e["name"];
                    // meta 本身由编辑器的 meta 区负责，不作为一个勾选项
                    if (string.IsNullOrEmpty(name) || (!meta && name == "meta")) continue;
                    list.Add(new SpecifierDef
                    {
                        Name = name, Macro = p.Name, IsMeta = meta, Doc = (string)e["doc"], DocZh = (string)e["zh"], ValueHint = (string)e["value"],
                        Conflicts = (e["conflicts"] as JArray)?.Select(x => (string)x).Where(x => !string.IsNullOrEmpty(x)).ToArray() ?? Array.Empty<string>(),
                    });
                }
                target[p.Name] = list;
            }
        }

        public IReadOnlyList<SpecifierDef> Specifiers(string macro) =>
            macro != null && _specifiers.TryGetValue(macro, out var l) ? l : (IReadOnlyList<SpecifierDef>)Array.Empty<SpecifierDef>();

        public IReadOnlyList<SpecifierDef> MetaKeys(string macro) =>
            macro != null && _meta.TryGetValue(macro, out var l) ? l : (IReadOnlyList<SpecifierDef>)Array.Empty<SpecifierDef>();

        /// <summary>按名字找（不区分大小写，和 UHT 一样）；不认识时 null。</summary>
        public SpecifierDef Find(string macro, string name, bool meta) =>
            (meta ? MetaKeys(macro) : Specifiers(macro)).FirstOrDefault(d => string.Equals(d.Name, name, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>宏参数里的一项：<c>EditAnywhere</c>（Value 为 null）或 <c>Category = "Combat"</c>（Value 是原文，含引号）。</summary>
    public sealed class SpecifierEntry
    {
        public SpecifierEntry(string key, string value)
        {
            Key = key;
            Value = value;
        }

        public string Key { get; }

        /// <summary>等号后面的原文（保留引号 / 括号）；纯开关为 null。</summary>
        public string Value { get; set; }

        public override string ToString() => Value == null ? Key : Key + "=" + Value;
    }

    /// <summary>编辑器发现的问题：两个互斥的说明符、RPC 缺 Reliable / Unreliable、要值的说明符没有值。</summary>
    public sealed class SpecifierProblem
    {
        public SpecifierProblem(string kind, string a, string b = null)
        {
            Kind = kind;
            A = a;
            B = b;
        }

        /// <summary>"conflict" | "rpc-reliability" | "missing-value"。</summary>
        public string Kind { get; }
        public string A { get; }
        public string B { get; }

        /// <summary>挡住“确定”的问题（UHT 会直接报错的）。</summary>
        public bool Blocking => Kind == "conflict" || Kind == "missing-value";

        public override string ToString() => Kind + ":" + A + (B != null ? "," + B : string.Empty);
    }

    /// <summary>
    /// 一个反射宏的参数表（顶层说明符 + meta=(…)）。解析时保留原文写法和顺序，知识库里没有的项原样留着；
    /// 改完后 <see cref="Format"/> 按原来的风格（"Key = Value" 还是 "Key=Value"）写回。纯逻辑。
    /// </summary>
    public sealed class SpecifierSet
    {
        public List<SpecifierEntry> Items { get; } = new List<SpecifierEntry>();
        public List<SpecifierEntry> Meta { get; } = new List<SpecifierEntry>();

        /// <summary>meta 在顶层的位置（写回时放回去）；原来没有 meta 时为 -1（有了就放最后）。</summary>
        public int MetaIndex { get; private set; } = -1;

        /// <summary>"Key = Value"（等号两边有空格）还是 "Key=Value"：跟原文。</summary>
        public bool SpacedEquals { get; set; } = true;

        /// <summary>meta 的键是 "meta" 还是 "Meta"：跟原文。</summary>
        public string MetaKeyword { get; private set; } = "meta";

        public static SpecifierSet Parse(string arguments)
        {
            var s = new SpecifierSet();
            if (string.IsNullOrWhiteSpace(arguments)) return s;
            bool sawEquals = false, spaced = false;
            foreach (var raw in SplitTopLevel(arguments))
            {
                string part = raw.Trim();
                if (part.Length == 0) continue;
                int eq = IndexOfTopLevel(part, '=');
                if (eq < 0)
                {
                    s.Items.Add(new SpecifierEntry(part, null));
                    continue;
                }
                if (!sawEquals)
                {
                    sawEquals = true;
                    spaced = eq > 0 && char.IsWhiteSpace(part[eq - 1]);
                }
                string key = part.Substring(0, eq).Trim();
                string value = part.Substring(eq + 1).Trim();
                if (string.Equals(key, "meta", StringComparison.OrdinalIgnoreCase))
                {
                    s.MetaKeyword = key;
                    s.MetaIndex = s.Items.Count;
                    if (value.StartsWith("(", StringComparison.Ordinal) && value.EndsWith(")", StringComparison.Ordinal)) value = value.Substring(1, value.Length - 2);
                    foreach (var m in SplitTopLevel(value))
                    {
                        string mp = m.Trim();
                        if (mp.Length == 0) continue;
                        int meq = IndexOfTopLevel(mp, '=');
                        s.Meta.Add(meq < 0 ? new SpecifierEntry(mp, null) : new SpecifierEntry(mp.Substring(0, meq).Trim(), mp.Substring(meq + 1).Trim()));
                    }
                    continue;
                }
                s.Items.Add(new SpecifierEntry(key, value));
            }
            if (sawEquals) s.SpacedEquals = spaced;
            return s;
        }

        private List<SpecifierEntry> ListOf(bool meta) => meta ? Meta : Items;

        public SpecifierEntry Get(string key, bool meta) =>
            ListOf(meta).FirstOrDefault(e => string.Equals(e.Key, key, StringComparison.OrdinalIgnoreCase));

        public bool Has(string key, bool meta) => Get(key, meta) != null;

        /// <summary>加上或改值（已有的保留原位置和原拼写）。</summary>
        public void Set(string key, string value, bool meta)
        {
            var e = Get(key, meta);
            if (e != null) e.Value = value;
            else ListOf(meta).Add(new SpecifierEntry(key, value));
        }

        public void Remove(string key, bool meta) => ListOf(meta).RemoveAll(e => string.Equals(e.Key, key, StringComparison.OrdinalIgnoreCase));

        public SpecifierSet Clone()
        {
            var c = new SpecifierSet { SpacedEquals = SpacedEquals, MetaIndex = MetaIndex, MetaKeyword = MetaKeyword };
            c.Items.AddRange(Items.Select(e => new SpecifierEntry(e.Key, e.Value)));
            c.Meta.AddRange(Meta.Select(e => new SpecifierEntry(e.Key, e.Value)));
            return c;
        }

        /// <summary>写回括号里的文本：<c>EditAnywhere, Category = "X", meta = (ClampMin = "0")</c>。</summary>
        public string Format()
        {
            string eq = SpacedEquals ? " = " : "=";
            var parts = Items.Select(e => e.Value == null ? e.Key : e.Key + eq + e.Value).ToList();
            if (Meta.Count > 0)
            {
                string meta = MetaKeyword + eq + "(" + string.Join(", ", Meta.Select(e => e.Value == null ? e.Key : e.Key + eq + e.Value)) + ")";
                int at = MetaIndex < 0 || MetaIndex > parts.Count ? parts.Count : MetaIndex;
                parts.Insert(at, meta);
            }
            return string.Join(", ", parts);
        }

        /// <summary>
        /// 对话框里输入的值按知识库的写法提示补上引号 / 括号：<c>="Category"</c> 的值不带引号时加引号，
        /// <c>=(A, ...)</c> 的值不带括号时加括号；其它（函数名、数字、true）原样。空白返回空串。
        /// </summary>
        public static string NormalizeValue(string hint, string input)
        {
            string v = (input ?? string.Empty).Trim();
            if (v.Length == 0) return string.Empty;
            string h = (hint ?? string.Empty).TrimStart('=').Trim();
            if (h.StartsWith("\"", StringComparison.Ordinal) && !v.StartsWith("\"", StringComparison.Ordinal))
                return "\"" + v.Replace("\\", "\\\\").Replace("\"", "\\\"") + "\"";
            if (h.StartsWith("(", StringComparison.Ordinal) && !v.StartsWith("(", StringComparison.Ordinal)) return "(" + v + ")";
            return v;
        }

        /// <summary>对话框里显示用：去掉外层引号（转义还原）。</summary>
        public static string DisplayValue(string value)
        {
            if (value == null) return string.Empty;
            string v = value.Trim();
            if (v.Length >= 2 && v[0] == '"' && v[v.Length - 1] == '"') return v.Substring(1, v.Length - 2).Replace("\\\"", "\"").Replace("\\\\", "\\");
            return v;
        }

        /// <summary>按知识库检查：互斥的说明符（每对只报一次）、要值却没值、RPC 没写 Reliable / Unreliable。</summary>
        public List<SpecifierProblem> Check(SpecifierCatalog catalog, string macro)
        {
            var list = new List<SpecifierProblem>();
            if (catalog == null) return list;
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var e in Items)
            {
                var def = catalog.Find(macro, e.Key, false);
                if (def == null) continue;
                foreach (var other in def.Conflicts)
                {
                    var o = Get(other, false);
                    if (o == null) continue;
                    string a = def.Name, b = catalog.Find(macro, o.Key, false)?.Name ?? o.Key;
                    string pair = string.CompareOrdinal(a, b) < 0 ? a + "|" + b : b + "|" + a;
                    if (seen.Add(pair)) list.Add(new SpecifierProblem("conflict", a, b));
                }
                if (def.HasValue && string.IsNullOrWhiteSpace(e.Value) && !IsOptionalValue(def)) list.Add(new SpecifierProblem("missing-value", def.Name));
            }
            foreach (var e in Meta)
            {
                var def = catalog.Find(macro, e.Key, true);
                if (def != null && def.HasValue && string.IsNullOrWhiteSpace(e.Value) && !IsOptionalValue(def)) list.Add(new SpecifierProblem("missing-value", "meta." + def.Name));
            }
            if (macro == "UFUNCTION")
            {
                string rpc = new[] { "Server", "Client", "NetMulticast" }.FirstOrDefault(r => Has(r, false));
                if (rpc != null && !Has("Reliable", false) && !Has("Unreliable", false)) list.Add(new SpecifierProblem("rpc-reliability", rpc));
            }
            return list;
        }

        /// <summary>
        /// 写法提示里的值是可写可不写的：UFUNCTION 的 BlueprintGetter / BlueprintSetter 是开关（没有值提示），
        /// meta 里的 "=true" 类提示（AllowPrivateAccess、ExposeOnSpawn…）单写键名也行。
        /// </summary>
        private static bool IsOptionalValue(SpecifierDef def)
        {
            string h = (def.ValueHint ?? string.Empty).TrimStart('=').Trim().Trim('"');
            return h == "true" || h == "false";
        }

        // ---- 和 UhtRules 一样的顶层切分（引号里、括号里的逗号不算）

        private static IEnumerable<string> SplitTopLevel(string text)
        {
            int depth = 0, start = 0;
            bool quote = false;
            for (int i = 0; i < text.Length; i++)
            {
                char c = text[i];
                if (c == '\\' && quote) { i++; continue; }
                if (c == '"') quote = !quote;
                if (quote) continue;
                if (c == '(' || c == '<' || c == '[' || c == '{') depth++;
                else if ((c == ')' || c == '>' || c == ']' || c == '}') && depth > 0) depth--;
                else if (c == ',' && depth == 0)
                {
                    yield return text.Substring(start, i - start);
                    start = i + 1;
                }
            }
            yield return text.Substring(start);
        }

        private static int IndexOfTopLevel(string text, char wanted)
        {
            int depth = 0;
            bool quote = false;
            for (int i = 0; i < text.Length; i++)
            {
                char c = text[i];
                if (c == '\\' && quote) { i++; continue; }
                if (c == '"') quote = !quote;
                if (quote) continue;
                if (c == '(' || c == '<' || c == '[' || c == '{') depth++;
                else if ((c == ')' || c == '>' || c == ']' || c == '}') && depth > 0) depth--;
                else if (c == wanted && depth == 0) return i;
            }
            return -1;
        }
    }

    public static class SpecifierEditorIds
    {
        /// <summary>打开说明符编辑器（灯泡里参数是 <see cref="ReflectionMacroCall"/>；命令 / Find Action 里按光标找）。</summary>
        public const string Edit = "uetovs.edit.specifiers";
    }

    /// <summary>光标在 UPROPERTY / UFUNCTION / UCLASS / USTRUCT 上（宏名或括号里）时，灯泡和 Alt+Enter 里给“编辑说明符…”。</summary>
    public sealed class SpecifierEditorProvider : UEToVS.Host.Logic.Refactoring.IContextActionProvider
    {
        public string Id => "editor.specifiers";

        public IEnumerable<UEToVS.Host.Logic.Refactoring.ContextActionItem> GetActions(UEToVS.Host.Logic.Refactoring.RefactorContext c)
        {
            if (c == null || !c.IsProjectFile || c.CaretOffset < 0 || c.HasSelection) yield break;
            // FindAt 只看光标前后各 4000 字，大文件里也不会全文扫描
            var call = ReflectionMacroText.FindAt(c.Text, c.CaretOffset);
            if (call == null) yield break;
            yield return new UEToVS.Host.Logic.Refactoring.ContextActionItem(SpecifierEditorIds.Edit, UEToVS.Host.Logic.Localization.CodingAssistStrings.EditSpecifiers(call.Name), 60, call)
                { Group = UEToVS.Host.Logic.Localization.LightBulbStrings.GroupEdit };
        }
    }

    /// <summary>说明符编辑器的改写：把宏括号里的文本整体换成新的（一次编辑，一次撤销）。</summary>
    public static class SpecifierEditorPlanner
    {
        /// <summary>
        /// 计算替换括号里文本的那一条编辑；文本没变时 null。<paramref name="call"/> 来自 <see cref="ReflectionMacroText.FindAt"/>，
        /// 用的是同一份文本。
        /// </summary>
        public static UEToVS.Host.Logic.Refactoring.TextEdit Edit(string file, string text, ReflectionMacroCall call, SpecifierSet edited)
        {
            if (call == null || edited == null || text == null) return null;
            string now = edited.Format();
            if (string.Equals(now, call.Arguments.Trim(), StringComparison.Ordinal)) return null;
            var starts = UEToVS.Host.Logic.Refactoring.TextEditApplier.LineStarts(text);
            var (line, col) = UeRuleFixPlanner.PositionOf(starts, call.OpenParen + 1);
            return new UEToVS.Host.Logic.Refactoring.TextEdit
            {
                FilePath = file, Line = line, Column = col, Length = call.Arguments.Length, OldText = call.Arguments, NewText = now,
            };
        }
    }
}
