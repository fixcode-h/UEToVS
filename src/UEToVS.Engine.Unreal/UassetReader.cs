using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace UEToVS.Engine.Unreal
{
    /// <summary>What <see cref="UassetReader"/> gets out of a package header without the editor.</summary>
    public sealed class UassetInfo
    {
        /// <summary>The package name table (FNames used anywhere in the package: called functions, read properties, class names…). Empty when it could not be read.</summary>
        public string[] Names { get; set; } = Array.Empty<string>();

        /// <summary>Asset registry tag "ParentClass" as an object path, e.g. "/Script/LyraGame.LyraCharacter" or "/Game/B_Hero.B_Hero_C"; null when absent.</summary>
        public string ParentClass { get; set; }

        /// <summary>Asset registry tag "NativeParentClass": nearest C++ ancestor, e.g. "/Script/LyraGame.LyraCharacter"; null when absent.</summary>
        public string NativeParentClass { get; set; }

        /// <summary>Asset registry tag "GeneratedClass", e.g. "/Game/B_Hero.B_Hero_C"; null when absent.</summary>
        public string GeneratedClass { get; set; }

        /// <summary>UE4 / UE5 package file versions from the summary (0 when the summary could not be read).</summary>
        public int FileVersionUE4 { get; set; }
        public int FileVersionUE5 { get; set; }

        /// <summary>Why the name table is empty (null when it was read).</summary>
        public string NamesError { get; set; }

        public bool IsBlueprint => ParentClass != null || NativeParentClass != null || GeneratedClass != null;
    }

    /// <summary>
    /// Read-only, offline reader for .uasset / .umap package headers (no UE editor, no AssetRegistry): the name table
    /// from FPackageFileSummary and the Blueprint asset registry tags (ParentClass / NativeParentClass / GeneratedClass).
    /// Tags are found by scanning the header for their FString keys, which works across package versions; the name table
    /// needs the summary layout (UE 4.26 – 5.x, uncooked editor packages). Anything unexpected yields an empty result,
    /// never an exception for a malformed file.
    /// </summary>
    public static class UassetReader
    {
        public const uint PackageTag = 0x9E2A83C1;

        /// <summary>Header bytes read when the summary does not say how big the header is.</summary>
        public const int DefaultHeaderBytes = 4 * 1024 * 1024;

        /// <summary>Upper bound for a header we read (bigger headers are clipped; tags near the end may then be missed).</summary>
        public const int MaxHeaderBytes = 16 * 1024 * 1024;

        private const int MaxNames = 1_000_000;
        private const uint PkgFilterEditorOnly = 0x80000000;
        private const int Ue5PackageSavedHash = 1016;
        private const int Ue4NameHashesSerialized = 504;

        public static readonly string[] TagKeys = { "ParentClass", "NativeParentClass", "GeneratedClass" };

        public static UassetInfo Read(string path)
        {
            byte[] header;
            try
            {
                using (var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 64 * 1024))
                {
                    int want = (int)Math.Min(fs.Length, DefaultHeaderBytes);
                    header = ReadExactly(fs, want);
                    int total = PeekTotalHeaderSize(header);
                    if (total > header.Length && total <= Math.Min(fs.Length, MaxHeaderBytes))
                    {
                        fs.Position = 0;
                        header = ReadExactly(fs, total);
                    }
                }
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
            {
                return new UassetInfo { NamesError = ex.Message };
            }
            return Parse(header);
        }

        private static byte[] ReadExactly(Stream s, int count)
        {
            var buf = new byte[count];
            int read = 0;
            while (read < count)
            {
                int n = s.Read(buf, read, count - read);
                if (n <= 0) break;
                read += n;
            }
            if (read == count) return buf;
            Array.Resize(ref buf, read);
            return buf;
        }

        /// <summary>Parses header bytes (the beginning of the file).</summary>
        public static UassetInfo Parse(byte[] header)
        {
            var info = new UassetInfo();
            if (header == null || header.Length < 4 || BitConverter.ToUInt32(header, 0) != PackageTag)
            {
                info.NamesError = "not a package";
                return info;
            }
            try
            {
                var s = Summary.Read(header);
                info.FileVersionUE4 = s.FileVersionUE4;
                info.FileVersionUE5 = s.FileVersionUE5;
                info.Names = ReadNames(header, s);
            }
            catch (FormatException ex)
            {
                info.NamesError = ex.Message;
            }
            info.ParentClass = ObjectPathOf(FindTag(header, "ParentClass"));
            info.NativeParentClass = ObjectPathOf(FindTag(header, "NativeParentClass"));
            info.GeneratedClass = ObjectPathOf(FindTag(header, "GeneratedClass"));
            return info;
        }

        private static int PeekTotalHeaderSize(byte[] header)
        {
            try { return Summary.Read(header).TotalHeaderSize; }
            catch (FormatException) { return 0; } // not readable: the default window is used
        }

        private sealed class Summary
        {
            public int LegacyFileVersion, FileVersionUE4, FileVersionUE5, TotalHeaderSize, NameCount, NameOffset;
            public uint PackageFlags;

            public static Summary Read(byte[] b)
            {
                var r = new Reader(b);
                if (r.U32() != PackageTag) throw new FormatException("bad tag");
                var s = new Summary { LegacyFileVersion = r.I32() };
                if (s.LegacyFileVersion >= 0 || s.LegacyFileVersion < -9) throw new FormatException("unsupported legacy version " + s.LegacyFileVersion);
                if (s.LegacyFileVersion != -4) r.I32(); // LegacyUE3Version
                s.FileVersionUE4 = r.I32();
                if (s.LegacyFileVersion <= -8) s.FileVersionUE5 = r.I32();
                r.I32(); // FileVersionLicenseeUE
                if (s.FileVersionUE4 == 0 && s.FileVersionUE5 == 0) throw new FormatException("unversioned package (cooked)");
                if (s.FileVersionUE5 >= Ue5PackageSavedHash)
                {
                    r.Skip(20); // FIoHash SavedHash
                    s.TotalHeaderSize = r.I32();
                }
                if (s.LegacyFileVersion <= -2)
                {
                    int custom = r.I32();
                    if (custom < 0 || custom > 10000) throw new FormatException("bad custom version count");
                    r.Skip(custom * 20); // FGuid + int32 each (optimized format, LegacyFileVersion <= -5)
                }
                if (s.FileVersionUE5 < Ue5PackageSavedHash) s.TotalHeaderSize = r.I32();
                r.FString(); // PackageName / FolderName
                s.PackageFlags = r.U32();
                s.NameCount = r.I32();
                s.NameOffset = r.I32();
                if (s.TotalHeaderSize <= 0 || s.NameCount < 0 || s.NameCount > MaxNames || s.NameOffset <= 0 || s.NameOffset >= b.Length)
                    throw new FormatException("bad summary (names " + s.NameCount + " @ " + s.NameOffset + ")");
                return s;
            }
        }

        private static string[] ReadNames(byte[] header, Summary s)
        {
            var r = new Reader(header) { Position = s.NameOffset };
            bool hashes = s.FileVersionUE4 >= Ue4NameHashesSerialized || s.FileVersionUE5 > 0;
            var names = new string[s.NameCount];
            for (int i = 0; i < names.Length; i++)
            {
                names[i] = r.FString();
                if (hashes) r.Skip(4); // two uint16 hashes
            }
            return names;
        }

        /// <summary>
        /// Value of an asset registry tag: the FString that follows the FString key in the header (asset registry data is a
        /// list of FString key / value pairs). Null when the key is not there.
        /// </summary>
        public static string FindTag(byte[] header, string key)
        {
            byte[] needle = EncodeFString(key);
            int at = 0;
            while ((at = IndexOf(header, needle, at)) >= 0)
            {
                var r = new Reader(header) { Position = at + needle.Length };
                try
                {
                    string value = r.FString();
                    if (!string.IsNullOrEmpty(value) && (value.IndexOf('/') >= 0 || value.IndexOf('\'') >= 0)) return value;
                }
                catch (FormatException)
                {
                    // the bytes matched inside something else (e.g. the name table entry "ParentClass"): keep looking
                }
                at += needle.Length;
            }
            return null;
        }

        /// <summary>"/Script/CoreUObject.Class'/Script/LyraGame.LyraCharacter'" → "/Script/LyraGame.LyraCharacter"; a bare path stays as it is.</summary>
        public static string ObjectPathOf(string tagValue)
        {
            if (string.IsNullOrEmpty(tagValue)) return null;
            string v = tagValue.Trim();
            int q2 = v.LastIndexOf('\'');
            if (q2 > 0)
            {
                int q1 = v.LastIndexOf('\'', q2 - 1);
                if (q1 >= 0) v = v.Substring(q1 + 1, q2 - q1 - 1);
            }
            v = v.Trim('"');
            return v.Length == 0 || v == "None" ? null : v;
        }

        /// <summary>
        /// Short class name of an object path: "/Script/LyraGame.LyraCharacter" → "LyraCharacter",
        /// "/Game/B_Hero.B_Hero_C" → "B_Hero" (the "_C" of a generated class is dropped).
        /// </summary>
        public static string ClassName(string objectPath)
        {
            if (string.IsNullOrEmpty(objectPath)) return null;
            int dot = objectPath.LastIndexOf('.');
            string name = dot >= 0 ? objectPath.Substring(dot + 1) : objectPath.Substring(objectPath.LastIndexOf('/') + 1);
            int colon = name.LastIndexOf(':');
            if (colon >= 0) name = name.Substring(colon + 1);
            if (!IsNative(objectPath) && name.EndsWith("_C", StringComparison.Ordinal)) name = name.Substring(0, name.Length - 2);
            return name.Length == 0 ? null : name;
        }

        /// <summary>"/Script/…" paths are C++ classes.</summary>
        public static bool IsNative(string objectPath) => objectPath != null && objectPath.StartsWith("/Script/", StringComparison.Ordinal);

        /// <summary>Module of a native path: "/Script/LyraGame.LyraCharacter" → "LyraGame".</summary>
        public static string NativeModule(string objectPath)
        {
            if (!IsNative(objectPath)) return null;
            int dot = objectPath.IndexOf('.', 8);
            return dot > 8 ? objectPath.Substring(8, dot - 8) : null;
        }

        public static byte[] EncodeFString(string s)
        {
            var bytes = new byte[4 + s.Length + 1];
            BitConverter.GetBytes(s.Length + 1).CopyTo(bytes, 0);
            Encoding.ASCII.GetBytes(s, 0, s.Length, bytes, 4);
            return bytes;
        }

        private static int IndexOf(byte[] hay, byte[] needle, int start)
        {
            int last = hay.Length - needle.Length;
            byte first = needle[0];
            for (int i = start; i <= last; i++)
            {
                if (hay[i] != first) continue;
                int j = 1;
                while (j < needle.Length && hay[i + j] == needle[j]) j++;
                if (j == needle.Length) return i;
            }
            return -1;
        }

        private sealed class Reader
        {
            private readonly byte[] _b;
            public int Position;

            public Reader(byte[] b) => _b = b;

            private void Need(int n)
            {
                if (Position < 0 || Position + n > _b.Length) throw new FormatException("read past the header at " + Position);
            }

            public int I32()
            {
                Need(4);
                int v = BitConverter.ToInt32(_b, Position);
                Position += 4;
                return v;
            }

            public uint U32() => unchecked((uint)I32());

            public void Skip(int n)
            {
                Need(n);
                Position += n;
            }

            /// <summary>int32 length incl. the terminator; positive = Latin-1 bytes, negative = UTF-16 code units.</summary>
            public string FString()
            {
                int len = I32();
                if (len == 0) return string.Empty;
                if (len > 0)
                {
                    if (len > 65536) throw new FormatException("string too long");
                    Need(len);
                    if (_b[Position + len - 1] != 0) throw new FormatException("string not terminated");
                    string s = Encoding.Latin1.GetString(_b, Position, len - 1);
                    Position += len;
                    return s;
                }
                if (len < -65536) throw new FormatException("string too long");
                int bytes = -len * 2;
                Need(bytes);
                if (_b[Position + bytes - 1] != 0 || _b[Position + bytes - 2] != 0) throw new FormatException("string not terminated");
                string u = Encoding.Unicode.GetString(_b, Position, bytes - 2);
                Position += bytes;
                return u;
            }
        }
    }
}
