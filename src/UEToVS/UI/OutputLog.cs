using System;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Shell.Interop;

namespace UEToVS.UI
{
    /// <summary>"UEToVS" pane in the Output window.</summary>
    internal static class OutputLog
    {
        private static readonly Guid PaneGuid = new Guid("b4a3a8c2-3f5e-4d07-9a56-0f6c3c2c1a11");
        private static IVsOutputWindowPane _pane;

        public static void Write(string message)
        {
            ThreadHelper.JoinableTaskFactory.RunAsync(async () =>
            {
                await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
                try
                {
                    if (_pane == null)
                    {
                        var output = (IVsOutputWindow)Package.GetGlobalService(typeof(SVsOutputWindow));
                        if (output == null) return;
                        var guid = PaneGuid;
                        output.CreatePane(ref guid, "UEToVS", 1, 1);
                        output.GetPane(ref guid, out _pane);
                    }
                    _pane?.OutputStringThreadSafe($"[{DateTime.Now:HH:mm:ss}] {message}{Environment.NewLine}");
                }
                catch (Exception)
                {
                }
            }).FileAndForget("UEToVS/OutputLog");
        }

        public static void Activate()
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            _pane?.Activate();
        }

        public static void Status(string text)
        {
            ThreadHelper.JoinableTaskFactory.RunAsync(async () =>
            {
                await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
                var bar = (IVsStatusbar)Package.GetGlobalService(typeof(SVsStatusbar));
                bar?.SetText(text);
            }).FileAndForget("UEToVS/Status");
        }
    }
}
