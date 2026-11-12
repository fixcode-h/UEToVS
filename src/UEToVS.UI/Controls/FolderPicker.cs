using System;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;

namespace UEToVS.UI.Controls
{
    /// <summary>
    /// Modern Windows folder picker (the Explorer-style IFileOpenDialog with FOS_PICKFOLDERS, as VS uses), so the UI
    /// library needs neither WinForms' old tree dialog nor a VS service.
    /// </summary>
    public static class FolderPicker
    {
        /// <summary>Shows the dialog; returns the chosen folder, or null when cancelled or the dialog is unavailable.</summary>
        public static string Pick(DependencyObject owner, string title, string initialFolder)
        {
            IFileOpenDialog dialog = null;
            try
            {
                dialog = (IFileOpenDialog)new FileOpenDialogRcw();
                dialog.GetOptions(out uint options);
                dialog.SetOptions(options | FosPickFolders | FosForceFileSystem | FosNoChangeDir | FosPathMustExist);
                if (!string.IsNullOrEmpty(title)) dialog.SetTitle(title);
                if (!string.IsNullOrEmpty(initialFolder) && System.IO.Directory.Exists(initialFolder) &&
                    SHCreateItemFromParsingName(initialFolder, IntPtr.Zero, typeof(IShellItem).GUID, out IShellItem start) == 0)
                {
                    dialog.SetFolder(start);
                    Marshal.ReleaseComObject(start);
                }
                IntPtr hwnd = IntPtr.Zero;
                var window = owner == null ? null : Window.GetWindow(owner);
                if (window != null) hwnd = new WindowInteropHelper(window).Handle;
                if (dialog.Show(hwnd) != 0) return null; // cancelled (HRESULT_FROM_WIN32(ERROR_CANCELLED)) or failed
                dialog.GetResult(out IShellItem item);
                try
                {
                    item.GetDisplayName(SigdnFileSysPath, out IntPtr p);
                    try { return Marshal.PtrToStringUni(p); }
                    finally { Marshal.FreeCoTaskMem(p); }
                }
                finally { Marshal.ReleaseComObject(item); }
            }
            catch (COMException)
            {
                return null;
            }
            finally
            {
                if (dialog != null) Marshal.ReleaseComObject(dialog);
            }
        }

        private const uint FosNoChangeDir = 0x8, FosPickFolders = 0x20, FosForceFileSystem = 0x40, FosPathMustExist = 0x800;
        private const uint SigdnFileSysPath = 0x80058000;

        [DllImport("shell32.dll", CharSet = CharSet.Unicode, PreserveSig = true)]
        private static extern int SHCreateItemFromParsingName([MarshalAs(UnmanagedType.LPWStr)] string path, IntPtr bindCtx,
            [MarshalAs(UnmanagedType.LPStruct)] Guid riid, out IShellItem item);

        [ComImport, Guid("DC1C5A9C-E88A-4dde-A5A1-60F82A20AEF7")]
        private class FileOpenDialogRcw { }

        [ComImport, Guid("d57c7288-d4ad-4768-be02-9d969532d960"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IFileOpenDialog
        {
            [PreserveSig] int Show(IntPtr parent);
            void SetFileTypes(uint cFileTypes, IntPtr rgFilterSpec);
            void SetFileTypeIndex(uint iFileType);
            void GetFileTypeIndex(out uint piFileType);
            void Advise(IntPtr pfde, out uint pdwCookie);
            void Unadvise(uint dwCookie);
            void SetOptions(uint fos);
            void GetOptions(out uint pfos);
            void SetDefaultFolder(IShellItem psi);
            void SetFolder(IShellItem psi);
            void GetFolder(out IShellItem ppsi);
            void GetCurrentSelection(out IShellItem ppsi);
            void SetFileName([MarshalAs(UnmanagedType.LPWStr)] string pszName);
            void GetFileName([MarshalAs(UnmanagedType.LPWStr)] out string pszName);
            void SetTitle([MarshalAs(UnmanagedType.LPWStr)] string pszTitle);
            void SetOkButtonLabel([MarshalAs(UnmanagedType.LPWStr)] string pszText);
            void SetFileNameLabel([MarshalAs(UnmanagedType.LPWStr)] string pszLabel);
            void GetResult(out IShellItem ppsi);
            void AddPlace(IShellItem psi, int fdap);
            void SetDefaultExtension([MarshalAs(UnmanagedType.LPWStr)] string pszDefaultExtension);
            void Close(int hr);
            void SetClientGuid(ref Guid guid);
            void ClearClientData();
            void SetFilter(IntPtr pFilter);
            void GetResults(out IntPtr ppenum);
            void GetSelectedItems(out IntPtr ppsai);
        }

        [ComImport, Guid("43826D1E-E718-42EE-BC55-A1E261C37BFE"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IShellItem
        {
            void BindToHandler(IntPtr pbc, ref Guid bhid, ref Guid riid, out IntPtr ppv);
            void GetParent(out IShellItem ppsi);
            void GetDisplayName(uint sigdnName, out IntPtr ppszName);
            void GetAttributes(uint sfgaoMask, out uint psfgaoAttribs);
            void Compare(IShellItem psi, uint hint, out int piOrder);
        }
    }
}
