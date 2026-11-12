using System;
using System.Collections.Generic;
using System.Globalization;

namespace UEToVS.UI.Logic
{
    /// <summary>How a declared setting is edited in the settings window.</summary>
    public enum SettingKind
    {
        Bool = 0,
        Int = 1,
        /// <summary>One of <see cref="SettingDescriptor.Options"/> (segmented control up to 4 options, dropdown above).</summary>
        Choice = 2,
        Text = 3,
        /// <summary>File or folder path (text box + browse).</summary>
        Path = 4,
        /// <summary>Not a value: a button that runs <see cref="SettingDescriptor.ActionId"/>.</summary>
        Action = 5,
        /// <summary>Read-only line whose text comes from the host at render time (e.g. current index status).</summary>
        Info = 6,
        /// <summary>Several folders (<see cref="PathListModel"/>): stored ';'-separated, edited as a list with a folder picker.</summary>
        PathList = 7,
    }

    /// <summary>One choice of a <see cref="SettingKind.Choice"/> setting.</summary>
    public sealed class SettingOption
    {
        public SettingOption(string value, string zh, string en)
        {
            Value = value;
            TitleZh = zh;
            TitleEn = en;
        }

        public string Value { get; }
        public string TitleZh { get; }
        public string TitleEn { get; }
    }

    /// <summary>
    /// Declarative description of one setting (设置窗口由描述生成). Adding a descriptor to a schema is all it takes
    /// for the setting to appear in the settings window, be searchable, validated, reset to default and persisted.
    /// Values travel as invariant strings ("true", "4", enum names) so the descriptor stays type-agnostic.
    /// </summary>
    public sealed class SettingDescriptor
    {
        public string Key { get; set; }
        /// <summary>Category (page) id, e.g. "general", "indexing".</summary>
        public string Category { get; set; }
        /// <summary>Optional group header inside the page.</summary>
        public string GroupZh { get; set; }
        public string GroupEn { get; set; }
        public SettingKind Kind { get; set; }
        public string TitleZh { get; set; }
        public string TitleEn { get; set; }
        public string DescriptionZh { get; set; }
        public string DescriptionEn { get; set; }
        /// <summary>Default value (invariant string). For Int, "0" may mean "auto" (see <see cref="AutoLabelZh"/>).</summary>
        public string Default { get; set; }
        public int Min { get; set; } = int.MinValue;
        public int Max { get; set; } = int.MaxValue;
        /// <summary>Label shown next to an Int value of 0 (e.g. "自动（4）").</summary>
        public string AutoLabelZh { get; set; }
        public string AutoLabelEn { get; set; }
        /// <summary>What "auto" (stored 0) resolves to on this machine, shown greyed in the disabled number box; null = unknown.</summary>
        public int? AutoValue { get; set; }
        public IReadOnlyList<SettingOption> Options { get; set; } = Array.Empty<SettingOption>();
        /// <summary>
        /// Choice: options computed when the editor is built (e.g. installed fonts), replacing <see cref="Options"/> in the
        /// dropdown. <see cref="Options"/> stays the fixed part used for validation. Null = static options only.
        /// </summary>
        public Func<IReadOnlyList<SettingOption>> OptionsSource { get; set; }
        /// <summary>Choice: any non-empty value is accepted (the options are suggestions), e.g. a font family name.</summary>
        public bool AllowCustomValue { get; set; }
        /// <summary>Action: short button text ("重建", "打开"); null = the title is used.</summary>
        public string ActionLabelZh { get; set; }
        public string ActionLabelEn { get; set; }
        /// <summary>Action/Info: id the host resolves (action id, or info provider id).</summary>
        public string ActionId { get; set; }
        /// <summary>Extra search words (both languages).</summary>
        public string Keywords { get; set; }
        /// <summary>Takes effect only after restarting VS (shown as a hint).</summary>
        public bool RequiresRestart { get; set; }

        public bool HasValue => Kind != SettingKind.Action && Kind != SettingKind.Info;

        /// <summary>
        /// Options for the editor: <see cref="OptionsSource"/> when set (falls back to <see cref="Options"/> when it throws
        /// or returns nothing), plus the current value when <see cref="AllowCustomValue"/> and it is not listed.
        /// </summary>
        public IReadOnlyList<SettingOption> ResolveOptions(string current = null)
        {
            IReadOnlyList<SettingOption> options = null;
            if (OptionsSource != null)
            {
                try { options = OptionsSource(); }
                catch (Exception) { options = null; } // a broken provider must not break the page; static options remain
            }
            if (options == null || options.Count == 0) options = Options ?? Array.Empty<SettingOption>();
            if (!AllowCustomValue || string.IsNullOrWhiteSpace(current)) return options;
            foreach (var o in options)
                if (string.Equals(o.Value, current.Trim(), StringComparison.OrdinalIgnoreCase)) return options;
            var list = new List<SettingOption>(options) { new SettingOption(current.Trim(), current.Trim(), current.Trim()) };
            return list;
        }

        public string Title(bool english) => english ? (TitleEn ?? TitleZh) : (TitleZh ?? TitleEn);
        public string Description(bool english) => english ? (DescriptionEn ?? DescriptionZh) : (DescriptionZh ?? DescriptionEn);
        public string Group(bool english) => english ? (GroupEn ?? GroupZh) : (GroupZh ?? GroupEn);
        public string ActionLabel(bool english) => english ? (ActionLabelEn ?? ActionLabelZh) : (ActionLabelZh ?? ActionLabelEn);

        public override string ToString() => Category + "/" + Key;
    }

    /// <summary>One page of the settings window.</summary>
    public sealed class SettingCategory
    {
        public SettingCategory(string id, string zh, string en)
        {
            Id = id;
            TitleZh = zh;
            TitleEn = en;
        }

        public string Id { get; }
        public string TitleZh { get; }
        public string TitleEn { get; }
        public string Title(bool english) => english ? TitleEn : TitleZh;
    }

    /// <summary>Pure helpers for descriptors: validation, search, change sets.</summary>
    public static class SettingsModel
    {
        /// <summary>
        /// Normalizes <paramref name="value"/> for <paramref name="d"/>: bools to "true"/"false", ints clamped to
        /// [Min, Max], choices must be one of the options (case-insensitive). Invalid → default; <paramref name="ok"/>
        /// tells whether the input was valid as given.
        /// </summary>
        public static string Normalize(SettingDescriptor d, string value, out bool ok)
        {
            ok = true;
            if (d == null) throw new ArgumentNullException(nameof(d));
            switch (d.Kind)
            {
                case SettingKind.Bool:
                    if (bool.TryParse(value?.Trim(), out bool b)) return b ? "true" : "false";
                    ok = false;
                    return d.Default ?? "false";
                case SettingKind.Int:
                    if (int.TryParse(value?.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out int i))
                    {
                        int c = Math.Max(d.Min, Math.Min(d.Max, i));
                        ok = c == i;
                        return c.ToString(CultureInfo.InvariantCulture);
                    }
                    ok = false;
                    return d.Default ?? "0";
                case SettingKind.Choice:
                    if (d.Options != null)
                        foreach (var o in d.Options)
                            if (string.Equals(o.Value, value?.Trim(), StringComparison.OrdinalIgnoreCase)) return o.Value;
                    if (d.AllowCustomValue && !string.IsNullOrWhiteSpace(value)) return value.Trim();
                    ok = false;
                    return d.Default;
                case SettingKind.Text:
                case SettingKind.Path:
                    return (value ?? string.Empty).Trim();
                case SettingKind.PathList:
                    return PathListModel.Join(PathListModel.Parse(value));
                default:
                    return null;
            }
        }

        /// <summary>
        /// Search box filter: every whitespace-separated term must occur (case-insensitive) in the title, description,
        /// group, key or keywords, in either language. Empty query matches everything.
        /// </summary>
        public static bool Matches(SettingDescriptor d, string query)
        {
            if (d == null) return false;
            if (string.IsNullOrWhiteSpace(query)) return true;
            string hay = string.Join("\n", d.Key, d.TitleZh, d.TitleEn, d.DescriptionZh, d.DescriptionEn, d.GroupZh, d.GroupEn, d.Keywords);
            foreach (var term in query.Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries))
                if (hay.IndexOf(term, StringComparison.OrdinalIgnoreCase) < 0) return false;
            return true;
        }

        /// <summary>Keys whose normalized value differs between <paramref name="a"/> and <paramref name="b"/>.</summary>
        public static List<string> Diff(IEnumerable<SettingDescriptor> schema, IReadOnlyDictionary<string, string> a, IReadOnlyDictionary<string, string> b)
        {
            var keys = new List<string>();
            foreach (var d in schema)
            {
                if (!d.HasValue) continue;
                a.TryGetValue(d.Key, out var va);
                b.TryGetValue(d.Key, out var vb);
                if (!string.Equals(Normalize(d, va ?? d.Default, out _), Normalize(d, vb ?? d.Default, out _), StringComparison.Ordinal)) keys.Add(d.Key);
            }
            return keys;
        }

        /// <summary>Defaults of every value setting.</summary>
        public static Dictionary<string, string> Defaults(IEnumerable<SettingDescriptor> schema)
        {
            var r = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var d in schema)
                if (d.HasValue) r[d.Key] = d.Default;
            return r;
        }

        /// <summary>Validates a schema (unique keys, defaults valid, choices non-empty). Returns problems; empty = fine.</summary>
        public static List<string> Validate(IEnumerable<SettingDescriptor> schema, IEnumerable<SettingCategory> categories)
        {
            var problems = new List<string>();
            var cats = new HashSet<string>(StringComparer.Ordinal);
            foreach (var c in categories) cats.Add(c.Id);
            var keys = new HashSet<string>(StringComparer.Ordinal);
            foreach (var d in schema)
            {
                if (string.IsNullOrEmpty(d.Key)) { problems.Add("descriptor without key"); continue; }
                if (!keys.Add(d.Key)) problems.Add("duplicate key " + d.Key);
                if (!cats.Contains(d.Category ?? "")) problems.Add(d.Key + ": unknown category " + d.Category);
                if (string.IsNullOrEmpty(d.TitleZh) || string.IsNullOrEmpty(d.TitleEn)) problems.Add(d.Key + ": missing title");
                if (d.Kind == SettingKind.Choice && (d.Options == null || d.Options.Count == 0)) problems.Add(d.Key + ": choice without options");
                if ((d.Kind == SettingKind.Action || d.Kind == SettingKind.Info) && string.IsNullOrEmpty(d.ActionId)) problems.Add(d.Key + ": missing action id");
                if (d.HasValue && d.Kind != SettingKind.Text && d.Kind != SettingKind.Path && d.Kind != SettingKind.PathList)
                {
                    Normalize(d, d.Default, out bool ok);
                    if (!ok) problems.Add(d.Key + ": invalid default " + d.Default);
                }
            }
            return problems;
        }
    }
}
