using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using UEToVS.UI.Logic;

namespace UEToVS.Host.Logic.Fonts
{
    /// <summary>Whether a VS font category shows code (monospace) or interface text.</summary>
    public enum VsFontRole
    {
        Code = 0,
        Ui = 1,
    }

    /// <summary>One Tools &gt; Options &gt; Fonts and Colors category whose font UEToVS can manage.</summary>
    public sealed class VsFontCategory
    {
        public VsFontCategory(string id, string guid, string zh, string en, VsFontRole role, FontChoice defaultFamily, int defaultSize)
        {
            Id = id;
            Guid = new Guid(guid);
            TitleZh = zh;
            TitleEn = en;
            Role = role;
            DefaultFamily = defaultFamily;
            DefaultSize = defaultSize;
        }

        /// <summary>Stable id used in settings keys ("fonts.vs.&lt;id&gt;.family") and the backup file.</summary>
        public string Id { get; }

        /// <summary>Category GUID passed to IVsFontAndColorStorage.OpenCategory.</summary>
        public Guid Guid { get; }

        public string TitleZh { get; }
        public string TitleEn { get; }
        public VsFontRole Role { get; }

        /// <summary>Default family: <see cref="FontChoice.SameAsCode"/> / <see cref="FontChoice.SameAsUi"/> / <see cref="FontChoice.Keep"/>.</summary>
        public FontChoice DefaultFamily { get; }

        /// <summary>Default size in points; 0 = keep VS's size.</summary>
        public int DefaultSize { get; }

        public string Title(bool english) => english ? TitleEn : TitleZh;
        public override string ToString() => Id;
    }

    /// <summary>
    /// The VS font categories UEToVS manages (设置 > 外观 > 字体 replaces Tools &gt; Options &gt; Fonts and Colors for them).
    /// GUIDs are Microsoft.VisualStudio.Shell.Interop.FontsAndColorsCategory (Microsoft.VisualStudio.Shell.Interop.8.0).
    /// Defaults follow the three bundled fonts: code-like categories use the 代码字体 (Maple Mono NL), interface-like
    /// ones the 英文字体 (IBM Plex Sans; VS takes one family per category, so CJK there falls back through Windows).
    /// </summary>
    public static class VsFontCategories
    {
        public const int MinSize = 6;
        public const int MaxSize = 48;

        /// <summary>Text Editor default size (points) when UEToVS manages it.</summary>
        public const int DefaultEditorSize = 10;

        public static readonly VsFontCategory TextEditor = new VsFontCategory("textEditor", "A27B4E24-A735-4D1D-B8E7-9716E1E3D8E0",
            "文本编辑器", "Text Editor", VsFontRole.Code, FontChoice.SameAsCode, DefaultEditorSize);

        public static readonly VsFontCategory OutputWindow = new VsFontCategory("output", "9973EFDF-317D-431C-8BC1-5E88CBFD4F7F",
            "输出窗口", "Output Window", VsFontRole.Code, FontChoice.SameAsCode, 0);

        public static readonly VsFontCategory FindResults = new VsFontCategory("findResults", "5C48B2CB-0366-4FBF-9786-0BB37E945687",
            "查找结果", "Find Results", VsFontRole.Code, FontChoice.SameAsCode, 0);

        public static readonly VsFontCategory CommandWindow = new VsFontCategory("commandWindow", "EE1BE240-4E81-4BEB-8EEA-54322B6B1BF5",
            "命令窗口", "Command Window", VsFontRole.Code, FontChoice.SameAsCode, 0);

        public static readonly VsFontCategory ImmediateWindow = new VsFontCategory("immediateWindow", "6BB65C5A-2F31-4BDE-9F48-8A38DC0C63E7",
            "即时窗口", "Immediate Window", VsFontRole.Code, FontChoice.SameAsCode, 0);

        public static readonly VsFontCategory Environment = new VsFontCategory("environment", "1F987C00-E7C4-4869-8A17-23FD602268B0",
            "环境（对话框和工具窗口）", "Environment (dialogs and tool windows)", VsFontRole.Ui, FontChoice.SameAsUi, 0);

        public static readonly VsFontCategory StatementCompletion = new VsFontCategory("statementCompletion", "C1614BB1-734F-4A31-BD42-5AE6275E16D2",
            "语句完成", "Statement Completion", VsFontRole.Ui, FontChoice.SameAsUi, 0);

        public static readonly VsFontCategory Tooltip = new VsFontCategory("tooltip", "A9A5637F-B2A8-422E-8FB5-DFB4625F0111",
            "编辑器工具提示", "Editor Tooltip", VsFontRole.Ui, FontChoice.SameAsUi, 0);

        public static readonly VsFontCategory Printer = new VsFontCategory("printer", "47724E70-AF55-48FB-A928-BB161C1D0C05",
            "打印机", "Printer", VsFontRole.Code, FontChoice.Keep, 0);

        public static IReadOnlyList<VsFontCategory> All { get; } = new[]
        {
            TextEditor, OutputWindow, FindResults, CommandWindow, ImmediateWindow, Environment, StatementCompletion, Tooltip, Printer,
        };

        public static VsFontCategory Find(string id) =>
            All.FirstOrDefault(c => string.Equals(c.Id, id, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>The user's family + size for one category.</summary>
    public readonly struct VsFontSetting : IEquatable<VsFontSetting>
    {
        public VsFontSetting(FontChoice family, int size)
        {
            Family = family;
            Size = size;
        }

        public FontChoice Family { get; }

        /// <summary>Points; 0 = keep VS's size.</summary>
        public int Size { get; }

        public bool Equals(VsFontSetting other) => Family.Equals(other.Family) && Size == other.Size;
        public override bool Equals(object obj) => obj is VsFontSetting o && Equals(o);
        public override int GetHashCode() => Family.GetHashCode() * 31 + Size;
    }

    /// <summary>
    /// The per-category settings in one string (HostSettings.VsFonts): "textEditor=code@10;environment=ui@0".
    /// Only categories that differ from their default are written; unknown ids are kept out; never throws.
    /// </summary>
    public static class VsFontSettingsText
    {
        public static VsFontSetting Default(VsFontCategory c) => new VsFontSetting(c.DefaultFamily, c.DefaultSize);

        public static Dictionary<string, VsFontSetting> Parse(string text)
        {
            var map = new Dictionary<string, VsFontSetting>(StringComparer.OrdinalIgnoreCase);
            foreach (var c in VsFontCategories.All) map[c.Id] = Default(c);
            if (string.IsNullOrWhiteSpace(text)) return map;
            foreach (var part in text.Split(';'))
            {
                int eq = part.IndexOf('=');
                if (eq <= 0) continue;
                var cat = VsFontCategories.Find(part.Substring(0, eq).Trim());
                if (cat == null) continue;
                string value = part.Substring(eq + 1);
                int at = value.LastIndexOf('@');
                int size = cat.DefaultSize;
                if (at >= 0)
                {
                    if (!int.TryParse(value.Substring(at + 1).Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out size)) size = cat.DefaultSize;
                    value = value.Substring(0, at);
                }
                map[cat.Id] = new VsFontSetting(FontChoice.Parse(value), NormalizeSize(size));
            }
            return map;
        }

        public static string Format(IReadOnlyDictionary<string, VsFontSetting> map)
        {
            var sb = new StringBuilder();
            foreach (var c in VsFontCategories.All)
            {
                if (map == null || !map.TryGetValue(c.Id, out var v) || v.Equals(Default(c))) continue;
                if (sb.Length > 0) sb.Append(';');
                sb.Append(c.Id).Append('=').Append(v.Family.ToString()).Append('@').Append(v.Size.ToString(CultureInfo.InvariantCulture));
            }
            return sb.ToString();
        }

        /// <summary>0 stays 0 (keep); anything else is clamped to [<see cref="VsFontCategories.MinSize"/>, <see cref="VsFontCategories.MaxSize"/>].</summary>
        public static int NormalizeSize(int size) =>
            size <= 0 ? 0 : Math.Max(VsFontCategories.MinSize, Math.Min(VsFontCategories.MaxSize, size));

        /// <summary>Returns <paramref name="text"/> with one category's family or size replaced.</summary>
        public static string With(string text, string categoryId, FontChoice? family, int? size)
        {
            var map = Parse(text);
            var cat = VsFontCategories.Find(categoryId);
            if (cat == null) return Format(map);
            var cur = map[cat.Id];
            map[cat.Id] = new VsFontSetting(family ?? cur.Family, size.HasValue ? NormalizeSize(size.Value) : cur.Size);
            return Format(map);
        }
    }
}
