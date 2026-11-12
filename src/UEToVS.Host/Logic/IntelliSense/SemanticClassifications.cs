using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using UEToVS.Contracts;
using UEToVS.Core.Localization;
using UEToVS.Host.Logic.Navigation;

namespace UEToVS.Host.Logic.IntelliSense
{
    /// <summary>
    /// Classification type names (also the Fonts and Colors item names) of the VA-style semantic colouring (§7.7), one per
    /// <see cref="SemanticKind"/>. Stable: users' Fonts and Colors customizations are stored under these names.
    /// </summary>
    public static class SemanticClassificationNames
    {
        public const string Class = "UEToVS Class";
        public const string Struct = "UEToVS Struct";
        public const string Enum = "UEToVS Enum";
        public const string EnumValue = "UEToVS Enum Member";
        public const string Typedef = "UEToVS Typedef";
        public const string TemplateParameter = "UEToVS Template Parameter";
        public const string Namespace = "UEToVS Namespace";
        public const string Function = "UEToVS Function";
        public const string Method = "UEToVS Method";
        public const string StaticMethod = "UEToVS Static Method";
        public const string VirtualMethod = "UEToVS Virtual Method";
        public const string Field = "UEToVS Field";
        public const string StaticField = "UEToVS Static Field";
        public const string LocalVariable = "UEToVS Local Variable";
        public const string Parameter = "UEToVS Parameter";
        public const string GlobalVariable = "UEToVS Global Variable";
        public const string Macro = "UEToVS Macro";
        public const string UeMacro = "UEToVS UE Reflection Macro";
        public const string UeType = "UEToVS UE Type";
        public const string Delegate = "UEToVS Delegate";
        public const string UeSpecifier = "UEToVS Reflection Specifier";
    }

    /// <summary>Colour group of §7.7's table (the VA category each kind follows).</summary>
    public enum SemanticColorGroup
    {
        Type,
        Function,
        Variable,
        Macro,
        EnumMember,
        Namespace,
        UeMacro,
        UeSpecifier,
    }

    public sealed class SemanticClassificationInfo
    {
        public SemanticClassificationInfo(SemanticKind kind, string name, string zh, string en, SemanticColorGroup group, uint dark, uint light)
        {
            Kind = kind;
            Name = name;
            Zh = zh;
            En = en;
            Group = group;
            Dark = dark;
            Light = light;
        }

        public SemanticKind Kind { get; }

        /// <summary>Classification type / format name ("UEToVS …").</summary>
        public string Name { get; }

        public string Zh { get; }
        public string En { get; }

        /// <summary>Name shown in Tools &gt; Options &gt; Fonts and Colors (starts with "UEToVS ").</summary>
        public string DisplayName => "UEToVS " + Loc.T(Zh, En);

        public SemanticColorGroup Group { get; }

        /// <summary>Default foreground (ARGB) on a dark / light editor background.</summary>
        public uint Dark { get; }

        public uint Light { get; }

        public uint Default(bool dark) => dark ? Dark : Light;
    }

    /// <summary>
    /// SemanticKind → classification, display name and the dark / light default colours of §7.7 (VA palette), plus the
    /// theme-follow rule and the "per-kind colour override" setting format. Pure.
    /// </summary>
    public static class SemanticClassifications
    {
        // §7.7 table (dark, light).
        public const uint TypeDark = 0xFF4EC9B0, TypeLight = 0xFF2B91AF;
        public const uint FunctionDark = 0xFFDCDCAA, FunctionLight = 0xFF74531F;
        public const uint VariableDark = 0xFF9CDCFE, VariableLight = 0xFF1F377F;
        public const uint MacroDark = 0xFFBD63C5, MacroLight = 0xFF8A1BFF;
        public const uint EnumMemberDark = 0xFFB8D7A3, EnumMemberLight = 0xFF2F4F4F;
        public const uint NamespaceDark = 0xFFC8C8C8, NamespaceLight = 0xFF000000;
        public const uint UeMacroDark = 0xFFC586C0, UeMacroLight = 0xFFAF00DB;
        public const uint UeSpecifierDark = 0xFFCE9178, UeSpecifierLight = 0xFFA31515;

        /// <summary>Opacity of reflection specifiers ("弱化" in §7.7).</summary>
        public const double UeSpecifierOpacity = 0.8;

        private static readonly SemanticClassificationInfo[] Table =
        {
            I(SemanticKind.Class, SemanticClassificationNames.Class, "类", "Class", SemanticColorGroup.Type),
            I(SemanticKind.Struct, SemanticClassificationNames.Struct, "结构体", "Struct", SemanticColorGroup.Type),
            I(SemanticKind.Enum, SemanticClassificationNames.Enum, "枚举", "Enum", SemanticColorGroup.Type),
            I(SemanticKind.EnumValue, SemanticClassificationNames.EnumValue, "枚举值", "Enum Member", SemanticColorGroup.EnumMember),
            I(SemanticKind.Typedef, SemanticClassificationNames.Typedef, "类型别名", "Typedef", SemanticColorGroup.Type),
            I(SemanticKind.TemplateParameter, SemanticClassificationNames.TemplateParameter, "模板参数", "Template Parameter", SemanticColorGroup.Type),
            I(SemanticKind.Namespace, SemanticClassificationNames.Namespace, "命名空间", "Namespace", SemanticColorGroup.Namespace),
            I(SemanticKind.Function, SemanticClassificationNames.Function, "函数", "Function", SemanticColorGroup.Function),
            I(SemanticKind.Method, SemanticClassificationNames.Method, "方法", "Method", SemanticColorGroup.Function),
            I(SemanticKind.StaticMethod, SemanticClassificationNames.StaticMethod, "静态方法", "Static Method", SemanticColorGroup.Function),
            I(SemanticKind.VirtualMethod, SemanticClassificationNames.VirtualMethod, "虚方法", "Virtual Method", SemanticColorGroup.Function),
            I(SemanticKind.Field, SemanticClassificationNames.Field, "成员变量", "Field", SemanticColorGroup.Variable),
            I(SemanticKind.StaticField, SemanticClassificationNames.StaticField, "静态成员变量", "Static Field", SemanticColorGroup.Variable),
            I(SemanticKind.LocalVariable, SemanticClassificationNames.LocalVariable, "局部变量", "Local Variable", SemanticColorGroup.Variable),
            I(SemanticKind.Parameter, SemanticClassificationNames.Parameter, "参数", "Parameter", SemanticColorGroup.Variable),
            I(SemanticKind.GlobalVariable, SemanticClassificationNames.GlobalVariable, "全局变量", "Global Variable", SemanticColorGroup.Variable),
            I(SemanticKind.Macro, SemanticClassificationNames.Macro, "宏", "Macro", SemanticColorGroup.Macro),
            I(SemanticKind.UeMacro, SemanticClassificationNames.UeMacro, "UE 反射宏", "UE Reflection Macro", SemanticColorGroup.UeMacro),
            I(SemanticKind.UeType, SemanticClassificationNames.UeType, "UE 反射类型", "UE Reflected Type", SemanticColorGroup.Type),
            I(SemanticKind.Delegate, SemanticClassificationNames.Delegate, "委托", "Delegate", SemanticColorGroup.Type),
            I(SemanticKind.UeSpecifier, SemanticClassificationNames.UeSpecifier, "反射说明符", "Reflection Specifier", SemanticColorGroup.UeSpecifier),
        };

        private static readonly Dictionary<SemanticKind, SemanticClassificationInfo> ByKind = Table.ToDictionary(t => t.Kind);

        private static SemanticClassificationInfo I(SemanticKind kind, string name, string zh, string en, SemanticColorGroup group)
        {
            var (dark, light) = GroupColors(group);
            return new SemanticClassificationInfo(kind, name, zh, en, group, dark, light);
        }

        public static (uint Dark, uint Light) GroupColors(SemanticColorGroup group)
        {
            switch (group)
            {
                case SemanticColorGroup.Type: return (TypeDark, TypeLight);
                case SemanticColorGroup.Function: return (FunctionDark, FunctionLight);
                case SemanticColorGroup.Variable: return (VariableDark, VariableLight);
                case SemanticColorGroup.Macro: return (MacroDark, MacroLight);
                case SemanticColorGroup.EnumMember: return (EnumMemberDark, EnumMemberLight);
                case SemanticColorGroup.Namespace: return (NamespaceDark, NamespaceLight);
                case SemanticColorGroup.UeMacro: return (UeMacroDark, UeMacroLight);
                default: return (UeSpecifierDark, UeSpecifierLight);
            }
        }

        /// <summary>Every coloured kind (SemanticKind.None excluded), in Fonts and Colors order.</summary>
        public static IReadOnlyList<SemanticClassificationInfo> All => Table;

        public static SemanticClassificationInfo Get(SemanticKind kind) => ByKind.TryGetValue(kind, out var i) ? i : null;

        /// <summary>Classification name for a span, or null when it gets no colour (None, unresolved names).</summary>
        public static string NameFor(SemanticKind kind, SemanticModifiers modifiers = SemanticModifiers.None)
        {
            if ((modifiers & SemanticModifiers.Unresolved) != 0) return null;
            return Get(kind)?.Name;
        }

        /// <summary>
        /// Theme following without trampling the user's Fonts and Colors choices: the override wins; otherwise the
        /// current colour is replaced by this theme's default only when it is still one of our defaults (or the colour we
        /// applied last). Returns null = leave as is (user customized it, or already right).
        /// </summary>
        public static uint? TargetColor(SemanticClassificationInfo info, bool dark, uint? current, uint? overrideArgb, uint? lastApplied)
        {
            if (info == null) return null;
            uint want = overrideArgb ?? info.Default(dark);
            if (current == want) return null;
            bool ours = current == null || current == info.Dark || current == info.Light || (lastApplied.HasValue && current == lastApplied);
            if (overrideArgb.HasValue) return want;
            return ours ? want : (uint?)null;
        }

        // ------------------------------------------------------------------ per-kind override setting

        /// <summary>Group aliases accepted in the override setting ("Type=#…" colours every type kind).</summary>
        private static readonly Dictionary<string, SemanticColorGroup> GroupAliases = new Dictionary<string, SemanticColorGroup>(StringComparer.OrdinalIgnoreCase)
        {
            ["Types"] = SemanticColorGroup.Type,
            ["Functions"] = SemanticColorGroup.Function,
            ["Variables"] = SemanticColorGroup.Variable,
            ["Macros"] = SemanticColorGroup.Macro,
            ["EnumMembers"] = SemanticColorGroup.EnumMember,
            ["Namespaces"] = SemanticColorGroup.Namespace,
            ["UeMacros"] = SemanticColorGroup.UeMacro,
            ["UeSpecifiers"] = SemanticColorGroup.UeSpecifier,
        };

        /// <summary>
        /// Parses HostSettings.SemanticColorOverrides: "Class=#4EC9B0; Method=#DCDCAA; Variables=#9CDCFE" — keys are
        /// SemanticKind names or the plural group aliases (Types, Functions, Variables, Macros, EnumMembers, Namespaces,
        /// UeMacros, UeSpecifiers), values "#RRGGBB" / "#AARRGGBB". A kind key beats a group key. Bad entries are
        /// reported in <paramref name="errors"/> and skipped.
        /// </summary>
        public static Dictionary<SemanticKind, uint> ParseOverrides(string text, List<string> errors = null)
        {
            var result = new Dictionary<SemanticKind, uint>();
            if (string.IsNullOrWhiteSpace(text)) return result;
            var byKind = new Dictionary<SemanticKind, uint>();
            foreach (var raw in text.Split(new[] { ';', '\n', '\r' }, StringSplitOptions.RemoveEmptyEntries))
            {
                string entry = raw.Trim();
                if (entry.Length == 0) continue;
                int eq = entry.IndexOf('=');
                if (eq <= 0)
                {
                    errors?.Add(entry);
                    continue;
                }
                string key = entry.Substring(0, eq).Trim();
                string value = entry.Substring(eq + 1).Trim();
                if (!HighlightColors.TryParse(value, out uint argb))
                {
                    errors?.Add(entry);
                    continue;
                }
                if (GroupAliases.TryGetValue(key, out var group))
                {
                    foreach (var info in Table)
                        if (info.Group == group && !byKind.ContainsKey(info.Kind)) result[info.Kind] = argb;
                }
                else if (System.Enum.TryParse(key, true, out SemanticKind kind) && kind != SemanticKind.None && System.Enum.IsDefined(typeof(SemanticKind), kind))
                {
                    byKind[kind] = argb;
                }
                else
                {
                    errors?.Add(entry);
                }
            }
            foreach (var kv in byKind) result[kv.Key] = kv.Value;
            return result;
        }

        /// <summary>Inverse of <see cref="ParseOverrides"/> (kind keys, stable order).</summary>
        public static string FormatOverrides(IReadOnlyDictionary<SemanticKind, uint> overrides)
        {
            if (overrides == null || overrides.Count == 0) return string.Empty;
            return string.Join("; ", Table.Where(t => overrides.ContainsKey(t.Kind))
                                          .Select(t => t.Kind.ToString() + "=" + HighlightColors.Format(overrides[t.Kind])));
        }

        public static string Hex(uint argb) => "#" + (argb & 0xFFFFFF).ToString("X6", CultureInfo.InvariantCulture);
    }
}
