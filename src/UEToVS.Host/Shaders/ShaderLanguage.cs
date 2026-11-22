using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.ComponentModel.Composition;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.VisualStudio.Core.Imaging;
using Microsoft.VisualStudio.Imaging;
using Microsoft.VisualStudio.Imaging.Interop;
using Microsoft.VisualStudio.Language.Intellisense;
using Microsoft.VisualStudio.Language.Intellisense.AsyncCompletion;
using Microsoft.VisualStudio.Language.Intellisense.AsyncCompletion.Data;
using Microsoft.VisualStudio.Language.StandardClassification;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Text;
using Microsoft.VisualStudio.Text.Adornments;
using Microsoft.VisualStudio.Text.Editor;
using Microsoft.VisualStudio.Utilities;
using UEToVS.Core.Diagnostics;
using UEToVS.Host.Diagnostics;
using UEToVS.Host.IntelliSense;
using UEToVS.Host.Logic.Localization;
using UEToVS.Host.Logic.Search;
using UEToVS.Host.Logic.Shaders;
using UEToVS.Host.Navigation;
using UEToVS.Host.Search;
using UEToVS.Host.Shell;
using UiGlyph = UEToVS.UI.Logic.SymbolKind;
using VsItem = Microsoft.VisualStudio.Language.Intellisense.AsyncCompletion.Data.CompletionItem;

namespace UEToVS.Host.Shaders
{
    /// <summary>
    /// 着色器语言功能共用的状态：最近解析过的文件（LRU）、引擎 / 项目目录、按需找一次的插件列表（/Plugin/&lt;名字&gt;/ 映射），以及
    /// include 解析。不在后台扫描：插件列表第一次用到 /Plugin/ 时才找，之后整个会话复用。
    /// </summary>
    internal static class ShaderWorkspace
    {
        private static readonly Logger L = Log.For("Shaders");
        public static readonly ShaderDocumentCache Cache = new ShaderDocumentCache(48);
        private static readonly object Gate = new object();
        private static Task<Dictionary<string, string>> _plugins;
        private static string _pluginsKey;

        public static bool Enabled => HostPackage.Instance?.Settings?.Effective?.ShaderIntelliSense ?? true;

        public static string EngineDir
        {
            get
            {
                string root = HostPackage.Instance?.Unreal?.EngineRoot;
                return string.IsNullOrEmpty(root) ? null : Path.Combine(root, "Engine");
            }
        }

        public static string ProjectDir
        {
            get
            {
                string p = HostPackage.Instance?.Unreal?.ProjectFile;
                return string.IsNullOrEmpty(p) ? null : Path.GetDirectoryName(p);
            }
        }

        public static bool IsShaderFile(string path)
        {
            string ext = Path.GetExtension(path ?? string.Empty);
            return ext.Equals(".usf", StringComparison.OrdinalIgnoreCase) || ext.Equals(".ush", StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>插件名 → .uplugin（项目的 Plugins 优先，再引擎的 Plugins）。第一次调用时找一遍，之后复用（换项目时重找）。</summary>
        public static Task<Dictionary<string, string>> PluginsAsync()
        {
            string engine = EngineDir, project = ProjectDir;
            string key = engine + "|" + project;
            lock (Gate)
            {
                if (_plugins != null && _pluginsKey == key && !_plugins.IsFaulted && !_plugins.IsCanceled) return _plugins;
                _pluginsKey = key;
                _plugins = Task.Run(() =>
                {
                    var sw = Stopwatch.StartNew();
                    var roots = new[] { project == null ? null : Path.Combine(project, "Plugins"), engine == null ? null : Path.Combine(engine, "Plugins") }
                        .Where(r => r != null && Directory.Exists(r));
                    using (var cts = new CancellationTokenSource(TimeSpan.FromSeconds(20)))
                    {
                        var found = PluginDiscovery.Find(roots, new DiskLister(), cts.Token);
                        L.Info("shader.plugins.discovered", null, ("plugins", found.Count), ("ms", sw.ElapsedMilliseconds));
                        return found;
                    }
                });
                return _plugins;
            }
        }

        /// <summary>已找到的插件列表（还没找过时为 null，不触发查找）。</summary>
        public static Dictionary<string, string> PluginsIfKnown()
        {
            // 只在任务已完成时取结果，不会阻塞
#pragma warning disable VSTHRD002
            lock (Gate) return _plugins != null && _plugins.Status == TaskStatus.RanToCompletion ? _plugins.Result : null;
#pragma warning restore VSTHRD002
        }

        /// <summary>有 Shaders 目录的插件：名字 → Shaders 目录。</summary>
        public static Dictionary<string, string> PluginShaderDirs(Dictionary<string, string> plugins)
        {
            var d = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            if (plugins == null) return d;
            foreach (var kv in plugins)
            {
                string dir = Path.Combine(Path.GetDirectoryName(kv.Value) ?? string.Empty, "Shaders");
                if (Directory.Exists(dir)) d[kv.Key] = dir;
            }
            return d;
        }

        /// <summary>
        /// include 解析器（给 <see cref="ShaderDefinitionFinder"/> 用）：/Plugin/ 开头的需要插件列表，已经找过就用，没找过就当场等它。
        /// 在后台线程调用。
        /// </summary>
        public static Func<string, string, string> Resolver()
        {
            string engine = EngineDir, project = ProjectDir;
            Dictionary<string, string> roots = null;
            return (include, from) =>
            {
                if (include.StartsWith("/Plugin/", StringComparison.OrdinalIgnoreCase) && roots == null)
                {
                    try
                    {
                        // 解析器只在后台线程调用（见上面的说明），等插件列表不会卡 UI 线程
#pragma warning disable VSTHRD002
                        roots = ShaderIncludePaths.AsVirtualRoots(PluginShaderDirs(PluginsAsync().GetAwaiter().GetResult()));
#pragma warning restore VSTHRD002
                    }
                    catch (Exception ex)
                    {
                        L.Warn("shader.plugins.failed", null, ex);
                        roots = new Dictionary<string, string>();
                    }
                }
                foreach (var c in HlslLexer.ResolveInclude(include, from, engine, project, roots))
                    if (File.Exists(c)) return c;
                return null;
            };
        }

        /// <summary>当前缓冲区的文本解析一次（不进缓存：没保存的内容和磁盘不同）。超过 4 MB 返回空文档。</summary>
        public static HlslDocument ParseBuffer(ITextSnapshot snapshot) =>
            snapshot == null || snapshot.Length > HlslParser.MaxLength ? HlslDocument.Empty : HlslParser.Parse(snapshot.GetText());

        public static string PathOf(ITextBuffer buffer) =>
            buffer != null && buffer.Properties.TryGetProperty(typeof(ITextDocument), out ITextDocument doc) ? doc.FilePath : null;

        public static ImageMoniker MonikerFor(HlslSymbolKind kind)
        {
            switch (kind)
            {
                case HlslSymbolKind.Function: return KnownMonikers.MethodPublic;
                case HlslSymbolKind.Struct: return KnownMonikers.StructurePublic;
                case HlslSymbolKind.Macro: return KnownMonikers.MacroPublic;
                case HlslSymbolKind.CBuffer: return KnownMonikers.StructurePublic;
                case HlslSymbolKind.Typedef: return KnownMonikers.TypeDefinition;
                default: return KnownMonikers.FieldPublic;
            }
        }

        public static ImageMoniker MonikerFor(HlslBuiltinKind kind)
        {
            switch (kind)
            {
                case HlslBuiltinKind.Type: return KnownMonikers.StructurePublic;
                case HlslBuiltinKind.UeMacro: return KnownMonikers.MacroPublic;
                case HlslBuiltinKind.Keyword: return KnownMonikers.IntellisenseKeyword;
                default: return KnownMonikers.MethodPublic;
            }
        }

        public static UiGlyph GlyphFor(HlslSymbolKind kind)
        {
            switch (kind)
            {
                case HlslSymbolKind.Function: return UiGlyph.Function;
                case HlslSymbolKind.Struct: return UiGlyph.Struct;
                case HlslSymbolKind.CBuffer: return UiGlyph.Struct;
                case HlslSymbolKind.Macro: return UiGlyph.Macro;
                case HlslSymbolKind.Typedef: return UiGlyph.Typedef;
                default: return UiGlyph.Variable;
            }
        }

        /// <summary>磁盘文件（大小 + 修改时间作版本戳）。</summary>
        internal sealed class DiskSource : IShaderFileSource
        {
            public string Stamp(string path)
            {
                try
                {
                    var fi = new FileInfo(path);
                    return fi.Exists && fi.Length <= HlslParser.MaxLength ? fi.Length + ":" + fi.LastWriteTimeUtc.Ticks : null;
                }
                catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException || ex is ArgumentException || ex is NotSupportedException)
                {
                    return null;
                }
            }

            public string Read(string path)
            {
                try
                {
                    return File.ReadAllText(path);
                }
                catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
                {
                    return null;
                }
            }
        }

        internal sealed class DiskLister : IDirectoryLister
        {
            public IEnumerable<string> Directories(string dir) => Directory.EnumerateDirectories(dir);

            public IEnumerable<string> Files(string dir, string pattern) => Directory.EnumerateFiles(dir, pattern);
        }

        /// <summary>沿 include 链找 <paramref name="word"/>（后台线程）；带超时。</summary>
        public static Task<List<ShaderDefinition>> FindAsync(string word, string file, HlslDocument current, bool memberOnly, TimeSpan timeout, CancellationToken ct)
        {
            var resolve = Resolver();
            return Task.Run(() =>
            {
                using (var cts = CancellationTokenSource.CreateLinkedTokenSource(ct))
                {
                    cts.CancelAfter(timeout);
                    try
                    {
                        return ShaderDefinitionFinder.Find(word, file, current, Cache, new DiskSource(), resolve, cts.Token, memberOnly);
                    }
                    catch (OperationCanceledException)
                    {
                        return new List<ShaderDefinition>();
                    }
                }
            }, ct);
        }
    }

    // ================================================================== 补全

    [Export(typeof(IAsyncCompletionSourceProvider))]
    [Name("UEToVS.ShaderCompletion")]
    [ContentType(ShaderContentType.Name)]
    internal sealed class ShaderCompletionSourceProvider : IAsyncCompletionSourceProvider
    {
        public IAsyncCompletionSource GetOrCreate(ITextView textView) =>
            textView.Properties.GetOrCreateSingletonProperty(typeof(ShaderCompletionSource), () => new ShaderCompletionSource(textView));
    }

    /// <summary>
    /// .usf / .ush 补全：#include "…" 里列虚拟目录 / 文件夹 / 头文件；其余位置列 HLSL 内建函数、UE 常用函数和宏、本文件和 include 链上的
    /// 函数 / 结构体 / 宏 / 全局变量；'.' 后面列纹理方法和结构体成员。
    /// </summary>
    internal sealed class ShaderCompletionSource : IAsyncCompletionSource
    {
        private static readonly Logger L = Log.For("Shaders");
        private static readonly object BuiltinKey = typeof(HlslBuiltin);
        private static readonly object SymbolKey = typeof(ShaderDefinition);
        private readonly ITextView _view;

        public ShaderCompletionSource(ITextView view) => _view = view;

        public CompletionStartData InitializeCompletion(CompletionTrigger trigger, SnapshotPoint triggerLocation, CancellationToken token)
        {
            if (!ShaderWorkspace.Enabled) return CompletionStartData.DoesNotParticipateInCompletion;
            return Safe.Execute(L, "shader.completion.init", () =>
            {
                var line = triggerLocation.GetContainingLine();
                if (line.Length > 4000) return CompletionStartData.DoesNotParticipateInCompletion;
                string text = line.GetText();
                int col = triggerLocation.Position - line.Start.Position;
                bool explicitInvoke = trigger.Reason == CompletionTriggerReason.Invoke || trigger.Reason == CompletionTriggerReason.InvokeAndCommitIfUnique;
                var inc = ShaderIncludePaths.Analyze(text, col);
                if (inc != null)
                {
                    char ch = trigger.Character;
                    if (!explicitInvoke && trigger.Reason == CompletionTriggerReason.Insertion && !(ch == '"' || ch == '/' || HlslParser.IsWord(ch) || ch == '.'))
                        return CompletionStartData.DoesNotParticipateInCompletion;
                    return new CompletionStartData(CompletionParticipation.ProvidesItems,
                        new SnapshotSpan(line.Start + inc.ReplaceStart, inc.ReplaceLength));
                }
                if (InCommentOrString(text, col)) return CompletionStartData.DoesNotParticipateInCompletion;
                if (!explicitInvoke)
                {
                    if (trigger.Reason != CompletionTriggerReason.Insertion) return CompletionStartData.DoesNotParticipateInCompletion;
                    char ch = trigger.Character;
                    if (!(char.IsLetter(ch) || ch == '_' || ch == '.')) return CompletionStartData.DoesNotParticipateInCompletion;
                    if (text.TrimStart().StartsWith("#", StringComparison.Ordinal) && ch != '.') return CompletionStartData.DoesNotParticipateInCompletion;
                }
                int s = col;
                while (s > 0 && HlslParser.IsWord(text[s - 1])) s--;
                if (s < col && char.IsDigit(text[s])) return CompletionStartData.DoesNotParticipateInCompletion;
                return new CompletionStartData(CompletionParticipation.ProvidesItems, new SnapshotSpan(line.Start + s, col - s));
            }, CompletionStartData.DoesNotParticipateInCompletion);
        }

        /// <summary>光标前的同一行里有没进入 // 注释或字符串（块注释跨行不管）。</summary>
        private static bool InCommentOrString(string line, int col)
        {
            bool str = false;
            for (int i = 0; i < col && i < line.Length; i++)
            {
                char c = line[i];
                if (c == '"') str = !str;
                else if (!str && c == '/' && i + 1 < line.Length && line[i + 1] == '/') return true;
            }
            return str;
        }

        public async Task<CompletionContext> GetCompletionContextAsync(IAsyncCompletionSession session, CompletionTrigger trigger, SnapshotPoint triggerLocation,
                                                                       SnapshotSpan applicableToSpan, CancellationToken token)
        {
            var sw = Stopwatch.StartNew();
            try
            {
                var snapshot = triggerLocation.Snapshot;
                var line = triggerLocation.GetContainingLine();
                string text = line.GetText();
                int col = triggerLocation.Position - line.Start.Position;
                string file = ShaderWorkspace.PathOf(_view.TextBuffer);
                var items = ImmutableArray.CreateBuilder<VsItem>();
                string kind;
                var inc = ShaderIncludePaths.Analyze(text, col);
                if (inc != null)
                {
                    kind = "include";
                    foreach (var it in await IncludeItemsAsync(inc, file, token).ConfigureAwait(false)) items.Add(it);
                }
                else
                {
                    int start = applicableToSpan.Start.Position - line.Start.Position;
                    bool afterDot = start > 0 && text[start - 1] == '.';
                    kind = afterDot ? "member" : "symbol";
                    var seen = new HashSet<string>(StringComparer.Ordinal);
                    var current = ShaderWorkspace.ParseBuffer(snapshot);
                    if (afterDot)
                    {
                        foreach (var s in current.Symbols.Where(s => s.Container != null))
                            if (seen.Add(s.Name)) items.Add(Item(new ShaderDefinition { File = file, Symbol = s }));
                    }
                    else
                    {
                        var resolve = ShaderWorkspace.Resolver();
                        var all = await Task.Run(() =>
                        {
                            using (var cts = CancellationTokenSource.CreateLinkedTokenSource(token))
                            {
                                cts.CancelAfter(TimeSpan.FromSeconds(2));
                                try
                                {
                                    return ShaderDefinitionFinder.All(file, current, ShaderWorkspace.Cache, new ShaderWorkspace.DiskSource(), resolve, cts.Token);
                                }
                                catch (OperationCanceledException)
                                {
                                    return new List<ShaderDefinition>();
                                }
                            }
                        }, token).ConfigureAwait(false);
                        foreach (var d in all)
                            if (seen.Add(d.Symbol.Name)) items.Add(Item(d));
                    }
                    foreach (var b in HlslBuiltins.For(afterDot))
                        if (seen.Add(b.Name)) items.Add(Item(b));
                }
                L.Debug("shader.completion", null, ("kind", kind), ("items", items.Count), ("ms", sw.ElapsedMilliseconds));
                return items.Count == 0 ? CompletionContext.Empty : new CompletionContext(items.ToImmutable());
            }
            catch (OperationCanceledException)
            {
                return CompletionContext.Empty;
            }
            catch (Exception ex)
            {
                L.Warn("shader.completion.error", null, ex);
                return CompletionContext.Empty;
            }
        }

        private VsItem Item(HlslBuiltin b)
        {
            var item = new VsItem(b.Name, this, IntelliSenseEditor.Image(ShaderWorkspace.MonikerFor(b.Kind)), ImmutableArray<CompletionFilter>.Empty,
                                  HlslBuiltins.KindText(b.Kind, UnrealFileStrings.English));
            item.Properties[BuiltinKey] = b;
            return item;
        }

        private VsItem Item(ShaderDefinition d)
        {
            var item = new VsItem(d.Symbol.Name, this, IntelliSenseEditor.Image(ShaderWorkspace.MonikerFor(d.Symbol.Kind)), ImmutableArray<CompletionFilter>.Empty,
                                  d.Depth == 0 ? HlslParser.Describe(d.Symbol.Kind, UnrealFileStrings.English) : Path.GetFileName(d.File ?? string.Empty));
            item.Properties[SymbolKey] = d;
            return item;
        }

        private async Task<List<VsItem>> IncludeItemsAsync(IncludeCompletionContext inc, string file, CancellationToken token)
        {
            var list = new List<VsItem>();
            string dir = inc.Directory;
            if (dir.Length == 0)
            {
                foreach (var r in ShaderIncludePaths.VirtualRoots)
                    list.Add(new VsItem(r, this, IntelliSenseEditor.Image(KnownMonikers.FolderClosed), ImmutableArray<CompletionFilter>.Empty, UnrealFileStrings.ShaderIncludeVirtualRoot));
            }
            Dictionary<string, string> pluginDirs = null;
            if (dir.StartsWith("/Plugin/", StringComparison.OrdinalIgnoreCase))
            {
                var plugins = await ShaderWorkspace.PluginsAsync().ConfigureAwait(false);
                pluginDirs = ShaderWorkspace.PluginShaderDirs(plugins);
                if (string.Equals(dir, "/Plugin/", StringComparison.OrdinalIgnoreCase))
                {
                    foreach (var name in pluginDirs.Keys.OrderBy(k => k, StringComparer.OrdinalIgnoreCase))
                        list.Add(new VsItem(name + "/", this, IntelliSenseEditor.Image(KnownMonikers.FolderClosed), ImmutableArray<CompletionFilter>.Empty, UnrealFileStrings.ShaderPluginDir));
                    return list;
                }
            }
            string engine = ShaderWorkspace.EngineDir, project = ShaderWorkspace.ProjectDir;
            string physical = ShaderIncludePaths.MapDirectory(dir, file, engine, project, pluginDirs);
            if (physical == null) return list;
            await Task.Run(() =>
            {
                if (!Directory.Exists(physical)) return;
                foreach (var d in Directory.EnumerateDirectories(physical).Take(500))
                {
                    token.ThrowIfCancellationRequested();
                    list.Add(new VsItem(Path.GetFileName(d) + "/", this, IntelliSenseEditor.Image(KnownMonikers.FolderClosed), ImmutableArray<CompletionFilter>.Empty, UnrealFileStrings.ShaderIncludeFolder));
                }
                foreach (var f in Directory.EnumerateFiles(physical).Where(ShaderIncludePaths.IsShaderInclude).Take(2000))
                {
                    token.ThrowIfCancellationRequested();
                    list.Add(new VsItem(Path.GetFileName(f), this, IntelliSenseEditor.Image(KnownMonikers.CPPHeaderFile), ImmutableArray<CompletionFilter>.Empty, UnrealFileStrings.ShaderIncludeFile));
                }
            }, token).ConfigureAwait(false);
            return list;
        }

        public Task<object> GetDescriptionAsync(IAsyncCompletionSession session, VsItem item, CancellationToken token)
        {
            if (item.Properties.TryGetProperty(BuiltinKey, out HlslBuiltin b)) return Task.FromResult(ShaderQuickInfo.Describe(b));
            if (item.Properties.TryGetProperty(SymbolKey, out ShaderDefinition d)) return Task.FromResult(ShaderQuickInfo.Describe(d));
            return Task.FromResult<object>(string.IsNullOrEmpty(item.Suffix) ? null : item.Suffix);
        }
    }

    // ================================================================== 悬停

    [Export(typeof(IAsyncQuickInfoSourceProvider))]
    [Name("UEToVS.ShaderQuickInfo")]
    [ContentType(ShaderContentType.Name)]
    internal sealed class ShaderQuickInfoSourceProvider : IAsyncQuickInfoSourceProvider
    {
        public IAsyncQuickInfoSource TryCreateQuickInfoSource(ITextBuffer textBuffer) =>
            textBuffer.Properties.GetOrCreateSingletonProperty(typeof(ShaderQuickInfo), () => new ShaderQuickInfo(textBuffer));
    }

    /// <summary>悬停：内建函数 / UE 宏显示签名和说明，本文件或 include 链上的名字显示声明行和位置。</summary>
    internal sealed class ShaderQuickInfo : IAsyncQuickInfoSource
    {
        private static readonly Logger L = Log.For("Shaders");
        private readonly ITextBuffer _buffer;

        public ShaderQuickInfo(ITextBuffer buffer) => _buffer = buffer;

        public async Task<QuickInfoItem> GetQuickInfoItemAsync(IAsyncQuickInfoSession session, CancellationToken cancellationToken)
        {
            try
            {
                if (!ShaderWorkspace.Enabled) return null;
                var point = session.GetTriggerPoint(_buffer.CurrentSnapshot);
                if (point == null) return null;
                var line = point.Value.GetContainingLine();
                if (line.Length > 4000) return null;
                string text = line.GetText();
                int col = point.Value.Position - line.Start.Position;
                if (col >= text.Length || !HlslParser.IsWord(text[col])) return null;
                string word = HlslParser.WordAt(text, col, out int start);
                if (word == null || HlslLexer.IsType(word) && HlslBuiltins.Get(word) == null) return null;
                var span = point.Value.Snapshot.CreateTrackingSpan(line.Start.Position + start, word.Length, SpanTrackingMode.EdgeInclusive);
                bool member = HlslParser.IsMemberAccess(text, start);
                var current = ShaderWorkspace.ParseBuffer(point.Value.Snapshot);
                var hits = await ShaderWorkspace.FindAsync(word, ShaderWorkspace.PathOf(_buffer), current, member, TimeSpan.FromSeconds(1.5), cancellationToken).ConfigureAwait(false);
                if (hits.Count > 0) return new QuickInfoItem(span, Describe(hits[0]));
                var b = HlslBuiltins.Get(word);
                return b != null ? new QuickInfoItem(span, Describe(b)) : null;
            }
            catch (OperationCanceledException)
            {
                return null;
            }
            catch (Exception ex)
            {
                L.Warn("shader.quickInfo.error", null, ex);
                return null;
            }
        }

        internal static object Describe(HlslBuiltin b)
        {
            bool en = UnrealFileStrings.English;
            return new ContainerElement(ContainerElementStyle.Stacked,
                new ContainerElement(ContainerElementStyle.Wrapped, IntelliSenseEditor.Image(ShaderWorkspace.MonikerFor(b.Kind)),
                    new ClassifiedTextElement(new ClassifiedTextRun(PredefinedClassificationTypeNames.Identifier, b.Signature))),
                new ClassifiedTextElement(new ClassifiedTextRun(PredefinedClassificationTypeNames.Comment, HlslBuiltins.KindText(b.Kind, en))),
                new ClassifiedTextElement(new ClassifiedTextRun(PredefinedClassificationTypeNames.NaturalLanguage, b.Description(en))));
        }

        internal static object Describe(ShaderDefinition d)
        {
            bool en = UnrealFileStrings.English;
            string where = d.File == null ? null : UnrealFileStrings.ShaderInFile(Path.GetFileName(d.File), d.Symbol.Line + 1);
            var parts = new List<object>
            {
                new ContainerElement(ContainerElementStyle.Wrapped, IntelliSenseEditor.Image(ShaderWorkspace.MonikerFor(d.Symbol.Kind)),
                    new ClassifiedTextElement(new ClassifiedTextRun(PredefinedClassificationTypeNames.Identifier, d.Symbol.Signature ?? d.Symbol.Name))),
                new ClassifiedTextElement(new ClassifiedTextRun(PredefinedClassificationTypeNames.Comment,
                    HlslParser.Describe(d.Symbol.Kind, en) + (d.Symbol.Container != null ? " · " + d.Symbol.Container : "") + (where != null ? " · " + where : ""))),
            };
            var b = HlslBuiltins.Get(d.Symbol.Name);
            if (b != null) parts.Add(new ClassifiedTextElement(new ClassifiedTextRun(PredefinedClassificationTypeNames.NaturalLanguage, b.Description(en))));
            return new ContainerElement(ContainerElementStyle.Stacked, parts);
        }

        public void Dispose()
        {
        }
    }

    // ================================================================== 转到定义 / 文件结构

    /// <summary>F12 / Ctrl+点击在着色器标识符上：本文件优先，再沿 #include 链；多个时弹列表。</summary>
    internal static class ShaderNavigation
    {
        private static readonly Logger L = Log.For("Shaders");

        /// <summary>光标处能找定义的标识符（不是关键字 / 数字），否则 null。</summary>
        public static string WordAtCaret(ITextView view, out bool member)
        {
            member = false;
            var caret = view.Caret.Position.BufferPosition;
            var line = caret.GetContainingLine();
            if (line.Length > 4000) return null;
            string text = line.GetText();
            string w = HlslParser.WordAt(text, caret.Position - line.Start.Position, out int start);
            if (w == null || HlslLexer.IsType(w) && HlslBuiltins.Get(w) == null) return null;
            member = HlslParser.IsMemberAccess(text, start);
            return w;
        }

        public static void GoToDefinition(ITextView view, string word, bool member)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            string file = ShaderWorkspace.PathOf(view.TextBuffer);
            var snapshot = view.TextSnapshot;
            VsStatusBar.SetText(UnrealFileStrings.ShaderSearching);
            _ = ThreadHelper.JoinableTaskFactory.RunAsync(async () =>
            {
                var sw = Stopwatch.StartNew();
                List<ShaderDefinition> hits;
                try
                {
                    var current = await Task.Run(() => ShaderWorkspace.ParseBuffer(snapshot)).ConfigureAwait(false);
                    hits = await ShaderWorkspace.FindAsync(word, file, current, member, TimeSpan.FromSeconds(5), CancellationToken.None).ConfigureAwait(false);
                    if (hits.Count == 0 && member) // a.b 的 b 不是结构体成员（比如宏）：再按顶层名字找一次
                        hits = await ShaderWorkspace.FindAsync(word, file, current, false, TimeSpan.FromSeconds(5), CancellationToken.None).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    L.Warn("shader.goto.error", null, ex, ("word", word));
                    hits = new List<ShaderDefinition>();
                }
                await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
                L.Info("shader.goto", null, ("word", word), ("hits", hits.Count), ("member", member), ("cacheFiles", ShaderWorkspace.Cache.Count),
                    ("ms", sw.ElapsedMilliseconds));
                VsStatusBar.SetText(string.Empty);
                if (hits.Count == 0)
                {
                    var b = HlslBuiltins.Get(word);
                    VsStatusBar.SetText(b != null ? b.Signature + " — " + b.Description(UnrealFileStrings.English) : UnrealFileStrings.ShaderDefinitionNotFound(word));
                    return;
                }
                if (hits.Count == 1)
                {
                    Jump(hits[0], file, view);
                    return;
                }
                var rows = hits.Select(h => new SearchRow
                {
                    Kind = SearchRowKind.Symbol,
                    Title = h.Symbol.Name,
                    Detail = h.Symbol.Signature,
                    Right = Path.GetFileName(h.File ?? file) + ":" + (h.Symbol.Line + 1),
                    Glyph = ShaderWorkspace.GlyphFor(h.Symbol.Kind),
                    FilePath = h.File ?? file,
                    Line = h.Symbol.Line,
                    Column = h.Symbol.Column,
                }).ToList();
                var pick = await RowPopups.ShowAsync("uetovs.shaderDefinitions", UnrealFileStrings.ShaderDefinitionsTitle(word), rows, true, new System.Windows.Size(720, 420));
                if (pick.Chosen && pick.Item != null)
                    NavigationService.Instance.JumpTo(pick.Item.FilePath, pick.Item.Line, pick.Item.Column, "shaderDefinition");
            });
        }

        private static void Jump(ShaderDefinition d, string currentFile, ITextView view)
        {
            string target = d.File ?? currentFile;
            if (target != null) NavigationService.Instance.JumpTo(target, d.Symbol.Line, d.Symbol.Column, "shaderDefinition");
            else if (view != null && d.Symbol.Line < view.TextSnapshot.LineCount) // 没保存过的新文件：只在本视图里移光标
            {
                var line = view.TextSnapshot.GetLineFromLineNumber(d.Symbol.Line);
                view.Caret.MoveTo(new SnapshotPoint(view.TextSnapshot, Math.Min(line.End.Position, line.Start.Position + d.Symbol.Column)));
                view.ViewScroller.EnsureSpanVisible(new SnapshotSpan(view.Caret.Position.BufferPosition, 0));
            }
        }

        /// <summary>文件结构（Ctrl+Shift+Alt+M）的行：按文件顺序的函数、结构体（含成员）、宏、cbuffer、全局变量。</summary>
        public static List<SearchRow> FileStructureRows(ITextView view)
        {
            string file = ShaderWorkspace.PathOf(view.TextBuffer);
            var doc = ShaderWorkspace.ParseBuffer(view.TextSnapshot);
            return doc.Symbols.Select(s => new SearchRow
            {
                Kind = SearchRowKind.Symbol,
                Title = s.Name,
                Detail = s.Container ?? HlslParser.Describe(s.Kind, UnrealFileStrings.English),
                Right = ":" + (s.Line + 1),
                Glyph = ShaderWorkspace.GlyphFor(s.Kind),
                FilePath = file,
                Line = s.Line,
                Column = s.Column,
            }).ToList();
        }
    }
}
