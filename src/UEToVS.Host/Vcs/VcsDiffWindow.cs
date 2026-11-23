using System;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Threading;
using Microsoft.VisualStudio.Shell.Interop;
using UEToVS.Host.Diagnostics;
using UEToVS.Host.Logic.Localization;
using UEToVS.Host.Logic.Vcs;
using UEToVS.Host.Navigation;
using UEToVS.Host.Shell;

namespace UEToVS.Host.Vcs
{
    /// <summary>
    /// 用 VS 自己的比较窗口（IVsDifferenceService）看 diff：两个版本写成只读临时文件（VcsTempFiles），和工作区比较时右边就是真实文件，
    /// 可以直接在比较窗口里改。“追溯上一版”把旧版本写成临时文件打开，登记到 VcsService，打开后自动显示注解栏。
    /// </summary>
    internal static class VcsDiffWindow
    {
        /// <summary>某个文件两个版本之间的 diff（<paramref name="before"/> 为 null 是新文件，<paramref name="after"/> 为 null 是删除）。</summary>
        public static async Task ShowRevisionsAsync(IVcsProvider p, string beforePath, string before, string afterPath, string after, string caption, CancellationToken ct)
        {
            var sw = Stopwatch.StartNew();
            await TaskScheduler.Default;
            string left = null, right = null;
            string beforeText = before == null ? string.Empty : await p.GetFileTextAsync(beforePath, before, ct).ConfigureAwait(false) ?? string.Empty;
            string afterText = after == null ? string.Empty : await p.GetFileTextAsync(afterPath, after, ct).ConfigureAwait(false) ?? string.Empty;
            string name = Path.GetFileName((afterPath ?? beforePath ?? "file").Replace('/', '\\'));
            left = VcsTempFiles.Write(Label(name, before == null ? null : p.ShortId(before)), beforeText);
            right = VcsTempFiles.Write(Label(name, after == null ? null : p.ShortId(after)), afterText);
            await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
            Open(left, right, caption, before == null ? VcsStrings.NewFile : name + " " + p.ShortId(before), after == null ? "-" : name + " " + p.ShortId(after), true);
            VcsService.L.Info("vcs.diff.open", null, ("kind", p.Kind.ToString()), ("file", name), ("before", before), ("after", after), ("ms", Math.Round(sw.Elapsed.TotalMilliseconds, 1)));
        }

        /// <summary>基准版本 ↔ 工作区（右边是真实文件）。</summary>
        public static async Task ShowBaseAsync(VcsDocument doc)
        {
            await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
            if (doc?.Base?.Text == null || doc.FilePath == null) return;
            string name = Path.GetFileName(doc.FilePath);
            string label = doc.Base.RevisionLabel ?? "HEAD";
            string text = doc.Base.Text;
            string left = await Task.Run(() => VcsTempFiles.Write(Label(name, label), text));
            await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
            Open(left, doc.FilePath, VcsStrings.CompareCaption(name, label), name + " " + label, name + " (" + VcsStrings.WorkingCopy + ")", false);
            VcsService.L.Info("vcs.diff.base", null, ("file", name), ("rev", label));
        }

        private static string Label(string name, string rev)
        {
            string ext = Path.GetExtension(name);
            string stem = Path.GetFileNameWithoutExtension(name);
            return stem + "@" + (rev ?? "none").Replace('#', '_').Replace('@', '_').Replace('=', '_') + ext;
        }

        private static void Open(string left, string right, string caption, string leftLabel, string rightLabel, bool rightTemporary)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            if (!(Package.GetGlobalService(typeof(SVsDifferenceService)) is IVsDifferenceService diff))
            {
                VsStatusBar.SetText(VcsStrings.Failed("IVsDifferenceService"));
                return;
            }
            uint options = (uint)__VSDIFFSERVICEOPTIONS.VSDIFFOPT_LeftFileIsTemporary;
            if (rightTemporary) options |= (uint)__VSDIFFSERVICEOPTIONS.VSDIFFOPT_RightFileIsTemporary;
            diff.OpenComparisonWindow2(left, right, caption, caption, leftLabel, rightLabel, null, null, options);
        }

        /// <summary>“追溯上一版”：把 <paramref name="revision"/> 版本写成只读临时文件打开，滚到 <paramref name="line"/>（从 0 开始），显示注解栏。</summary>
        public static async Task OpenRevisionAsync(IVcsProvider p, string path, string revision, int line, CancellationToken ct)
        {
            var sw = Stopwatch.StartNew();
            await TaskScheduler.Default;
            string text = await p.GetFileTextAsync(path, revision, ct).ConfigureAwait(false);
            await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
            if (text == null)
            {
                VsStatusBar.SetText(VcsStrings.NoPrevious);
                return;
            }
            string name = Path.GetFileName((path ?? "file").Replace('/', '\\'));
            string temp = await Task.Run(() => VcsTempFiles.Write(Label(name, p.ShortId(revision)), text));
            await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
            VcsService.Instance.RegisterRevisionDocument(temp, new RevisionDocument { Provider = p, Path = path, Revision = revision, Label = name + " @ " + p.ShortId(revision) });
            BlameGutterMargin.ShowForNextOpen(temp);
            VsEditor.OpenAt(temp, Math.Max(0, line), 0);
            VcsService.L.Info("vcs.blamePrevious", null, ("kind", p.Kind.ToString()), ("file", name), ("rev", revision), ("ms", Math.Round(sw.Elapsed.TotalMilliseconds, 1)));
        }

        /// <summary>按钮点了以后在后台做、失败时状态栏说明（不弹框）。</summary>
        public static void Run(string evt, Func<CancellationToken, Task> work)
        {
            var ct = NavigationContext.Disposal;
            _ = Safe.RunAsync(VcsService.L, evt, async () =>
            {
                try
                {
                    await work(ct);
                }
                catch (Exception ex) when (ex is VcsException || ex is OperationCanceledException || ex is IOException || ex is UnauthorizedAccessException)
                {
                    VsStatusBar.SetText(VcsStrings.Failed(VcsService.Describe(ex)));
                    VcsService.L.Info(evt + ".failed", ex.Message);
                }
            });
        }
    }
}
