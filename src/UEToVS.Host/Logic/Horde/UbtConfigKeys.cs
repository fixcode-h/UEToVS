using System;
using System.Collections.Generic;
using System.IO;
using System.Text.RegularExpressions;

namespace UEToVS.Host.Logic.Horde
{
    /// <summary>
    /// The BuildConfiguration.xml keys an engine's UnrealBuildTool actually reads, found by scanning its C# sources for
    /// <c>[XmlConfigFile(Category = "...", Name = "...")]</c> fields. Lets UEToVS adapt to each engine version (5.4, 5.5,
    /// …) without a hard-coded table. Sections the scan never saw are treated as unknown (everything allowed), so an
    /// installed engine without sources, or a section declared outside UnrealBuildTool, never blocks a write.
    /// </summary>
    public sealed class UbtConfigKeys
    {
        private static readonly Regex Attribute = new Regex(
            @"\[\s*XmlConfigFile\s*(?:\((?<args>[^)]*)\))?\s*\]\s*(?:\[[^\]]*\]\s*)*(?:(?:public|private|protected|internal|static|readonly|override|virtual|new)\s+)*[\w<>\[\],.?]+(?:\s*<[^>]*>)?\s+(?<name>\w+)\s*(?:;|=|\{)",
            RegexOptions.Compiled | RegexOptions.CultureInvariant);
        private static readonly Regex ClassDecl = new Regex(@"\b(?:class|struct)\s+(?<name>\w+)", RegexOptions.Compiled | RegexOptions.CultureInvariant);
        private static readonly Regex Category = new Regex(@"Category\s*=\s*""(?<v>[^""]*)""", RegexOptions.Compiled | RegexOptions.CultureInvariant);
        private static readonly Regex NameArg = new Regex(@"Name\s*=\s*""(?<v>[^""]*)""", RegexOptions.Compiled | RegexOptions.CultureInvariant);

        private readonly Dictionary<string, HashSet<string>> _sections = new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);

        /// <summary>No sources scanned: every key is allowed.</summary>
        public static UbtConfigKeys Unknown { get; } = new UbtConfigKeys();

        public int Count { get; private set; }
        public int FilesScanned { get; private set; }

        public IEnumerable<string> Sections => _sections.Keys;

        /// <summary>False only when the scan saw this section and the key is not in it.</summary>
        public bool Supports(string section, string key) =>
            !_sections.TryGetValue(section, out var keys) || keys.Contains(key);

        public bool Knows(string section) => _sections.ContainsKey(section);

        /// <summary>Adds the keys declared in one C# source file.</summary>
        public void AddSource(string text)
        {
            if (string.IsNullOrEmpty(text) || text.IndexOf("XmlConfigFile", StringComparison.Ordinal) < 0) return;
            FilesScanned++;
            foreach (Match m in Attribute.Matches(text))
            {
                string args = m.Groups["args"].Value;
                string section = Value(Category, args) ?? EnclosingClass(text, m.Index);
                string key = Value(NameArg, args) ?? m.Groups["name"].Value;
                if (string.IsNullOrEmpty(section) || string.IsNullOrEmpty(key)) continue;
                if (!_sections.TryGetValue(section, out var set)) _sections[section] = set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                if (set.Add(key)) Count++;
            }
        }

        private static string Value(Regex r, string args)
        {
            if (string.IsNullOrEmpty(args)) return null;
            var m = r.Match(args);
            return m.Success ? m.Groups["v"].Value : null;
        }

        private static string EnclosingClass(string text, int index)
        {
            string name = null;
            foreach (Match c in ClassDecl.Matches(text))
            {
                if (c.Index > index) break;
                name = c.Groups["name"].Value;
            }
            return name;
        }

        /// <summary>
        /// Scans &lt;Engine&gt;/Source/Programs/UnrealBuildTool (and the shared UBA/Horde libraries next to it). Returns
        /// <see cref="Unknown"/> when the engine has no UBT sources (installed build) or nothing was found.
        /// </summary>
        public static UbtConfigKeys Scan(string engineRoot, Action<string, Exception> onError = null)
        {
            if (string.IsNullOrEmpty(engineRoot)) return Unknown;
            var result = new UbtConfigKeys();
            string programs = Path.Combine(engineRoot, "Engine", "Source", "Programs");
            foreach (var dir in new[] { Path.Combine(programs, "UnrealBuildTool"), Path.Combine(programs, "Shared", "EpicGames.UBA"), Path.Combine(programs, "Shared", "EpicGames.Horde") })
            {
                if (!Directory.Exists(dir)) continue;
                IEnumerable<string> files;
                try { files = Directory.EnumerateFiles(dir, "*.cs", SearchOption.AllDirectories); }
                catch (Exception ex) { onError?.Invoke(dir, ex); continue; }
                foreach (var f in files)
                {
                    try { result.AddSource(File.ReadAllText(f)); }
                    catch (Exception ex) { onError?.Invoke(f, ex); }
                }
            }
            return result.Count == 0 ? Unknown : result;
        }
    }
}
