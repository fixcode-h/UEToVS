using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using UEToVS.Core.Diagnostics;

namespace UEToVS.Engine.Includes
{
    /// <summary>Include folders of one module as UBT evaluated its rules (v0.11.0, design 后续规划-代码诊断与提示 1.7).</summary>
    public sealed class UbtModuleExport
    {
        public string Name { get; set; }

        /// <summary>Public (and public system) include folders: dependents compile with these too.</summary>
        public List<string> Public { get; } = new List<string>();

        /// <summary>Private and internal include folders: the module's own files only.</summary>
        public List<string> Private { get; } = new List<string>();
    }

    /// <summary>
    /// Reads the file UBT writes with <c>-Mode=JsonExport</c> (the host runs it in the background when a Build.cs changes):
    /// per module the include folders UBT computed from the rules, which settles every Build.cs expression the include model
    /// cannot evaluate. Parsed leniently — any property of a module whose name ends with "IncludePaths" — because the layout
    /// moved between engine versions. Re-read when the file's time stamp changes (checked at most every few seconds).
    /// </summary>
    public sealed class UbtExport
    {
        private static readonly Logger L = Log.For("Includes");
        private static readonly TimeSpan CheckEvery = TimeSpan.FromSeconds(5);

        private readonly Func<string> _path;
        private readonly object _gate = new object();
        private Dictionary<string, UbtModuleExport> _modules = new Dictionary<string, UbtModuleExport>(StringComparer.OrdinalIgnoreCase);
        private DateTime _stamp = DateTime.MinValue;
        private DateTime _lastCheck = DateTime.MinValue;
        private int _version;

        public UbtExport(Func<string> path) => _path = path;

        /// <summary>The usual place: &lt;solution&gt;\.vs\UEToVS\ubt\export.json (next to the index and the logs).</summary>
        public static string DefaultPath(string solutionDir) =>
            string.IsNullOrEmpty(solutionDir) ? null : Path.Combine(solutionDir, ".vs", "UEToVS", "ubt", "export.json");

        /// <summary>Changes whenever a new export was read (the include model rebuilds its module views then).</summary>
        public int Version
        {
            get
            {
                Check();
                return _version;
            }
        }

        public UbtModuleExport Get(string module)
        {
            Check();
            lock (_gate) return module != null && _modules.TryGetValue(module, out var m) ? m : null;
        }

        private void Check()
        {
            var now = DateTime.UtcNow;
            lock (_gate)
            {
                if (now - _lastCheck < CheckEvery) return;
                _lastCheck = now;
            }
            string path = null;
            try
            {
                path = _path?.Invoke();
                if (string.IsNullOrEmpty(path) || !File.Exists(path)) return;
                var stamp = File.GetLastWriteTimeUtc(path);
                lock (_gate) if (stamp == _stamp) return;
                var modules = Parse(File.ReadAllText(path));
                lock (_gate)
                {
                    _modules = modules;
                    _stamp = stamp;
                    _version++;
                }
                L.Info("includes.ubtExport.loaded", null, ("modules", modules.Count), ("file", path));
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException || ex is JsonException || ex is InvalidOperationException)
            {
                L.Warn("includes.ubtExport.failed", "Reading the UBT export failed", ex, ("file", path));
            }
        }

        /// <summary>Modules of a JsonExport document (Modules as an object keyed by name, or as an array of objects with Name).</summary>
        public static Dictionary<string, UbtModuleExport> Parse(string json)
        {
            var result = new Dictionary<string, UbtModuleExport>(StringComparer.OrdinalIgnoreCase);
            using var doc = JsonDocument.Parse(json, new JsonDocumentOptions { AllowTrailingCommas = true, CommentHandling = JsonCommentHandling.Skip });
            if (doc.RootElement.ValueKind != JsonValueKind.Object || !doc.RootElement.TryGetProperty("Modules", out var modules)) return result;
            if (modules.ValueKind == JsonValueKind.Object)
            {
                foreach (var p in modules.EnumerateObject())
                    Add(result, p.Value, p.Name);
            }
            else if (modules.ValueKind == JsonValueKind.Array)
            {
                foreach (var m in modules.EnumerateArray())
                    Add(result, m, null);
            }
            return result;
        }

        private static void Add(Dictionary<string, UbtModuleExport> result, JsonElement m, string key)
        {
            if (m.ValueKind != JsonValueKind.Object) return;
            string name = m.TryGetProperty("Name", out var n) && n.ValueKind == JsonValueKind.String ? n.GetString() : key;
            if (string.IsNullOrEmpty(name)) return;
            var e = new UbtModuleExport { Name = name };
            foreach (var p in m.EnumerateObject())
            {
                if (!p.Name.EndsWith("IncludePaths", StringComparison.Ordinal) || p.Value.ValueKind != JsonValueKind.Array) continue;
                // PublicIncludePathModules lists module names, not folders (it does not end with "Paths")
                var target = p.Name.StartsWith("Public", StringComparison.Ordinal) ? e.Public : e.Private;
                foreach (var v in p.Value.EnumerateArray())
                    if (v.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(v.GetString())) target.Add(v.GetString());
            }
            result[name] = e;
        }
    }
}
