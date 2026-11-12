using System;
using System.Collections.Generic;
using System.Linq;

namespace UEToVS.Mcp.Data
{
    /// <summary>
    /// The argument list of a UE reflection macro (<c>UPROPERTY(EditAnywhere, ReplicatedUsing=OnRep_Health, meta=(ClampMin="0"))</c>),
    /// as stored in the index (<see cref="UEToVS.Contracts.SymbolInfo.Specifiers"/>: the text between the parentheses,
    /// whitespace collapsed), split into flags, key=value specifiers and meta entries. Keys keep their spelling; lookups
    /// ignore case like UHT does.
    /// </summary>
    public sealed class UeSpecifiers
    {
        private static readonly string[] RpcSpecifiers = { "Server", "Client", "NetMulticast" };

        public List<string> Flags { get; } = new List<string>();
        public Dictionary<string, string> Values { get; } = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        public Dictionary<string, string> Meta { get; } = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        public bool Has(string name) => Flags.Any(f => string.Equals(f, name, StringComparison.OrdinalIgnoreCase)) || Values.ContainsKey(name);

        public string Value(string name) => Values.TryGetValue(name, out var v) ? v : null;

        /// <summary>Replicated / ReplicatedUsing=OnRep_X (properties).</summary>
        public bool IsReplicated => Has("Replicated") || Values.ContainsKey("ReplicatedUsing");

        /// <summary>Server / Client / NetMulticast (functions), or null.</summary>
        public string Rpc => RpcSpecifiers.FirstOrDefault(Has);

        /// <summary>Reliable / Unreliable for RPCs, or null.</summary>
        public string Reliability => Has("Reliable") ? "Reliable" : Has("Unreliable") ? "Unreliable" : null;

        /// <summary>Config / GlobalConfig (property saved to ini).</summary>
        public bool IsConfig => Has("Config") || Has("GlobalConfig");

        public static UeSpecifiers Parse(string text)
        {
            var s = new UeSpecifiers();
            if (string.IsNullOrWhiteSpace(text)) return s;
            foreach (var part in SplitTopLevel(text))
            {
                int eq = IndexOfTopLevel(part, '=');
                if (eq < 0)
                {
                    if (part.Length > 0) s.Flags.Add(part);
                    continue;
                }
                string key = part.Substring(0, eq).Trim();
                string value = part.Substring(eq + 1).Trim();
                if (key.Length == 0) continue;
                if (string.Equals(key, "meta", StringComparison.OrdinalIgnoreCase))
                {
                    if (value.StartsWith("(", StringComparison.Ordinal) && value.EndsWith(")", StringComparison.Ordinal)) value = value.Substring(1, value.Length - 2);
                    foreach (var m in SplitTopLevel(value))
                    {
                        int meq = IndexOfTopLevel(m, '=');
                        if (meq < 0) s.Meta[m] = string.Empty;
                        else
                        {
                            string mk = m.Substring(0, meq).Trim();
                            if (mk.Length > 0) s.Meta[mk] = Unquote(m.Substring(meq + 1).Trim());
                        }
                    }
                    continue;
                }
                s.Values[key] = Unquote(value);
            }
            return s;
        }

        /// <summary>All specifier names (flags and keys, not meta) for filtering.</summary>
        public IEnumerable<string> Names => Flags.Concat(Values.Keys);

        private static string Unquote(string v)
        {
            if (v.StartsWith("TEXT(", StringComparison.Ordinal) && v.EndsWith(")", StringComparison.Ordinal)) v = v.Substring(5, v.Length - 6).Trim();
            return v.Length >= 2 && v[0] == '"' && v[v.Length - 1] == '"' ? v.Substring(1, v.Length - 2) : v;
        }

        /// <summary>Splits at commas outside parentheses and quotes; parts are trimmed, empty ones dropped.</summary>
        public static List<string> SplitTopLevel(string text)
        {
            var parts = new List<string>();
            int depth = 0, start = 0;
            bool quoted = false;
            for (int i = 0; i < text.Length; i++)
            {
                char c = text[i];
                if (c == '"' && (i == 0 || text[i - 1] != '\\')) quoted = !quoted;
                else if (quoted) continue;
                else if (c == '(') depth++;
                else if (c == ')') depth = Math.Max(0, depth - 1);
                else if (c == ',' && depth == 0)
                {
                    Add(text.Substring(start, i - start));
                    start = i + 1;
                }
            }
            Add(text.Substring(start));
            return parts;

            void Add(string p)
            {
                p = p.Trim();
                if (p.Length > 0) parts.Add(p);
            }
        }

        private static int IndexOfTopLevel(string text, char ch)
        {
            int depth = 0;
            bool quoted = false;
            for (int i = 0; i < text.Length; i++)
            {
                char c = text[i];
                if (c == '"') quoted = !quoted;
                else if (quoted) continue;
                else if (c == '(') depth++;
                else if (c == ')') depth--;
                else if (c == ch && depth == 0) return i;
            }
            return -1;
        }
    }
}
