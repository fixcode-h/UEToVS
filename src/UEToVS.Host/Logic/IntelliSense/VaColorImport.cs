using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Xml;
using UEToVS.Contracts;

namespace UEToVS.Host.Logic.IntelliSense
{
    /// <summary>One &lt;Item&gt; of a .vssettings Fonts and Colors category.</summary>
    public sealed class VsSettingsColorItem
    {
        public string Name { get; set; }

        /// <summary>Fonts and Colors category GUID the item belongs to ({A27B4E24-…} = Text Editor).</summary>
        public string Category { get; set; }

        /// <summary>ARGB, or null when the file says "automatic" / "default" / a system colour index.</summary>
        public uint? Foreground { get; set; }

        public uint? Background { get; set; }

        public bool Bold { get; set; }
    }

    /// <summary>One colour the import will write.</summary>
    public sealed class VaColorAssignment
    {
        public SemanticKind Kind { get; set; }
        public string ClassificationName { get; set; }
        public uint Foreground { get; set; }
        public bool Bold { get; set; }

        /// <summary>VA item the colour came from (for the report and the log).</summary>
        public string Source { get; set; }
    }

    public sealed class VaImportPlan
    {
        public List<VaColorAssignment> Assignments { get; } = new List<VaColorAssignment>();

        /// <summary>VA items found in the file (any colour state).</summary>
        public List<string> VaItems { get; } = new List<string>();

        /// <summary>VA items that carry no colour of their own ("automatic"), so nothing was taken from them.</summary>
        public List<string> WithoutColor { get; } = new List<string>();

        /// <summary>VA items we do not map (brace matching, find results, …).</summary>
        public List<string> Ignored { get; } = new List<string>();

        public bool IsEmpty => Assignments.Count == 0;
    }

    /// <summary>
    /// "Import VA colours" (§7.7): reads the Fonts and Colors section of a .vssettings file exported from Visual Studio
    /// with Visual Assist installed, picks VA's syntax colouring items and maps them to the UEToVS classifications.
    ///
    /// VA item names (Tools &gt; Options &gt; Environment &gt; Fonts and Colors, Text Editor category): current VA uses
    /// "VA Class", "VA Method", "VA Variable", "VA Macro", "VA Enum Member", "VA Namespace" (plus non-syntax items such as
    /// "VA Brace Matching", "VA Find Reference", "VA Find Reference (Modified)", "VA Find Result", "VA Current Line");
    /// Visual Assist X (VS 2010–2013 era) used "VA X Class", "VA X Method", "VA X Variable" and a combined
    /// "VA X Macro / Enum". Both spellings are accepted. Colours in .vssettings are COLORREF hex "0x00BBGGRR"; values
    /// with a non-zero high byte (0x01000000 automatic, 0x02000000 default, system indices) carry no colour. Pure.
    /// </summary>
    public static class VaColorImport
    {
        /// <summary>VA syntax item (normalized to the "VA …" spelling) → UEToVS kinds it colours.</summary>
        public static readonly IReadOnlyDictionary<string, SemanticKind[]> Mapping = new Dictionary<string, SemanticKind[]>(StringComparer.OrdinalIgnoreCase)
        {
            ["VA Class"] = new[]
            {
                SemanticKind.Class, SemanticKind.Struct, SemanticKind.Enum, SemanticKind.Typedef, SemanticKind.TemplateParameter,
                SemanticKind.UeType, SemanticKind.Delegate,
            },
            ["VA Method"] = new[] { SemanticKind.Function, SemanticKind.Method, SemanticKind.StaticMethod, SemanticKind.VirtualMethod },
            ["VA Variable"] = new[]
            {
                SemanticKind.LocalVariable, SemanticKind.Parameter, SemanticKind.Field, SemanticKind.StaticField, SemanticKind.GlobalVariable,
            },
            ["VA Macro"] = new[] { SemanticKind.Macro, SemanticKind.UeMacro },
            ["VA Macro / Enum"] = new[] { SemanticKind.Macro, SemanticKind.UeMacro, SemanticKind.EnumValue },
            ["VA Enum Member"] = new[] { SemanticKind.EnumValue },
            ["VA Namespace"] = new[] { SemanticKind.Namespace },
        };

        /// <summary>When several VA items colour the same kind, the more specific one wins (higher = stronger).</summary>
        private static int Specificity(string vaName)
        {
            switch (vaName.ToLowerInvariant())
            {
                case "va enum member": return 3;
                case "va macro": return 2;
                case "va macro / enum": return 1;
                default: return 2;
            }
        }

        /// <summary>"VA X Class" → "VA Class"; other names unchanged.</summary>
        public static string Normalize(string name)
        {
            if (name == null) return null;
            string n = name.Trim();
            if (n.StartsWith("VA X ", StringComparison.OrdinalIgnoreCase)) n = "VA " + n.Substring(5);
            return n;
        }

        public static bool IsVaItem(string name) => name != null && name.TrimStart().StartsWith("VA ", StringComparison.OrdinalIgnoreCase);

        /// <summary>COLORREF "0x00BBGGRR" (or decimal) → ARGB; null for automatic / default / system colours.</summary>
        public static uint? FromColorRef(string value)
        {
            if (string.IsNullOrWhiteSpace(value)) return null;
            string v = value.Trim();
            uint raw;
            if (v.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
            {
                if (!uint.TryParse(v.Substring(2), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out raw)) return null;
            }
            else if (!uint.TryParse(v, NumberStyles.Integer, CultureInfo.InvariantCulture, out raw))
            {
                return null;
            }
            if ((raw & 0xFF000000) != 0) return null;
            uint r = raw & 0xFF, g = (raw >> 8) & 0xFF, b = (raw >> 16) & 0xFF;
            return 0xFF000000 | (r << 16) | (g << 8) | b;
        }

        /// <summary>ARGB → COLORREF (for IVsFontAndColorStorage).</summary>
        public static uint ToColorRef(uint argb) => ((argb & 0xFF) << 16) | (argb & 0xFF00) | ((argb >> 16) & 0xFF);

        /// <summary>Every Fonts and Colors &lt;Item&gt; in a .vssettings document (DTDs refused; unknown structure tolerated).</summary>
        public static List<VsSettingsColorItem> Parse(TextReader reader)
        {
            var items = new List<VsSettingsColorItem>();
            var settings = new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null, IgnoreComments = true };
            string category = null;
            int categoryDepth = -1;
            using (var x = XmlReader.Create(reader, settings))
            {
                while (x.Read())
                {
                    if (x.NodeType == XmlNodeType.EndElement && x.Depth == categoryDepth && x.LocalName == "Category")
                    {
                        category = null;
                        categoryDepth = -1;
                        continue;
                    }
                    if (x.NodeType != XmlNodeType.Element) continue;
                    if (x.LocalName == "Category" && x.GetAttribute("GUID") != null)
                    {
                        category = x.GetAttribute("GUID");
                        categoryDepth = x.IsEmptyElement ? -1 : x.Depth;
                    }
                    else if (x.LocalName == "Item" && x.GetAttribute("Name") != null)
                    {
                        items.Add(new VsSettingsColorItem
                        {
                            Name = x.GetAttribute("Name"),
                            Category = category,
                            Foreground = FromColorRef(x.GetAttribute("Foreground")),
                            Background = FromColorRef(x.GetAttribute("Background")),
                            Bold = string.Equals(x.GetAttribute("BoldFont"), "Yes", StringComparison.OrdinalIgnoreCase),
                        });
                    }
                }
            }
            return items;
        }

        public static List<VsSettingsColorItem> Parse(string xml)
        {
            using (var r = new StringReader(xml ?? string.Empty)) return Parse(r);
        }

        /// <summary>VA items → UEToVS assignments (one per kind; the more specific VA item wins).</summary>
        public static VaImportPlan Map(IEnumerable<VsSettingsColorItem> items)
        {
            var plan = new VaImportPlan();
            var chosen = new Dictionary<SemanticKind, (VaColorAssignment A, int Specificity)>();
            foreach (var item in items ?? Enumerable.Empty<VsSettingsColorItem>())
            {
                if (!IsVaItem(item?.Name)) continue;
                if (!plan.VaItems.Contains(item.Name)) plan.VaItems.Add(item.Name);
                string norm = Normalize(item.Name);
                if (!Mapping.TryGetValue(norm, out var kinds))
                {
                    if (!plan.Ignored.Contains(item.Name)) plan.Ignored.Add(item.Name);
                    continue;
                }
                if (item.Foreground == null)
                {
                    if (!plan.WithoutColor.Contains(item.Name)) plan.WithoutColor.Add(item.Name);
                    continue;
                }
                int spec = Specificity(norm);
                foreach (var kind in kinds)
                {
                    if (chosen.TryGetValue(kind, out var existing) && existing.Specificity > spec) continue;
                    chosen[kind] = (new VaColorAssignment
                    {
                        Kind = kind,
                        ClassificationName = SemanticClassifications.NameFor(kind),
                        Foreground = item.Foreground.Value,
                        Bold = item.Bold,
                        Source = item.Name,
                    }, spec);
                }
            }
            foreach (var info in SemanticClassifications.All)
                if (chosen.TryGetValue(info.Kind, out var c)) plan.Assignments.Add(c.A);
            return plan;
        }
    }
}
