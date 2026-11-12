using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.Json;
using UEToVS.Mcp.Data;

namespace UEToVS.Mcp.Tools
{
    /// <summary>
    /// Validated access to a tools/call "arguments" object. Every accessor throws <see cref="ToolException"/>
    /// ("invalid_argument") with a message that tells the agent exactly what to fix. Unknown argument names are
    /// rejected too (a misspelt filter silently ignored is worse than an error).
    /// </summary>
    public sealed class ToolArgs
    {
        private readonly IReadOnlyDictionary<string, JsonElement> _args;

        public ToolArgs(IReadOnlyDictionary<string, JsonElement> args, IEnumerable<string> allowed)
        {
            _args = args ?? new Dictionary<string, JsonElement>();
            var allowedSet = new HashSet<string>(allowed ?? Array.Empty<string>(), StringComparer.Ordinal);
            var unknown = _args.Keys.Where(k => !allowedSet.Contains(k)).ToList();
            if (unknown.Count > 0)
                throw Invalid($"unknown argument(s): {string.Join(", ", unknown)}. Valid: {string.Join(", ", allowedSet.OrderBy(x => x, StringComparer.Ordinal))}");
        }

        public bool Has(string name) => _args.TryGetValue(name, out var v) && v.ValueKind != JsonValueKind.Null && v.ValueKind != JsonValueKind.Undefined;

        public string String(string name, bool required = false, int maxLength = 1024)
        {
            if (!Has(name))
            {
                if (required) throw Invalid($"'{name}' is required");
                return null;
            }
            var v = _args[name];
            if (v.ValueKind != JsonValueKind.String) throw Invalid($"'{name}' must be a string");
            string s = v.GetString();
            if (required && string.IsNullOrWhiteSpace(s)) throw Invalid($"'{name}' must not be empty");
            if (s != null && s.Length > maxLength) throw Invalid($"'{name}' is longer than {maxLength} characters");
            return s;
        }

        public int Int(string name, int defaultValue, int min, int max)
        {
            if (!Has(name)) return defaultValue;
            var v = _args[name];
            int value;
            if (v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out int i)) value = i;
            else if (v.ValueKind == JsonValueKind.String && int.TryParse(v.GetString(), out int j)) value = j; // lenient: "20"
            else throw Invalid($"'{name}' must be an integer");
            if (value < min || value > max) throw Invalid($"'{name}' must be between {min} and {max} (got {value})");
            return value;
        }

        /// <summary>Optional integer; null when absent.</summary>
        public int? OptionalInt(string name, int min, int max) => Has(name) ? Int(name, 0, min, max) : (int?)null;

        public bool Bool(string name, bool defaultValue)
        {
            if (!Has(name)) return defaultValue;
            var v = _args[name];
            if (v.ValueKind == JsonValueKind.True) return true;
            if (v.ValueKind == JsonValueKind.False) return false;
            if (v.ValueKind == JsonValueKind.String && bool.TryParse(v.GetString(), out bool b)) return b;
            throw Invalid($"'{name}' must be true or false");
        }

        /// <summary>One of <paramref name="allowed"/> (case-insensitive), returned lower-case.</summary>
        public string Choice(string name, string defaultValue, params string[] allowed)
        {
            string s = String(name);
            if (s == null) return defaultValue;
            string lower = s.Trim().ToLowerInvariant();
            if (!allowed.Contains(lower)) throw Invalid($"'{name}' must be one of: {string.Join(", ", allowed)} (got '{s}')");
            return lower;
        }

        /// <summary>A string array; a single string is accepted as a one-element array.</summary>
        public List<string> StringList(string name, int maxItems = 32)
        {
            if (!Has(name)) return new List<string>();
            var v = _args[name];
            var list = new List<string>();
            if (v.ValueKind == JsonValueKind.String)
            {
                foreach (var part in v.GetString().Split(new[] { ',' }, StringSplitOptions.RemoveEmptyEntries))
                    if (part.Trim().Length > 0) list.Add(part.Trim());
            }
            else if (v.ValueKind == JsonValueKind.Array)
            {
                foreach (var item in v.EnumerateArray())
                {
                    if (item.ValueKind != JsonValueKind.String) throw Invalid($"'{name}' must be an array of strings");
                    string s = item.GetString()?.Trim();
                    if (!string.IsNullOrEmpty(s)) list.Add(s);
                }
            }
            else throw Invalid($"'{name}' must be an array of strings");
            if (list.Count > maxItems) throw Invalid($"'{name}' has more than {maxItems} items");
            return list;
        }

        /// <summary>Compact one-line summary for logs (strings cut at 120 chars).</summary>
        public string Summary()
        {
            var sb = new StringBuilder();
            foreach (var kv in _args.OrderBy(k => k.Key, StringComparer.Ordinal))
            {
                if (sb.Length > 0) sb.Append(' ');
                sb.Append(kv.Key).Append('=');
                string raw = kv.Value.ValueKind == JsonValueKind.String ? kv.Value.GetString() : kv.Value.GetRawText();
                if (raw != null && raw.Length > 120) raw = raw.Substring(0, 120) + "…";
                sb.Append(raw);
            }
            return sb.ToString();
        }

        public static ToolException Invalid(string message) => new ToolException("invalid_argument", message);
    }
}
