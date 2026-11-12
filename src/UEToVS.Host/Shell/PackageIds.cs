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
        /// <summary>Single toggle: take over / restore, depending on the current state (0x0121 was the old Restore item).</summary>
        public const int ToggleIntelliSense = 0x0120;
        public const int ShowComponentGallery = 0x0130;
        public const int Settings = 0x0140;
        public const int FindAction = 0x0150;

        // ---- v0.3.0 (M2 0x0200–0x02FF: NavigationCommandIds; M1 0x0300–0x03FF: M1CommandIds; catalog: Logic/Commands/CommandCatalog.cs)
        // settings / windows / toolbar 0x0400–0x04FF
        public const int Toolbar = 0x0400;
        public const int TbSettings = 0x0480;
        public const int TbSearchEverywhere = 0x0481;
        public const int TbGoToDefinition = 0x0484;
        public const int TbFindReferences = 0x0485;
        public const int TbPerformance = 0x0486;

        // ---- MCP (0x0500–0x05FF) ----
        public const int WriteMcpConfig = 0x0500;

        // ---- v0.4.2 Generate (0x0160–0x016F) ----
        public const int GenerateConstructor = 0x0160;
        public const int GenerateReplication = 0x0161;
        public const int GenerateRpc = 0x0162;
        public const int ExpandSelection = 0x0163;
        public const int ShrinkSelection = 0x0164;
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

        // ---- MCP ----
        public const string WriteMcpConfig = "uetovs.mcp.writeConfig";

        // ---- v0.4.2 Generate ----
        public const string GenerateConstructor = "uetovs.generate.constructor";
        public const string GenerateReplication = "uetovs.generate.replication";
        public const string GenerateRpc = "uetovs.generate.rpc";
        public const string ExpandSelection = "uetovs.edit.expandSelection";
        public const string ShrinkSelection = "uetovs.edit.shrinkSelection";
    }
}
