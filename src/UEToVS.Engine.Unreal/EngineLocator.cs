using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Text.Json;

namespace UEToVS.Engine.Unreal
{
    /// <summary>Contents of Engine/Build/Build.version.</summary>
    public sealed class UnrealVersion
    {
        public int Major { get; set; }
        public int Minor { get; set; }
        public int Patch { get; set; }
        public int Changelist { get; set; }
        public int CompatibleChangelist { get; set; }
        public string BranchName { get; set; }

        public override string ToString() => $"{Major}.{Minor}.{Patch}";

        public static UnrealVersion Read(string engineRoot)
        {
            if (string.IsNullOrEmpty(engineRoot)) return null;
            string file = Path.Combine(engineRoot, "Engine", "Build", "Build.version");
            try
            {
                if (!File.Exists(file)) return null;
                using var doc = JsonDocument.Parse(File.ReadAllText(file), new JsonDocumentOptions { AllowTrailingCommas = true, CommentHandling = JsonCommentHandling.Skip });
                var r = doc.RootElement;
                return new UnrealVersion
                {
                    Major = Int(r, "MajorVersion"),
                    Minor = Int(r, "MinorVersion"),
                    Patch = Int(r, "PatchVersion"),
                    Changelist = Int(r, "Changelist"),
                    CompatibleChangelist = Int(r, "CompatibleChangelist"),
                    BranchName = r.TryGetProperty("BranchName", out var b) && b.ValueKind == JsonValueKind.String ? b.GetString() : null,
                };
            }
            catch (Exception)
            {
                return null;
            }
        }

        private static int Int(JsonElement e, string name) =>
            e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out int i) ? i : 0;
    }

    /// <summary>
    /// Resolves the engine root (the folder that contains "Engine") for a project. Order:
    /// explicit override → project inside a source engine tree → path-like EngineAssociation →
    /// HKCU\Software\Epic Games\Unreal Engine\Builds (source builds, GUID association) →
    /// HKLM\SOFTWARE\EpicGames\Unreal Engine\&lt;ver&gt; → %ProgramData%\Epic\UnrealEngineLauncher\LauncherInstalled.dat.
    /// Registry lookups only run on Windows.
    /// </summary>
    public sealed class EngineLocator
    {
        /// <summary>Overridable for tests; defaults to %ProgramData%\Epic\UnrealEngineLauncher\LauncherInstalled.dat.</summary>
        public string LauncherInstalledPath { get; set; } = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "Epic", "UnrealEngineLauncher", "LauncherInstalled.dat");

        /// <summary>When false, the Windows registry is not consulted (tests).</summary>
        public bool UseRegistry { get; set; } = true;

        /// <summary>How the root was found (for the session log): override, source-tree, association-path, registry-builds, registry-hklm, launcher.</summary>
        public string LastMethod { get; private set; }

        public string Find(string projectFile, string engineRootOverride, string association = null)
        {
            LastMethod = null;
            if (!string.IsNullOrEmpty(engineRootOverride))
            {
                LastMethod = "override";
                return NormalizeEngineRoot(engineRootOverride);
            }
            if (string.IsNullOrEmpty(projectFile) || !File.Exists(projectFile)) return null;

            string dir = Path.GetDirectoryName(Path.GetFullPath(projectFile));
            for (string d = Path.GetDirectoryName(dir); d != null; d = Path.GetDirectoryName(d))
            {
                if (IsEngineRoot(d))
                {
                    LastMethod = "source-tree";
                    return d;
                }
            }

            association ??= UnrealDescriptor.Load(projectFile).EngineAssociation;
            if (string.IsNullOrEmpty(association)) return null;

            if (association.IndexOfAny(new[] { '/', '\\' }) >= 0 || association.StartsWith(".", StringComparison.Ordinal))
            {
                try
                {
                    string p = Path.GetFullPath(Path.Combine(dir, association));
                    if (Directory.Exists(p))
                    {
                        LastMethod = "association-path";
                        return NormalizeEngineRoot(p);
                    }
                }
                catch (Exception) { }
            }

            string r = FindRegisteredBuild(association);
            if (r != null) { LastMethod = "registry-builds"; return r; }
            r = FindInstalledVersionInRegistry(association);
            if (r != null) { LastMethod = "registry-hklm"; return r; }
            r = FindLauncherInstall(association);
            if (r != null) { LastMethod = "launcher"; return r; }
            return null;
        }

        public static bool IsEngineRoot(string dir) =>
            !string.IsNullOrEmpty(dir) && File.Exists(Path.Combine(dir, "Engine", "Build", "Build.version"));

        /// <summary>Accepts both "C:/UE_5.4" and "C:/UE_5.4/Engine".</summary>
        public static string NormalizeEngineRoot(string path)
        {
            path = Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            if (Directory.Exists(Path.Combine(path, "Engine"))) return path;
            if (string.Equals(Path.GetFileName(path), "Engine", StringComparison.OrdinalIgnoreCase))
                return Path.GetDirectoryName(path);
            return path;
        }

        private string FindRegisteredBuild(string association)
        {
            if (!UseRegistry || !OperatingSystem.IsWindows()) return null;
            try
            {
                using var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(@"Software\Epic Games\Unreal Engine\Builds");
                if (key?.GetValue(association) is string value && !string.IsNullOrEmpty(value) && Directory.Exists(value))
                    return NormalizeEngineRoot(value);
            }
            catch (Exception) { }
            return null;
        }

        private string FindInstalledVersionInRegistry(string version)
        {
            if (!UseRegistry || !OperatingSystem.IsWindows()) return null;
            try
            {
                using var key = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(@"SOFTWARE\EpicGames\Unreal Engine\" + version);
                if (key?.GetValue("InstalledDirectory") is string dir && !string.IsNullOrEmpty(dir) && Directory.Exists(dir))
                    return NormalizeEngineRoot(dir);
            }
            catch (Exception) { }
            return null;
        }

        private string FindLauncherInstall(string version)
        {
            try
            {
                if (string.IsNullOrEmpty(LauncherInstalledPath) || !File.Exists(LauncherInstalledPath)) return null;
                using var doc = JsonDocument.Parse(File.ReadAllText(LauncherInstalledPath));
                if (!doc.RootElement.TryGetProperty("InstallationList", out var list) || list.ValueKind != JsonValueKind.Array) return null;
                foreach (var item in list.EnumerateArray())
                {
                    string app = item.TryGetProperty("AppName", out var a) && a.ValueKind == JsonValueKind.String ? a.GetString() : null;
                    string loc = item.TryGetProperty("InstallLocation", out var l) && l.ValueKind == JsonValueKind.String ? l.GetString() : null;
                    if (string.Equals(app, "UE_" + version, StringComparison.OrdinalIgnoreCase) && !string.IsNullOrEmpty(loc) && Directory.Exists(loc))
                        return NormalizeEngineRoot(loc);
                }
            }
            catch (Exception) { }
            return null;
        }

        /// <summary>
        /// Stable identifier of an engine install, used to key engine index segments: version, changelist and a hash of the
        /// root path. Only [A-Za-z0-9._-] characters. The binary BuildId (Engine/Binaries/&lt;platform&gt;/UnrealEditor.version)
        /// is deliberately left out: a source-built engine gets a new one on every editor build, which threw away the whole
        /// engine index (120k+ files re-parsed). Changed engine files are caught per file (size + last write time) instead.
        /// </summary>
        public static string ComputeBuildId(string engineRoot, UnrealVersion version)
        {
            if (string.IsNullOrEmpty(engineRoot)) return "no-engine";
            string ver = version?.ToString() ?? "unknown";
            return Sanitize($"{ver}-cl{version?.Changelist ?? 0}-{Fnv(engineRoot.ToLowerInvariant().TrimEnd('/', '\\')):x8}");
        }

        private static string Sanitize(string s)
        {
            var sb = new StringBuilder(s.Length);
            foreach (char c in s) sb.Append(char.IsLetterOrDigit(c) || c == '.' || c == '-' || c == '_' ? c : '_');
            return sb.ToString();
        }

        internal static uint Fnv(string s)
        {
            uint h = 2166136261;
            foreach (char c in s)
            {
                h ^= c;
                h *= 16777619;
            }
            return h;
        }
    }
}
