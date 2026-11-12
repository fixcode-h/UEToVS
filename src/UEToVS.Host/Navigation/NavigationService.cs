using System;
using System.Diagnostics;
using System.IO;
using System.Threading.Tasks;
using Microsoft.VisualStudio;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Shell.Interop;
using Microsoft.VisualStudio.Text;
using Microsoft.VisualStudio.Text.Editor;
using Microsoft.VisualStudio.TextManager.Interop;
using UEToVS.Core.Diagnostics;
using UEToVS.Core.Navigation;
using UEToVS.Host.Diagnostics;
using UEToVS.Host.Editor;
using UEToVS.Host.Logic.Localization;
using UEToVS.Host.Shell;

namespace UEToVS.Host.Navigation
{
    /// <summary>
    /// The single entry point for "put the caret there" (§7.4): opens the file, moves the caret, centers the line, focuses
    /// the editor and records the jump in <see cref="History"/> (Alt+← / Alt+→). Every navigation feature (go to definition,
    /// references, hierarchy, member lists, navigation bar) goes through <see cref="JumpToAsync"/> so history and logging
    /// stay consistent. Reasons are stable ids for logs: "goToDefinition", "goToImplementation", "references", "hierarchy",
    /// "members", "navigationBar", "ctrlClick", "back", "forward" …
    /// </summary>
    internal static class NavigationService
    {
        private static readonly Logger L = Log.For("Navigation");

        /// <summary>Back / forward stack shared by every jump (the back/forward commands read it).</summary>
        public static NavigationHistory History { get; } = new NavigationHistory();

        /// <summary>Fire-and-forget variant for UI event handlers; failures are logged and shown in the status bar.</summary>
        public static void JumpTo(CodeLocation location, string reason) =>
            Safe.RunAsync(L, "nav.jump", () => JumpToAsync(location, reason), ("reason", reason));

        /// <summary>
        /// Opens <paramref name="location"/> (0-based line/column, clamped to the document) and records the jump.
        /// Returns false when the file cannot be opened (status bar says so). UI thread is entered internally.
        /// </summary>
        public static async Task<bool> JumpToAsync(CodeLocation location, string reason, bool recordHistory = true)
        {
            if (location == null) return false;
            await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
            var sw = Stopwatch.StartNew();
            var from = EditorServices.Location(EditorServices.ActiveView());
            if (!File.Exists(location.FilePath) && !IsOpenInEditor(location.FilePath))
            {
                L.Warn("nav.jump.missing", "Target file does not exist", ("reason", reason), ("to", location.ToString()));
                VsStatusBar.SetText(NavStrings.JumpFailed(location.FilePath));
                return false;
            }
            IWpfTextView view;
            using (UiThread.Measure(L, "nav.jump.open", ("reason", reason)))
                view = OpenAndGetView(location.FilePath);
            if (view == null)
            {
                L.Warn("nav.jump.openFailed", "Cannot open the target in an editor", ("reason", reason), ("to", location.ToString()));
                VsStatusBar.SetText(NavStrings.JumpFailed(location.FilePath));
                return false;
            }
            var actual = MoveCaret(view, location);
            if (recordHistory) History.RecordJump(from, actual);
            L.Info("nav.jump", null, ("reason", reason), ("from", from?.ToString()), ("to", actual.ToString()),
                   ("sameFile", from?.SameFile(actual) ?? false), ("ms", Math.Round(sw.Elapsed.TotalMilliseconds, 1)));
            return true;
        }

        private static bool IsOpenInEditor(string path) =>
            VsShellUtilities.IsDocumentOpen(ServiceProvider.GlobalProvider, path, Guid.Empty, out _, out _, out _);

        private static IWpfTextView OpenAndGetView(string path)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            VsShellUtilities.OpenDocument(ServiceProvider.GlobalProvider, path, VSConstants.LOGVIEWID.TextView_guid,
                                          out IVsUIHierarchy _, out uint _, out IVsWindowFrame frame, out IVsTextView vsView);
            frame?.Show();
            if (vsView == null && frame != null) vsView = VsShellUtilities.GetTextView(frame);
            return vsView == null ? null : EditorServices.Adapters?.GetWpfTextView(vsView);
        }

        /// <summary>Caret to line/column (clamped), selection cleared, line centered unless already visible, editor focused.</summary>
        private static CodeLocation MoveCaret(IWpfTextView view, CodeLocation target)
        {
            var snapshot = view.TextSnapshot;
            int lineNo = Math.Min(Math.Max(0, target.Line), snapshot.LineCount - 1);
            var line = snapshot.GetLineFromLineNumber(lineNo);
            int col = Math.Min(Math.Max(0, target.Column), line.Length);
            var point = new SnapshotPoint(snapshot, line.Start.Position + col);
            view.Selection.Clear();
            view.Caret.MoveTo(point);
            var lines = view.InLayout ? null : view.TextViewLines;
            bool visible = lines != null && lines.FormattedSpan.Contains(point) &&
                           lines.GetTextViewLineContainingBufferPosition(point)?.VisibilityState == Microsoft.VisualStudio.Text.Formatting.VisibilityState.FullyVisible;
            if (!visible) view.ViewScroller.EnsureSpanVisible(new SnapshotSpan(point, 0), EnsureSpanVisibleOptions.AlwaysCenter);
            Keyboard(view);
            return new CodeLocation(target.FilePath, lineNo, col);
        }

        private static void Keyboard(IWpfTextView view)
        {
            try
            {
                var vsView = EditorServices.Adapters?.GetViewAdapter(view);
                vsView?.SendExplicitFocus();
                if (vsView == null) view.VisualElement.Focus();
            }
            catch (Exception ex)
            {
                L.Debug("nav.jump.focusFailed", ex.Message);
            }
        }
    }
}
