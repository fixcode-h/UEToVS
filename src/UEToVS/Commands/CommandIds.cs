namespace UEToVS.Commands
{
    /// <summary>Command ids; must match UEToVSPackage.vsct.</summary>
    internal static class CommandIds
    {
        public const string CommandSetString = "15220305-5cc6-47e4-a051-ea4b17aca31e";
        public static readonly System.Guid CommandSet = new System.Guid(CommandSetString);

        public const int SearchEverywhere = 0x0100;
        public const int GoToEverything = 0x0101;
        public const int GoToType = 0x0102;
        public const int GoToFile = 0x0103;
        public const int GoToSymbol = 0x0104;
        public const int FindAction = 0x0105;
        public const int FindInFiles = 0x0106;
        public const int GoToAsset = 0x0107;
        public const int NavigateBack = 0x0110;
        public const int NavigateForward = 0x0111;
        public const int RecentFiles = 0x0112;
        public const int RecentLocations = 0x0113;
        public const int LastEditLocation = 0x0114;
        public const int NextEditLocation = 0x0115;
        public const int FileStructure = 0x0120;
        public const int NavigateTo = 0x0121;
        public const int GoToRelated = 0x0122;
        public const int AddModuleDependency = 0x0123;
        public const int ToggleBookmark = 0x0130;
        public const int ShowBookmarks = 0x0131;
        public const int LiveCoding = 0x0160;
        public const int GenerateProjectFiles = 0x0161;
        public const int ReindexProject = 0x0172;
        public const int ApplyKeymap = 0x0170;
        public const int RestoreKeymap = 0x0171;
        public const int SetBookmark0 = 0x0140;
        public const int GoToBookmark0 = 0x0150;
        public const int SetBookmark1 = 0x0141;
        public const int GoToBookmark1 = 0x0151;
        public const int SetBookmark2 = 0x0142;
        public const int GoToBookmark2 = 0x0152;
        public const int SetBookmark3 = 0x0143;
        public const int GoToBookmark3 = 0x0153;
        public const int SetBookmark4 = 0x0144;
        public const int GoToBookmark4 = 0x0154;
        public const int SetBookmark5 = 0x0145;
        public const int GoToBookmark5 = 0x0155;
        public const int SetBookmark6 = 0x0146;
        public const int GoToBookmark6 = 0x0156;
        public const int SetBookmark7 = 0x0147;
        public const int GoToBookmark7 = 0x0157;
        public const int SetBookmark8 = 0x0148;
        public const int GoToBookmark8 = 0x0158;
        public const int SetBookmark9 = 0x0149;
        public const int GoToBookmark9 = 0x0159;

        /// <summary>Maps OwnCommands names (UEToVS.Core.Keymap) to ids.</summary>
        public static readonly System.Collections.Generic.Dictionary<string, int> ByName = new System.Collections.Generic.Dictionary<string, int>
        {
            ["SearchEverywhere"] = SearchEverywhere,
            ["GoToEverything"] = GoToEverything,
            ["GoToType"] = GoToType,
            ["GoToFile"] = GoToFile,
            ["GoToSymbol"] = GoToSymbol,
            ["FindAction"] = FindAction,
            ["FindInFiles"] = FindInFiles,
            ["GoToAsset"] = GoToAsset,
            ["NavigateBack"] = NavigateBack,
            ["NavigateForward"] = NavigateForward,
            ["RecentFiles"] = RecentFiles,
            ["RecentLocations"] = RecentLocations,
            ["LastEditLocation"] = LastEditLocation,
            ["NextEditLocation"] = NextEditLocation,
            ["FileStructure"] = FileStructure,
            ["NavigateTo"] = NavigateTo,
            ["GoToRelated"] = GoToRelated,
            ["AddModuleDependency"] = AddModuleDependency,
            ["ToggleBookmark"] = ToggleBookmark,
            ["ShowBookmarks"] = ShowBookmarks,
            ["LiveCoding"] = LiveCoding,
            ["GenerateProjectFiles"] = GenerateProjectFiles,
            ["ReindexProject"] = ReindexProject,
            ["ApplyKeymap"] = ApplyKeymap,
            ["RestoreKeymap"] = RestoreKeymap,
            ["SetBookmark0"] = SetBookmark0,
            ["GoToBookmark0"] = GoToBookmark0,
            ["SetBookmark1"] = SetBookmark1,
            ["GoToBookmark1"] = GoToBookmark1,
            ["SetBookmark2"] = SetBookmark2,
            ["GoToBookmark2"] = GoToBookmark2,
            ["SetBookmark3"] = SetBookmark3,
            ["GoToBookmark3"] = GoToBookmark3,
            ["SetBookmark4"] = SetBookmark4,
            ["GoToBookmark4"] = GoToBookmark4,
            ["SetBookmark5"] = SetBookmark5,
            ["GoToBookmark5"] = GoToBookmark5,
            ["SetBookmark6"] = SetBookmark6,
            ["GoToBookmark6"] = GoToBookmark6,
            ["SetBookmark7"] = SetBookmark7,
            ["GoToBookmark7"] = GoToBookmark7,
            ["SetBookmark8"] = SetBookmark8,
            ["GoToBookmark8"] = GoToBookmark8,
            ["SetBookmark9"] = SetBookmark9,
            ["GoToBookmark9"] = GoToBookmark9,
        };
    }
}
