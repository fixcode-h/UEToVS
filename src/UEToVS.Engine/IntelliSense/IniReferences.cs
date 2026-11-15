using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using UEToVS.Contracts;

namespace UEToVS.Engine.IntelliSense
{
    /// <summary>
    /// v0.7.3: the C++ → ini direction of config properties. For a <c>UPROPERTY(Config)</c> member, the project ini lines
    /// that set it: <c>Key=</c> (also <c>+Key</c>, <c>-Key</c>, <c>.Key</c>, <c>!Key</c>, <c>Key[0]</c>) under a
    /// <c>[/Script/Module.Class]</c> section of the owning class or a subclass. Only files the <see cref="ConfigCatalog"/>
    /// already parsed and whose sections contain the key are read again, for line numbers.
    /// </summary>
    public static class IniReferences
    {
        /// <summary>True when the UPROPERTY specifiers contain Config or GlobalConfig.</summary>
        public static bool IsConfigProperty(string specifiers)
        {
            if (string.IsNullOrEmpty(specifiers)) return false;
            foreach (var raw in specifiers.Split(','))
            {
                string s = raw.Trim();
                int eq = s.IndexOf('=');
                if (eq >= 0) s = s.Substring(0, eq).Trim();
                if (s.Equals("Config", StringComparison.OrdinalIgnoreCase) || s.Equals("GlobalConfig", StringComparison.OrdinalIgnoreCase)) return true;
            }
            return false;
        }

        /// <summary>Class part of a <c>/Script/Module.Class</c> section name, or null.</summary>
        public static string ScriptClass(string section)
        {
            if (section == null) return null;
            string t = section.Trim();
            if (t.StartsWith("[", StringComparison.Ordinal)) t = t.Substring(1);
            int close = t.IndexOf(']');
            if (close >= 0) t = t.Substring(0, close);
            if (!t.StartsWith("/Script/", StringComparison.Ordinal)) return null;
            int dot = t.IndexOf('.', "/Script/".Length);
            return dot < 0 || dot == t.Length - 1 ? null : t.Substring(dot + 1);
        }

        /// <summary>The key an ini line sets (prefix operators and array index stripped), or null for comments, headers and blank lines.</summary>
        public static string KeyOf(string line)
        {
            if (line == null) return null;
            string t = line.TrimStart();
            if (t.Length == 0 || t[0] == ';' || t[0] == '#' || t[0] == '[') return null;
            int eq = t.IndexOf('=');
            if (eq <= 0) return null;
            string key = t.Substring(0, eq).TrimEnd();
            while (key.Length > 0 && "+-.!".IndexOf(key[0]) >= 0) key = key.Substring(1);
            int bracket = key.IndexOf('[');
            if (bracket > 0) key = key.Substring(0, bracket);
            return key.Trim();
        }

        /// <summary>
        /// Hits for <paramref name="key"/> in <paramref name="files"/> under sections of <paramref name="classes"/> (UE names
        /// without prefix, e.g. "LyraGameSettings"). <paramref name="readLines"/> reads a file (tests pass their own).
        /// </summary>
        public static List<ReferenceHit> Find(IEnumerable<IniFileData> files, ICollection<string> classes, string key, int max,
                                              Func<string, string[]> readLines = null)
        {
            var hits = new List<ReferenceHit>();
            if (files == null || classes == null || classes.Count == 0 || string.IsNullOrEmpty(key) || max <= 0) return hits;
            var wanted = new HashSet<string>(classes, StringComparer.OrdinalIgnoreCase);
            readLines = readLines ?? ReadLines;
            foreach (var f in files)
            {
                if (hits.Count >= max) break;
                if (!f.Sections.Any(s => s.Keys.ContainsKey(key) && wanted.Contains(ScriptClass(s.Name) ?? string.Empty))) continue;
                string[] lines;
                try { lines = readLines(f.Path); }
                catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException) { continue; } // file changed since the catalog read it
                bool inSection = false;
                for (int i = 0; i < lines.Length && hits.Count < max; i++)
                {
                    string t = lines[i].Trim();
                    if (t.StartsWith("[", StringComparison.Ordinal))
                    {
                        inSection = wanted.Contains(ScriptClass(t) ?? string.Empty);
                        continue;
                    }
                    if (!inSection || !string.Equals(KeyOf(lines[i]), key, StringComparison.OrdinalIgnoreCase)) continue;
                    int col = lines[i].IndexOf(key, StringComparison.OrdinalIgnoreCase);
                    hits.Add(new ReferenceHit
                    {
                        Location = new SourceLocation { FilePath = f.Path, Line = i, Column = Math.Max(0, col) },
                        Length = key.Length,
                        Kind = ReferenceKind.Write,
                        LineText = lines[i],
                        LineTextColumn = 0,
                        Context = ScriptSectionName(lines, i),
                        Confidence = ResolutionConfidence.Scoped,
                        Origin = ItemOrigin.Project,
                    });
                }
            }
            return hits;
        }

        private static string ScriptSectionName(string[] lines, int line)
        {
            for (int i = line; i >= 0; i--)
            {
                string t = lines[i].Trim();
                if (t.StartsWith("[", StringComparison.Ordinal)) return t.Trim('[', ']');
            }
            return null;
        }

        private static string[] ReadLines(string path) => File.ReadAllLines(path, Encoding.UTF8);
    }
}
