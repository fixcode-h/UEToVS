using System;
using System.Collections.Generic;
using System.ComponentModel.Composition;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Threading;
using Microsoft.VisualStudio.Imaging;
using Microsoft.VisualStudio.Language.Intellisense;
using Microsoft.VisualStudio.Language.StandardClassification;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Text;
using Microsoft.VisualStudio.Text.Adornments;
using Microsoft.VisualStudio.Text.Editor;
using Microsoft.VisualStudio.Text.Tagging;
using Microsoft.VisualStudio.Utilities;
using UEToVS.Contracts;
using UEToVS.Core.Diagnostics;
using UEToVS.Host.Diagnostics;
using UEToVS.Host.IntelliSense;
using UEToVS.Host.Logic.Localization;
using UEToVS.Host.Logic.ProjectFiles;
using UEToVS.Host.Search;
using UEToVS.Host.Unreal;

namespace UEToVS.Host.ProjectFiles
{
    // ================================================================== 悬停：ini 键 → C++ 属性

    [Export(typeof(IAsyncQuickInfoSourceProvider))]
    [Name("UEToVS.ConfigFileQuickInfo")]
    [ContentType("text")]
    internal sealed class ConfigFileQuickInfoProvider : IAsyncQuickInfoSourceProvider
    {
        public IAsyncQuickInfoSource TryCreateQuickInfoSource(ITextBuffer textBuffer) =>
            ProjectFileLogic.IsIni(ProjectFileDocs.PathOf(textBuffer))
                ? textBuffer.Properties.GetOrCreateSingletonProperty(typeof(ConfigFileQuickInfo), () => new ConfigFileQuickInfo(textBuffer))
                : null;
    }

    /// <summary>ini 里悬停在 [/Script/…] 节下的键上：对应 C++ 属性的声明、值的类型和写法提示。</summary>
    internal sealed class ConfigFileQuickInfo : IAsyncQuickInfoSource
    {
        private static readonly Logger L = Log.For("ProjectFiles");
        private readonly ITextBuffer _buffer;

        public ConfigFileQuickInfo(ITextBuffer buffer) => _buffer = buffer;

        public async Task<QuickInfoItem> GetQuickInfoItemAsync(IAsyncQuickInfoSession session, CancellationToken cancellationToken)
        {
            try
            {
                var point = session.GetTriggerPoint(_buffer.CurrentSnapshot);
                if (point == null) return null;
                var line = point.Value.GetContainingLine();
                if (line.Length > 4000) return null;
                string text = line.GetText();
                int eq = text.IndexOf('=');
                int col = point.Value.Position - line.Start.Position;
                if (eq < 0 || col >= eq) return null;
                int s = ProjectFileLogic.IniKeyStart(text, eq, out string key);
                if (s < 0 || key.Length == 0 || col < s) return null;
                var section = ProjectFileCompletionSource.ScriptSectionAbove(point.Value.Snapshot, line.LineNumber);
                if (section == null) return null;
                var (_, members) = await ProjectFileDocs.ConfigClassAsync(section.Value.Module, section.Value.Class, cancellationToken).ConfigureAwait(false);
                var m = members.FirstOrDefault(x => string.Equals(x.Name, key, StringComparison.OrdinalIgnoreCase));
                if (m == null) return null;
                var span = point.Value.Snapshot.CreateTrackingSpan(line.Start.Position + s, key.Length, SpanTrackingMode.EdgeInclusive);
                return new QuickInfoItem(span, Describe(m));
            }
            catch (OperationCanceledException)
            {
                return null;
            }
            catch (Exception ex)
            {
                L.Warn("projectFiles.quickInfo.error", null, ex);
                return null;
            }
        }

        /// <summary>属性的声明行、类型提示和所属类。</summary>
        internal static object Describe(SymbolInfo m)
        {
            bool en = UnrealFileStrings.English;
            string type = IniLogic.TypeFromSignature(m.Signature, m.Name);
            var hint = IniLogic.HintFor(type);
            var parts = new List<object>
            {
                new ContainerElement(ContainerElementStyle.Wrapped, IntelliSenseEditor.Image(KnownMonikers.FieldPublic),
                    new ClassifiedTextElement(new ClassifiedTextRun(PredefinedClassificationTypeNames.Identifier, m.Signature ?? m.Name))),
            };
            if (type != null) parts.Add(new ClassifiedTextElement(new ClassifiedTextRun(PredefinedClassificationTypeNames.Comment, UnrealFileStrings.IniValueOf(type))));
            parts.Add(new ClassifiedTextElement(new ClassifiedTextRun(PredefinedClassificationTypeNames.NaturalLanguage, hint.Hint(en))));
            if (!string.IsNullOrEmpty(m.Container))
                parts.Add(new ClassifiedTextElement(new ClassifiedTextRun(PredefinedClassificationTypeNames.Comment, UnrealFileStrings.IniDeclaredIn(m.Container))));
            return new ContainerElement(ContainerElementStyle.Stacked, parts);
        }

        public void Dispose()
        {
        }
    }

    // ================================================================== 校验波浪线

    [Export(typeof(IViewTaggerProvider))]
    [ContentType("text")]
    [TagType(typeof(IErrorTag))]
    [TextViewRole(PredefinedTextViewRoles.Document)]
    internal sealed class ConfigFileDiagnosticsProvider : IViewTaggerProvider
    {
        public ITagger<T> CreateTagger<T>(ITextView textView, ITextBuffer buffer) where T : ITag
        {
            if (buffer != textView.TextBuffer) return null;
            string path = ProjectFileDocs.PathOf(buffer);
            if (!ProjectFileLogic.IsIni(path) && !ProjectFileLogic.IsDescriptor(path)) return null;
            return textView.Properties.GetOrCreateSingletonProperty(typeof(ConfigFileDiagnostics), () => new ConfigFileDiagnostics(textView, path)) as ITagger<T>;
        }
    }

    /// <summary>
    /// ini：项目自己的 config 类下写了类里没有的 config 属性（提示级），项目模块里找不到的类（提示级）。引擎类不查（常用 GConfig 手动读）。
    /// .uproject / .uplugin：Modules 里找不到 Build.cs 的模块名（警告）。打开文件和停止编辑 800 ms 后算一次，只在引擎连上、索引就绪时报。
    /// </summary>
    internal sealed class ConfigFileDiagnostics : ITagger<IErrorTag>
    {
        private static readonly Logger L = Log.For("ProjectFiles");
        private const int MaxSections = 64;
        private readonly ITextView _view;
        private readonly string _path;
        private readonly DispatcherTimer _timer;
        private List<(ITrackingSpan Span, IErrorTag Tag)> _tags = new List<(ITrackingSpan, IErrorTag)>();
        private int _generation;

        public ConfigFileDiagnostics(ITextView view, string path)
        {
            _view = view;
            _path = path;
            _timer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(800) };
            _timer.Tick += (s, e) => Safe.Execute(L, "projectFiles.check.tick", () =>
            {
                _timer.Stop();
                Run();
            });
            _view.TextBuffer.Changed += OnChanged;
            _view.Closed += OnClosed;
            _timer.Start();
        }

        public event EventHandler<SnapshotSpanEventArgs> TagsChanged;

        private static bool Enabled => HostPackage.Instance?.Settings?.Effective?.ConfigFileValidation ?? true;

        private void OnChanged(object sender, TextContentChangedEventArgs e)
        {
            _timer.Stop();
            _timer.Start();
        }

        private void OnClosed(object sender, EventArgs e)
        {
            _timer.Stop();
            _view.TextBuffer.Changed -= OnChanged;
            _view.Closed -= OnClosed;
        }

        public IEnumerable<ITagSpan<IErrorTag>> GetTags(NormalizedSnapshotSpanCollection spans)
        {
            if (spans.Count == 0 || _tags.Count == 0 || !Enabled) yield break;
            var snapshot = spans[0].Snapshot;
            foreach (var (span, tag) in _tags)
            {
                if (span.TextBuffer != snapshot.TextBuffer) continue;
                var s = span.GetSpan(snapshot);
                if (s.IsEmpty || !spans.IntersectsWith(new NormalizedSnapshotSpanCollection(s))) continue;
                yield return new TagSpan<IErrorTag>(s, tag);
            }
        }

        private void Run()
        {
            var engine = HostPackage.Instance?.Engine;
            if (!Enabled || engine == null || !engine.IsConnected || engine.LastStatus?.Phase != IndexPhase.Ready)
            {
                Publish(new List<(ITrackingSpan, IErrorTag)>());
                return;
            }
            var snapshot = _view.TextSnapshot;
            if (snapshot.Length > 2 * 1024 * 1024) return;
            int gen = ++_generation;
            bool ini = ProjectFileLogic.IsIni(_path);
            string text = snapshot.GetText();
            string path = _path;
            _ = ThreadHelper.JoinableTaskFactory.RunAsync(async () =>
            {
                var sw = Stopwatch.StartNew();
                List<(Span Span, string Type, string Message)> found;
                try
                {
                    using (var cts = new CancellationTokenSource(TimeSpan.FromSeconds(20)))
                        found = ini ? await CheckIniAsync(text, cts.Token).ConfigureAwait(false) : await CheckDescriptorAsync(path, text, cts.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    return;
                }
                catch (Exception ex)
                {
                    L.Warn("projectFiles.check.error", null, ex, ("file", path));
                    return;
                }
                await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
                if (gen != _generation || _view.IsClosed) return;
                var tags = found.Where(f => f.Span.End <= snapshot.Length)
                                .Select(f => (snapshot.CreateTrackingSpan(f.Span, SpanTrackingMode.EdgeExclusive), (IErrorTag)new ErrorTag(f.Type, f.Message))).ToList();
                L.Info("projectFiles.check", null, ("kind", ini ? "ini" : "descriptor"), ("file", Path.GetFileName(path)), ("problems", tags.Count),
                    ("ms", sw.ElapsedMilliseconds));
                Publish(tags);
            });
        }

        private void Publish(List<(ITrackingSpan, IErrorTag)> tags)
        {
            if (tags.Count == 0 && _tags.Count == 0) return;
            _tags = tags;
            var snapshot = _view.TextSnapshot;
            TagsChanged?.Invoke(this, new SnapshotSpanEventArgs(new SnapshotSpan(snapshot, 0, snapshot.Length)));
        }

        private static List<int> LineStarts(string text)
        {
            var starts = new List<int> { 0 };
            for (int i = 0; i < text.Length; i++)
                if (text[i] == '\n') starts.Add(i + 1);
            return starts;
        }

        private static async Task<List<(Span, string, string)>> CheckIniAsync(string text, CancellationToken ct)
        {
            var result = new List<(Span, string, string)>();
            var starts = LineStarts(text);
            var lines = text.Split('\n').Select(l => l.TrimEnd('\r')).ToList();
            var sections = IniLogic.Parse(lines).Where(s => s.Module != null).Take(MaxSections).ToList();
            if (sections.Count == 0) return result;
            HashSet<string> projectModules = null;
            foreach (var s in sections)
            {
                ct.ThrowIfCancellationRequested();
                var (type, members) = await ProjectFileDocs.ConfigClassAsync(s.Module, s.Class, ct).ConfigureAwait(false);
                if (type == null)
                {
                    if (projectModules == null) projectModules = await ProjectModules.NamesAsync().ConfigureAwait(false);
                    if (projectModules.Contains(s.Module) && s.Start >= 0)
                        result.Add((new Span(starts[s.Line] + s.Start, s.Length), PredefinedErrorTypeNames.HintedSuggestion, UnrealFileStrings.IniUnknownClass(s.Module, s.Class)));
                    continue;
                }
                // 只查项目自己的类：引擎类大量用 GConfig 手动读键，按 UPROPERTY 判断会误报
                if (type.Origin != ItemOrigin.Project && type.Origin != ItemOrigin.ProjectPlugin) continue;
                foreach (var k in IniLogic.UnknownKeys(s, members.Select(m => m.Name).ToList()))
                    result.Add((new Span(starts[k.Line] + k.Start, k.Length), PredefinedErrorTypeNames.HintedSuggestion, UnrealFileStrings.IniUnknownKey(k.Name, type.Name)));
            }
            return result;
        }

        private static async Task<List<(Span, string, string)>> CheckDescriptorAsync(string path, string text, CancellationToken ct)
        {
            var result = new List<(Span, string, string)>();
            var engine = HostPackage.Instance?.Engine;
            foreach (var t in DescriptorLogic.ModuleNames(text))
            {
                ct.ThrowIfCancellationRequested();
                string name = t.Value.Trim();
                if (ProjectFileLogic.ModuleCandidates(path, name).Any(File.Exists)) continue;
                var files = await EngineQueries.FindFilesByNameAsync(engine, name + ".Build.cs", ct).ConfigureAwait(false);
                if (files.Count > 0) continue;
                result.Add((new Span(t.Start, t.Length), PredefinedErrorTypeNames.Warning, UnrealFileStrings.DescriptorModuleMissing(name)));
            }
            return result;
        }
    }
}
