using System;

namespace UEToVS.Host.Logic.Editing
{
    /// <summary>
    /// v0.7.5 auto-save rules (IDEA style), pure so they are testable without VS: when to save, and which documents.
    /// </summary>
    public static class AutoSavePolicy
    {
        /// <summary>Why a save round runs (logged).</summary>
        public const string ReasonDeactivated = "deactivated", ReasonIdle = "idle";

        /// <summary>
        /// Whether the idle timer should save now: there was an edit since the last save round and the last edit is at
        /// least <paramref name="idleSeconds"/> old. 0 or less = idle saving is off (only on deactivation).
        /// </summary>
        public static bool IdleDue(DateTime lastEditUtc, DateTime lastRoundUtc, DateTime nowUtc, int idleSeconds) =>
            idleSeconds > 0 && lastEditUtc > lastRoundUtc && (nowUtc - lastEditUtc).TotalSeconds >= idleSeconds;

        /// <summary>
        /// Whether one open document is saved: modified, a real file that already exists (never an untitled document, which
        /// would open Save As), and not read-only (which would prompt to overwrite or check out).
        /// </summary>
        public static bool ShouldSave(string path, bool modified, bool existsOnDisk, bool readOnly) =>
            modified && existsOnDisk && !readOnly && !string.IsNullOrEmpty(path) && System.IO.Path.IsPathRooted(path);
    }
}
