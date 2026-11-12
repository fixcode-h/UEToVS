using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace UEToVS.Core.Diagnostics
{
    /// <summary>
    /// Formats a <see cref="LogEvent"/> as one JSON line with a fixed field order:
    /// ts, lvl, proc, mod, evt, msg, req, ms, tid, data, ex. Dependency-free (netstandard2.0).
    /// </summary>
    public static class JsonLineFormatter
    {
        public static string Format(LogEvent e)
        {
            var sb = new StringBuilder(256);
            sb.Append('{');
            Prop(sb, "ts", e.Timestamp.ToString("yyyy-MM-ddTHH:mm:ss.fffZ", CultureInfo.InvariantCulture), true);
            Prop(sb, "lvl", e.Level.ToString().ToLowerInvariant());
            Prop(sb, "proc", e.Process);
            Prop(sb, "mod", e.Module);
            Prop(sb, "evt", e.Event);
            if (e.Message != null) Prop(sb, "msg", e.Message);
            if (e.RequestId != null) Prop(sb, "req", e.RequestId);
            if (e.ElapsedMs.HasValue)
            {
                sb.Append(",\"ms\":");
                sb.Append(Math.Round(e.ElapsedMs.Value, 2).ToString(CultureInfo.InvariantCulture));
            }
            sb.Append(",\"tid\":").Append(e.ThreadId.ToString(CultureInfo.InvariantCulture));
            if (e.Data != null && e.Data.Count > 0)
            {
                sb.Append(",\"data\":{");
                for (int i = 0; i < e.Data.Count; i++)
                {
                    if (i > 0) sb.Append(',');
                    String(sb, e.Data[i].Key);
                    sb.Append(':');
                    Value(sb, e.Data[i].Value, 0);
                }
                sb.Append('}');
            }
            if (e.Exception != null)
            {
                sb.Append(",\"ex\":");
                ExceptionValue(sb, e.Exception, 0);
            }
            sb.Append('}');
            return sb.ToString();
        }

        private static void Prop(StringBuilder sb, string name, string value, bool first = false)
        {
            if (!first) sb.Append(',');
            sb.Append('"').Append(name).Append("\":");
            String(sb, value);
        }

        private static void ExceptionValue(StringBuilder sb, Exception ex, int depth)
        {
            sb.Append("{\"type\":");
            String(sb, ex.GetType().FullName);
            sb.Append(",\"message\":");
            String(sb, ex.Message);
            sb.Append(",\"hresult\":").Append(ex.HResult.ToString(CultureInfo.InvariantCulture));
            sb.Append(",\"stack\":");
            String(sb, ex.StackTrace);
            if (ex is AggregateException agg && agg.InnerExceptions.Count > 1 && depth < 4)
            {
                sb.Append(",\"inner\":[");
                for (int i = 0; i < agg.InnerExceptions.Count && i < 8; i++)
                {
                    if (i > 0) sb.Append(',');
                    ExceptionValue(sb, agg.InnerExceptions[i], depth + 1);
                }
                sb.Append(']');
            }
            else if (ex.InnerException != null && depth < 4)
            {
                sb.Append(",\"inner\":");
                ExceptionValue(sb, ex.InnerException, depth + 1);
            }
            sb.Append('}');
        }

        public static void Value(StringBuilder sb, object v, int depth)
        {
            switch (v)
            {
                case null:
                    sb.Append("null");
                    return;
                case string s:
                    String(sb, s);
                    return;
                case bool b:
                    sb.Append(b ? "true" : "false");
                    return;
                case int _:
                case long _:
                case short _:
                case byte _:
                case uint _:
                case ulong _:
                case ushort _:
                    sb.Append(Convert.ToString(v, CultureInfo.InvariantCulture));
                    return;
                case double d:
                    sb.Append(double.IsNaN(d) || double.IsInfinity(d) ? "null" : Math.Round(d, 3).ToString(CultureInfo.InvariantCulture));
                    return;
                case float f:
                    sb.Append(float.IsNaN(f) || float.IsInfinity(f) ? "null" : Math.Round(f, 3).ToString(CultureInfo.InvariantCulture));
                    return;
                case decimal m:
                    sb.Append(m.ToString(CultureInfo.InvariantCulture));
                    return;
                case Enum en:
                    String(sb, en.ToString());
                    return;
                case DateTime dt:
                    String(sb, dt.ToUniversalTime().ToString("o", CultureInfo.InvariantCulture));
                    return;
                case TimeSpan ts:
                    sb.Append(Math.Round(ts.TotalMilliseconds, 2).ToString(CultureInfo.InvariantCulture));
                    return;
            }
            if (depth > 4)
            {
                String(sb, v.ToString());
                return;
            }
            if (v is IDictionary dict)
            {
                sb.Append('{');
                bool first = true;
                foreach (DictionaryEntry kv in dict)
                {
                    if (!first) sb.Append(',');
                    first = false;
                    String(sb, Convert.ToString(kv.Key, CultureInfo.InvariantCulture));
                    sb.Append(':');
                    Value(sb, kv.Value, depth + 1);
                }
                sb.Append('}');
                return;
            }
            if (v is IEnumerable<KeyValuePair<string, object>> pairs)
            {
                sb.Append('{');
                bool first = true;
                foreach (var kv in pairs)
                {
                    if (!first) sb.Append(',');
                    first = false;
                    String(sb, kv.Key);
                    sb.Append(':');
                    Value(sb, kv.Value, depth + 1);
                }
                sb.Append('}');
                return;
            }
            if (v is IEnumerable seq)
            {
                sb.Append('[');
                int n = 0;
                foreach (var item in seq)
                {
                    if (n > 0) sb.Append(',');
                    if (n++ >= 100)
                    {
                        String(sb, "…");
                        break;
                    }
                    Value(sb, item, depth + 1);
                }
                sb.Append(']');
                return;
            }
            String(sb, v.ToString());
        }

        public static void String(StringBuilder sb, string s)
        {
            if (s == null)
            {
                sb.Append("null");
                return;
            }
            sb.Append('"');
            foreach (char c in s)
            {
                switch (c)
                {
                    case '"': sb.Append("\\\""); break;
                    case '\\': sb.Append("\\\\"); break;
                    case '\n': sb.Append("\\n"); break;
                    case '\r': sb.Append("\\r"); break;
                    case '\t': sb.Append("\\t"); break;
                    default:
                        if (c < 0x20) sb.Append("\\u").Append(((int)c).ToString("x4", CultureInfo.InvariantCulture));
                        else sb.Append(c);
                        break;
                }
            }
            sb.Append('"');
        }
    }
}
