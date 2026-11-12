using System;
using System.Collections.Generic;
using System.IO;
using System.Windows.Media;
using UEToVS.Core.Diagnostics;
using UEToVS.UI.Logic;

namespace UEToVS.UI.Theming
{
    /// <summary>
    /// The fonts shipped in the VSIX (&lt;extension&gt;\Fonts\&lt;Family&gt;\*.ttf, see <see cref="BundledFontCatalog"/>) as WPF
    /// FontFamily objects. Nothing is read at startup: a FontFamily over a folder URI is only a name until WPF lays out
    /// text with it, and then only the needed file (weight) is mapped. Families are cached by their fallback list, so
    /// every window shares the same objects. UI thread only (like the rest of the theme).
    /// </summary>
    public static class BundledFonts
    {
        private static readonly Logger L = Log.For("UI");
        private static readonly Dictionary<string, FontFamily> Cache = new Dictionary<string, FontFamily>(StringComparer.Ordinal);
        private static string _directory;
        private static Uri _baseUri;

        /// <summary>
        /// Folder holding one sub folder per bundled family. Defaults to "Fonts" next to UEToVS.UI.dll (the extension
        /// directory); the host may set it explicitly.
        /// </summary>
        public static string Directory
        {
            get
            {
                if (_directory == null)
                {
                    string asm = typeof(BundledFonts).Assembly.Location;
                    _directory = Path.Combine(string.IsNullOrEmpty(asm) ? AppDomain.CurrentDomain.BaseDirectory : Path.GetDirectoryName(asm), BundledFontCatalog.RootFolder);
                }
                return _directory;
            }
            set
            {
                _directory = value;
                _baseUri = null;
                Cache.Clear();
            }
        }

        /// <summary>Full path of one bundled file.</summary>
        public static string FilePath(BundledFont font, string file) => Path.Combine(Directory, font.Folder, file);

        /// <summary>The family's files are all present (a trimmed or broken install degrades to the system fonts).</summary>
        public static bool IsAvailable(BundledFont font)
        {
            if (font == null) return false;
            foreach (var f in font.Files)
                if (!File.Exists(FilePath(font, f))) return false;
            return true;
        }

        /// <summary>
        /// FontFamily for a fallback list (null for an empty list). Bundled entries whose files are missing are dropped
        /// (logged once per list) so WPF falls through to the next family instead of rendering with a fallback face.
        /// </summary>
        public static FontFamily Create(IReadOnlyList<FontRef> refs)
        {
            if (refs == null || refs.Count == 0) return null;
            var usable = new List<FontRef>(refs.Count);
            var missing = new List<string>();
            foreach (var r in refs)
            {
                if (r.IsBundled && !IsAvailable(r.Bundled)) { missing.Add(r.Family); continue; }
                usable.Add(r);
            }
            if (usable.Count == 0) return null;
            string list = FontSettings.ToWpfFamilyList(usable);
            if (Cache.TryGetValue(list, out var cached)) return cached;
            FontFamily family;
            try
            {
                if (_baseUri == null) _baseUri = new Uri(Directory.TrimEnd('\\', '/') + Path.DirectorySeparatorChar);
                family = new FontFamily(_baseUri, list);
            }
            catch (Exception ex) when (ex is ArgumentException || ex is UriFormatException || ex is IOException)
            {
                L.Warn("font.family.failed", "Could not create font family", ex, ("list", list));
                return null;
            }
            Cache[list] = family;
            L.Info("font.family.created", null, ("list", list), ("missingBundled", string.Join(",", missing)), ("dir", Directory), ("cached", Cache.Count));
            return family;
        }
    }
}
