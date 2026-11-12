using System;

namespace UEToVS.Host.Shell
{
    /// <summary>GUIDs of the host. New for the rewrite (legacy GUIDs are not reused). Must match UEToVSHostPackage.vsct.</summary>
    internal static class PackageGuids
    {
        public const string PackageString = "b77c0841-5e27-4061-b4ec-364fc5d594a6";
        public const string CommandSetString = "aacea68e-71c0-4b88-baf0-43e34d9fa8a7";

        /// <summary>
        /// Rule-based UIContext that autoloads the package (background): a fully loaded solution that contains a C++ project.
        /// Cheap prefilter only; the real decision (is this a UE solution?) is made by UnrealSolutionDetector after load.
        /// </summary>
        public const string CppSolutionContextString = "359b4eb3-ef53-44bb-b003-73c87201ef84";

        /// <summary>
        /// UIContext activated in code once the open solution is confirmed to be an Unreal Engine solution. Editor
        /// adapters, tool windows and future features key off this (VisibilityConstraints / UIContext.WhenActivated).
        /// </summary>
        public const string UnrealSolutionContextString = "67abac9a-1494-4743-8a5c-8ea3e3142c4c";

        public const string OutputPaneString = "03c095f0-3cf0-49f7-a05c-f77fa5bf161a";
        public const string PerformanceToolWindowString = "9f182b61-0231-4693-91d5-89b848b38d86";

        public static readonly Guid Package = new Guid(PackageString);
        public static readonly Guid CommandSet = new Guid(CommandSetString);
        public static readonly Guid CppSolutionContext = new Guid(CppSolutionContextString);
        public static readonly Guid UnrealSolutionContext = new Guid(UnrealSolutionContextString);
        public static readonly Guid OutputPane = new Guid(OutputPaneString);
    }

    internal static class CommandIds
    {
        public const int OpenLogFolder = 0x0100;
        public const int OpenCurrentLog = 0x0101;
        public const int CreateDiagnostics = 0x0102;
        public const int ShowPerformancePanel = 0x0104;
        public const int RestartEngine = 0x0110;
        public const int TakeOverIntelliSense = 0x0120;
        public const int RestoreIntelliSense = 0x0121;
        public const int ShowComponentGallery = 0x0130;
        public const int Settings = 0x0140;
        public const int FindAction = 0x0150;
    }

    /// <summary>Stable action ids (§7.4). Commands dispatch to these.</summary>
    internal static class ActionIds
    {
        public const string OpenLogFolder = "uetovs.diagnostics.openLogFolder";
        public const string OpenCurrentLog = "uetovs.diagnostics.openCurrentLog";
        public const string CreateDiagnostics = "uetovs.diagnostics.createPackage";
        public const string ShowPerformancePanel = "uetovs.diagnostics.performancePanel";
        public const string RestartEngine = "uetovs.engine.restart";
        public const string TakeOverIntelliSense = "uetovs.intellisense.takeOver";
        public const string RestoreIntelliSense = "uetovs.intellisense.restore";
        public const string ShowComponentGallery = "uetovs.ui.componentGallery";
        public const string Settings = "uetovs.settings.open";
        public const string FindAction = "uetovs.findAction";
    }
}
