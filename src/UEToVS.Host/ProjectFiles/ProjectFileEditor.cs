using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.ComponentModel.Composition;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.VisualStudio;
using Microsoft.VisualStudio.Editor;
using Microsoft.VisualStudio.Imaging;
using Microsoft.VisualStudio.Language.Intellisense.AsyncCompletion;
using Microsoft.VisualStudio.Language.Intellisense.AsyncCompletion.Data;
using Microsoft.VisualStudio.OLE.Interop;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Text;
using Microsoft.VisualStudio.Text.Editor;
using Microsoft.VisualStudio.TextManager.Interop;
using Microsoft.VisualStudio.Utilities;
using UEToVS.Contracts;
using UEToVS.Core.Diagnostics;
using UEToVS.Core.Localization;
using UEToVS.Host.Diagnostics;
using UEToVS.Host.IntelliSense;
using UEToVS.Host.Logic.ProjectFiles;
using UEToVS.Host.Navigation;
using UEToVS.Host.Search;
using UEToVS.Host.Shell;
using VsItem = Microsoft.VisualStudio.Language.Intellisense.AsyncCompletion.Data.CompletionItem;

namespace UEToVS.Host.ProjectFiles
{
    internal static class ProjectFileDocs
    {
        public static string PathOf(ITextBuffer buffer) =>
            buffer != null && buffer.Properties.TryGetProperty(typeof(ITextDocument), out ITextDocument doc) ? doc.FilePath : null;

        public static bool Supported(string path) => ProjectFileLogic.IsDescriptor(path) || ProjectFileLogic.IsIni(path);

        private static readonly ConcurrentDictionary<string, (DateTime At, List<SymbolInfo> Members, SymbolInfo Type)> ConfigCache =
            new ConcurrentDictionary<string, (DateTime, List<SymbolInfo>, SymbolInfo)>(StringComparer.Ordinal);

        /// <summary>
        /// The class an ini <c>[/Script/Module.Class]</c> section configures and its Config properties (inherited included).
        /// Two engine lookups, on demand only; cached 30 s per section.
        /// </summary>
        public static async Task<(SymbolInfo Type, List<SymbolInfo> Members)> ConfigClassAsync(string module, string cls, CancellationToken ct)
        {
            string key = module + "." + cls;
            if (ConfigCache.TryGetValue(key, out var hit) && DateTime.UtcNow - hit.At < TimeSpan.FromSeconds(30)) return (hit.Type, hit.Members);
            var engine = HostPackage.Instance?.Engine;
            if (engine == null) return (null, new List<SymbolInfo>());
            SymbolInfo type = null;
            foreach (var name in ProjectFileLogic.ClassCandidates(cls))
            {
                var found = await EngineQueries.FindSymbolsAsync(engine, name, null, ct).ConfigureAwait(false);
                type = found.FirstOrDefault(s => s.Kind == SymbolKind.Class && s.Name == name &&
                                                 (s.Module == null || string.Equals(s.Module, module, StringComparison.OrdinalIgnoreCase)))
                       ?? found.FirstOrDefault(s => s.Kind == SymbolKind.Class && s.Name == name);
                if (type != null) break;
            }
            var members = new List<SymbolInfo>();
            if (type != null)
            {
                var r = await engine.GetMembersAsync(new MembersRequest { TypeName = type.Name, IncludeInherited = true }, ct).ConfigureAwait(false);
                if (r.Ok && r.Value != null)
                    members = r.Value.Members.Where(m => (m.Kind == SymbolKind.Field || m.Kind == SymbolKind.Variable) &&
                                                         ProjectFileLogic.IsConfigProperty(m.Specifiers)).ToList();
            }
            ConfigCache[key] = (DateTime.UtcNow, members, type);
            return (type, members);
        }
    }

    [Export(typeof(IAsyncCompletionSourceProvider))]
    [Name("UEToVS.ProjectFileCompletion")]
    [ContentType("text")]
    internal sealed class ProjectFileCompletionSourceProvider : IAsyncCompletionSourceProvider
    {
        public IAsyncCompletionSource GetOrCreate(ITextView textView) =>
            textView.Properties.GetOrCreateSingletonProperty(typeof(ProjectFileCompletionSource), () => new ProjectFileCompletionSource(textView));
    }

    /// <summary>
    /// .uproject / .uplugin: fixed value lists (module Type, LoadingPhase, platform / target lists). .ini: Config property names of
    /// the class a <c>[/Script/Module.Class]</c> section configures. Other files do not participate (extension check only).
    /// </summary>
    internal sealed class ProjectFileCompletionSource : IAsyncCompletionSource
    {
        private static readonly Logger L = Log.For("ProjectFiles");
        private readonly ITextView _view;

        public ProjectFileCompletionSource(ITextView view) => _view = view;

        public CompletionStartData InitializeCompletion(CompletionTrigger trigger, SnapshotPoint triggerLocation, CancellationToken token)
        {
            string path = ProjectFileDocs.PathOf(_view.TextBuffer);
            if (!ProjectFileDocs.Supported(path)) return CompletionStartData.DoesNotParticipateInCompletion;
            return Safe.Execute(L, "projectFiles.completion.init", () =>
            {
                var snapshot = triggerLocation.Snapshot;
                if (ProjectFileLogic.IsDescriptor(path))
                {
                    if (snapshot.Length > 512 * 1024) return CompletionStartData.DoesNotParticipateInCompletion;
                    var c = ProjectFileLogic.At(snapshot.GetText(), triggerLocation.Position);
                    if (ProjectFileLogic.ValuesFor(c) == null) return CompletionStartData.DoesNotParticipateInCompletion;
                    return new CompletionStartData(CompletionParticipation.ProvidesItems, new SnapshotSpan(snapshot, c.ValueStart, c.ValueLength));
                }
                var line = triggerLocation.GetContainingLine();
                int s = ProjectFileLogic.IniKeyStart(line.GetText(), triggerLocation.Position - line.Start.Position, out _);
                if (s < 0 || ScriptSectionAbove(snapshot, line.LineNumber) == null) return CompletionStartData.DoesNotParticipateInCompletion;
                return new CompletionStartData(CompletionParticipation.ProvidesItems, new SnapshotSpan(line.Start + s, triggerLocation));
            }, CompletionStartData.DoesNotParticipateInCompletion);
        }

        internal static (string Module, string Class)? ScriptSectionAbove(ITextSnapshot snapshot, int lineNumber)
        {
            for (int n = lineNumber; n >= 0 && lineNumber - n < 5000; n--)
            {
                string t = snapshot.GetLineFromLineNumber(n).GetText().Trim();
                if (t.StartsWith("[", StringComparison.Ordinal)) return ProjectFileLogic.ScriptSection(t);
            }
            return null;
        }

        public async Task<CompletionContext> GetCompletionContextAsync(IAsyncCompletionSession session, CompletionTrigger trigger, SnapshotPoint triggerLocation,
                                                                       SnapshotSpan applicableToSpan, CancellationToken token)
        {
            var sw = Stopwatch.StartNew();
            string path = ProjectFileDocs.PathOf(_view.TextBuffer);
            try
            {
                var items = ImmutableArray.CreateBuilder<VsItem>();
                string kind;
                if (ProjectFileLogic.IsDescriptor(path))
                {
                    kind = "descriptor";
                    var c = ProjectFileLogic.At(triggerLocation.Snapshot.GetText(), triggerLocation.Position);
                    foreach (var v in ProjectFileLogic.ValuesFor(c) ?? Array.Empty<string>())
                        items.Add(new VsItem(v, this, IntelliSenseEditor.Image(KnownMonikers.EnumerationItemPublic)));
                }
                else
                {
                    kind = "ini";
                    var section = ScriptSectionAbove(triggerLocation.Snapshot, triggerLocation.GetContainingLine().LineNumber);
                    if (section == null) return CompletionContext.Empty;
                    var (_, members) = await ProjectFileDocs.ConfigClassAsync(section.Value.Module, section.Value.Class, token).ConfigureAwait(false);
                    foreach (var m in members.GroupBy(m => m.Name).Select(g => g.First()))
                        items.Add(new VsItem(m.Name, this, IntelliSenseEditor.Image(KnownMonikers.FieldPublic), ImmutableArray<CompletionFilter>.Empty,
                                             m.Container ?? string.Empty));
                }
                L.Debug("projectFiles.completion", null, ("kind", kind), ("items", items.Count), ("ms", sw.ElapsedMilliseconds));
                return items.Count == 0 ? CompletionContext.Empty : new CompletionContext(items.ToImmutable());
            }
            catch (OperationCanceledException)
            {
                return CompletionContext.Empty;
            }
            catch (Exception ex)
            {
                L.Warn("projectFiles.completion.error", null, ex);
                return CompletionContext.Empty;
            }
        }

        public Task<object> GetDescriptionAsync(IAsyncCompletionSession session, VsItem item, CancellationToken token) =>
            Task.FromResult<object>(string.IsNullOrEmpty(item.Suffix) ? null : item.Suffix);
    }

    [Export(typeof(IVsTextViewCreationListener))]
    [ContentType("text")]
    [TextViewRole(PredefinedTextViewRoles.Editable)]
    internal sealed class ProjectFileNavigationFilterProvider : IVsTextViewCreationListener
    {
        private static readonly Logger L = Log.For("ProjectFiles");

        [Import]
        internal IVsEditorAdaptersFactoryService Adapters { get; set; }

        public void VsTextViewCreated(IVsTextView textViewAdapter) => Safe.Execute(L, "projectFiles.filter.attach", () =>
        {
            var view = Adapters?.GetWpfTextView(textViewAdapter);
            if (view == null || !ProjectFileDocs.Supported(ProjectFileDocs.PathOf(view.TextBuffer))) return;
            var filter = new ProjectFileNavigationFilter(view);
            if (ErrorHandler.Succeeded(textViewAdapter.AddCommandFilter(filter, out IOleCommandTarget next))) filter.Next = next;
        });
    }

    /// <summary>
    /// F12 in descriptor / config files: module name → &lt;Module&gt;.Build.cs, plugin name → &lt;Plugin&gt;.uplugin,
    /// ini <c>[/Script/Module.Class]</c> → the class, a key under it → its Config property.
    /// </summary>
    internal sealed class ProjectFileNavigationFilter : IOleCommandTarget
    {
        private static readonly Logger L = Log.For("ProjectFiles");
        private readonly IWpfTextView _view;

        public ProjectFileNavigationFilter(IWpfTextView view) => _view = view;

        public IOleCommandTarget Next { get; set; }

        private sealed class Target
        {
            public string Kind;
            public string Name;
            public string Module;
            public string Class;
        }

        public int QueryStatus(ref Guid pguidCmdGroup, uint cCmds, OLECMD[] prgCmds, IntPtr pCmdText)
        {
            if (pguidCmdGroup == VSConstants.GUID_VSStandardCommandSet97 && cCmds == 1 && prgCmds[0].cmdID == (uint)VSConstants.VSStd97CmdID.GotoDefn &&
                TargetAtCaret() != null)
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
                var target = TargetAtCaret();
                if (target != null)
                {
                    Go(target, ProjectFileDocs.PathOf(_view.TextBuffer));
                    return VSConstants.S_OK;
                }
            }
            return Next?.Exec(ref pguidCmdGroup, nCmdID, nCmdexecopt, pvaIn, pvaOut) ?? (int)Constants.OLECMDERR_E_NOTSUPPORTED;
        }

        private Target TargetAtCaret()
        {
            var caret = _view.Caret.Position.BufferPosition;
            string path = ProjectFileDocs.PathOf(_view.TextBuffer);
            if (ProjectFileLogic.IsDescriptor(path))
            {
                if (caret.Snapshot.Length > 512 * 1024) return null;
                var c = ProjectFileLogic.At(caret.Snapshot.GetText(), caret.Position);
                switch (ProjectFileLogic.TargetOf(c))
                {
                    case DescriptorTargetKind.Module: return new Target { Kind = "module", Name = c.Value.Trim() };
                    case DescriptorTargetKind.Plugin: return new Target { Kind = "plugin", Name = c.Value.Trim() };
                    default: return null;
                }
            }
            if (!ProjectFileLogic.IsIni(path)) return null;
            var line = caret.GetContainingLine();
            string text = line.GetText();
            var header = ProjectFileLogic.ScriptSection(text);
            if (header != null) return new Target { Kind = "class", Module = header.Value.Module, Class = header.Value.Class };
            int eq = text.IndexOf('=');
            int col = caret.Position - line.Start.Position;
            if (eq < 0 || col > eq) return null;
            int s = ProjectFileLogic.IniKeyStart(text, eq, out string key);
            if (s < 0 || key.Length == 0) return null;
            var section = ProjectFileCompletionSource.ScriptSectionAbove(caret.Snapshot, line.LineNumber);
            if (section == null) return null;
            return new Target { Kind = "property", Name = key, Module = section.Value.Module, Class = section.Value.Class };
        }

        private static void Go(Target target, string currentFile)
        {
            var unreal = HostPackage.Instance?.Unreal;
            string engineRoot = unreal?.EngineRoot;
            string engineDir = string.IsNullOrEmpty(engineRoot) ? null : Path.Combine(engineRoot, "Engine");
            string projectDir = string.IsNullOrEmpty(unreal?.ProjectFile) ? null : Path.GetDirectoryName(unreal.ProjectFile);
            var engine = HostPackage.Instance?.Engine;
            _ = ThreadHelper.JoinableTaskFactory.RunAsync(async () =>
            {
                var sw = Stopwatch.StartNew();
                string file = null;
                SourceLocation location = null;
                try
                {
                    using (var cts = new CancellationTokenSource(TimeSpan.FromSeconds(8)))
                    {
                        switch (target.Kind)
                        {
                            case "module":
                                file = ProjectFileLogic.ModuleCandidates(currentFile, target.Name).FirstOrDefault(File.Exists)
                                       ?? (await EngineQueries.FindFilesByNameAsync(engine, target.Name + ".Build.cs", cts.Token).ConfigureAwait(false)).FirstOrDefault();
                                break;
                            case "plugin":
                                file = (await EngineQueries.FindFilesByNameAsync(engine, target.Name + ".uplugin", cts.Token).ConfigureAwait(false)).FirstOrDefault()
                                       ?? await Task.Run(() => FindPlugin(target.Name, projectDir, engineDir, cts.Token), cts.Token).ConfigureAwait(false);
                                break;
                            default:
                                var (type, members) = await ProjectFileDocs.ConfigClassAsync(target.Module, target.Class, cts.Token).ConfigureAwait(false);
                                location = target.Kind == "class" ? type?.Location : members.FirstOrDefault(m => m.Name == target.Name)?.Location;
                                break;
                        }
                    }
                }
                catch (OperationCanceledException)
                {
                }
                catch (Exception ex)
                {
                    L.Warn("projectFiles.goto.error", null, ex, ("kind", target.Kind));
                }
                await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
                bool opened = location != null ? NavigationService.Instance.JumpTo(location, "projectFiles") : file != null && VsEditor.OpenAt(file, -1, 0);
                L.Info("projectFiles.goto", null, ("kind", target.Kind), ("name", target.Name ?? target.Class), ("opened", opened), ("ms", sw.ElapsedMilliseconds));
                if (!opened)
                    VsStatusBar.SetText(Loc.T("未找到定义：" + (target.Name ?? target.Class), "Definition not found: " + (target.Name ?? target.Class)));
            });
        }

        /// <summary>Bounded on-demand fallback when the index has no such .uplugin: project Plugins first, then engine Plugins.</summary>
        private static string FindPlugin(string name, string projectDir, string engineDir, CancellationToken ct)
        {
            foreach (var root in new[] { projectDir, engineDir })
            {
                if (string.IsNullOrEmpty(root)) continue;
                string plugins = Path.Combine(root, "Plugins");
                if (!Directory.Exists(plugins)) continue;
                foreach (var f in Directory.EnumerateFiles(plugins, name + ".uplugin", SearchOption.AllDirectories))
                {
                    ct.ThrowIfCancellationRequested();
                    return f;
                }
            }
            return null;
        }
    }
}
