using System;
using System.Collections.Generic;
using System.IO;

namespace UEToVS.Host.Logic.IntelliSense
{
    /// <summary>
    /// Finds a Visual Assist installation by scanning VS extension directories for its binaries (VA_X*.dll).
    /// Complements the package-GUID check done in VS (IVsShell.IsPackageInstalled). Pure file-system logic.
    /// </summary>
    public static class VisualAssistDetection
    {
        /// <summary>Visual Assist (Whole Tomato) VS package GUID. TODO(verify on Windows): confirm against an installed VA.</summary>
        public const string PackageGuid = "44630d46-96b5-488c-8df9-26e21db8c1a3";

        public static readonly string[] BinaryPatterns = { "VA_X*.dll", "VAssist*.dll" };

        /// <summary>
        /// Looks one level below each extensions root (VS installs every extension in its own folder) and returns the first
        /// matching binary, or null. Never throws; does not recurse deeper to keep it cheap.
        /// </summary>
        public static string FindInExtensionDirectories(IEnumerable<string> extensionRoots)
        {
            if (extensionRoots == null) return null;
            foreach (var root in extensionRoots)
            {
                if (string.IsNullOrEmpty(root)) continue;
                try
                {
                    if (!Directory.Exists(root)) continue;
                    string hit = FindIn(root);
                    if (hit != null) return hit;
                    foreach (var sub in Directory.EnumerateDirectories(root))
                    {
                        hit = FindIn(sub);
                        if (hit != null) return hit;
                        // Publisher folder layout: Extensions\<Publisher>\<Name>\<Version>.
                        if (Path.GetFileName(sub).IndexOf("Whole Tomato", StringComparison.OrdinalIgnoreCase) >= 0) return sub;
                    }
                }
                catch (Exception)
                {
                    // access denied etc.: keep scanning other roots
                }
            }
            return null;
        }

        private static string FindIn(string dir)
        {
            foreach (var pattern in BinaryPatterns)
            {
                foreach (var f in Directory.EnumerateFiles(dir, pattern, SearchOption.TopDirectoryOnly)) return f;
            }
            return null;
        }
    }
}
