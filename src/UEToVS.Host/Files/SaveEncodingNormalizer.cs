using System;
using System.IO;
using System.Text;
using Microsoft.VisualStudio;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Shell.Interop;
using Microsoft.VisualStudio.Text;
using Microsoft.VisualStudio.TextManager.Interop;
using UEToVS.Core.Diagnostics;
using UEToVS.Core.Text;
using UEToVS.Host.Editor;
using UEToVS.Host.IntelliSense;
using UEToVS.Host.Logic.Files;

namespace UEToVS.Host.Files
{
    /// <summary>
    /// v0.9.13 设置 > 编辑 > 文件编码 >“VS 保存时统一编码”：VS 保存文本文件前，按设置把文档的编码（<see cref="ITextDocument.Encoding"/>）
    /// 换掉，VS 随后就按新编码写盘。默认关；只在 UE 解决方案、只对源码 / 配置类扩展名生效；转换会丢字时不换。
    /// </summary>
    internal sealed class SaveEncodingNormalizer : IVsRunningDocTableEvents3
    {
        private static readonly Logger L = Log.For("Files");
        private static SaveEncodingNormalizer _instance;
        private IVsRunningDocumentTable _rdt;
        private uint _cookie;

        public static void Initialize()
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            if (_instance != null) return;
            if (!(Package.GetGlobalService(typeof(SVsRunningDocumentTable)) is IVsRunningDocumentTable rdt)) return;
            var n = new SaveEncodingNormalizer { _rdt = rdt };
            if (ErrorHandler.Succeeded(rdt.AdviseRunningDocTableEvents(n, out n._cookie))) _instance = n;
        }

        public int OnBeforeSave(uint docCookie)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            try
            {
                var s = IntelliSenseContext.Settings;
                if (s == null || s.SaveEncoding == SaveEncodingMode.Off || !IntelliSenseContext.IsUnrealSolution) return VSConstants.S_OK;
                if (ErrorHandler.Failed(_rdt.GetDocumentInfo(docCookie, out _, out _, out _, out string path, out _, out _, out IntPtr docData))) return VSConstants.S_OK;
                try
                {
                    if (!SaveEncodingPolicy.AppliesTo(path) || docData == IntPtr.Zero) return VSConstants.S_OK;
                    var obj = System.Runtime.InteropServices.Marshal.GetObjectForIUnknown(docData);
                    var lines = obj as IVsTextBuffer;
                    var buffer = lines == null ? null : EditorServices.Adapters?.GetDocumentBuffer(lines);
                    var doc = buffer == null ? null : EditorServices.Document(buffer);
                    if (doc == null) return VSConstants.S_OK;
                    var current = ToFormat(doc.Encoding);
                    if (current == null) return VSConstants.S_OK;
                    var target = TextFileCodec.ChooseOnSave(current.Value, buffer.CurrentSnapshot.GetText(), s.FileEncoding, s.SaveEncoding);
                    if (target == null) return VSConstants.S_OK;
                    doc.Encoding = ToEncoding(target.Value);
                    L.Info("files.saveEncoding", null, ("file", path), ("from", current.Value.Name), ("to", target.Value.Name));
                }
                finally
                {
                    if (docData != IntPtr.Zero) System.Runtime.InteropServices.Marshal.Release(docData);
                }
            }
            catch (Exception ex)
            {
                L.Warn("files.saveEncoding.failed", "Changing the encoding before save failed; saved unchanged", ex);
            }
            return VSConstants.S_OK;
        }

        private static TextFileFormat? ToFormat(Encoding e)
        {
            if (e == null) return null;
            bool bom = e.GetPreamble().Length > 0;
            switch (e.CodePage)
            {
                case 65001: return bom ? TextFileFormat.Utf8Bom : TextFileFormat.Utf8;
                case 1200: return new TextFileFormat(TextFileEncodingKind.Utf16LE);
                case 1201: return new TextFileFormat(TextFileEncodingKind.Utf16BE);
                default: return e.IsSingleByte || e.CodePage == TextFileCodec.GbkCodePage || e.CodePage == 54936 ? new TextFileFormat(TextFileEncodingKind.CodePage, e.CodePage) : (TextFileFormat?)null;
            }
        }

        private static Encoding ToEncoding(TextFileFormat f)
        {
            switch (f.Kind)
            {
                case TextFileEncodingKind.Utf8: return new UTF8Encoding(false);
                case TextFileEncodingKind.Utf8Bom: return new UTF8Encoding(true);
                case TextFileEncodingKind.Utf16LE: return new UnicodeEncoding(false, true);
                case TextFileEncodingKind.Utf16BE: return new UnicodeEncoding(true, true);
                default: return Encoding.GetEncoding(f.CodePage);
            }
        }

        public int OnAfterFirstDocumentLock(uint docCookie, uint dwRDTLockType, uint dwReadLocksRemaining, uint dwEditLocksRemaining) => VSConstants.S_OK;
        public int OnBeforeLastDocumentUnlock(uint docCookie, uint dwRDTLockType, uint dwReadLocksRemaining, uint dwEditLocksRemaining) => VSConstants.S_OK;
        public int OnAfterSave(uint docCookie) => VSConstants.S_OK;
        public int OnAfterAttributeChange(uint docCookie, uint grfAttribs) => VSConstants.S_OK;
        public int OnBeforeDocumentWindowShow(uint docCookie, int fFirstShow, IVsWindowFrame pFrame) => VSConstants.S_OK;
        public int OnAfterDocumentWindowHide(uint docCookie, IVsWindowFrame pFrame) => VSConstants.S_OK;
        public int OnAfterAttributeChangeEx(uint docCookie, uint grfAttribs, IVsHierarchy pHierOld, uint itemidOld, string pszMkDocumentOld, IVsHierarchy pHierNew, uint itemidNew, string pszMkDocumentNew) => VSConstants.S_OK;
    }
}
