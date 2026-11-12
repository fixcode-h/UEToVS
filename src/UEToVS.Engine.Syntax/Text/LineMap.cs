using System;
using System.Text;

namespace UEToVS.Engine.Syntax
{
    /// <summary>A byte range in the UTF-8 text.</summary>
    public readonly struct TextRange : IEquatable<TextRange>
    {
        public static readonly TextRange None = new TextRange(-1, 0);

        public TextRange(int start, int length)
        {
            Start = start;
            Length = length;
        }

        public int Start { get; }
        public int Length { get; }
        public int End => Start + Length;
        public bool IsNone => Start < 0;

        public bool Equals(TextRange other) => Start == other.Start && Length == other.Length;
        public override bool Equals(object obj) => obj is TextRange r && Equals(r);
        public override int GetHashCode() => (Start * 397) ^ Length;
        public override string ToString() => IsNone ? "(none)" : $"[{Start}..{End})";
    }

    /// <summary>
    /// Line-start table for converting byte offsets to (line, column) and back. Lines and columns are 0-based;
    /// columns count UTF-16 code units, matching the editor. Lines end at '\n' (a preceding '\r' belongs to the line).
    /// </summary>
    public sealed class LineMap
    {
        private readonly int[] _starts;
        private readonly int _count;

        private LineMap(int[] starts, int count)
        {
            _starts = starts;
            _count = count;
        }

        public int LineCount => _count;

        /// <summary>Byte offset where <paramref name="line"/> starts.</summary>
        public int GetLineStart(int line) => _starts[Math.Clamp(line, 0, _count - 1)];

        public static LineMap Build(ReadOnlySpan<byte> text)
        {
            var starts = new int[Math.Max(16, text.Length / 32 + 2)];
            int n = 0;
            starts[n++] = 0;
            int p = 0;
            while (true)
            {
                int k = text.Slice(p).IndexOf((byte)'\n');
                if (k < 0) break;
                p += k + 1;
                if (n == starts.Length) Array.Resize(ref starts, starts.Length * 2);
                starts[n++] = p;
            }
            return new LineMap(starts, n);
        }

        /// <summary>0-based line containing the byte offset.</summary>
        public int GetLine(int offset)
        {
            int i = Array.BinarySearch(_starts, 0, _count, offset);
            return i >= 0 ? i : Math.Max(0, ~i - 1);
        }

        /// <summary>0-based (line, UTF-16 column) of a byte offset.</summary>
        public (int Line, int Column) GetPosition(ReadOnlySpan<byte> text, int offset)
        {
            offset = Math.Clamp(offset, 0, text.Length);
            int line = GetLine(offset);
            return (line, Utf16Length(text.Slice(_starts[line], offset - _starts[line])));
        }

        /// <summary>Byte offset of a 0-based (line, UTF-16 column); clamps to the line end.</summary>
        public int GetOffset(ReadOnlySpan<byte> text, int line, int column)
        {
            if (line < 0) return 0;
            if (line >= _count) return text.Length;
            int p = _starts[line];
            int end = line + 1 < _count ? _starts[line + 1] - 1 : text.Length;
            int col = 0;
            while (p < end && col < column)
            {
                byte b = text[p];
                int len = b < 0x80 ? 1 : b >= 0xF0 ? 4 : b >= 0xE0 ? 3 : b >= 0xC0 ? 2 : 1;
                col += len == 4 ? 2 : 1;
                p += len;
            }
            return Math.Min(p, text.Length);
        }

        /// <summary>Number of UTF-16 code units needed for a UTF-8 byte sequence (lenient on invalid input).</summary>
        public static int Utf16Length(ReadOnlySpan<byte> utf8)
        {
            int n = 0;
            for (int i = 0; i < utf8.Length; i++)
            {
                byte b = utf8[i];
                if ((b & 0xC0) == 0x80) continue; // continuation byte
                n += b >= 0xF0 ? 2 : 1;
            }
            return n;
        }
    }

    /// <summary>Detects the text encoding and produces UTF-8 bytes.</summary>
    internal static class SourceDecoder
    {
        /// <summary>
        /// Returns the UTF-8 bytes to parse. For UTF-8 input (with or without BOM) this is a copy into <paramref name="buffer"/>;
        /// for UTF-16 input (FF FE or FE FF BOM) it is a transcoded array and <paramref name="transcoded"/> is true.
        /// </summary>
        public static int Prepare(ReadOnlySpan<byte> input, out byte[] buffer, out bool transcoded, out bool rented)
        {
            if (input.Length >= 2 && ((input[0] == 0xFF && input[1] == 0xFE) || (input[0] == 0xFE && input[1] == 0xFF)))
            {
                var enc = input[0] == 0xFF ? (Encoding)new UnicodeEncoding(false, false) : new UnicodeEncoding(true, false);
                string s = enc.GetString(input.Slice(2));
                buffer = Encoding.UTF8.GetBytes(s);
                transcoded = true;
                rented = false;
                return buffer.Length;
            }
            buffer = System.Buffers.ArrayPool<byte>.Shared.Rent(Math.Max(1, input.Length));
            input.CopyTo(buffer);
            transcoded = false;
            rented = true;
            return input.Length;
        }
    }
}
