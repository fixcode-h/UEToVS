using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace UEToVS.Host.Logic.Settings
{
    /// <summary>
    /// v0.5.1: default Lua script folder. When the user has not set any Lua folder, &lt;Project&gt;/Content/Script is indexed
    /// automatically, but only if it exists and holds at least one .lua file (UnLua / sluaunreal projects); otherwise
    /// there is no Lua index, as before. Folders the user sets replace the default.
    /// </summary>
    public static class LuaScriptDefaults
    {
        /// <summary>Relative to the .uproject folder, like the folders the settings window stores.</summary>
        public const string DefaultFolder = "Content/Script";

        private static readonly TimeSpan CacheFor = TimeSpan.FromSeconds(30);
        private static readonly object Gate = new object();
        private static string _cachedDir;
        private static bool _cachedResult;
        private static DateTime _cachedUtc;

        /// <summary>
        /// True when <paramref name="directory"/> exists and has a .lua file (stops at the first one). The answer is kept
        /// for 30 s, because the settings window's status row asks every few seconds. Never throws.
        /// </summary>
        public static bool ContainsLua(string directory)
        {
            var now = DateTime.UtcNow;
            lock (Gate)
                if (string.Equals(_cachedDir, directory, StringComparison.OrdinalIgnoreCase) && now - _cachedUtc < CacheFor) return _cachedResult;
            bool result;
            try
            {
                result = Directory.Exists(directory) && Directory.EnumerateFiles(directory, "*.lua", SearchOption.AllDirectories).Any();
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException || ex is ArgumentException || ex is System.Security.SecurityException)
            {
                result = false;
            }
            lock (Gate)
            {
                _cachedDir = directory;
                _cachedResult = result;
                _cachedUtc = now;
            }
            return result;
        }

        /// <summary>
        /// The Lua folders to index: the configured ones, or <see cref="DefaultFolder"/> when none is configured and
        /// &lt;<paramref name="projectDirectory"/>&gt;/Content/Script contains Lua scripts. <paramref name="isDefault"/> tells which.
        /// </summary>
        public static List<string> Resolve(string configured, string projectDirectory, out bool isDefault, Func<string, bool> containsLua = null)
        {
            isDefault = false;
            var list = HostSettingsSchema.Codec.SplitPaths(configured);
            if (list.Count > 0 || string.IsNullOrEmpty(projectDirectory)) return list;
            string dir;
            try { dir = Path.Combine(projectDirectory, "Content", "Script"); }
            catch (ArgumentException) { return list; }
            if (!(containsLua ?? ContainsLua)(dir)) return list;
            isDefault = true;
            list.Add(DefaultFolder);
            return list;
        }
    }
}
