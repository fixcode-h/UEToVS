using System;
using UEToVS.Host.Engine;
using UEToVS.Host.Logic.IntelliSense;
using UEToVS.Host.Logic.Settings;
using UEToVS.Host.Navigation;

namespace UEToVS.Host.IntelliSense
{
    /// <summary>
    /// What the M3 editor parts (completion, Quick Info, parameter info, semantic colouring) need from the package, tolerant
    /// of the package not being loaded yet (MEF parts are created by the editor on their own). Shares
    /// <see cref="NavigationContext"/>'s settings / engine / solution state; adds the takeover state (cached, it is a
    /// File.Exists) and the per-session "recently used completions" list.
    /// </summary>
    internal static class IntelliSenseContext
    {
        private static bool _takenOver;
        private static int _checkedAt;

        public static HostSettings Settings => NavigationContext.Settings;

        public static EngineClient Engine => NavigationContext.Engine;

        public static bool IsUnrealSolution => NavigationContext.IsUnrealSolution;

        /// <summary>Recently committed completion labels (VA "recent first"), shared by all editors of the session.</summary>
        public static RecentCompletions Recent { get; } = new RecentCompletions(300);

        /// <summary>
        /// VS's own C++ IntelliSense has been switched off by UEToVS (a backup of the originals exists). Then every C++
        /// insight feature comes from UEToVS; before that UEToVS only adds UE enhancements (completion scope Auto).
        /// Cached for 2 s (QueryStatus / typing call this often).
        /// </summary>
        public static bool TakenOver
        {
            get
            {
                int now = Environment.TickCount;
                if (_checkedAt == 0 || unchecked(now - _checkedAt) > 2000)
                {
                    _takenOver = HostPackage.Instance?.IntelliSense?.BackupExists == true;
                    _checkedAt = now == 0 ? 1 : now;
                }
                return _takenOver;
            }
        }

        /// <summary>Forget the cached takeover state (after take over / restore).</summary>
        public static void InvalidateTakeover() => _checkedAt = 0;

        /// <summary>UEToVS provides the whole completion list (vs. UE enhancements only next to VS's list).</summary>
        public static bool ProvidesAllCompletions => CompletionPolicy.ProvidesAll(Settings.CompletionScope, TakenOver);

        public static bool CompletionActive => IsUnrealSolution && Settings.CompletionEnabled;

        /// <summary>Quick Info: full after the takeover; before it only the UE parts (reflection, macro expansion) next to VS's tooltip.</summary>
        public static bool QuickInfoActive => IsUnrealSolution && Settings.QuickInfoEnabled;

        /// <summary>Quick Info shows only what VS's own tooltip lacks (VS IntelliSense still on).</summary>
        public static bool QuickInfoUeOnly => !ProvidesAllCompletions;

        /// <summary>Parameter info: VS's own lists the same signatures until the takeover, so ours starts with it (or scope All).</summary>
        public static bool SignatureHelpActive => IsUnrealSolution && Settings.SignatureHelpEnabled && ProvidesAllCompletions;

        public static bool ColoringActive => IsUnrealSolution && Settings.SemanticColoring;
    }
}
