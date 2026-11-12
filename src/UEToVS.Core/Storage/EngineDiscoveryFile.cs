using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using UEToVS.Core.Diagnostics;

namespace UEToVS.Core.Storage
{
    /// <summary>
    /// &lt;solution&gt;/.vs/UEToVS/engine.json: written by a running UEToVS.Engine (server mode) so other local clients —
    /// the MCP server for AI agents (src/UEToVS.Mcp), diagnostics tools — can find its named pipe without the VS host.
    /// Deleted on clean exit; a stale file (crashed engine) is recognised by its pid no longer running.
    /// <code>
    /// { "pipe": "uetovs-1234-89abcdef", "pid": 4321, "protocolVersion": 1, "engineVersion": "0.3.0",
    ///   "solutionDir": "D:\\Proj", "startedUtc": "2026-11-12T08:00:00.0000000Z", "formatVersion": 1 }
    /// </code>
    /// Format is additive only (readers ignore unknown keys). Readers parse it with their own JSON library.
    /// </summary>
    public sealed class EngineDiscoveryFile
    {
        public const string FileName = "engine.json";
        public const int CurrentFormatVersion = 1;

        public string Pipe { get; set; }
        public int Pid { get; set; }
        public int ProtocolVersion { get; set; }
        public string EngineVersion { get; set; }
        public string SolutionDir { get; set; }
        public DateTime StartedUtc { get; set; }
        public int FormatVersion { get; set; } = CurrentFormatVersion;

        public static string PathFor(StoragePaths paths) => Path.Combine(paths.Root, FileName);

        public string ToJson()
        {
            var sb = new StringBuilder();
            sb.Append("{\n");
            void Field(string name, object value, bool last = false)
            {
                sb.Append("  ");
                JsonLineFormatter.String(sb, name);
                sb.Append(": ");
                JsonLineFormatter.Value(sb, value, 0);
                sb.Append(last ? "\n" : ",\n");
            }
            Field("pipe", Pipe);
            Field("pid", Pid);
            Field("protocolVersion", ProtocolVersion);
            Field("engineVersion", EngineVersion);
            Field("solutionDir", SolutionDir);
            Field("startedUtc", StartedUtc.ToUniversalTime().ToString("o", CultureInfo.InvariantCulture));
            Field("formatVersion", FormatVersion, last: true);
            sb.Append('}');
            return sb.ToString();
        }

        /// <summary>Atomically writes the file (temp + replace). Returns the path, or null on failure (logged).</summary>
        public string Write(StoragePaths paths)
        {
            string path = PathFor(paths);
            try
            {
                Directory.CreateDirectory(paths.Root);
                string tmp = path + "." + Pid.ToString(CultureInfo.InvariantCulture) + ".tmp";
                File.WriteAllText(tmp, ToJson(), new UTF8Encoding(false));
                if (File.Exists(path)) File.Delete(path);
                File.Move(tmp, path);
                return path;
            }
            catch (Exception ex)
            {
                Log.For("Discovery").Warn("discovery.write.failed", "Could not write the engine discovery file", ex, ("path", path));
                return null;
            }
        }

        /// <summary>Deletes the file if it still belongs to <paramref name="pid"/> (a newer engine may have replaced it).</summary>
        public static void DeleteIfOwned(StoragePaths paths, int pid)
        {
            string path = PathFor(paths);
            try
            {
                if (!File.Exists(path)) return;
                string text = File.ReadAllText(path);
                if (text.IndexOf("\"pid\": " + pid.ToString(CultureInfo.InvariantCulture) + ",", StringComparison.Ordinal) < 0) return;
                File.Delete(path);
            }
            catch (Exception ex)
            {
                Log.For("Discovery").Debug("discovery.delete.failed", ex.Message, ("path", path));
            }
        }
    }
}
