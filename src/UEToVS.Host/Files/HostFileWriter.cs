using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Microsoft.VisualStudio;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Shell.Interop;
using UEToVS.Core.Diagnostics;
using UEToVS.Core.Localization;
using UEToVS.Core.Text;
using UEToVS.Host.IntelliSense;

namespace UEToVS.Host.Files
{
    /// <summary>
    /// v0.9.13 UEToVS 直接写磁盘（不经过编辑器缓冲区）的统一出口，修两类问题：
    /// <list type="number">
    /// <item>绕过 VS 的签出流程：P4 工作区里没签出的文件是只读的，以前直接 File.WriteAllText 会失败，或者改名后 P4 不知道。
    /// 现在写已有文件前先问 VS（<see cref="IVsQueryEditQuerySave2.QueryEditFiles"/>），装了 P4VS 等源代码管理插件时由它签出；</item>
    /// <item>编码：以前按 UTF-8 读写，GBK 文件的中文被写坏。现在按 <see cref="TextFileCodec"/> 识别、按设置选编码、无损才写。</item>
    /// </list>
    /// 只用在 UI 线程（QueryEdit 要求）。
    /// </summary>
    internal static class HostFileWriter
    {
        private static readonly Logger L = Log.For("Files");

        private static FileEncodingChoice DefaultChoice => IntelliSenseContext.Settings?.FileEncoding ?? FileEncodingChoice.Utf8NoBom;
        private static ExistingFileEncodingMode ExistingMode => IntelliSenseContext.Settings?.ExistingFileEncoding ?? ExistingFileEncodingMode.Preserve;

        /// <summary>
        /// 准备改这些已有文件：让源代码管理签出，然后确认都可写。返回 null 表示都可以写；否则是给用户看的原因（没有任何文件被改）。
        /// 不存在的文件跳过（新文件）。
        /// </summary>
        public static string PrepareEdit(IEnumerable<string> paths)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            var files = paths.Where(p => !string.IsNullOrEmpty(p) && File.Exists(p)).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
            if (files.Length == 0) return null;
            if (Package.GetGlobalService(typeof(SVsQueryEditQuerySave)) is IVsQueryEditQuerySave2 qe)
            {
                try
                {
                    int hr = qe.QueryEditFiles((uint)tagVSQueryEditFlags.QEF_DisallowInMemoryEdits, files.Length, files, null, null,
                        out uint result, out uint info);
                    L.Info("files.queryEdit", null, ("files", files.Length), ("hr", hr), ("result", result), ("info", info));
                    if (ErrorHandler.Succeeded(hr) && result != (uint)tagVSQueryEditResult.QER_EditOK)
                        return (info & (uint)tagVSQueryEditResultFlags.QER_CheckoutCanceledOrFailed) != 0
                            ? Loc.T("签出被取消或失败，文件没有修改", "Checkout was cancelled or failed; nothing was changed")
                            : Loc.T("版本管理不允许修改这些文件，文件没有修改", "Source control does not allow editing these files; nothing was changed");
                }
                catch (Exception ex)
                {
                    L.Warn("files.queryEdit.failed", "QueryEditFiles failed; checking the read-only attribute only", ex);
                }
            }
            foreach (var f in files)
            {
                try
                {
                    if ((File.GetAttributes(f) & FileAttributes.ReadOnly) != 0)
                        return Loc.T("文件是只读的（P4 下可能还没签出）：", "The file is read-only (not checked out in P4?): ") + f;
                }
                catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
                {
                    return ex.Message;
                }
            }
            return null;
        }

        /// <summary>
        /// 改写一个已有文件：按原编码读，<paramref name="transform"/> 给出新内容，按设置选编码写回。
        /// 返回 null 成功（含内容没变），否则是原因。调用前应先 <see cref="PrepareEdit"/>。
        /// </summary>
        public static string RewriteExisting(string path, Func<string, string> transform)
        {
            try
            {
                string text = TextFileCodec.ReadFile(path, out var original);
                string updated = transform(text);
                if (updated == null || string.Equals(updated, text, StringComparison.Ordinal)) return null;
                var format = TextFileCodec.ChooseForExisting(original, updated, DefaultChoice, ExistingMode);
                if (!TextFileCodec.TryWriteFile(path, updated, format))
                {
                    L.Warn("files.write.lossy", "New text cannot be written in the file's encoding; not written", null, ("file", path), ("encoding", format.Name));
                    return Loc.T("新内容里有 " + format.Name + " 编码表示不了的字符，文件没有修改：", "The new text has characters " + format.Name + " cannot store; not written: ") + path;
                }
                L.Info("files.write", null, ("file", path), ("encoding", format.Name), ("was", original.Name));
                return null;
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
            {
                L.Warn("files.write.failed", "Writing a file failed", ex, ("file", path));
                return ex.Message;
            }
        }

        /// <summary>写一个新文件（已存在则按已有文件规则改写），用默认编码。</summary>
        public static void WriteNew(string path, string text)
        {
            var dir = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            var format = TextFileCodec.FromChoice(DefaultChoice);
            if (!TextFileCodec.TryWriteFile(path, text, format)) TextFileCodec.TryWriteFile(path, text, TextFileFormat.Utf8); // GBK 写不下的字符：退回 UTF-8，不丢字
            L.Info("files.create", null, ("file", path), ("encoding", format.Name));
        }
    }
}
