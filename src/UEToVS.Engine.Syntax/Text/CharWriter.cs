using System;
using System.Text;

namespace UEToVS.Engine.Syntax
{
    /// <summary>
    /// Fixed-capacity character buffer (usually stack-allocated) for building one-line display strings from UTF-8 tokens
    /// without intermediate allocations. Writes past the capacity are dropped and the result gets an ellipsis.
    /// </summary>
    internal ref struct CharWriter
    {
        private readonly Span<char> _buf;
        private int _len;
        private bool _overflow;

        public CharWriter(Span<char> buffer)
        {
            _buf = buffer;
            _len = 0;
            _overflow = false;
        }

        public int Length => _len;

        public void Append(char c)
        {
            if (_len < _buf.Length) _buf[_len++] = c;
            else _overflow = true;
        }

        public void Append(string s)
        {
            if (string.IsNullOrEmpty(s)) return;
            int n = Math.Min(s.Length, _buf.Length - _len);
            s.AsSpan(0, n).CopyTo(_buf.Slice(_len));
            _len += n;
            if (n < s.Length) _overflow = true;
        }

        /// <summary>Appends UTF-8 text; ASCII is copied directly, anything else is decoded.</summary>
        public void AppendUtf8(ReadOnlySpan<byte> utf8)
        {
            int room = _buf.Length - _len;
            int i = 0;
            int lim = Math.Min(utf8.Length, room);
            var dst = _buf.Slice(_len);
            for (; i < lim; i++)
            {
                byte b = utf8[i];
                if (b >= 0x80) break;
                dst[i] = (char)b;
            }
            _len += i;
            if (i == utf8.Length) return;
            if (i == lim && i < utf8.Length && utf8[i] < 0x80)
            {
                _overflow = true;
                return;
            }
            var rest = utf8.Slice(i);
            if (Encoding.UTF8.GetMaxCharCount(rest.Length) <= _buf.Length - _len)
                _len += Encoding.UTF8.GetChars(rest, _buf.Slice(_len));
            else
                Append(Encoding.UTF8.GetString(rest));
        }

        /// <summary>Appends UTF-8 text with whitespace runs (and line continuations) collapsed to single spaces.</summary>
        public void AppendCollapsed(ReadOnlySpan<byte> utf8)
        {
            int i = 0;
            bool space = false;
            while (i < utf8.Length)
            {
                byte b = utf8[i];
                if (b == (byte)' ' || b == (byte)'\t' || b == (byte)'\r' || b == (byte)'\n' || b == (byte)'\\')
                {
                    space = true;
                    i++;
                    continue;
                }
                if (space && _len > 0 && _buf[_len - 1] != ' ') Append(' ');
                space = false;
                int j = i + 1;
                while (j < utf8.Length && utf8[j] != (byte)' ' && utf8[j] != (byte)'\t' && utf8[j] != (byte)'\r' && utf8[j] != (byte)'\n' && utf8[j] != (byte)'\\') j++;
                AppendUtf8(utf8.Slice(i, j - i));
                i = j;
            }
        }

        /// <summary>The text, cut to <paramref name="max"/> characters with a trailing ellipsis when longer or overflowed.</summary>
        public string ToString(int max)
        {
            if (_len == 0) return string.Empty;
            if (max > 1 && _len > max)
            {
                _len = max;
                _buf[max - 1] = '…';
            }
            else if (_overflow)
            {
                _buf[_len - 1] = '…';
            }
            return new string(_buf.Slice(0, _len));
        }

        /// <summary>Substring of what was written (no truncation applied).</summary>
        public string Slice(int start, int length) =>
            length <= 0 ? string.Empty : new string(_buf.Slice(start, Math.Min(length, _len - start)));
    }
}
