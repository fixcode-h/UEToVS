using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Newtonsoft.Json;
using UEToVS.Host.Logic.Settings;
using UEToVS.UI.Logic;

namespace UEToVS.Host.Logic.Fonts
{
    /// <summary>A font VS should use for one category (resolved from the settings).</summary>
    public sealed class VsFontTarget
    {
        public VsFontCategory Category { get; set; }

        /// <summary>Family name written to VS.</summary>
        public string Face { get; set; }

        /// <summary>Points; null = keep VS's current size.</summary>
        public int? Size { get; set; }

        /// <summary>Bundled family that must be installed (per user) first; null for an installed font.</summary>
        public BundledFont Bundled { get; set; }

        public override string ToString() => Category.Id + "=" + Face + (Size.HasValue ? "@" + Size.Value : "");
    }

    /// <summary>One category's font as UEToVS found it before its first write (the value restored later).</summary>
    public sealed class VsFontBackupEntry
    {
        public string Category { get; set; }
        public Guid CategoryGuid { get; set; }

        /// <summary>Face VS reported (default or user value).</summary>
        public string Face { get; set; }

        public int PointSize { get; set; }
        public int CharSet { get; set; }

        /// <summary>The user had no own value (VS default in effect): restore reverts to the default instead of writing <see cref="Face"/>.</summary>
        public bool WasDefault { get; set; }

        public DateTime ChangedUtc { get; set; }
    }

    /// <summary>
    /// %LocalAppData%\UEToVS\vs-fonts-backup.json. Per user (VS fonts are per user). The first original of a category
    /// wins: later applies never overwrite it, so restoring always gives back the font from before UEToVS.
    /// </summary>
    public sealed class VsFontBackup
    {
        public const int CurrentFormat = 1;
        public const string FileName = "vs-fonts-backup.json";

        public int Format { get; set; } = CurrentFormat;
        public DateTime CreatedUtc { get; set; } = DateTime.UtcNow;
        public string VsVersion { get; set; }
        public List<VsFontBackupEntry> Entries { get; set; } = new List<VsFontBackupEntry>();

        [JsonIgnore]
        public bool IsEmpty => Entries == null || Entries.Count == 0;

        public VsFontBackupEntry Find(string category) =>
            Entries?.FirstOrDefault(e => string.Equals(e.Category, category, StringComparison.OrdinalIgnoreCase));

        /// <summary>Adds <paramref name="entry"/> unless the category already has one. Returns whether it was added.</summary>
        public bool AddIfMissing(VsFontBackupEntry entry)
        {
            if (entry == null || Find(entry.Category) != null) return false;
            if (Entries == null) Entries = new List<VsFontBackupEntry>();
            Entries.Add(entry);
            return true;
        }

        public bool Remove(string category) => Entries != null && Entries.RemoveAll(e => string.Equals(e.Category, category, StringComparison.OrdinalIgnoreCase)) > 0;

        public static string DefaultPath
        {
            get
            {
                string baseDir = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
                if (string.IsNullOrEmpty(baseDir)) baseDir = Path.GetTempPath();
                return Path.Combine(baseDir, "UEToVS", FileName);
            }
        }

        /// <summary>Null when missing; throws <see cref="InvalidDataException"/> when corrupt (never silently dropped: it holds the user's fonts).</summary>
        public static VsFontBackup Load(string path)
        {
            if (!File.Exists(path)) return null;
            try
            {
                var b = JsonConvert.DeserializeObject<VsFontBackup>(File.ReadAllText(path));
                if (b != null && b.Entries == null) b.Entries = new List<VsFontBackupEntry>();
                return b;
            }
            catch (JsonException ex)
            {
                throw new InvalidDataException("VS font backup is corrupt: " + path, ex);
            }
        }

        /// <summary>Atomic save (temp + replace); an empty backup deletes the file.</summary>
        public static void SaveOrDelete(string path, VsFontBackup backup)
        {
            if (backup == null || backup.IsEmpty)
            {
                if (File.Exists(path)) File.Delete(path);
                return;
            }
            Directory.CreateDirectory(Path.GetDirectoryName(path));
            string tmp = path + ".tmp";
            File.WriteAllText(tmp, JsonConvert.SerializeObject(backup, Formatting.Indented));
            if (File.Exists(path)) File.Replace(tmp, path, null);
            else File.Move(tmp, path);
        }
    }

    /// <summary>What to do with VS's fonts for the current settings.</summary>
    public sealed class VsFontPlan
    {
        /// <summary>Categories to back up before writing (not in the backup yet).</summary>
        public List<VsFontCategory> Backup { get; } = new List<VsFontCategory>();

        /// <summary>Categories to set.</summary>
        public List<VsFontTarget> Write { get; } = new List<VsFontTarget>();

        /// <summary>Backup entries to restore (category no longer managed, or management turned off).</summary>
        public List<VsFontBackupEntry> Restore { get; } = new List<VsFontBackupEntry>();

        /// <summary>Bundled families to install per user before writing.</summary>
        public List<BundledFont> Install { get; } = new List<BundledFont>();

        public bool IsEmpty => Backup.Count == 0 && Write.Count == 0 && Restore.Count == 0;
    }

    /// <summary>Pure planning for <c>Fonts/VsFontService</c>: settings → targets → plan against the backup.</summary>
    public static class VsFontPlanner
    {
        /// <summary>The font each category should have; categories set to "不修改" (or resolving to "跟随 VS") are absent.</summary>
        public static List<VsFontTarget> Targets(HostSettings s)
        {
            var fonts = UiFonts(s);
            var map = VsFontSettingsText.Parse(s.VsFonts);
            var list = new List<VsFontTarget>();
            foreach (var c in VsFontCategories.All)
            {
                var v = map[c.Id];
                var family = v.Family;
                if (family.Kind == FontChoiceKind.SameAsCode) family = fonts.Code;
                else if (family.Kind == FontChoiceKind.SameAsUi) family = fonts.UiLatin;
                if (family.Kind != FontChoiceKind.Bundled && family.Kind != FontChoiceKind.System) continue; // keep / follow VS
                list.Add(new VsFontTarget
                {
                    Category = c,
                    Face = family.Kind == FontChoiceKind.Bundled ? family.Bundled?.Family ?? family.Family : family.Family,
                    Size = v.Size > 0 ? v.Size : (int?)null,
                    Bundled = family.Bundled,
                });
            }
            return list;
        }

        public static FontSettings UiFonts(HostSettings s) => new FontSettings
        {
            UiLatin = FontChoice.Parse(s.UiLatinFont),
            UiCjk = FontChoice.Parse(s.UiCjkFont),
            Code = FontChoice.Parse(s.CodeFont),
        };

        /// <summary>
        /// Managed: back up the targets not yet in <paramref name="backup"/>, write every target, restore backed-up
        /// categories that are no longer targets. Not managed: restore everything in the backup.
        /// </summary>
        public static VsFontPlan Plan(bool manage, IReadOnlyList<VsFontTarget> targets, VsFontBackup backup)
        {
            var plan = new VsFontPlan();
            var entries = backup?.Entries ?? new List<VsFontBackupEntry>();
            if (!manage)
            {
                plan.Restore.AddRange(entries);
                return plan;
            }
            foreach (var t in targets ?? Array.Empty<VsFontTarget>())
            {
                if (backup?.Find(t.Category.Id) == null) plan.Backup.Add(t.Category);
                plan.Write.Add(t);
                if (t.Bundled != null && !plan.Install.Contains(t.Bundled)) plan.Install.Add(t.Bundled);
            }
            foreach (var e in entries)
                if (targets == null || !targets.Any(t => string.Equals(t.Category.Id, e.Category, StringComparison.OrdinalIgnoreCase)))
                    plan.Restore.Add(e);
            return plan;
        }

        /// <summary>True when VS already shows <paramref name="t"/> (no write needed).</summary>
        public static bool IsApplied(VsFontTarget t, string currentFace, int currentSize) =>
            string.Equals((currentFace ?? string.Empty).Trim(), t.Face, StringComparison.OrdinalIgnoreCase) &&
            (!t.Size.HasValue || t.Size.Value == currentSize);
    }

    /// <summary>Pure parts of the per-user font install (no admin): file → registry value name.</summary>
    public static class FontInstallNames
    {
        /// <summary>HKCU\Software\Microsoft\Windows NT\CurrentVersion\Fonts.</summary>
        public const string RegistryKey = @"Software\Microsoft\Windows NT\CurrentVersion\Fonts";

        /// <summary>"Maple Mono NL Bold (TrueType)" for MapleMonoNL-Bold.ttf of family "Maple Mono NL".</summary>
        public static string RegistryValueName(BundledFont font, string fileName)
        {
            string stem = Path.GetFileNameWithoutExtension(fileName ?? string.Empty);
            int dash = stem.LastIndexOf('-');
            string style = dash >= 0 ? stem.Substring(dash + 1) : "Regular";
            string name = string.Equals(style, "Regular", StringComparison.OrdinalIgnoreCase) ? font.Family : font.Family + " " + SplitWords(style);
            return name + " (TrueType)";
        }

        private static string SplitWords(string style)
        {
            var sb = new System.Text.StringBuilder();
            foreach (char ch in style)
            {
                if (sb.Length > 0 && char.IsUpper(ch)) sb.Append(' ');
                sb.Append(ch);
            }
            return sb.ToString();
        }
    }
}
