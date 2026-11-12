using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace UEToVS.Host.Logic.Snippets
{
    /// <summary>One field of an expanded template: offsets into <see cref="SnippetExpansion.Text"/>.</summary>
    public sealed class SnippetField
    {
        public string Name { get; set; }
        public int Start { get; set; }
        public int Length { get; set; }

        /// <summary>Fields with the same name are copies of the first one (edited together).</summary>
        public bool IsPrimary { get; set; }

        public override string ToString() => $"{Name}@{Start}+{Length}{(IsPrimary ? "*" : string.Empty)}";
    }

    public sealed class SnippetExpansion
    {
        public string Text { get; set; } = string.Empty;
        public List<SnippetField> Fields { get; } = new List<SnippetField>();

        /// <summary>Where the caret goes when the fields are done ($END$, else the end of the text).</summary>
        public int EndOffset { get; set; }

        /// <summary>Primary fields in tab order.</summary>
        public List<SnippetField> TabStops()
        {
            var list = new List<SnippetField>();
            foreach (var f in Fields)
                if (f.IsPrimary) list.Add(f);
            return list;
        }
    }

    /// <summary>Expands <see cref="SnippetTemplate"/> bodies into text + field spans, with the editor's indentation and line breaks. Pure.</summary>
    public static class SnippetExpander
    {
        /// <param name="indent">Leading white space of the line the template is inserted on (added after every line break).</param>
        /// <param name="indentUnit">One indent level (tab or spaces) that replaces '\t' in the body.</param>
        /// <param name="newLine">The buffer's line break.</param>
        /// <param name="filePath">Current file, for the {FILE} / {CLASS} variables (may be null).</param>
        public static SnippetExpansion Expand(SnippetTemplate template, string indent, string indentUnit, string newLine, string filePath) =>
            Expand(template?.Body, indent, indentUnit, newLine, filePath);

        public static SnippetExpansion Expand(string body, string indent, string indentUnit, string newLine, string filePath)
        {
            var result = new SnippetExpansion();
            if (string.IsNullOrEmpty(body)) return result;
            indent = indent ?? string.Empty;
            indentUnit = indentUnit ?? "\t";
            newLine = string.IsNullOrEmpty(newLine) ? "\r\n" : newLine;
            string file = string.IsNullOrEmpty(filePath) ? "MyClass" : Path.GetFileNameWithoutExtension(filePath);
            string cls = StripUePrefix(file);
            var defaults = new Dictionary<string, string>(StringComparer.Ordinal);
            var sb = new StringBuilder(body.Length + 32);
            int end = -1;
            int i = 0;
            while (i < body.Length)
            {
                char c = body[i];
                if (c == '\n')
                {
                    sb.Append(newLine).Append(indent);
                    i++;
                    continue;
                }
                if (c == '\t')
                {
                    sb.Append(indentUnit);
                    i++;
                    continue;
                }
                if (c == '\r')
                {
                    i++;
                    continue;
                }
                if (c != '$')
                {
                    sb.Append(c);
                    i++;
                    continue;
                }
                int close = body.IndexOf('$', i + 1);
                if (close < 0)
                {
                    sb.Append(c);
                    i++;
                    continue;
                }
                string field = body.Substring(i + 1, close - i - 1);
                i = close + 1;
                if (field.Length == 0)
                {
                    sb.Append('$'); // "$$"
                    continue;
                }
                if (field == "END")
                {
                    end = sb.Length;
                    continue;
                }
                int eq = field.IndexOf('=');
                string name = eq < 0 ? field : field.Substring(0, eq);
                bool primary = !defaults.ContainsKey(name);
                string value;
                if (primary)
                {
                    value = eq < 0 ? string.Empty : field.Substring(eq + 1).Replace("{FILE}", file).Replace("{CLASS}", cls);
                    defaults[name] = value;
                }
                else value = defaults[name];
                result.Fields.Add(new SnippetField { Name = name, Start = sb.Length, Length = value.Length, IsPrimary = primary });
                sb.Append(value);
            }
            result.Text = sb.ToString();
            result.EndOffset = end >= 0 ? end : result.Text.Length;
            return result;
        }

        /// <summary>"AMyActor" → "MyActor", "UHealthComponent" → "HealthComponent", "MyThing" stays.</summary>
        public static string StripUePrefix(string name)
        {
            if (string.IsNullOrEmpty(name) || name.Length < 3) return name ?? string.Empty;
            if ("AUFIESTG".IndexOf(name[0]) >= 0 && char.IsUpper(name[1]) && char.IsLower(name[2])) return name.Substring(1);
            return name;
        }

        /// <summary>Leading white space of a line.</summary>
        public static string LeadingWhitespace(string line)
        {
            if (string.IsNullOrEmpty(line)) return string.Empty;
            int n = 0;
            while (n < line.Length && (line[n] == ' ' || line[n] == '\t')) n++;
            return line.Substring(0, n);
        }

        /// <summary>
        /// After the user edited a primary field: the text the linked copies should get. Returns the edits (offset, old length,
        /// new text) in descending offset order so they can be applied one after another. Pure helper for the placeholder session.
        /// </summary>
        public static List<(int Start, int Length, string Text)> SyncLinked(IReadOnlyList<(string Name, int Start, int Length, bool Primary)> fields, Func<int, int, string> textAt)
        {
            var edits = new List<(int, int, string)>();
            if (fields == null || textAt == null) return edits;
            var values = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var f in fields)
                if (f.Primary) values[f.Name] = textAt(f.Start, f.Length);
            foreach (var f in fields)
            {
                if (f.Primary || !values.TryGetValue(f.Name, out var v)) continue;
                if (textAt(f.Start, f.Length) != v) edits.Add((f.Start, f.Length, v));
            }
            edits.Sort((a, b) => b.Item1.CompareTo(a.Item1));
            return edits;
        }
    }
}
