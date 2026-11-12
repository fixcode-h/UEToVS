using System;
using System.Collections.Generic;
using System.ComponentModel.Composition;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.VisualStudio;
using Microsoft.VisualStudio.Editor;
using Microsoft.VisualStudio.Language.StandardClassification;
using Microsoft.VisualStudio.OLE.Interop;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Text;
using Microsoft.VisualStudio.Text.Classification;
using Microsoft.VisualStudio.Text.Editor;
using Microsoft.VisualStudio.TextManager.Interop;
using Microsoft.VisualStudio.Utilities;
using UEToVS.Core.Diagnostics;
using UEToVS.Core.Localization;
using UEToVS.Host.Diagnostics;
using UEToVS.Host.Shell;
using UEToVS.Host.Logic.Search;
using UEToVS.Host.Logic.Shaders;
using UEToVS.Host.Navigation;

namespace UEToVS.Host.Shaders
{
    /// <summary>Content type for UE shader sources (.usf / .ush): lexical colouring and #include navigation only.</summary>
    internal static class ShaderContentType
    {
        public const string Name = "UEToVS.Shader";

        [Export]
        [Name(Name)]
        [BaseDefinition("code")]
        internal static ContentTypeDefinition Definition = null;

        [Export]
        [FileExtension(".usf")]
        [ContentType(Name)]
        internal static FileExtensionToContentTypeDefinition Usf = null;

        [Export]
        [FileExtension(".ush")]
        [ContentType(Name)]
        internal static FileExtensionToContentTypeDefinition Ush = null;
    }

    [Export(typeof(IClassifierProvider))]
    [ContentType(ShaderContentType.Name)]
    internal sealed class ShaderClassifierProvider : IClassifierProvider
    {
        [Import]
        internal IStandardClassificationService Standard { get; set; }

        [Import]
        internal IClassificationTypeRegistryService Registry { get; set; }

        public IClassifier GetClassifier(ITextBuffer textBuffer) =>
            textBuffer.Properties.GetOrCreateSingletonProperty(() => new ShaderClassifier(textBuffer, Standard, Registry));
    }

    /// <summary>
    /// Colours only the lines VS asks for. The block-comment state at each line start is cached per snapshot and
    /// computed lazily up to the highest requested line (shader files are small; nothing runs in the background).
    /// </summary>
    internal sealed class ShaderClassifier : IClassifier
    {
        private static readonly Logger L = Log.For("Shaders");
        private readonly ITextBuffer _buffer;
        private readonly Dictionary<HlslTokenKind, IClassificationType> _types;
        private ITextSnapshot _stateSnapshot;
        private readonly List<CppLexState> _lineStates = new List<CppLexState>();

        public ShaderClassifier(ITextBuffer buffer, IStandardClassificationService standard, IClassificationTypeRegistryService registry)
        {
            _buffer = buffer;
            _types = new Dictionary<HlslTokenKind, IClassificationType>
            {
                [HlslTokenKind.Comment] = standard.Comment,
                [HlslTokenKind.String] = standard.StringLiteral,
                [HlslTokenKind.Number] = standard.NumberLiteral,
                [HlslTokenKind.Keyword] = standard.Keyword,
                [HlslTokenKind.Preprocessor] = standard.PreprocessorKeyword,
                [HlslTokenKind.Type] = registry.GetClassificationType("type") ?? standard.Keyword,
                [HlslTokenKind.Intrinsic] = registry.GetClassificationType("method name") ?? standard.Identifier,
                [HlslTokenKind.Semantic] = registry.GetClassificationType("macro name") ?? standard.SymbolReference,
            };
        }

#pragma warning disable CS0067 // colouring is purely lexical per line; the editor re-asks on every change
        public event EventHandler<ClassificationChangedEventArgs> ClassificationChanged;
#pragma warning restore CS0067

        public IList<ClassificationSpan> GetClassificationSpans(SnapshotSpan span)
        {
            var result = new List<ClassificationSpan>();
            Safe.Execute(L, "shader.classify", () =>
            {
                var snapshot = span.Snapshot;
                int first = snapshot.GetLineNumberFromPosition(span.Start);
                int last = snapshot.GetLineNumberFromPosition(Math.Max(span.Start, span.End - (span.Length > 0 ? 1 : 0)));
                EnsureStates(snapshot, last);
                var tokens = new List<HlslToken>();
                for (int n = first; n <= last; n++)
                {
                    var line = snapshot.GetLineFromLineNumber(n);
                    tokens.Clear();
                    HlslLexer.LexLine(line.GetText(), _lineStates[n], tokens);
                    foreach (var t in tokens)
                    {
                        if (!_types.TryGetValue(t.Kind, out var type) || type == null) continue;
                        var s = new SnapshotSpan(line.Start + t.Start, t.Length);
                        if (s.IntersectsWith(span)) result.Add(new ClassificationSpan(s, type));
                    }
                }
            });
            return result;
        }

        /// <summary>_lineStates[n] = lexer state at the start of line n, valid for <paramref name="snapshot"/>.</summary>
        private void EnsureStates(ITextSnapshot snapshot, int upTo)
        {
            if (_stateSnapshot != snapshot)
            {
                int keep = _lineStates.Count;
                if (_stateSnapshot != null && _stateSnapshot.Version.VersionNumber < snapshot.Version.VersionNumber)
                {
                    // keep the states above the first changed line
                    for (var v = _stateSnapshot.Version; v != null && v.Changes != null && v != snapshot.Version; v = v.Next)
                        foreach (var c in v.Changes)
                            keep = Math.Min(keep, _stateSnapshot.GetLineNumberFromPosition(Math.Min(c.OldPosition, _stateSnapshot.Length)) + 1);
                }
                else keep = 0;
                if (keep < _lineStates.Count) _lineStates.RemoveRange(keep, _lineStates.Count - keep);
                if (_lineStates.Count == 0) _lineStates.Add(default);
                _stateSnapshot = snapshot;
            }
            var scratch = new List<HlslToken>();
            while (_lineStates.Count <= upTo)
            {
                int n = _lineStates.Count - 1;
                scratch.Clear();
                _lineStates.Add(HlslLexer.LexLine(snapshot.GetLineFromLineNumber(n).GetText(), _lineStates[n], scratch));
            }
        }
    }

    [Export(typeof(IVsTextViewCreationListener))]
    [ContentType(ShaderContentType.Name)]
    [TextViewRole(PredefinedTextViewRoles.Editable)]
    internal sealed class ShaderIncludeFilterProvider : IVsTextViewCreationListener
    {
        private static readonly Logger L = Log.For("Shaders");

        [Import]
        internal IVsEditorAdaptersFactoryService Adapters { get; set; }

        public void VsTextViewCreated(IVsTextView textViewAdapter) => Safe.Execute(L, "shader.filter.attach", () =>
        {
            var view = Adapters?.GetWpfTextView(textViewAdapter);
            if (view == null) return;
            var filter = new ShaderIncludeFilter(view);
            if (ErrorHandler.Succeeded(textViewAdapter.AddCommandFilter(filter, out IOleCommandTarget next))) filter.Next = next;
        });
    }

    /// <summary>F12 on an <c>#include "…"</c> line of a shader opens the included file (virtual shader paths resolved).</summary>
    internal sealed class ShaderIncludeFilter : IOleCommandTarget
    {
        private static readonly Logger L = Log.For("Shaders");
        private readonly IWpfTextView _view;

        public ShaderIncludeFilter(IWpfTextView view) => _view = view;

        public IOleCommandTarget Next { get; set; }

        public int QueryStatus(ref Guid pguidCmdGroup, uint cCmds, OLECMD[] prgCmds, IntPtr pCmdText)
        {
            if (pguidCmdGroup == VSConstants.GUID_VSStandardCommandSet97 && cCmds == 1 && prgCmds[0].cmdID == (uint)VSConstants.VSStd97CmdID.GotoDefn &&
                IncludeAtCaret(out _) != null)
            {
                prgCmds[0].cmdf = (uint)(OLECMDF.OLECMDF_SUPPORTED | OLECMDF.OLECMDF_ENABLED);
                return VSConstants.S_OK;
            }
            return Next?.QueryStatus(ref pguidCmdGroup, cCmds, prgCmds, pCmdText) ?? (int)Constants.OLECMDERR_E_NOTSUPPORTED;
        }

        public int Exec(ref Guid pguidCmdGroup, uint nCmdID, uint nCmdexecopt, IntPtr pvaIn, IntPtr pvaOut)
        {
            if (pguidCmdGroup == VSConstants.GUID_VSStandardCommandSet97 && nCmdID == (uint)VSConstants.VSStd97CmdID.GotoDefn)
            {
                string include = IncludeAtCaret(out string currentFile);
                if (include != null)
                {
                    Open(include, currentFile);
                    return VSConstants.S_OK;
                }
            }
            return Next?.Exec(ref pguidCmdGroup, nCmdID, nCmdexecopt, pvaIn, pvaOut) ?? (int)Constants.OLECMDERR_E_NOTSUPPORTED;
        }

        private string IncludeAtCaret(out string currentFile)
        {
            currentFile = null;
            var caret = _view.Caret.Position.BufferPosition;
            var line = caret.GetContainingLine();
            string include = HlslLexer.IncludeAt(line.GetText(), caret.Position - line.Start.Position, out _, out _);
            if (include == null) return null;
            if (_view.TextBuffer.Properties.TryGetProperty(typeof(ITextDocument), out ITextDocument doc)) currentFile = doc.FilePath;
            return include;
        }

        private static void Open(string include, string currentFile)
        {
            var unreal = HostPackage.Instance?.Unreal;
            string engineRoot = unreal?.EngineRoot;
            string engineDir = string.IsNullOrEmpty(engineRoot) ? null : Path.Combine(engineRoot, "Engine");
            string projectDir = string.IsNullOrEmpty(unreal?.ProjectFile) ? null : Path.GetDirectoryName(unreal.ProjectFile);
            _ = ThreadHelper.JoinableTaskFactory.RunAsync(async () =>
            {
                var started = DateTime.UtcNow;
                string found = null;
                string via = "none";
                try
                {
                    using (var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5)))
                    {
                        var plugins = await Task.Run(() => PluginShaderDirs(include, engineDir, projectDir, cts.Token), cts.Token);
                        found = await Task.Run(() =>
                            HlslLexer.ResolveInclude(include, currentFile, engineDir, projectDir, plugins).FirstOrDefault(File.Exists), cts.Token);
                        via = plugins.Count > 0 ? "plugin" : include.StartsWith("/", StringComparison.Ordinal) ? "virtual" : "relative";
                    }
                }
                catch (OperationCanceledException)
                {
                    via = "timeout";
                }
                catch (Exception ex)
                {
                    L.Warn("shader.include.error", null, ex, ("include", include));
                }
                await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
                bool opened = found != null && VsEditor.OpenAt(found, -1, 0);
                L.Info("shader.include.goto", null, ("include", include), ("via", via), ("found", found != null), ("opened", opened),
                    ("ms", (int)(DateTime.UtcNow - started).TotalMilliseconds));
                if (!opened) VsStatusBar.SetText(Loc.T("未找到着色器头文件：" + include, "Shader include not found: " + include));
            });
        }

        /// <summary>
        /// "/Plugin/&lt;Name&gt;/…" → &lt;Plugin&gt;/Shaders, looked up only for this include: one bounded search for &lt;Name&gt;.uplugin
        /// under the project's and the engine's Plugins folders. Plugins that map a different virtual root are not found.
        /// </summary>
        private static Dictionary<string, string> PluginShaderDirs(string include, string engineDir, string projectDir, CancellationToken ct)
        {
            var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            string inc = include.Replace('\\', '/');
            if (!inc.StartsWith("/Plugin/", StringComparison.OrdinalIgnoreCase)) return map;
            int slash = inc.IndexOf('/', "/Plugin/".Length);
            if (slash < 0) return map;
            string name = inc.Substring("/Plugin/".Length, slash - "/Plugin/".Length);
            foreach (var root in new[] { projectDir, engineDir })
            {
                if (string.IsNullOrEmpty(root)) continue;
                string plugins = Path.Combine(root, "Plugins");
                if (!Directory.Exists(plugins)) continue;
                foreach (var file in Directory.EnumerateFiles(plugins, name + ".uplugin", SearchOption.AllDirectories))
                {
                    ct.ThrowIfCancellationRequested();
                    map["/Plugin/" + name] = Path.Combine(Path.GetDirectoryName(file), "Shaders");
                    return map;
                }
            }
            return map;
        }
    }
}
