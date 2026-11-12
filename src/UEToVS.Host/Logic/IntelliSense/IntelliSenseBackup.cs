using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Newtonsoft.Json;

namespace UEToVS.Host.Logic.IntelliSense
{
    /// <summary>One VS property UEToVS changed, with the user's original value.</summary>
    public sealed class IntelliSenseBackupEntry
    {
        /// <summary>Logical setting key (<see cref="CppSettingDefinition.Key"/>).</summary>
        public string Key { get; set; }

        public string Page { get; set; }

        /// <summary>Actual property name found on this VS.</summary>
        public string Property { get; set; }

        /// <summary>Value before UEToVS touched it. Restore writes exactly this.</summary>
        public bool Original { get; set; }

        /// <summary>Value UEToVS wrote.</summary>
        public bool Applied { get; set; }

        public DateTime ChangedUtc { get; set; }
    }

    /// <summary>
    /// Content of %LocalAppData%\UEToVS\intellisense-backup.json. The VS settings are per user (not per solution), so is
    /// the backup. The first recorded original value of a property wins: re-applying the takeover never overwrites it.
    /// </summary>
    public sealed class IntelliSenseBackup
    {
        public const int CurrentFormat = 1;

        public int Format { get; set; } = CurrentFormat;

        public DateTime CreatedUtc { get; set; } = DateTime.UtcNow;

        /// <summary>VS version that produced the backup (informational).</summary>
        public string VsVersion { get; set; }

        public List<IntelliSenseBackupEntry> Entries { get; set; } = new List<IntelliSenseBackupEntry>();

        [JsonIgnore]
        public bool IsEmpty => Entries == null || Entries.Count == 0;

        public IntelliSenseBackupEntry Find(string page, string property) =>
            Entries?.FirstOrDefault(e => string.Equals(e.Page, page, StringComparison.OrdinalIgnoreCase) &&
                                         string.Equals(e.Property, property, StringComparison.OrdinalIgnoreCase));

        public IntelliSenseBackupEntry FindByKey(string key) =>
            Entries?.FirstOrDefault(e => string.Equals(e.Key, key, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>Loads / saves the backup file atomically (write temp + replace). Never throws on load.</summary>
    public static class IntelliSenseBackupFile
    {
        public const string FileName = "intellisense-backup.json";

        /// <summary>Backup of the "Disable Background Code Analysis" option (separate setting, separate file).</summary>
        public const string CodeAnalysisFileName = "code-analysis-backup.json";

        /// <summary>%LocalAppData%\UEToVS\intellisense-backup.json.</summary>
        public static string DefaultPath => InAppData(FileName);

        /// <summary>%LocalAppData%\UEToVS\code-analysis-backup.json.</summary>
        public static string DefaultCodeAnalysisPath => InAppData(CodeAnalysisFileName);

        private static string InAppData(string fileName)
        {
            string baseDir = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            if (string.IsNullOrEmpty(baseDir)) baseDir = Path.GetTempPath();
            return Path.Combine(baseDir, "UEToVS", fileName);
        }

        /// <summary>Null when the file does not exist; throws <see cref="InvalidDataException"/> when it is corrupt.</summary>
        public static IntelliSenseBackup Load(string path)
        {
            if (!File.Exists(path)) return null;
            string text = File.ReadAllText(path);
            try
            {
                var b = JsonConvert.DeserializeObject<IntelliSenseBackup>(text);
                if (b == null) return null;
                if (b.Entries == null) b.Entries = new List<IntelliSenseBackupEntry>();
                return b;
            }
            catch (JsonException ex)
            {
                throw new InvalidDataException("IntelliSense backup is corrupt: " + path, ex);
            }
        }

        public static void Save(string path, IntelliSenseBackup backup)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path));
            string tmp = path + ".tmp";
            File.WriteAllText(tmp, JsonConvert.SerializeObject(backup, Formatting.Indented));
            if (File.Exists(path)) File.Replace(tmp, path, null);
            else File.Move(tmp, path);
        }

        /// <summary>Deletes the backup, or saves what is left of it.</summary>
        public static void SaveOrDelete(string path, IntelliSenseBackup backup)
        {
            if (backup == null || backup.IsEmpty)
            {
                if (File.Exists(path)) File.Delete(path);
                return;
            }
            Save(path, backup);
        }
    }
}
