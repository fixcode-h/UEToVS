using System;

namespace UEToVS.Host.Logic.Shell
{
    /// <summary>Text edit operation a UEToVS window performs itself instead of letting VS route it to the last editor.</summary>
    public enum WindowEditCommand
    {
        None = 0,
        Copy,
        Cut,
        Paste,
        Undo,
        Redo,
        SelectAll,
        Delete,
    }

    /// <summary>
    /// VS command (group GUID + id) → edit operation. UEToVS's settings window and popups are modeless WPF windows: VS
    /// translates Ctrl+V / Ctrl+C / Ctrl+Z … into Edit.Paste / Edit.Copy / Edit.Undo before WPF sees the key, and runs
    /// them on the last active code editor (v0.4.0 report: pasting an API key into the settings window pasted it into
    /// the code, several times). <c>ModelessEditCommandRouter</c> intercepts exactly these commands while one of our
    /// windows is active and runs them on the focused WPF control.
    /// </summary>
    public static class WindowEditCommands
    {
        /// <summary>VSConstants.GUID_VSStandardCommandSet97.</summary>
        public static readonly Guid VsStd97 = new Guid("5EFC7975-14BC-11CF-9B2B-00AA00573819");
        // VSConstants.VSStd97CmdID values
        public const uint Copy97 = 15, Cut97 = 16, Delete97 = 17, Paste97 = 26, Redo97 = 29, SelectAll97 = 31, Undo97 = 43,
                          MultiLevelRedo97 = 30, MultiLevelUndo97 = 44;
        /// <summary>Edit.* commands of the VS 97 command set (VSStd2K ones are mapped by the router with VSConstants).</summary>
        public static WindowEditCommand Map(Guid group, uint id)
        {
            if (group == VsStd97)
            {
                switch (id)
                {
                    case Copy97: return WindowEditCommand.Copy;
                    case Cut97: return WindowEditCommand.Cut;
                    case Paste97: return WindowEditCommand.Paste;
                    case Undo97: case MultiLevelUndo97: return WindowEditCommand.Undo;
                    case Redo97: case MultiLevelRedo97: return WindowEditCommand.Redo;
                    case SelectAll97: return WindowEditCommand.SelectAll;
                    case Delete97: return WindowEditCommand.Delete;
                }
            }
            return WindowEditCommand.None;
        }
    }
}
