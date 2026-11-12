using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using UEToVS.Contracts;
using UEToVS.Core.Diagnostics;
using UEToVS.Engine.Navigation;

namespace UEToVS.Engine.IntelliSense
{
    /// <summary>
    /// v0.4.5 Quick Info on <c>GENERATED_BODY()</c> / <c>GENERATED_UCLASS_BODY()</c>: what UHT generated for that line, read
    /// from the class's <c>X.generated.h</c> (located by <see cref="GeneratedBodyLocator"/>) with the per-line sub-macros
    /// (<c>…_12_INCLASS_NO_PURE_DECLS</c>, <c>…_12_RPC_WRAPPERS…</c>) expanded one statement per line. On demand only: the
    /// header location is remembered for a few seconds and the parsed <c>#define</c>s per file until its timestamp or size
    /// changes, so hovering again costs a File.GetLastWriteTimeUtc.
    /// </summary>
    public static class GeneratedCodePreview
    {
        private static readonly Logger L = Log.For("IntelliSense");

        public const int MaxLines = 400;
        public const int MaxChars = 16 * 1024;
        private const int MaxCachedFiles = 32;
        private static readonly TimeSpan LocateTtl = TimeSpan.FromSeconds(10);

        private sealed class Defines
        {
            public DateTime WriteTimeUtc;
            public long Length;
            public Dictionary<string, string> Bodies;
        }

        private static readonly Dictionary<string, Defines> Files = new Dictionary<string, Defines>(StringComparer.OrdinalIgnoreCase);
        private static readonly LinkedList<string> Lru = new LinkedList<string>();
        private static readonly Dictionary<string, (string File, DateTime At)> Located = new Dictionary<string, (string, DateTime)>(StringComparer.OrdinalIgnoreCase);

        /// <summary>Fills the GeneratedCode* fields of <paramref name="resp"/> for the body macro on 0-based <paramref name="line"/>.</summary>
        public static void Fill(QuickInfoResponse resp, string headerPath, Func<string> headerText, string word, int line)
        {
            if (!GeneratedBodyLocator.IsBodyMacro(word) || string.IsNullOrEmpty(headerPath)) return;
            string genName = GeneratedBodyLocator.GeneratedHeaderName(headerText());
            if (genName == null)
            {
                resp.GeneratedCodeState = GeneratedCodeState.NotBuilt;
                return;
            }
            string file = Locate(headerPath, genName);
            if (file == null)
            {
                resp.GeneratedCodeState = GeneratedCodeState.NotBuilt;
                resp.GeneratedCodeFile = genName;
                L.Debug("intellisense.generatedBody", null, ("header", Path.GetFileName(headerPath)), ("state", "notBuilt"));
                return;
            }
            resp.GeneratedCodeFile = file;
            var defines = Read(file);
            string macro = defines == null ? null : FindLineMacro(defines.Keys, line + 1, GeneratedBodyLocator.MacroSuffix(word));
            if (macro == null)
            {
                resp.GeneratedCodeState = GeneratedCodeState.Stale;
                L.Debug("intellisense.generatedBody", null, ("header", Path.GetFileName(headerPath)), ("state", "stale"), ("line", line + 1));
                return;
            }
            resp.GeneratedCode = Expand(defines, macro, line + 1);
            resp.GeneratedCodeState = GeneratedCodeState.Available;
        }

        private static string Locate(string headerPath, string genName)
        {
            string key = headerPath + "|" + genName;
            var now = DateTime.UtcNow;
            lock (Located)
            {
                if (Located.TryGetValue(key, out var hit) && now - hit.At < LocateTtl && (hit.File == null || File.Exists(hit.File))) return hit.File;
            }
            string file = GeneratedBodyLocator.FindGeneratedHeader(headerPath, genName);
            lock (Located)
            {
                if (Located.Count > 256) Located.Clear();
                Located[key] = (file, now);
            }
            return file;
        }

        /// <summary>The #define bodies of a .generated.h (cached by write time and size).</summary>
        private static Dictionary<string, string> Read(string file)
        {
            DateTime t;
            long len;
            try
            {
                var info = new FileInfo(file);
                if (!info.Exists) return null;
                t = info.LastWriteTimeUtc;
                len = info.Length;
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException) { return null; }
            lock (Files)
            {
                if (Files.TryGetValue(file, out var d) && d.WriteTimeUtc == t && d.Length == len)
                {
                    Lru.Remove(file);
                    Lru.AddFirst(file);
                    return d.Bodies;
                }
            }
            Dictionary<string, string> bodies;
            try { bodies = ParseDefines(File.ReadAllLines(file)); }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
            {
                L.Warn("intellisense.generatedBody.readFailed", ex.Message, ("file", file));
                return null;
            }
            lock (Files)
            {
                Files[file] = new Defines { WriteTimeUtc = t, Length = len, Bodies = bodies };
                Lru.Remove(file);
                Lru.AddFirst(file);
                while (Lru.Count > MaxCachedFiles)
                {
                    Files.Remove(Lru.Last.Value);
                    Lru.RemoveLast();
                }
            }
            return bodies;
        }

        /// <summary>Object-like <c>#define NAME body</c> (continuation lines kept as separate lines, the trailing backslashes removed).</summary>
        public static Dictionary<string, string> ParseDefines(IEnumerable<string> lines)
        {
            var map = new Dictionary<string, string>(StringComparer.Ordinal);
            string name = null;
            var body = new StringBuilder();
            foreach (var raw in lines)
            {
                if (name == null)
                {
                    var t = raw.TrimStart();
                    if (!t.StartsWith("#define", StringComparison.Ordinal)) continue;
                    t = t.Substring(7).TrimStart();
                    int end = 0;
                    while (end < t.Length && (char.IsLetterOrDigit(t[end]) || t[end] == '_')) end++;
                    if (end == 0 || end < t.Length && t[end] == '(') continue; // function-like macros are not UHT's per-line macros
                    name = t.Substring(0, end);
                    body.Clear();
                    Append(body, t.Substring(end), out bool more);
                    if (!more) { map[name] = body.ToString(); name = null; }
                    continue;
                }
                Append(body, raw, out bool cont);
                if (!cont) { map[name] = body.ToString(); name = null; }
            }
            if (name != null) map[name] = body.ToString();
            return map;
        }

        private static void Append(StringBuilder body, string line, out bool continues)
        {
            string l = line.TrimEnd();
            continues = l.EndsWith("\\", StringComparison.Ordinal);
            if (continues) l = l.Substring(0, l.Length - 1).TrimEnd();
            if (l.Trim().Length == 0) return;
            if (body.Length > 0) body.Append('\n');
            body.Append(l.Trim());
        }

        /// <summary>The define ending in <c>_&lt;line&gt;&lt;suffix&gt;</c>, e.g. FID_Game_Foo_h_12_GENERATED_BODY.</summary>
        public static string FindLineMacro(IEnumerable<string> names, int line1, string suffix)
        {
            string want = "_" + line1.ToString(System.Globalization.CultureInfo.InvariantCulture) + suffix;
            foreach (var n in names)
                if (n.EndsWith(want, StringComparison.Ordinal)) return n;
            return null;
        }

        /// <summary>
        /// The macro's body with every line that is itself a define of the file replaced by that define's lines (recursively,
        /// with a "// &lt;short name&gt;" header), PRAGMA_* lines dropped; at most <see cref="MaxLines"/> lines.
        /// </summary>
        public static string Expand(IReadOnlyDictionary<string, string> defines, string macro, int line1)
        {
            var sb = new StringBuilder();
            int lines = 0;
            bool cut = false;
            string prefixMarker = "_" + line1.ToString(System.Globalization.CultureInfo.InvariantCulture) + "_";

            void Emit(string text, int indent)
            {
                if (lines >= MaxLines || sb.Length >= MaxChars) { cut = true; return; }
                if (sb.Length > 0) sb.Append('\n');
                sb.Append(' ', indent * 4).Append(text);
                lines++;
            }

            void Walk(string name, int indent, HashSet<string> path)
            {
                if (!defines.TryGetValue(name, out var body) || !path.Add(name)) return;
                foreach (var raw in body.Split('\n'))
                {
                    string l = raw.Trim();
                    if (l.Length == 0 || l.StartsWith("PRAGMA_", StringComparison.Ordinal)) continue;
                    if (IsIdentifier(l) && defines.ContainsKey(l))
                    {
                        if (defines[l].Trim().Length == 0) continue; // empty sub-macro
                        Emit("// " + ShortName(l, prefixMarker), indent);
                        Walk(l, indent, path);
                        continue;
                    }
                    bool access = l == "public:" || l == "private:" || l == "protected:";
                    Emit(l, access ? Math.Max(0, indent - 1) : indent);
                }
                path.Remove(name);
            }

            Walk(macro, 1, new HashSet<string>(StringComparer.Ordinal));
            if (cut) sb.Append("\n…");
            return sb.ToString();
        }

        private static bool IsIdentifier(string s)
        {
            if (s.Length == 0 || !(char.IsLetter(s[0]) || s[0] == '_')) return false;
            foreach (char c in s)
                if (!(char.IsLetterOrDigit(c) || c == '_')) return false;
            return true;
        }

        /// <summary>FID_Game_Source_Foo_h_12_INCLASS_NO_PURE_DECLS → INCLASS_NO_PURE_DECLS.</summary>
        public static string ShortName(string name, string lineMarker)
        {
            int i = name.LastIndexOf(lineMarker, StringComparison.Ordinal);
            return i >= 0 ? name.Substring(i + lineMarker.Length) : name;
        }

        /// <summary>Tests: forget cached locations and files.</summary>
        internal static void ClearCaches()
        {
            lock (Files) { Files.Clear(); Lru.Clear(); }
            lock (Located) Located.Clear();
        }
    }
}
