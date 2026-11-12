using System;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Media;
using Microsoft.VisualStudio;
using Microsoft.VisualStudio.ComponentModelHost;
using Microsoft.VisualStudio.Editor;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Shell.Interop;
using Microsoft.VisualStudio.Text;
using Microsoft.VisualStudio.Text.Editor;
using Microsoft.VisualStudio.TextManager.Interop;
using UEToVS.Contracts;
using UEToVS.Core.Diagnostics;
using UEToVS.Core.Navigation;
using UEToVS.Host.Logic.Navigation;
using UEToVS.UI.Logic;
using UEToVS.UI.Shells;

namespace UEToVS.Host.Editor
{
    /// <summary>Content types the navigation features attach to (VS C++ editor content type).</summary>
    internal static class EditorContentTypes
    {
        public const string Cpp = "C/C++";
    }

    /// <summary>
    /// Thin VS editor helpers shared by the navigation features: the active C++ text view, document path / dirty state,
    /// caret requests from a view, screen anchors for popups. UI thread only unless noted.
    /// </summary>
    internal static class EditorServices
    {
        private static readonly Logger L = Log.For("Editor");
        private static IVsEditorAdaptersFactoryService _adapters;

        public static IVsEditorAdaptersFactoryService Adapters
        {
            get
            {
                ThreadHelper.ThrowIfNotOnUIThread();
                if (_adapters == null && Package.GetGlobalService(typeof(SComponentModel)) is IComponentModel cm)
                    _adapters = cm.GetService<IVsEditorAdaptersFactoryService>();
                return _adapters;
            }
        }

        /// <summary>The focused / last active document text view, or null.</summary>
        public static IWpfTextView ActiveView()
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            try
            {
                if (!(Package.GetGlobalService(typeof(SVsTextManager)) is IVsTextManager tm)) return null;
                if (ErrorHandler.Failed(tm.GetActiveView(1, null, out IVsTextView vsView)) || vsView == null) return null;
                return Adapters?.GetWpfTextView(vsView);
            }
            catch (Exception ex)
            {
                L.Warn("editor.activeView.failed", "Cannot get the active text view", ex);
                return null;
            }
        }

        public static bool IsCpp(ITextBuffer buffer) => buffer?.ContentType?.IsOfType(EditorContentTypes.Cpp) == true;

        public static ITextDocument Document(ITextBuffer buffer) =>
            buffer != null && buffer.Properties.TryGetProperty(typeof(ITextDocument), out ITextDocument doc) ? doc : null;

        public static string FilePath(ITextBuffer buffer) => Document(buffer)?.FilePath;

        /// <summary>The caret (or <paramref name="at"/>) of a view as a CodeLocation, or null.</summary>
        public static CodeLocation Location(ITextView view, SnapshotPoint? at = null)
        {
            if (view == null || view.IsClosed) return null;
            string path = FilePath(view.TextBuffer);
            if (string.IsNullOrEmpty(path)) return null;
            var p = at ?? view.Caret.Position.BufferPosition;
            var line = p.GetContainingLine();
            return new CodeLocation(path, line.LineNumber, p.Position - line.Start.Position);
        }

        /// <summary>
        /// Captures what a CaretRequest needs on the calling (UI) thread — cheap: snapshot, numbers and dirty flag — and
        /// returns a factory that builds the request (materializing a dirty buffer's text) on any thread.
        /// </summary>
        public static Func<CaretRequest> CaptureCaret(ITextView view, SnapshotPoint? at = null)
        {
            if (view == null || view.IsClosed) return null;
            var buffer = view.TextBuffer;
            var doc = Document(buffer);
            string path = doc?.FilePath;
            if (string.IsNullOrEmpty(path)) return null;
            var snapshot = buffer.CurrentSnapshot;
            var point = (at ?? view.Caret.Position.BufferPosition).TranslateTo(snapshot, PointTrackingMode.Positive);
            var line = point.GetContainingLine();
            bool dirty = doc.IsDirty;
            int lineNumber = line.LineNumber, lineStart = line.Start.Position, lineLength = line.Length, position = point.Position;
            return () => CaretRequestBuilder.Build(path, lineNumber, lineStart, lineLength, position, dirty, () => snapshot.GetText(), snapshot.Length,
                                                   snapshot.Version.VersionNumber);
        }

        /// <summary>Builds the caret request off the UI thread (text of a dirty buffer can be megabytes).</summary>
        public static Task<CaretRequest> CaretRequestAsync(ITextView view, SnapshotPoint? at = null)
        {
            var capture = CaptureCaret(view, at);
            return capture == null ? Task.FromResult<CaretRequest>(null) : Task.Run(capture);
        }

        /// <summary>Popup anchor just below the caret (or <paramref name="at"/>) in device pixels; mouse position as fallback.</summary>
        public static PopupAnchor AnchorAt(IWpfTextView view, SnapshotPoint? at = null)
        {
            try
            {
                if (view == null || view.IsClosed || view.InLayout) return PopupAnchor.Mouse();
                var p = at ?? view.Caret.Position.BufferPosition;
                var line = view.TextViewLines?.GetTextViewLineContainingBufferPosition(p);
                if (line == null) return PopupAnchor.Mouse();
                var bounds = line.GetCharacterBounds(p);
                var element = view.VisualElement;
                if (PresentationSource.FromVisual(element) == null) return PopupAnchor.Mouse();
                var topLeft = element.PointToScreen(new Point(bounds.Left - view.ViewportLeft, bounds.Top - view.ViewportTop));
                double scale = VisualTreeHelper.GetDpi(element).DpiScaleY;
                return PopupAnchor.Caret(topLeft, bounds.Height * scale);
            }
            catch (Exception ex)
            {
                L.Debug("editor.anchor.failed", ex.Message);
                return PopupAnchor.Mouse();
            }
        }

        /// <summary>Is the open solution an Unreal Engine solution (features stay idle otherwise)?</summary>
        public static bool IsUnrealSolution => HostPackage.Instance?.Unreal.IsUnreal == true;
    }
}
