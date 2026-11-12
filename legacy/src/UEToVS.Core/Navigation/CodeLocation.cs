using System;
using System.Globalization;

namespace UEToVS.Core.Navigation
{
    /// <summary>A position in a file. Line and Column are 0-based.</summary>
    public sealed class CodeLocation : IEquatable<CodeLocation>
    {
        public CodeLocation(string filePath, int line, int column)
        {
            FilePath = filePath ?? throw new ArgumentNullException(nameof(filePath));
            Line = Math.Max(0, line);
            Column = Math.Max(0, column);
        }

        public string FilePath { get; }
        public int Line { get; }
        public int Column { get; }

        public bool SameFile(CodeLocation other) =>
            other != null && string.Equals(FilePath, other.FilePath, StringComparison.OrdinalIgnoreCase);

        public bool IsNear(CodeLocation other, int lineThreshold) =>
            SameFile(other) && Math.Abs(Line - other.Line) <= lineThreshold;

        public bool Equals(CodeLocation other) =>
            other != null && SameFile(other) && Line == other.Line && Column == other.Column;

        public override bool Equals(object obj) => Equals(obj as CodeLocation);

        public override int GetHashCode() =>
            StringComparer.OrdinalIgnoreCase.GetHashCode(FilePath) ^ (Line * 397) ^ Column;

        public override string ToString() => $"{FilePath}:{Line + 1}:{Column + 1}";

        internal string Serialize() =>
            string.Join("\t", FilePath, Line.ToString(CultureInfo.InvariantCulture), Column.ToString(CultureInfo.InvariantCulture));

        internal static CodeLocation Deserialize(string line)
        {
            var parts = line.Split('\t');
            if (parts.Length < 3) return null;
            if (!int.TryParse(parts[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out int l)) return null;
            if (!int.TryParse(parts[2], NumberStyles.Integer, CultureInfo.InvariantCulture, out int c)) return null;
            return new CodeLocation(parts[0], l, c);
        }
    }
}
