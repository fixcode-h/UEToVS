using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using Microsoft.Win32;
using UEToVS.Core.Diagnostics;
using UEToVS.Host.Logic.Fonts;
using UEToVS.UI.Logic;
using UEToVS.UI.Theming;

namespace UEToVS.Host.Fonts
{
    /// <summary>
    /// Installs a bundled family for the current user, without admin rights (Windows 10 1809+ per-user fonts): copy the
    /// TTFs to %LocalAppData%\Microsoft\Windows\Fonts, register them under HKCU\…\Windows NT\CurrentVersion\Fonts, load
    /// them into this session (AddFontResource) and broadcast WM_FONTCHANGE. VS's editor only uses installed fonts, so
    /// this runs before a bundled font is written to a VS font category. Idempotent; installed fonts are left in place
    /// when UEToVS stops managing VS fonts (harmless, and other programs may have picked them up). Any thread.
    /// </summary>
    internal static class FontInstaller
    {
        private static readonly Logger L = Log.For("Fonts");
        private const int WM_FONTCHANGE = 0x001D;
        private static readonly IntPtr HWND_BROADCAST = new IntPtr(0xFFFF);
        private const uint SMTO_ABORTIFHUNG = 0x0002;
        private static readonly HashSet<string> _installedThisSession = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        /// <summary>
        /// Families copied or registered by this VS process. VS's WPF editor only sees the fonts present when VS started,
        /// so these show in VS's own windows after a restart (UEToVS's UI loads them from the VSIX and is not affected).
        /// </summary>
        public static bool InstalledThisSession(string family)
        {
            lock (_installedThisSession) return _installedThisSession.Contains(family ?? string.Empty);
        }

        public static string UserFontsDirectory
        {
            get
            {
                string local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
                return Path.Combine(local, "Microsoft", "Windows", "Fonts");
            }
        }

        /// <summary>True when the family is usable by VS afterwards (installed now, before, or system-wide).</summary>
        public static bool EnsureInstalled(BundledFont font)
        {
            var sw = System.Diagnostics.Stopwatch.StartNew();
            if (IsInstalledForMachine(font))
            {
                L.Info("font.install", null, ("family", font.Family), ("result", "machineInstalled"), ("ms", sw.ElapsedMilliseconds));
                return true;
            }
            if (!BundledFonts.IsAvailable(font))
            {
                L.Warn("font.install", "Bundled font files missing", ("family", font.Family), ("result", "bundleMissing"), ("dir", BundledFonts.Directory));
                return false;
            }
            int copied = 0, registered = 0, loaded = 0;
            var errors = new List<string>();
            string dir = UserFontsDirectory;
            try
            {
                Directory.CreateDirectory(dir);
                using (var key = Registry.CurrentUser.CreateSubKey(FontInstallNames.RegistryKey))
                {
                    foreach (var file in font.Files)
                    {
                        string src = BundledFonts.FilePath(font, file);
                        string dst = Path.Combine(dir, file);
                        bool fresh = false;
                        try
                        {
                            if (!File.Exists(dst) || new FileInfo(dst).Length != new FileInfo(src).Length)
                            {
                                File.Copy(src, dst, true);
                                copied++;
                                fresh = true;
                            }
                        }
                        catch (IOException ex)
                        {
                            // In use by the session with an older copy: keep it; the registry still points at it.
                            errors.Add(file + ": " + ex.Message);
                            if (!File.Exists(dst)) continue;
                        }
                        string name = FontInstallNames.RegistryValueName(font, file);
                        if (!string.Equals(key?.GetValue(name) as string, dst, StringComparison.OrdinalIgnoreCase))
                        {
                            key?.SetValue(name, dst, RegistryValueKind.String);
                            registered++;
                            fresh = true;
                        }
                        if (fresh && AddFontResourceW(dst) > 0) loaded++;
                    }
                }
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException || ex is System.Security.SecurityException)
            {
                L.Error("font.install", "Per-user font install failed", ex, ("family", font.Family), ("dir", dir));
                return false;
            }
            if (loaded > 0) Broadcast();
            if (copied + registered > 0)
                lock (_installedThisSession) _installedThisSession.Add(font.Family);
            L.Info("font.install", null, ("family", font.Family), ("result", copied + registered == 0 ? "alreadyInstalled" : "installed"),
                   ("copied", copied), ("registered", registered), ("loaded", loaded), ("errors", errors), ("dir", dir), ("ms", sw.ElapsedMilliseconds));
            return true;
        }

        /// <summary>The family is registered for all users (HKLM), e.g. the user installed it themselves.</summary>
        private static bool IsInstalledForMachine(BundledFont font)
        {
            try
            {
                using (var key = Registry.LocalMachine.OpenSubKey(FontInstallNames.RegistryKey))
                {
                    if (key == null) return false;
                    foreach (var name in key.GetValueNames())
                        if (name.StartsWith(font.Family + " ", StringComparison.OrdinalIgnoreCase) || name.StartsWith(font.Family + " (", StringComparison.OrdinalIgnoreCase))
                            return true;
                }
            }
            catch (Exception ex) when (ex is System.Security.SecurityException || ex is UnauthorizedAccessException || ex is IOException)
            {
                L.Debug("font.install.machineCheck", ex.Message, ("family", font.Family));
            }
            return false;
        }

        private static void Broadcast()
        {
            // Timeout + abort-if-hung: a frozen top-level window must not block us.
            SendMessageTimeoutW(HWND_BROADCAST, WM_FONTCHANGE, IntPtr.Zero, IntPtr.Zero, SMTO_ABORTIFHUNG, 1000, out _);
        }

        [DllImport("gdi32.dll", CharSet = CharSet.Unicode)]
        private static extern int AddFontResourceW(string lpFileName);

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        private static extern IntPtr SendMessageTimeoutW(IntPtr hWnd, int msg, IntPtr wParam, IntPtr lParam, uint flags, uint timeout, out IntPtr result);
    }
}
