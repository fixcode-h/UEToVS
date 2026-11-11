using System;
using System.Collections.Generic;
using UEToVS.Core.Protocol;

namespace UEToVS.Search
{
    internal enum ItemKind
    {
        Type,
        Symbol,
        File,
        Asset,
        Action,
        Text,
        Recent,
        Header,
        More,
        Hint,
        PrefixCommand,
    }

    internal enum OpenMode
    {
        Normal,
        SplitGroup,
        KeepOpen,
    }

    /// <summary>One row in the Search Everywhere list.</summary>
    internal sealed class SearchItem
    {
        public ItemKind Kind { get; set; }
        public string Title { get; set; }
        public int[] Highlights { get; set; } = Array.Empty<int>();
        public string Detail { get; set; }
        public string Right { get; set; }

        /// <summary>Short badge text ("C", "S", "m", "h"...).</summary>
        public string Badge { get; set; }

        public BadgeColor BadgeColor { get; set; }
        public bool Dimmed { get; set; }
        public int Score { get; set; }

        public string FilePath { get; set; }
        public int Line { get; set; } = -1;
        public int Column { get; set; }

        /// <summary>For headers/"more" rows: the tab to switch to.</summary>
        public Core.Query.SearchTab? TargetTab { get; set; }

        /// <summary>Runs the item. Returns false to keep the popup open (toggles).</summary>
        public Func<OpenMode, bool> Activate { get; set; }

        public bool IsToggle { get; set; }
        public bool ToggleState { get; set; }

        public bool IsSelectable => Kind != ItemKind.Header && Kind != ItemKind.Hint;

        public SymbolDto Symbol { get; set; }
        public AssetDto Asset { get; set; }
    }

    internal enum BadgeColor
    {
        Gray,
        Blue,
        Green,
        Purple,
        Orange,
        Red,
        Teal,
    }

    internal static class Badges
    {
        public static (string text, BadgeColor color) For(SymbolDto s)
        {
            switch (s.Kind)
            {
                case SymbolKind.Class: return ((s.Ue & UeFlags.UClass) != 0 ? "U" : "C", BadgeColor.Blue);
                case SymbolKind.Struct: return ((s.Ue & UeFlags.UStruct) != 0 ? "U" : "S", BadgeColor.Teal);
                case SymbolKind.Union: return ("u", BadgeColor.Teal);
                case SymbolKind.Enum: return ("E", BadgeColor.Orange);
                case SymbolKind.EnumValue: return ("e", BadgeColor.Orange);
                case SymbolKind.Typedef: return ("T", BadgeColor.Gray);
                case SymbolKind.Delegate: return ("D", BadgeColor.Purple);
                case SymbolKind.Method:
                case SymbolKind.Function: return ((s.Ue & UeFlags.UFunction) != 0 ? "ƒ" : "m", BadgeColor.Red);
                case SymbolKind.Field:
                case SymbolKind.Variable: return ((s.Ue & UeFlags.UProperty) != 0 ? "P" : "f", BadgeColor.Green);
                case SymbolKind.Macro: return ("#", BadgeColor.Gray);
                case SymbolKind.Namespace: return ("N", BadgeColor.Gray);
            }
            return ("?", BadgeColor.Gray);
        }

        public static (string text, BadgeColor color) ForFile(string name)
        {
            string ext = System.IO.Path.GetExtension(name).ToLowerInvariant();
            switch (ext)
            {
                case ".h":
                case ".hpp":
                case ".inl": return ("h", BadgeColor.Purple);
                case ".cpp":
                case ".cc":
                case ".c": return ("c", BadgeColor.Blue);
                case ".cs": return ("#", BadgeColor.Green);
                case ".ini": return ("i", BadgeColor.Gray);
                case ".usf":
                case ".ush": return ("s", BadgeColor.Orange);
                case ".uplugin":
                case ".uproject": return ("U", BadgeColor.Teal);
                default: return ("·", BadgeColor.Gray);
            }
        }

        public static string OriginLabel(ItemOrigin o)
        {
            switch (o)
            {
                case ItemOrigin.Engine: return "Engine";
                case ItemOrigin.EnginePlugin: return "Engine Plugin";
                case ItemOrigin.ThirdParty: return "ThirdParty";
                case ItemOrigin.ProjectPlugin: return "Plugin";
                default: return null;
            }
        }
    }
}
