using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using EnvDTE;
using Microsoft.VisualStudio.Shell;
using UEToVS.Core.Diagnostics;
using UEToVS.Host.Editor;
using UEToVS.Host.Integration;
using UEToVS.Host.IntelliSense;
using UEToVS.Host.Logic.Editing;
using UEToVS.Host.Logic.Localization;
using UEToVS.Host.Navigation;
using UEToVS.Host.Shell;
using UEToVS.UI.Patterns;
using UEToVS.UI.Shells;

namespace UEToVS.Host.Editing
{
    /// <summary>
    /// Comment #tag bookmarks (v0.4.5, Ctrl+Shift+Alt+U, 3): scans the comments of the documents open in VS (on demand,
    /// nothing resident), groups the tags in a popup (#todo (3) › file:line text) and opens the chosen one.
    /// </summary>
    internal static class HashtagsFeature
    {
        private static readonly Logger L = Log.For("Editing");
        private const int MaxDocumentChars = 4 * 1024 * 1024;
        private static ContextMenuPopup _menu;

        public static async Task ShowAsync()
        {
            await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
            if (IntelliSenseContext.Settings?.HashtagsEnabled == false)
            {
                VsStatusBar.SetText(EditorFeatureStrings.HashtagsDisabled);
                return;
            }
            var sw = Stopwatch.StartNew();
            var texts = OpenDocuments();
            long readMs = sw.ElapsedMilliseconds;
            var hits = await Task.Run(() => texts.SelectMany(t => HashtagScanner.Scan(t.Text, t.Path)).ToList());
            await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
            var groups = HashtagScanner.Group(hits);
            L.Info("editor.hashtags", null, ("documents", texts.Count), ("tags", groups.Count), ("hits", hits.Count), ("readMs", readMs), ("ms", sw.ElapsedMilliseconds));
            if (groups.Count == 0)
            {
                VsStatusBar.SetText(EditorFeatureStrings.HashtagsEmpty);
                return;
            }
            var entries = groups.SelectMany(g => g.Hits.Select((h, i) => new MenuEntry("tag:" + g.Tag + ":" + i, () => Path.GetFileName(h.FilePath) + ":" + (h.Line + 1) + "  " + h.Text)
            {
                Group = () => EditorFeatureStrings.HashtagGroup(g.Tag, g.Hits.Count),
                Description = () => h.FilePath,
                Tag = h,
            })).ToList();
            if (_menu == null) _menu = new ContextMenuPopup("uetovs.edit.hashtags");
            _menu.OwnerHandle = VsMainWindow.Handle;
            var view = EditorServices.ActiveView();
            var chosen = await _menu.ShowAsync(entries, view != null ? EditorServices.AnchorAt(view) : PopupAnchor.CenterOf(VsMainWindow.Handle));
            await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
            if (!(chosen?.Tag is HashtagHit hit)) return;
            VsEditor.OpenAt(hit.FilePath, hit.Line, hit.Column);
            L.Info("editor.hashtags.open", null, ("tag", hit.Tag), ("file", Path.GetFileName(hit.FilePath)), ("line", hit.Line + 1));
        }

        private static List<(string Path, string Text)> OpenDocuments()
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            var result = new List<(string, string)>();
            try
            {
                foreach (Document d in VsEditor.Dte.Documents)
                {
                    try
                    {
                        string path = d.FullName;
                        if (string.IsNullOrEmpty(path) || !IsSourceFile(path)) continue;
                        string text = VsEditor.ReadDocument(d);
                        if (text != null && text.Length <= MaxDocumentChars) result.Add((path, text));
                    }
                    catch (Exception)
                    {
                        // not a text document / being closed
                    }
                }
            }
            catch (Exception ex)
            {
                L.Warn("editor.hashtags.documents.failed", "Enumerating open documents failed", ex);
            }
            return result;
        }

        private static bool IsSourceFile(string path)
        {
            string ext = Path.GetExtension(path).ToLowerInvariant();
            return ext == ".h" || ext == ".hpp" || ext == ".cpp" || ext == ".c" || ext == ".cc" || ext == ".inl" || ext == ".cs" || ext == ".ush" || ext == ".usf" || ext == ".hlsl";
        }
    }
}
