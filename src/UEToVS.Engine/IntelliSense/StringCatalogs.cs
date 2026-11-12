using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using UEToVS.Contracts;
using UEToVS.Core.Diagnostics;
using UEToVS.Engine.Indexing;
using UEToVS.Engine.Navigation;
using UEToVS.Engine.Unreal;

namespace UEToVS.Engine.IntelliSense
{
    /// <summary>A gameplay tag and where it is declared.</summary>
    public readonly struct TagDefinition
    {
        public TagDefinition(string tag, string comment, string source)
        {
            Tag = tag;
            Comment = comment;
            Source = source;
        }

        public string Tag { get; }
        public string Comment { get; }

        /// <summary>File name the tag comes from (DefaultGameplayTags.ini, MyTags.cpp...).</summary>
        public string Source { get; }
    }

    /// <summary>One [Section] of an ini file: keys (leading + - . ! stripped) with a short value preview.</summary>
    public sealed class IniSectionData
    {
        public IniSectionData(string name) => Name = name;

        public string Name { get; }
        public Dictionary<string, string> Keys { get; } = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
    }

    public sealed class IniFileData
    {
        public string Path { get; set; }
        public string Name { get; set; }
        public long Length { get; set; }
        public long Ticks { get; set; }
        public List<IniSectionData> Sections { get; } = new List<IniSectionData>();
        public List<TagDefinition> Tags { get; } = new List<TagDefinition>();
        public int KeyCount { get; set; }
    }

    /// <summary>
    /// The project's (and project plugins') Config/*.ini files, parsed lazily for string completion: sections and keys for
    /// GConfig calls, and the gameplay tags of DefaultGameplayTags.ini / Config/Tags/*.ini. Files come from the index's
    /// file table (listed, never parsed by the indexer); a file is re-read only when its size or time stamp changes and the
    /// table is re-checked at most every <see cref="RecheckMs"/>. Bounded: big files are skipped, keys and tags are capped.
    /// Thread-safe.
    /// </summary>
    public sealed class ConfigCatalog
    {
        private static readonly Logger L = Log.For("IS");

        public const long MaxFileBytes = 2L * 1024 * 1024;
        public const int MaxKeysPerFile = 20000;
        public const int MaxTotalKeys = 200000;
        public const int MaxTags = 100000;

        public int RecheckMs { get; set; } = 2000;

        private readonly object _gate = new object();
        private readonly Dictionary<string, IniFileData> _files = new Dictionary<string, IniFileData>(StringComparer.OrdinalIgnoreCase);
        private IniFileData[] _list = Array.Empty<IniFileData>();
        private FileEntry[] _source;
        private long _checkedAt;
        private int _version;

        /// <summary>Changes whenever a file was added, re-read or dropped.</summary>
        public int Version => Volatile.Read(ref _version);

        public static bool IsTagFile(string path)
        {
            if (string.IsNullOrEmpty(path)) return false;
            string norm = path.Replace('\\', '/');
            return norm.EndsWith("/DefaultGameplayTags.ini", StringComparison.OrdinalIgnoreCase) ||
                   norm.IndexOf("/Config/Tags/", StringComparison.OrdinalIgnoreCase) >= 0;
        }

        /// <summary>The parsed project ini files (refreshed when due).</summary>
        public IniFileData[] Files(FileTable table)
        {
            if (table == null) return Array.Empty<IniFileData>();
            var snap = table.Snapshot();
            long now = Environment.TickCount64;
            lock (_gate)
            {
                if (ReferenceEquals(snap, _source) && now - _checkedAt < RecheckMs) return _list;
                Refresh(snap);
                _source = snap;
                _checkedAt = now;
                return _list;
            }
        }

        private void Refresh(FileEntry[] snap)
        {
            var sw = Stopwatch.StartNew();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            int read = 0, skipped = 0, totalKeys = 0, totalTags = 0;
            bool changed = false;
            foreach (var e in snap)
            {
                var f = e.File;
                if (f == null || f.Kind != SourceFileKind.Config || !TypeText.IsProject(f.Origin)) continue;
                seen.Add(f.Path);
                long len, ticks;
                try
                {
                    var fi = new FileInfo(f.Path);
                    if (!fi.Exists) continue;
                    len = fi.Length;
                    ticks = fi.LastWriteTimeUtc.Ticks;
                }
                catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
                {
                    continue;
                }
                if (_files.TryGetValue(f.Path, out var old) && old.Length == len && old.Ticks == ticks)
                {
                    totalKeys += old.KeyCount;
                    totalTags += old.Tags.Count;
                    continue;
                }
                if (len > MaxFileBytes || totalKeys >= MaxTotalKeys)
                {
                    skipped++;
                    _files.Remove(f.Path);
                    continue;
                }
                IniFileData data;
                try
                {
                    data = Parse(f.Path, File.ReadAllText(f.Path), Math.Max(0, MaxTags - totalTags));
                }
                catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
                {
                    L.Debug("is.config.read_failed", null, ("file", f.Path), ("error", ex.Message));
                    continue;
                }
                data.Length = len;
                data.Ticks = ticks;
                _files[f.Path] = data;
                totalKeys += data.KeyCount;
                totalTags += data.Tags.Count;
                read++;
                changed = true;
            }
            foreach (var path in _files.Keys.Where(p => !seen.Contains(p)).ToList())
            {
                _files.Remove(path);
                changed = true;
            }
            if (!changed && _list.Length == _files.Count) return;
            _list = _files.Values.OrderBy(x => x.Path, StringComparer.OrdinalIgnoreCase).ToArray();
            Interlocked.Increment(ref _version);
            L.Info("is.config.refresh", null, ("files", _list.Length), ("read", read), ("skipped", skipped), ("keys", totalKeys), ("tags", totalTags),
                ("ms", Math.Round(sw.Elapsed.TotalMilliseconds, 2)));
        }

        /// <summary>Parses ini text: sections / keys, and gameplay tags when the file is a tag file.</summary>
        public static IniFileData Parse(string path, string text, int maxTags = MaxTags)
        {
            var data = new IniFileData { Path = path, Name = System.IO.Path.GetFileName(path) };
            bool tagFile = IsTagFile(path);
            IniSectionData section = null;
            var sections = new Dictionary<string, IniSectionData>(StringComparer.OrdinalIgnoreCase);
            using (var reader = new StringReader(text ?? string.Empty))
            {
                string raw;
                while ((raw = reader.ReadLine()) != null)
                {
                    string line = raw.Trim();
                    if (line.Length == 0 || line[0] == ';' || line[0] == '#') continue;
                    if (line[0] == '[')
                    {
                        int close = line.IndexOf(']');
                        string name = close > 1 ? line.Substring(1, close - 1).Trim() : null;
                        if (string.IsNullOrEmpty(name)) continue;
                        if (!sections.TryGetValue(name, out section))
                        {
                            section = new IniSectionData(name);
                            sections[name] = section;
                            data.Sections.Add(section);
                        }
                        continue;
                    }
                    int eq = line.IndexOf('=');
                    if (eq <= 0) continue;
                    string key = line.Substring(0, eq).Trim().TrimStart('+', '-', '.', '!').Trim();
                    if (key.Length == 0) continue;
                    string value = line.Substring(eq + 1).Trim();
                    if (tagFile && data.Tags.Count < maxTags && key.Equals("GameplayTagList", StringComparison.OrdinalIgnoreCase))
                    {
                        string tag = QuotedValue(value, "Tag");
                        if (!string.IsNullOrEmpty(tag)) data.Tags.Add(new TagDefinition(tag, NullIfEmpty(QuotedValue(value, "DevComment")), data.Name));
                    }
                    if (section == null || data.KeyCount >= MaxKeysPerFile) continue;
                    if (!section.Keys.ContainsKey(key))
                    {
                        section.Keys[key] = value.Length > 80 ? value.Substring(0, 79) + "…" : value;
                        data.KeyCount++;
                    }
                }
            }
            return data;
        }

        private static string NullIfEmpty(string s) => string.IsNullOrWhiteSpace(s) ? null : s;

        /// <summary>Value of <c>Name="…"</c> inside a struct literal like <c>(Tag="A.B",DevComment="x")</c>.</summary>
        public static string QuotedValue(string text, string name)
        {
            int i = 0;
            while (true)
            {
                i = text.IndexOf(name, i, StringComparison.OrdinalIgnoreCase);
                if (i < 0) return null;
                bool startOk = i == 0 || !(char.IsLetterOrDigit(text[i - 1]) || text[i - 1] == '_');
                int j = i + name.Length;
                while (j < text.Length && text[j] == ' ') j++;
                if (startOk && j < text.Length && text[j] == '=')
                {
                    j++;
                    while (j < text.Length && text[j] == ' ') j++;
                    if (j < text.Length && text[j] == '"')
                    {
                        int e = text.IndexOf('"', j + 1);
                        return e < 0 ? text.Substring(j + 1) : text.Substring(j + 1, e - j - 1);
                    }
                    int end = j;
                    while (end < text.Length && text[end] != ',' && text[end] != ')') end++;
                    return text.Substring(j, end - j).Trim();
                }
                i = j;
            }
        }
    }

    /// <summary>A node of the gameplay tag tree: one segment below its parent.</summary>
    public sealed class TagNode
    {
        public string Segment { get; set; }
        public string FullTag { get; set; }
        public string Comment { get; set; }
        public string Source { get; set; }
        public bool HasChildren { get; set; }

        /// <summary>Declared itself (not only implied by a longer tag).</summary>
        public bool Declared { get; set; }
    }

    /// <summary>
    /// Gameplay tags for completion: DefaultGameplayTags.ini / Config/Tags/*.ini (via <see cref="ConfigCatalog"/>) plus
    /// native tags defined in code with UE_DEFINE_GAMEPLAY_TAG / _COMMENT / _STATIC. Code files are found through the
    /// index's identifier occurrences (project files record the macro names), so no extra source scan is needed; each is
    /// re-read only when it changed. The tree (parent tag → child segments) is rebuilt only when a source changed.
    /// </summary>
    public sealed class GameplayTagCatalog
    {
        private static readonly Logger L = Log.For("IS");

        public static readonly string[] DefineMacros = { "UE_DEFINE_GAMEPLAY_TAG", "UE_DEFINE_GAMEPLAY_TAG_COMMENT", "UE_DEFINE_GAMEPLAY_TAG_STATIC" };

        private static readonly Regex DefineRegex = new Regex(
            @"\bUE_DEFINE_GAMEPLAY_TAG(?:_COMMENT|_STATIC)?\s*\(\s*\w+\s*,\s*(?:TEXT\s*\(\s*)?""([^""\r\n]*)""\s*\)?(?:\s*,\s*(?:TEXT\s*\(\s*)?""([^""\r\n]*)"")?",
            RegexOptions.Compiled | RegexOptions.CultureInvariant);

        public const int MaxCodeFiles = 2000;
        public const long MaxCodeFileBytes = 4L * 1024 * 1024;

        public int RecheckMs { get; set; } = 3000;

        private readonly ConfigCatalog _config;
        private readonly object _gate = new object();
        private readonly Dictionary<string, (long Length, long Ticks, List<TagDefinition> Tags)> _code =
            new Dictionary<string, (long, long, List<TagDefinition>)>(StringComparer.OrdinalIgnoreCase);
        private long _codeCheckedAt;
        private int _codeVersion;
        private int _builtConfigVersion = -1, _builtCodeVersion = -1;
        private Dictionary<string, List<TagNode>> _tree = new Dictionary<string, List<TagNode>>(StringComparer.OrdinalIgnoreCase);
        private int _count;

        public GameplayTagCatalog(ConfigCatalog config) => _config = config ?? new ConfigCatalog();

        public int Count => _count;

        /// <summary>The tag tree: parent ("" for the roots) → children, current as of the sources.</summary>
        public Dictionary<string, List<TagNode>> Tree(ISymbolIndexBackend backend, FileTable files, CancellationToken ct)
        {
            var ini = _config.Files(files);
            int configVersion = _config.Version;
            lock (_gate)
            {
                RefreshCode(backend, ct);
                if (configVersion == _builtConfigVersion && _codeVersion == _builtCodeVersion) return _tree;
                var all = new List<TagDefinition>();
                foreach (var f in ini) all.AddRange(f.Tags);
                foreach (var c in _code.Values) all.AddRange(c.Tags);
                _tree = Build(all);
                _count = all.Count;
                _builtConfigVersion = configVersion;
                _builtCodeVersion = _codeVersion;
                L.Info("is.tags.build", null, ("tags", all.Count), ("parents", _tree.Count), ("iniFiles", ini.Count(f => f.Tags.Count > 0)), ("codeFiles", _code.Count));
                return _tree;
            }
        }

        private void RefreshCode(ISymbolIndexBackend backend, CancellationToken ct)
        {
            long now = Environment.TickCount64;
            if (backend == null || (_codeCheckedAt != 0 && now - _codeCheckedAt < RecheckMs)) return;
            _codeCheckedAt = now;
            var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var macro in DefineMacros)
                foreach (var p in backend.FindOccurrenceFiles(macro, ct))
                {
                    paths.Add(p);
                    if (paths.Count >= MaxCodeFiles) break;
                }
            bool changed = false;
            foreach (var p in _code.Keys.Where(k => !paths.Contains(k)).ToList())
            {
                _code.Remove(p);
                changed = true;
            }
            foreach (var p in paths)
            {
                try
                {
                    var fi = new FileInfo(p);
                    if (!fi.Exists || fi.Length > MaxCodeFileBytes) continue;
                    if (_code.TryGetValue(p, out var old) && old.Length == fi.Length && old.Ticks == fi.LastWriteTimeUtc.Ticks) continue;
                    _code[p] = (fi.Length, fi.LastWriteTimeUtc.Ticks, Extract(File.ReadAllText(p), Path.GetFileName(p)));
                    changed = true;
                }
                catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
                {
                    L.Debug("is.tags.read_failed", null, ("file", p), ("error", ex.Message));
                }
            }
            if (changed) _codeVersion++;
        }

        /// <summary>UE_DEFINE_GAMEPLAY_TAG* definitions in C++ text.</summary>
        public static List<TagDefinition> Extract(string text, string source)
        {
            var list = new List<TagDefinition>();
            if (string.IsNullOrEmpty(text) || text.IndexOf("UE_DEFINE_GAMEPLAY_TAG", StringComparison.Ordinal) < 0) return list;
            foreach (Match m in DefineRegex.Matches(text))
            {
                string tag = m.Groups[1].Value.Trim();
                if (tag.Length == 0) continue;
                string comment = m.Groups[2].Success ? m.Groups[2].Value : null;
                list.Add(new TagDefinition(tag, string.IsNullOrWhiteSpace(comment) ? null : comment, source));
            }
            return list;
        }

        /// <summary>Parent → children with implied intermediate nodes ("A.B.C" implies "A" and "A.B").</summary>
        public static Dictionary<string, List<TagNode>> Build(IEnumerable<TagDefinition> tags)
        {
            var nodes = new Dictionary<string, TagNode>(StringComparer.OrdinalIgnoreCase);
            var tree = new Dictionary<string, List<TagNode>>(StringComparer.OrdinalIgnoreCase);
            foreach (var t in tags)
            {
                string tag = t.Tag.Trim().Trim('.');
                if (tag.Length == 0) continue;
                int start = 0;
                string parent = string.Empty;
                while (true)
                {
                    int dot = tag.IndexOf('.', start);
                    string full = dot < 0 ? tag : tag.Substring(0, dot);
                    if (!nodes.TryGetValue(full, out var node))
                    {
                        node = new TagNode { Segment = full.Substring(start), FullTag = full };
                        nodes[full] = node;
                        if (!tree.TryGetValue(parent, out var list)) tree[parent] = list = new List<TagNode>();
                        list.Add(node);
                    }
                    if (dot < 0)
                    {
                        node.Declared = true;
                        node.Comment ??= t.Comment;
                        node.Source ??= t.Source;
                        break;
                    }
                    node.HasChildren = true;
                    node.Source ??= t.Source;
                    parent = full;
                    start = dot + 1;
                }
            }
            return tree;
        }
    }
}
