using System;
using Microsoft.VisualStudio;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Shell.Interop;
using Microsoft.Win32;
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
            if (effective == null && c.RegisteredName != null && RefreshCache(c))
                effective = ReadRaw(c, (uint)(__FCSTORAGEFLAGS.FCSF_READONLY | __FCSTORAGEFLAGS.FCSF_LOADDEFAULTS));
            var user = ReadRaw(c, (uint)__FCSTORAGEFLAGS.FCSF_READONLY);
            if (effective == null)
            {
                // v0.9.7：VS 的 UE 日志类别在用户机器上连默认值都读不出（OpenCategory / GetFont 失败），它自己的窗口因此拿到空字体名崩溃。
                // 只对这类按名字找到的类别放行：当成「没有字体」返回，让上层照常备份并写入，写入走不依赖默认值的路子（见 Set）。
                if (c.RegisteredName == null || VsFontCategoryGuids.Of(c) == null) return null;
                return user != null && !string.IsNullOrEmpty(user.Face)
                    ? user
                    : new VsFontValue { Face = string.Empty, PointSize = 0, CharSet = DefaultCharSet, IsDefault = true };
            }
            effective.IsDefault = user == null || string.IsNullOrEmpty(user.Face);
            return effective;
        }

        /// <summary>
        /// 用户机器上 VS 的 UE 日志类别读不出来，「字体和颜色」的下拉里也没有它：多半是 VS 给这个类别的缓存坏了。
        /// 清掉缓存再让 VS 重建一次（一个会话只试一次），重建成功后它就和别的类别一样能读能写。
        /// </summary>
        private bool RefreshCache(VsFontCategory c)
        {
            var resolved = VsFontCategoryGuids.Of(c);
            if (resolved == null || !_refreshed.Add(resolved.Value)) return false;
            if (!(Package.GetGlobalService(typeof(SVsFontAndColorCacheManager)) is IVsFontAndColorCacheManager cache)) return false;
            var g = resolved.Value;
            int clear = cache.ClearCache(ref g);
            g = resolved.Value;
            int refresh = cache.RefreshCache(ref g);
            L.Info("font.vs.cache.refresh", null, ("category", c.Id), ("clear", clear), ("refresh", refresh));
            return true;
        }

        private static readonly System.Collections.Generic.HashSet<Guid> _refreshed = new System.Collections.Generic.HashSet<Guid>();

        private VsFontValue ReadRaw(VsFontCategory c, uint flags)
        {
            var resolved = VsFontCategoryGuids.Of(c);
            if (resolved == null) return null;
            var guid = resolved.Value;
            int hr = _storage.OpenCategory(ref guid, flags);
            if (ErrorHandler.Failed(hr))
            {
                if (c.RegisteredName != null) L.Info("font.vs.open.failed", "OpenCategory failed", ("category", c.Id), ("hr", hr), ("flags", flags));
                else L.Debug("font.vs.open", null, ("category", c.Id), ("hr", hr), ("flags", flags));
                return null;
            }
            try
            {
                var lf = new LOGFONTW[1];
                var info = new FontInfo[1];
                hr = _storage.GetFont(lf, info);
                if (ErrorHandler.Failed(hr))
                {
                    if (c.RegisteredName != null) L.Info("font.vs.get.failed", "GetFont failed", ("category", c.Id), ("hr", hr), ("flags", flags));
                    return null;
                }
                return new VsFontValue
                {
                    // 字体名为空也照常返回（Face = ""）：VS 的 UE 日志类别就是空字体名才让 VS 崩，要能备份并写入它
                    Face = info[0].bFaceNameValid != 0 ? info[0].bstrFaceName ?? string.Empty : string.Empty,
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
            var resolved = VsFontCategoryGuids.Of(c);
            if (resolved == null) return false;
            var guid = resolved.Value;
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
            var resolved = VsFontCategoryGuids.Of(c);
            if (resolved == null) return false;
            var guid = resolved.Value;
            int hr = TrySetFont(guid, (uint)(__FCSTORAGEFLAGS.FCSF_LOADDEFAULTS | __FCSTORAGEFLAGS.FCSF_PROPAGATECHANGES), face, size, charSet, out string step);
            if (ErrorHandler.Succeeded(hr)) return true;
            if (c.RegisteredName == null)
            {
                L.Warn(evt, step + " failed", ("category", c.Id), ("face", face), ("hr", hr));
                return false;
            }
            // VS 的 UE 日志类别读不出默认值时，带 LOADDEFAULTS 打开会失败：先不要默认值再写一次，还不行就直接写 VS 存这个类别用户设置的注册表
            L.Info(evt, step + " failed; retrying without defaults", ("category", c.Id), ("face", face), ("hr", hr));
            hr = TrySetFont(guid, (uint)__FCSTORAGEFLAGS.FCSF_PROPAGATECHANGES, face, size, charSet, out step);
            if (ErrorHandler.Succeeded(hr)) return true;
            L.Info(evt, step + " failed without defaults; writing the registry", ("category", c.Id), ("face", face), ("hr", hr));
            return WriteRegistry(c, guid, face, size, charSet, evt);
        }

        private int TrySetFont(Guid guid, uint flags, string face, int? size, byte charSet, out string step)
        {
            step = "OpenCategory";
            int hr = _storage.OpenCategory(ref guid, flags);
            if (ErrorHandler.Failed(hr)) return hr;
            try
            {
                step = "SetFont";
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
                return _storage.SetFont(info);
            }
            finally
            {
                _storage.CloseCategory();
            }
        }

        /// <summary>
        /// 最后一招：VS 把「字体和颜色」里用户改过的字体存在用户设置注册表 FontAndColors\{类别}（FontName / FontPointSize / FontCharSet），
        /// 直接写进去，再清掉这个类别的缓存让 VS 重新读。只给按名字找到的类别用（自带类别走 IVsFontAndColorStorage 从没失败过）。
        /// </summary>
        private static bool WriteRegistry(VsFontCategory c, Guid guid, string face, int? size, byte charSet, string evt)
        {
            try
            {
                using (var root = VSRegistry.RegistryRoot(__VsLocalRegistryType.RegType_UserSettings, true))
                using (var key = root?.CreateSubKey(@"FontAndColors\" + guid.ToString("B")))
                {
                    if (key == null) { L.Warn(evt, "user settings registry unavailable", ("category", c.Id)); return false; }
                    key.SetValue("FontName", face, RegistryValueKind.String);
                    key.SetValue("FontCharSet", (int)charSet, RegistryValueKind.DWord);
                    if (size.HasValue && size.Value > 0) key.SetValue("FontPointSize", size.Value, RegistryValueKind.DWord);
                }
                if (Package.GetGlobalService(typeof(SVsFontAndColorCacheManager)) is IVsFontAndColorCacheManager cache)
                {
                    var g = guid;
                    cache.ClearCache(ref g);
                    g = guid;
                    cache.RefreshCache(ref g);
                }
                L.Info(evt, "written to the registry", ("category", c.Id), ("face", face), ("size", size));
                return true;
            }
            catch (Exception ex) when (ex is System.Security.SecurityException || ex is UnauthorizedAccessException || ex is System.IO.IOException || ex is InvalidOperationException || ex is System.Runtime.InteropServices.COMException)
            {
                L.Warn(evt, "registry write failed", ex, ("category", c.Id), ("face", face));
                return false;
            }
        }
    }
}
