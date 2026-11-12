using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using UEToVS.Host.Logic.Refactoring;

namespace UEToVS.Host.Logic.Editing
{
    /// <summary>One occurrence edited in place: (line, column, length) in the current file.</summary>
    public readonly struct InlineOccurrence : IEquatable<InlineOccurrence>
    {
        public InlineOccurrence(int line, int column, int length)
        {
            Line = line;
            Column = column;
            Length = length;
        }

        public int Line { get; }
        public int Column { get; }
        public int Length { get; }

        public bool Equals(InlineOccurrence o) => Line == o.Line && Column == o.Column && Length == o.Length;
        public override bool Equals(object obj) => obj is InlineOccurrence o && Equals(o);
        public override int GetHashCode() => (Line * 397) ^ Column;
        public override string ToString() => $"{Line + 1}:{Column + 1}+{Length}";
    }

    /// <summary>
    /// Inline rename (editor.renameMode = Inline): which Rename locations are edited in place (the current file's checked,
    /// editable, non-#include ones), and how the typed name is checked before the project-wide preview.
    /// </summary>
    public static class InlineRenamePlan
    {
        public static List<InlineOccurrence> Occurrences(IEnumerable<RenameLocation> locations, string filePath, string oldName)
        {
            var result = new List<InlineOccurrence>();
            if (locations == null || string.IsNullOrEmpty(filePath) || string.IsNullOrEmpty(oldName)) return result;
            string file = Normalize(filePath);
            foreach (var l in locations)
            {
                if (l == null || !l.Checked || !l.IsEditable || l.IsInclude || l.Line < 0 || l.Column < 0) continue;
                if (!string.Equals(Normalize(l.FilePath), file, StringComparison.OrdinalIgnoreCase)) continue;
                int len = l.Length > 0 ? l.Length : oldName.Length;
                if (len != oldName.Length) continue; // only plain spellings of the name are mirrored
                var o = new InlineOccurrence(l.Line, l.Column, len);
                if (!result.Contains(o)) result.Add(o);
            }
            return result.OrderBy(o => o.Line).ThenBy(o => o.Column).ToList();
        }

        /// <summary>The typed name can go to the project-wide preview (a valid identifier that differs from the old one).</summary>
        public static bool IsAcceptable(string oldName, string newName) =>
            RenamePlanner.IsValidIdentifier(newName) && !string.Equals(oldName, newName, StringComparison.Ordinal);

        private static string Normalize(string p)
        {
            if (string.IsNullOrEmpty(p)) return string.Empty;
            try
            {
                return Path.GetFullPath(p).Replace('/', '\\').TrimEnd('\\');
            }
            catch (Exception)
            {
                return p.Replace('/', '\\');
            }
        }
    }
}
