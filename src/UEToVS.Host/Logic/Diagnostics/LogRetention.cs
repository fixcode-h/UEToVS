using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace UEToVS.Host.Logic.Diagnostics
{
    /// <summary>
    /// Applies the user's log retention settings (days / total size) to a Logs folder. Core's JsonLinesFileSink cleans up
    /// with its defaults when it is created; the host re-applies the configured limits after startup and when the
    /// settings change. Never deletes <paramref name="keep"/> (the files currently being written).
    /// </summary>
    public static class LogRetention
    {
        /// <summary>Files written more recently than this are never deleted (another process may be writing them).</summary>
        public static readonly TimeSpan ActiveWindow = TimeSpan.FromMinutes(10);

        public static List<string> Apply(string logDirectory, int retentionDays, long maxTotalBytes, ICollection<string> keep, DateTime? nowUtc = null)
        {
            var deleted = new List<string>();
            if (string.IsNullOrEmpty(logDirectory) || !Directory.Exists(logDirectory)) return deleted;
            var now = nowUtc ?? DateTime.UtcNow;
            var cutoff = now.AddDays(-Math.Max(1, retentionDays));
            var active = now - ActiveWindow;
            long total = 0;
            foreach (var f in new DirectoryInfo(logDirectory).GetFiles("*.log").OrderByDescending(f => f.LastWriteTimeUtc))
            {
                total += f.Length;
                if (keep != null && keep.Any(k => string.Equals(k, f.FullName, StringComparison.OrdinalIgnoreCase))) continue;
                if (f.LastWriteTimeUtc > active) continue; // possibly still open by another process (engine, other VS)
                if (f.LastWriteTimeUtc < cutoff || total > maxTotalBytes)
                {
                    try
                    {
                        f.Delete();
                        deleted.Add(f.Name);
                    }
                    catch (IOException) { }
                    catch (UnauthorizedAccessException) { }
                }
            }
            return deleted;
        }
    }
}
