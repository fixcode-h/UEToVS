using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using UEToVS.Core.Diagnostics;
using UEToVS.Engine.Index.Storage;

namespace UEToVS.Engine.Index.Segments
{
    /// <summary>manifest.json: which segment files form the index and which of their files are tombstoned.</summary>
    internal sealed class Manifest
    {
        public const string FileName = "manifest.json";
        private static readonly Logger L = Log.For("Index");

        private static readonly JsonSerializerOptions Json = new JsonSerializerOptions
        {
            WriteIndented = true,
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            Converters = { new JsonStringEnumConverter() },
        };

        public int FormatVersion { get; set; } = (int)SegmentFormat.Version;
        public string KnowledgeBaseVersion { get; set; }
        public string EngineBuildId { get; set; }
        public int NextSegmentNumber { get; set; } = 1;
        public DateTime WrittenUtc { get; set; }
        public List<ManifestSegment> Segments { get; set; } = new List<ManifestSegment>();

        public static Manifest Load(string dir, out string error)
        {
            error = null;
            string path = Path.Combine(dir, FileName);
            if (!File.Exists(path))
            {
                error = "missing";
                return null;
            }
            try
            {
                var m = JsonSerializer.Deserialize<Manifest>(File.ReadAllText(path), Json);
                if (m?.Segments == null)
                {
                    error = "empty manifest";
                    return null;
                }
                return m;
            }
            catch (Exception ex)
            {
                L.Error("index.manifest.read_failed", "Could not read index manifest; index will be rebuilt", ex, ("path", path));
                error = ex.Message;
                return null;
            }
        }

        /// <summary>Atomic write: temp file then replace.</summary>
        public void Save(string dir)
        {
            WrittenUtc = DateTime.UtcNow;
            string path = Path.Combine(dir, FileName);
            string tmp = path + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(this, Json));
            File.Move(tmp, path, overwrite: true);
        }
    }

    internal sealed class ManifestSegment
    {
        public string File { get; set; }
        public SegmentKind Kind { get; set; }

        /// <summary>Paths (as stored) of files in this segment that are superseded or deleted.</summary>
        public List<string> Tombstones { get; set; } = new List<string>();
    }
}
