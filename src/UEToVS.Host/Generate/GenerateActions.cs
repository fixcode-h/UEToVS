using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.VisualStudio;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Text;
using Microsoft.VisualStudio.Shell.Interop;
using Microsoft.VisualStudio.TextManager.Interop;
using UEToVS.Core.Diagnostics;
using UEToVS.Host.Editor;
using UEToVS.Host.Logic.Actions;
using UEToVS.Host.Logic.Generate;
using UEToVS.Host.Logic.Localization;
using UEToVS.Host.Navigation;
using UEToVS.Host.Shell;
using Task = System.Threading.Tasks.Task;

namespace UEToVS.Host.Generate
{
    /// <summary>
    /// Generate menu (Rider style): constructor, GetLifetimeReplicatedProps, RPC trio. Each is an action
    /// (<c>uetovs.generate.*</c>) bound to a command; the Alt+Enter hub can offer the same ids later (its availability check
    /// only needs the caret to be in a header class). Header edits go to the active buffer (one undo step), source edits to
    /// the counterpart .cpp buffer (opened when needed). Text-level, no engine request.
    /// </summary>
    internal static class GenerateActions
    {
        private static readonly Logger L = Log.For("Generate");

        public static void Register(ActionRegistry registry, HostPackage package)
        {
            registry.Register(new DelegateAction(ActionIds.GenerateConstructor, GenerateStrings.CmdConstructor, ActionCategory.Generate,
                ctx => RunAsync(package, "constructor", UeCodeGenerator.Constructor), IsHeader, null, GenerateStrings.Keywords));
            registry.Register(new DelegateAction(ActionIds.GenerateReplication, GenerateStrings.CmdReplication, ActionCategory.Generate,
                ctx => RunAsync(package, "replication", UeCodeGenerator.Replication), IsHeader, null, GenerateStrings.Keywords));
            registry.Register(new DelegateAction(ActionIds.GenerateRpc, GenerateStrings.CmdRpc, ActionCategory.Generate,
                ctx => RunAsync(package, "rpc", UeCodeGenerator.Rpc), IsHeader, null, GenerateStrings.Keywords));
        }

        private static bool IsHeader(ActionContext ctx) => ctx.IsUnrealSolution && IsHeaderPath(ctx.ActiveDocumentPath);

        private static bool IsHeaderPath(string path)
        {
            string ext = Path.GetExtension(path ?? string.Empty).ToLowerInvariant();
            return ext == ".h" || ext == ".hpp" || ext == ".hxx";
        }

        private static async Task RunAsync(HostPackage package, string kind, Func<string, int, string, string, GenerateResult> generator)
        {
            await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
            var sw = Stopwatch.StartNew();
            var view = EditorServices.ActiveView();
            string header = view == null ? null : EditorServices.FilePath(view.TextBuffer);
            if (view == null || !IsHeaderPath(header))
            {
                VsStatusBar.SetText(GenerateStrings.NotHeader);
                L.Info("generate.run", null, ("kind", kind), ("problem", "not-header"), ("file", Path.GetFileName(header ?? string.Empty)));
                return;
            }
            var snapshot = view.TextBuffer.CurrentSnapshot;
            int caret = view.Caret.Position.BufferPosition.Position;
            string nl = SnippetNewLine(snapshot);
            var counterparts = await RelatedNavigator.CounterpartFilesAsync(package, header, package.DisposalToken);
            await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
            string cpp = counterparts.FirstOrDefault(p => p.EndsWith(".cpp", StringComparison.OrdinalIgnoreCase));
            ITextBuffer cppBuffer = null;
            string cppText = null;
            if (cpp != null)
            {
                cppBuffer = OpenBuffer(cpp, false);
                cppText = cppBuffer?.CurrentSnapshot.GetText() ?? ReadText(cpp);
            }
            var result = generator(snapshot.GetText(), caret, cppText, nl);
            if (!result.Ok)
            {
                VsStatusBar.SetText(GenerateStrings.Problem(result.Problem));
                L.Info("generate.run", null, ("kind", kind), ("problem", result.Problem), ("class", result.ClassName), ("ms", sw.ElapsedMilliseconds));
                return;
            }
            Apply(view.TextBuffer, snapshot, result.Header);
            int sourceLine = -1;
            if (result.Source.Count > 0 && cpp != null)
            {
                cppBuffer = cppBuffer ?? OpenBuffer(cpp, true);
                if (cppBuffer != null)
                {
                    var before = cppBuffer.CurrentSnapshot;
                    if (before.GetText() == cppText)
                    {
                        Apply(cppBuffer, before, result.Source);
                        var last = result.Source.OrderByDescending(x => x.Offset).First();
                        sourceLine = cppBuffer.CurrentSnapshot.GetLineNumberFromPosition(Math.Min(cppBuffer.CurrentSnapshot.Length, last.Offset + 1)) + 1;
                    }
                }
            }
            string what = string.Join(", ", result.Generated);
            VsStatusBar.SetText(cpp == null && result.Source.Count == 0 && kind != "constructor" ? GenerateStrings.NoSource : GenerateStrings.Done(what, result.ClassName));
            L.Info("generate.run", null, ("kind", kind), ("class", result.ClassName), ("generated", what), ("headerEdits", result.Header.Count),
                ("sourceEdits", result.Source.Count), ("source", cpp == null ? null : Path.GetFileName(cpp)), ("ms", sw.ElapsedMilliseconds));
            if (sourceLine >= 0) VsEditor.OpenAt(cpp, sourceLine, 0);
        }

        private static string SnippetNewLine(ITextSnapshot s)
        {
            if (s.LineCount > 0)
            {
                string lb = s.GetLineFromLineNumber(0).GetLineBreakText();
                if (!string.IsNullOrEmpty(lb)) return lb;
            }
            return "\r\n";
        }

        /// <summary>All insertions as one edit (one undo step); skipped when the buffer changed since the text was read.</summary>
        private static void Apply(ITextBuffer buffer, ITextSnapshot read, IReadOnlyList<TextInsertion> inserts)
        {
            if (inserts.Count == 0) return;
            using (var edit = buffer.CreateEdit())
            {
                foreach (var i in inserts)
                {
                    var p = new SnapshotPoint(read, Math.Min(read.Length, Math.Max(0, i.Offset))).TranslateTo(buffer.CurrentSnapshot, PointTrackingMode.Positive);
                    edit.Insert(p.Position, i.Text);
                }
                edit.Apply();
            }
        }

        /// <summary>Text buffer of <paramref name="path"/>: already open, or opened when <paramref name="open"/>.</summary>
        private static ITextBuffer OpenBuffer(string path, bool open)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            try
            {
                IVsTextView vsView = null;
                if (VsShellUtilities.IsDocumentOpen(ServiceProvider.GlobalProvider, path, Guid.Empty, out _, out _, out IVsWindowFrame openFrame) && openFrame != null)
                    vsView = VsShellUtilities.GetTextView(openFrame); // no activation
                else if (open)
                {
                    VsShellUtilities.OpenDocument(ServiceProvider.GlobalProvider, path, VSConstants.LOGVIEWID.TextView_guid, out _, out _, out var frame, out vsView);
                    if (vsView == null && frame != null) vsView = VsShellUtilities.GetTextView(frame);
                }
                return vsView == null ? null : EditorServices.Adapters?.GetWpfTextView(vsView)?.TextBuffer;
            }
            catch (Exception ex)
            {
                L.Warn("generate.open.failed", "Opening the source file failed", ex, ("file", path));
                return null;
            }
        }

        private static string ReadText(string path)
        {
            try
            {
                return File.ReadAllText(path);
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
            {
                L.Warn("generate.read.failed", ex.Message, ("file", path));
                return null;
            }
        }
    }
}
