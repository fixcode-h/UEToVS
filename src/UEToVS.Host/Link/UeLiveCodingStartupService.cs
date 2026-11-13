using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using UEToVS.Core.Diagnostics;
using UEToVS.Host.Diagnostics;
using UEToVS.Host.Logic.Link;

namespace UEToVS.Host.Link
{
    /// <summary>
    /// File side of <see cref="UeLiveCodingStartup"/>: applies the "UE 编辑器启动时不启动 Live Coding" switch to the open
    /// project's EditorPerProjectUserSettings.ini (on UE solution open / close and when the switch changes) and restores
    /// every backed-up file for "恢复 VS 原样". The ini keeps its encoding; the backup is
    /// %LocalAppData%\UEToVS\ue-livecoding-backup.json. Logged as <c>link.ueLiveCoding.*</c>.
    /// </summary>
    internal static class UeLiveCodingStartupService
    {
        private static readonly Logger L = Log.For("Link");
        private static readonly object Sync = new object();

        private static string BackupPath
        {
            get
            {
                string baseDir = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
                if (string.IsNullOrEmpty(baseDir)) baseDir = Path.GetTempPath();
                return Path.Combine(baseDir, "UEToVS", UeLiveCodingStartup.BackupFileName);
            }
        }

        /// <summary>Background: applies <paramref name="manual"/> to the project's ini (no-op without a UE project).</summary>
        public static Task ApplyAsync(string projectDirectory, bool manual, string trigger) =>
            Safe.RunInBackgroundAsync(L, "link.ueLiveCoding.apply", () => Task.Run(() => Apply(UeLiveCodingStartup.IniPath(projectDirectory), manual, trigger)));

        /// <summary>"恢复 VS 原样": writes back every backed-up original. Returns false when a file failed.</summary>
        public static Task<bool> RestoreAllAsync(string trigger) => Task.Run(() =>
        {
            LiveCodingBackup backup;
            lock (Sync) backup = LoadBackup();
            if (backup == null || backup.IsEmpty)
            {
                L.Info("link.ueLiveCoding.restore.nothing", "UE Live Coding startup was not changed by UEToVS", ("trigger", trigger));
                return true;
            }
            bool ok = true;
            foreach (var path in backup.Originals.Keys.ToList())
                ok &= Apply(path, false, trigger);
            return ok;
        });

        private static bool Apply(string iniPath, bool manual, string trigger)
        {
            if (string.IsNullOrEmpty(iniPath)) return true;
            var sw = Stopwatch.StartNew();
            lock (Sync)
            {
                var backup = LoadBackup();
                if (backup == null)
                {
                    L.Warn("link.ueLiveCoding.backup.corrupt", "Live Coding backup unreadable; not changing the ini", ("path", BackupPath));
                    return false;
                }
                // Turning the switch off for a project UEToVS never changed: nothing to read.
                if (!manual && !backup.Originals.ContainsKey(iniPath)) return true;
                try
                {
                    string text = null;
                    Encoding encoding = new UTF8Encoding(false);
                    if (File.Exists(iniPath))
                    {
                        using (var reader = new StreamReader(iniPath, encoding, detectEncodingFromByteOrderMarks: true))
                        {
                            text = reader.ReadToEnd();
                            encoding = reader.CurrentEncoding;
                        }
                    }
                    string before = UeLiveCodingStartup.CurrentValue(text);
                    string updated = UeLiveCodingStartup.Apply(text, manual, iniPath, backup, out var change);
                    if (change != LiveCodingStartupChange.None && !string.Equals(updated, text, StringComparison.Ordinal))
                    {
                        Directory.CreateDirectory(Path.GetDirectoryName(iniPath));
                        string tmp = iniPath + ".uetovs.tmp";
                        File.WriteAllText(tmp, updated, encoding);
                        if (File.Exists(iniPath)) File.Replace(tmp, iniPath, null);
                        else File.Move(tmp, iniPath);
                    }
                    SaveBackup(backup);
                    L.Info("link.ueLiveCoding", null, ("trigger", trigger), ("manual", manual), ("change", change.ToString()), ("before", before),
                           ("ini", iniPath), ("editorRunning", EditorRunning()), ("ms", Math.Round(sw.Elapsed.TotalMilliseconds, 1)));
                    return true;
                }
                catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
                {
                    L.Warn("link.ueLiveCoding.failed", "Could not update the project's Live Coding startup setting", ex, ("ini", iniPath), ("manual", manual), ("trigger", trigger));
                    return false;
                }
            }
        }

        private static LiveCodingBackup LoadBackup()
        {
            string path = BackupPath;
            return File.Exists(path) ? LiveCodingBackup.Parse(File.ReadAllText(path)) : new LiveCodingBackup();
        }

        private static void SaveBackup(LiveCodingBackup backup)
        {
            string path = BackupPath;
            if (backup.IsEmpty)
            {
                if (File.Exists(path)) File.Delete(path);
                return;
            }
            Directory.CreateDirectory(Path.GetDirectoryName(path));
            string tmp = path + ".tmp";
            File.WriteAllText(tmp, backup.ToJson());
            if (File.Exists(path)) File.Replace(tmp, path, null);
            else File.Move(tmp, path);
        }

        /// <summary>For the log: a running editor reads the ini at its next start (and may write its own copy on exit).</summary>
        private static bool EditorRunning()
        {
            try
            {
                var p = Process.GetProcessesByName("UnrealEditor");
                bool any = p.Length > 0;
                foreach (var x in p) x.Dispose();
                return any;
            }
            catch (InvalidOperationException) { return false; }
        }
    }
}
