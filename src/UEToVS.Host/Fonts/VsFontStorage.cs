using System;
using Microsoft.VisualStudio;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Shell.Interop;
using UEToVS.Core.Diagnostics;
using UEToVS.Host.Logic.Fonts;

namespace UEToVS.Host.Fonts
{
    /// <summary>A category's font as VS reports it.</summary>
    internal sealed class VsFontValue
    {
        public string Face { get; set; }
        public int PointSize { get; set; }
        public int CharSet { get; set; }

        /// <summary>No user value stored: VS's default is in effect.</summary>
        public bool IsDefault { get; set; }
    }

    /// <summary>
    /// Reads and writes the font of a Fonts and Colors category through IVsFontAndColorStorage (the same store as
    /// Tools &gt; Options &gt; Environment &gt; Fonts and Colors; FCSF_PROPAGATECHANGES updates open editors and windows).
    /// UI thread only. Never throws: failures are logged and reported as null / false.
    /// </summary>
    internal sealed class VsFontStorage
    {
        private static readonly Logger L = Log.For("Fonts");
        private const byte DefaultCharSet = 1; // DEFAULT_CHARSET: let GDI pick the charset for the new face
        private readonly IVsFontAndColorStorage _storage;

        private VsFontStorage(IVsFontAndColorStorage storage) => _storage = storage;

        public static VsFontStorage TryCreate()
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            if (Package.GetGlobalService(typeof(SVsFontAndColorStorage)) is IVsFontAndColorStorage s) return new VsFontStorage(s);
            L.Warn("font.vs.storage.unavailable", "IVsFontAndColorStorage unavailable");
            return null;
        }

        public VsFontValue Read(VsFontCategory c)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            var effective = ReadRaw(c, (uint)(__FCSTORAGEFLAGS.FCSF_READONLY | __FCSTORAGEFLAGS.FCSF_LOADDEFAULTS));
            if (effective == null) return null;
            var user = ReadRaw(c, (uint)__FCSTORAGEFLAGS.FCSF_READONLY);
            effective.IsDefault = user == null || string.IsNullOrEmpty(user.Face);
            return effective;
        }

        private VsFontValue ReadRaw(VsFontCategory c, uint flags)
        {
            var guid = c.Guid;
            int hr = _storage.OpenCategory(ref guid, flags);
            if (ErrorHandler.Failed(hr))
            {
                L.Debug("font.vs.open", null, ("category", c.Id), ("hr", hr), ("flags", flags));
                return null;
            }
            try
            {
                var lf = new LOGFONTW[1];
                var info = new FontInfo[1];
                hr = _storage.GetFont(lf, info);
                if (ErrorHandler.Failed(hr) || info[0].bFaceNameValid == 0) return null;
                return new VsFontValue
                {
                    Face = info[0].bstrFaceName,
                    PointSize = info[0].bPointSizeValid != 0 ? info[0].wPointSize : 0,
                    CharSet = info[0].bCharSetValid != 0 ? info[0].iCharSet : DefaultCharSet,
                };
            }
            finally
            {
                _storage.CloseCategory();
            }
        }

        /// <summary>Sets face (and size when given; null keeps VS's size).</summary>
        public bool Write(VsFontCategory c, string face, int? size) => Set(c, face, size, DefaultCharSet, "font.vs.apply.write");

        /// <summary>Puts a backed-up font back: the VS default when the user had none, otherwise the exact original.</summary>
        public bool Restore(VsFontCategory c, VsFontBackupEntry e)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            if (!e.WasDefault || !(_storage is IVsFontAndColorStorage2 s2))
                return Set(c, e.Face, e.PointSize > 0 ? e.PointSize : (int?)null, (byte)Math.Max(0, Math.Min(255, e.CharSet)), "font.vs.restore.write");
            var guid = c.Guid;
            int hr = _storage.OpenCategory(ref guid, (uint)(__FCSTORAGEFLAGS.FCSF_LOADDEFAULTS | __FCSTORAGEFLAGS.FCSF_PROPAGATECHANGES));
            if (ErrorHandler.Failed(hr)) { L.Warn("font.vs.restore.open", null, ("category", c.Id), ("hr", hr)); return false; }
            try
            {
                hr = s2.RevertFontToDefault();
                if (ErrorHandler.Failed(hr)) L.Warn("font.vs.restore.revert", null, ("category", c.Id), ("hr", hr));
                return ErrorHandler.Succeeded(hr);
            }
            finally
            {
                _storage.CloseCategory();
            }
        }

        private bool Set(VsFontCategory c, string face, int? size, byte charSet, string evt)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            if (string.IsNullOrWhiteSpace(face)) return false;
            var guid = c.Guid;
            int hr = _storage.OpenCategory(ref guid, (uint)(__FCSTORAGEFLAGS.FCSF_LOADDEFAULTS | __FCSTORAGEFLAGS.FCSF_PROPAGATECHANGES));
            if (ErrorHandler.Failed(hr)) { L.Warn(evt, "OpenCategory failed", ("category", c.Id), ("hr", hr)); return false; }
            try
            {
                var info = new FontInfo[1];
                info[0].bstrFaceName = face;
                info[0].bFaceNameValid = 1;
                info[0].iCharSet = charSet;
                info[0].bCharSetValid = 1;
                if (size.HasValue && size.Value > 0)
                {
                    info[0].wPointSize = (ushort)size.Value;
                    info[0].bPointSizeValid = 1;
                }
                hr = _storage.SetFont(info);
                if (ErrorHandler.Failed(hr)) L.Warn(evt, "SetFont failed", ("category", c.Id), ("face", face), ("hr", hr));
                return ErrorHandler.Succeeded(hr);
            }
            finally
            {
                _storage.CloseCategory();
            }
        }
    }
}
