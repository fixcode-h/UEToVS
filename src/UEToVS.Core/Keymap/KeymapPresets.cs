using System.Collections.Generic;

namespace UEToVS.Core.Keymap
{
    /// <summary>
    /// Command names of this extension. They match the button ids in UEToVSPackage.vsct.
    /// </summary>
    public static class OwnCommands
    {
        public const string SearchEverywhere = "SearchEverywhere";
        public const string GoToEverything = "GoToEverything";
        public const string GoToType = "GoToType";
        public const string GoToFile = "GoToFile";
        public const string GoToSymbol = "GoToSymbol";
        public const string FindAction = "FindAction";
        public const string FindInFiles = "FindInFiles";
        public const string GoToAsset = "GoToAsset";
        public const string NavigateBack = "NavigateBack";
        public const string NavigateForward = "NavigateForward";
        public const string RecentFiles = "RecentFiles";
        public const string RecentLocations = "RecentLocations";
        public const string LastEditLocation = "LastEditLocation";
        public const string NextEditLocation = "NextEditLocation";
        public const string FileStructure = "FileStructure";
        public const string NavigateTo = "NavigateTo";
        public const string GoToRelated = "GoToRelated";
        public const string AddModuleDependency = "AddModuleDependency";
        public const string ToggleBookmark = "ToggleBookmark";
        public const string ShowBookmarks = "ShowBookmarks";
        public const string LiveCoding = "LiveCoding";
        public const string GenerateProjectFiles = "GenerateProjectFiles";
        public const string ApplyKeymap = "ApplyKeymap";
        public const string RestoreKeymap = "RestoreKeymap";

        public static string Bookmark(int digit) => "GoToBookmark" + digit;
        public static string SetBookmark(int digit) => "SetBookmark" + digit;
    }

    public static class KeymapPresets
    {
        public const string OwnPrefix = "UEToVS:";
        public const string RiderDefaultId = "rider-default";
        public const string RiderVisualStudioId = "rider-vs";

        private const string Global = "Global";
        private const string Editor = "Text Editor";

        private static string Own(string name) => OwnPrefix + name;

        public static KeymapPreset RiderDefault { get; } = new KeymapPreset(RiderDefaultId, "Rider 默认键位", BuildRiderDefault());

        public static KeymapPreset RiderVisualStudio { get; } = new KeymapPreset(RiderVisualStudioId, "Rider（Visual Studio 键位）", BuildRiderVs());

        public static IReadOnlyList<KeymapPreset> All { get; } = new[] { RiderDefault, RiderVisualStudio };

        public static KeymapPreset Find(string id)
        {
            foreach (var p in All) if (p.Id == id) return p;
            return RiderDefault;
        }

        private static List<KeyBindingEntry> BuildRiderDefault()
        {
            var b = new List<KeyBindingEntry>
            {
                new KeyBindingEntry(Own(OwnCommands.GoToEverything), Global, "Ctrl+T", "Go to Everything"),
                new KeyBindingEntry(Own(OwnCommands.GoToType), Global, "Ctrl+N", "Go to Type"),
                new KeyBindingEntry(Own(OwnCommands.GoToFile), Global, "Ctrl+Shift+N", "Go to File"),
                new KeyBindingEntry(Own(OwnCommands.GoToSymbol), Global, "Ctrl+Shift+Alt+N", "Go to Symbol"),
                new KeyBindingEntry(Own(OwnCommands.FindAction), Global, "Ctrl+Shift+A", "Find Action"),
                new KeyBindingEntry(Own(OwnCommands.FindInFiles), Global, "Ctrl+Shift+F", "Find in Files"),
                new KeyBindingEntry(Own(OwnCommands.RecentFiles), Global, "Ctrl+E", "Recent Files"),
                new KeyBindingEntry(Own(OwnCommands.RecentLocations), Global, "Ctrl+Shift+E", "Recent Locations"),
                new KeyBindingEntry(Own(OwnCommands.NavigateBack), Global, "Ctrl+Alt+Left Arrow", "Back"),
                new KeyBindingEntry(Own(OwnCommands.NavigateForward), Global, "Ctrl+Alt+Right Arrow", "Forward"),
                new KeyBindingEntry(Own(OwnCommands.LastEditLocation), Global, "Ctrl+Shift+Bkspce", "Last Edit Location"),
                new KeyBindingEntry(Own(OwnCommands.FileStructure), Editor, "Ctrl+F12", "File Structure"),
                new KeyBindingEntry(Own(OwnCommands.NavigateTo), Editor, "Ctrl+Shift+G", "Navigate To"),
                new KeyBindingEntry(Own(OwnCommands.GoToRelated), Editor, "Ctrl+Alt+Home", "Related Symbol / Header-Source"),
                new KeyBindingEntry(Own(OwnCommands.ToggleBookmark), Editor, "F11", "Toggle Bookmark"),
                new KeyBindingEntry(Own(OwnCommands.ShowBookmarks), Global, "Shift+F11", "Show Bookmarks"),
                new KeyBindingEntry(Own(OwnCommands.LiveCoding), Global, "Ctrl+Alt+F11", "Live Coding"),
                new KeyBindingEntry("Edit.GoToDefinition", Editor, "Ctrl+B", "Go to Declaration"),
                new KeyBindingEntry("Edit.GoToImplementation", Editor, "Ctrl+Alt+B", "Go to Implementation"),
                new KeyBindingEntry("Edit.FindAllReferences", Editor, "Alt+F7", "Find Usages"),
                new KeyBindingEntry("Edit.ExpandSelection", Editor, "Ctrl+W", "Extend Selection"),
                new KeyBindingEntry("Edit.ContractSelection", Editor, "Ctrl+Shift+W", "Shrink Selection"),
                new KeyBindingEntry("Edit.Duplicate", Editor, "Ctrl+D", "Duplicate Line"),
                new KeyBindingEntry("Edit.LineDelete", Editor, "Ctrl+Y", "Delete Line"),
                new KeyBindingEntry("Edit.Redo", Global, "Ctrl+Shift+Z", "Redo"),
                new KeyBindingEntry("Edit.InsertNextMatchingCaret", Editor, "Alt+J", "Add Selection for Next Occurrence"),
                new KeyBindingEntry("Edit.MoveSelectedLinesUp", Editor, "Alt+Shift+Up Arrow", "Move Line Up"),
                new KeyBindingEntry("Edit.MoveSelectedLinesDown", Editor, "Alt+Shift+Down Arrow", "Move Line Down"),
                new KeyBindingEntry("Edit.CommentSelection", Editor, "Ctrl+/", "Comment Line"),
                new KeyBindingEntry("Refactor.Rename", Editor, "Shift+F6", "Rename"),
                new KeyBindingEntry("Edit.QuickInfo", Editor, "Ctrl+Q", "Quick Documentation"),
                new KeyBindingEntry("Edit.ParameterInfo", Editor, "Ctrl+P", "Parameter Info"),
            };
            AddBookmarkDigits(b);
            return b;
        }

        private static List<KeyBindingEntry> BuildRiderVs()
        {
            var b = new List<KeyBindingEntry>
            {
                new KeyBindingEntry(Own(OwnCommands.GoToEverything), Global, "Ctrl+T", "Go to Everything"),
                new KeyBindingEntry(Own(OwnCommands.GoToFile), Global, "Ctrl+Shift+T", "Go to File"),
                new KeyBindingEntry(Own(OwnCommands.GoToSymbol), Global, "Shift+Alt+T", "Go to Symbol"),
                new KeyBindingEntry(Own(OwnCommands.FindAction), Global, "Ctrl+Shift+A", "Find Action"),
                new KeyBindingEntry(Own(OwnCommands.FindInFiles), Global, "Ctrl+Shift+F", "Find in Files"),
                new KeyBindingEntry(Own(OwnCommands.RecentFiles), Global, "Ctrl+,", "Recent Files"),
                new KeyBindingEntry(Own(OwnCommands.RecentLocations), Global, "Ctrl+Shift+,", "Recent Edits"),
                new KeyBindingEntry(Own(OwnCommands.NavigateBack), Global, "Ctrl+-", "Back"),
                new KeyBindingEntry(Own(OwnCommands.NavigateForward), Global, "Ctrl+Shift+-", "Forward"),
                new KeyBindingEntry(Own(OwnCommands.LastEditLocation), Global, "Ctrl+Shift+Bkspce", "Last Edit Location"),
                new KeyBindingEntry(Own(OwnCommands.FileStructure), Editor, "Alt+\\", "Go to File Member"),
                new KeyBindingEntry(Own(OwnCommands.NavigateTo), Editor, "Alt+`", "Navigate To"),
                new KeyBindingEntry(Own(OwnCommands.GoToRelated), Editor, "Ctrl+K, Ctrl+O", "Header / Source"),
                new KeyBindingEntry(Own(OwnCommands.LiveCoding), Global, "Ctrl+Alt+F11", "Live Coding"),
                new KeyBindingEntry("Edit.GoToDefinition", Editor, "F12", "Go to Declaration"),
                new KeyBindingEntry("Edit.GoToImplementation", Editor, "Ctrl+F12", "Go to Implementation"),
                new KeyBindingEntry("Edit.ExpandSelection", Editor, "Ctrl+W", "Extend Selection"),
                new KeyBindingEntry("Edit.ContractSelection", Editor, "Ctrl+Shift+W", "Shrink Selection"),
                new KeyBindingEntry("Edit.Duplicate", Editor, "Ctrl+D", "Duplicate Line"),
                new KeyBindingEntry("Edit.InsertNextMatchingCaret", Editor, "Alt+J", "Add Selection for Next Occurrence"),
            };
            AddBookmarkDigits(b);
            return b;
        }

        private static void AddBookmarkDigits(List<KeyBindingEntry> b)
        {
            for (int d = 0; d <= 9; d++)
            {
                b.Add(new KeyBindingEntry(Own(OwnCommands.SetBookmark(d)), Editor, "Ctrl+Shift+" + d, "Set Bookmark " + d));
                b.Add(new KeyBindingEntry(Own(OwnCommands.Bookmark(d)), Editor, "Ctrl+" + d, "Go to Bookmark " + d));
            }
        }
    }
}
