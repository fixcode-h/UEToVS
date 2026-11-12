using System;
using System.Text;

[module: System.Runtime.CompilerServices.SkipLocalsInit]

namespace UEToVS.Engine.Syntax
{
    internal static class TextUtil
    {
        /// <summary>Appends UTF-8 bytes to a builder; no allocation for short runs.</summary>
        public static void AppendUtf8(StringBuilder sb, ReadOnlySpan<byte> utf8)
        {
            if (utf8.Length == 1 && utf8[0] < 0x80)
            {
                sb.Append((char)utf8[0]);
                return;
            }
            Span<char> buf = utf8.Length <= 256 ? stackalloc char[256] : new char[utf8.Length];
            int n = Encoding.UTF8.GetChars(utf8, buf);
            sb.Append(buf.Slice(0, n));
        }

        /// <summary>Appends text with every whitespace run (including newlines and line continuations) collapsed to one space.</summary>
        public static void AppendCollapsed(StringBuilder sb, ReadOnlySpan<byte> utf8)
        {
            bool pendingSpace = false;
            int i = 0;
            while (i < utf8.Length)
            {
                byte b = utf8[i];
                if (b == (byte)' ' || b == (byte)'\t' || b == (byte)'\r' || b == (byte)'\n' || b == (byte)'\v' || b == (byte)'\f' ||
                    (b == (byte)'\\' && IsContinuation(utf8, i)))
                {
                    pendingSpace = sb.Length > 0;
                    i++;
                    continue;
                }
                if (pendingSpace)
                {
                    char last = sb[sb.Length - 1];
                    if (last != ' ') sb.Append(' ');
                    pendingSpace = false;
                }
                int j = i + 1;
                while (j < utf8.Length)
                {
                    byte c = utf8[j];
                    if (c == (byte)' ' || c == (byte)'\t' || c == (byte)'\r' || c == (byte)'\n' || c == (byte)'\\' || c == (byte)'\v' || c == (byte)'\f') break;
                    j++;
                }
                AppendUtf8(sb, utf8.Slice(i, j - i));
                i = j;
            }
        }

        private static bool IsContinuation(ReadOnlySpan<byte> s, int i)
        {
            int k = i + 1;
            while (k < s.Length && (s[k] == (byte)' ' || s[k] == (byte)'\t' || s[k] == (byte)'\r')) k++;
            return k >= s.Length || s[k] == (byte)'\n';
        }

        public static string Collapse(ReadOnlySpan<byte> utf8, StringBuilder scratch, int max)
        {
            scratch.Clear();
            AppendCollapsed(scratch, utf8);
            Truncate(scratch, max);
            return scratch.ToString();
        }

        public static void Truncate(StringBuilder sb, int max)
        {
            if (max > 1 && sb.Length > max)
            {
                sb.Length = max - 1;
                sb.Append('…');
            }
        }

        public static ReadOnlySpan<byte> Trim(ReadOnlySpan<byte> s)
        {
            int a = 0, b = s.Length;
            while (a < b && IsWs(s[a])) a++;
            while (b > a && IsWs(s[b - 1])) b--;
            return s.Slice(a, b - a);
        }

        private static bool IsWs(byte b) => b == (byte)' ' || b == (byte)'\t' || b == (byte)'\r' || b == (byte)'\n' || b == (byte)'\v' || b == (byte)'\f';
    }
}
