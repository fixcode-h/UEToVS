using System;
using System.Collections.Generic;
using System.ComponentModel.Composition;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Microsoft.VisualStudio.Language.Intellisense;
using Microsoft.VisualStudio.Language.StandardClassification;
using Microsoft.VisualStudio.Text;
using Microsoft.VisualStudio.Text.Adornments;
using Microsoft.VisualStudio.Text.Editor;
using Microsoft.VisualStudio.Utilities;
using UEToVS.Contracts;
using UEToVS.Core.Diagnostics;
using UEToVS.Host.Editor;
using UEToVS.Host.Logic.IntelliSense;
using UEToVS.Host.Logic.Localization;
using UEToVS.Host.Logic.Navigation;
using UEToVS.UI.Atoms;

namespace UEToVS.Host.IntelliSense
{
    [Export(typeof(IAsyncQuickInfoSourceProvider))]
    [Name("UEToVS Quick Info Source")]
    [ContentType(EditorContentTypes.Cpp)]
    internal sealed class UEToVSQuickInfoSourceProvider : IAsyncQuickInfoSourceProvider
    {
        public IAsyncQuickInfoSource TryCreateQuickInfoSource(ITextBuffer textBuffer) =>
            textBuffer.Properties.GetOrCreateSingletonProperty(typeof(UEToVSQuickInfoSource), () => new UEToVSQuickInfoSource(textBuffer));
    }

    /// <summary>
    /// Quick Info (§7.6) built from VS's own content elements, so it looks native: the declaration coloured with the same
    /// classifications as the editor (<see cref="DeclarationTokenizer"/>), "+N overloads", UE badge and location, the doc
    /// comment, the UE reflection summary with readable tags, and the macro expansion as a collapsible block. Before the
    /// takeover (VS's own tooltip still shows the declaration) only the UE parts are added. Lexical pre-check first: no RPC
    /// on whitespace, keywords, comments or literals.
    /// </summary>
    internal sealed class UEToVSQuickInfoSource : IAsyncQuickInfoSource
    {
        private static readonly Logger L = Log.For("IntelliSense");
        private readonly ITextBuffer _buffer;

        public UEToVSQuickInfoSource(ITextBuffer buffer) => _buffer = buffer;

        public async Task<QuickInfoItem> GetQuickInfoItemAsync(IAsyncQuickInfoSession session, CancellationToken cancellationToken)
        {
            try
            {
                if (!IntelliSenseContext.QuickInfoActive) return null;
                var engine = IntelliSenseContext.Engine;
                if (engine == null || !engine.IsConnected) return null;
                var point = session.GetTriggerPoint(_buffer.CurrentSnapshot);
                if (point == null) return null;
                var line = point.Value.GetContainingLine();
                if (line.Length > 4000) return null;
                string text = line.GetText();
                if (!IdentifierFinder.TryFind(text, point.Value.Position - line.Start.Position, false, out var word)) return null;
                var capture = IntelliSenseEditor.CaptureAt(_buffer, point.Value);
                if (capture == null) return null;
                var caret = await Task.Run(capture, cancellationToken).ConfigureAwait(false);
                var r = await engine.GetQuickInfoAsync(caret, cancellationToken).ConfigureAwait(false);
                if (!r.Ok) return null;
                var model = QuickInfoModel.Build(r.Value);
                bool ueOnly = IntelliSenseContext.QuickInfoUeOnly;
                var content = Build(model, r.Value, ueOnly);
                if (content == null) return null;
                var snapshot = point.Value.Snapshot;
                var span = SpanOf(snapshot, r.Value) ?? new Span(line.Start.Position + word.Start, word.Length);
                return new QuickInfoItem(snapshot.CreateTrackingSpan(span, SpanTrackingMode.EdgeInclusive), content);
            }
            catch (OperationCanceledException)
            {
                return null;
            }
            catch (Exception ex)
            {
                L.Error("is.quickInfo.failed", "Quick Info failed", ex);
                return null;
            }
        }

        private static Span? SpanOf(ITextSnapshot snapshot, QuickInfoResponse r)
        {
            if (r.Length <= 0 || r.Line < 0 || r.Line >= snapshot.LineCount) return null;
            var line = snapshot.GetLineFromLineNumber(r.Line);
            if (r.Column < 0 || r.Column + r.Length > line.Length) return null;
            return new Span(line.Start.Position + r.Column, r.Length);
        }

        /// <summary>The tooltip body; null when there is nothing (or, in UE-only mode, nothing UE-specific) to show.</summary>
        internal static object Build(QuickInfoModel m, QuickInfoResponse r, bool ueOnly)
        {
            if (m == null || m.IsEmpty) return null;
            var parts = new List<object>();
            if (!ueOnly)
            {
                var head = new List<object>();
                if (r.Symbol != null) head.Add(IntelliSenseEditor.Image(IntelliSenseEditor.MonikerFor(r.Symbol.Kind, r.Symbol.Ue)));
                var runs = m.Declaration.Select(t => new ClassifiedTextRun(IntelliSenseEditor.ClassificationFor(t), t.Text)).ToList();
                if (m.Overloads != null) runs.Add(new ClassifiedTextRun(PredefinedClassificationTypeNames.Comment, "  " + m.Overloads));
                if (runs.Count > 0) head.Add(new ClassifiedTextElement(runs));
                if (head.Count > 0) parts.Add(new ContainerElement(ContainerElementStyle.Wrapped, head));
                var meta = new List<ClassifiedTextRun>();
                foreach (var b in m.Badges) meta.Add(new ClassifiedTextRun(SemanticClassificationNames.UeMacro, b + "  "));
                if (m.Location != null) meta.Add(new ClassifiedTextRun(PredefinedClassificationTypeNames.Comment, m.Location));
                if (meta.Count > 0) parts.Add(new ClassifiedTextElement(meta));
                if (m.Documentation != null)
                    parts.Add(new ClassifiedTextElement(new ClassifiedTextRun(PredefinedClassificationTypeNames.NaturalLanguage, m.Documentation)));
            }
            if (m.Reflection != null)
            {
                if (parts.Count > 0) parts.Add(new ThematicBreakElement());
                parts.Add(new ClassifiedTextElement(ReflectionRuns(m.Reflection)));
                if (m.ReflectionTags.Count > 0)
                    parts.Add(new ClassifiedTextElement(new ClassifiedTextRun(PredefinedClassificationTypeNames.Comment, string.Join(" · ", m.ReflectionTags))));
            }
            if (m.MacroExpansion != null)
            {
                if (parts.Count > 0) parts.Add(new ThematicBreakElement());
                var steps = new List<MacroStepView>();
                for (int i = 0; i < m.MacroSteps.Count; i++)
                {
                    var step = m.MacroSteps[i];
                    var runs = DeclarationTokenizer.Tokenize(step.Text).Select(t => new ClassifiedTextRun(IntelliSenseEditor.ClassificationFor(t), t.Text)).ToList();
                    // "1 展开一层 → 2 完全展开 → 3 计算常量后": numbered so the order of the steps is obvious
                    string label = m.MacroSteps.Count > 1 ? (i + 1) + "  " + step.Label : step.Label;
                    steps.Add(new MacroStepView(label, new ClassifiedTextElement(runs)));
                }
                if (m.MacroExpansionTruncated && steps.Count > 0)
                    steps.Add(new MacroStepView(null, new ClassifiedTextElement(new ClassifiedTextRun(PredefinedClassificationTypeNames.Comment, IntelliSenseStrings.MacroExpansionTruncated))));
                string final = m.MacroSteps.Count > 0 ? m.MacroSteps[m.MacroSteps.Count - 1].Text : m.MacroExpansion;
                parts.Add(new MacroExpansionElement(IntelliSenseStrings.MacroExpansionTitle, steps, final, expanded: m.MacroExpansionLines <= 6));
            }
            if (!ueOnly && m.ConfidenceNote != null)
                parts.Add(new ClassifiedTextElement(new ClassifiedTextRun(PredefinedClassificationTypeNames.Comment, m.ConfidenceNote)));
            return parts.Count == 0 ? null : new ContainerElement(ContainerElementStyle.Stacked, parts);
        }

        /// <summary>UFUNCTION(BlueprintCallable, Category="X"): macro, specifiers and values coloured like the editor.</summary>
        private static List<ClassifiedTextRun> ReflectionRuns(string reflection)
        {
            var runs = new List<ClassifiedTextRun>();
            foreach (var t in DeclarationTokenizer.Tokenize(reflection))
            {
                string cls = t.Class == DeclTokenClass.Text || (t.Class == DeclTokenClass.Semantic && t.Kind != SemanticKind.UeMacro)
                    ? SemanticClassificationNames.UeSpecifier
                    : IntelliSenseEditor.ClassificationFor(t);
                runs.Add(new ClassifiedTextRun(cls, t.Text));
            }
            return runs;
        }

        public void Dispose()
        {
        }
    }

    /// <summary>One step of a macro expansion: a caption ("1  展开一层") and the coloured code.</summary>
    internal sealed class MacroStepView
    {
        public MacroStepView(string label, ClassifiedTextElement code)
        {
            Label = label;
            Code = code;
        }

        public string Label { get; }
        public ClassifiedTextElement Code { get; }
    }

    /// <summary>Collapsible macro expansion block inside Quick Info (rendered by <see cref="MacroExpansionViewElementFactory"/>).</summary>
    internal sealed class MacroExpansionElement
    {
        public MacroExpansionElement(string header, IReadOnlyList<MacroStepView> steps, string copyText, bool expanded)
        {
            Header = header;
            Steps = steps;
            CopyText = copyText;
            Expanded = expanded;
        }

        public string Header { get; }
        public IReadOnlyList<MacroStepView> Steps { get; }
        public string CopyText { get; }
        public bool Expanded { get; }
    }

    /// <summary>
    /// Renders <see cref="MacroExpansionElement"/>: a clickable header (▸/▾) with a "复制" link on the right, then each
    /// step as a small caption over a code block (monospace, subtle tinted background, rounded; VS's own factory
    /// renders the coloured runs so classifications apply). The tint is a neutral translucent gray so it reads on
    /// both light and dark Quick Info backgrounds.
    /// </summary>
    [Export(typeof(IViewElementFactory))]
    [Name("UEToVS Macro Expansion Element Factory")]
    [TypeConversion(from: typeof(MacroExpansionElement), to: typeof(UIElement))]
    [Order(Before = "default")]
    internal sealed class MacroExpansionViewElementFactory : IViewElementFactory
    {
        private static readonly Logger L = Log.For("IntelliSense");
        private static readonly System.Windows.Media.Brush CodeBackground = Frozen(System.Windows.Media.Color.FromArgb(0x1F, 0x80, 0x80, 0x80));
        private static readonly System.Windows.Media.Brush CodeBorder = Frozen(System.Windows.Media.Color.FromArgb(0x33, 0x80, 0x80, 0x80));
        private static readonly System.Windows.Media.FontFamily Mono = new System.Windows.Media.FontFamily("Cascadia Mono, Consolas, Courier New");

        [Import]
        internal IViewElementFactoryService Factory { get; set; }

        public TView CreateViewElement<TView>(ITextView textView, object model) where TView : class
        {
            if (!(model is MacroExpansionElement m) || typeof(TView) != typeof(UIElement)) return null;
            try
            {
                var body = new StackPanel { Margin = new Thickness(0, 2, 0, 0) };
                foreach (var step in m.Steps)
                {
                    if (step.Label != null)
                        body.Children.Add(new UiText(step.Label, TextVariant.Caption) { Margin = new Thickness(0, 6, 0, 3), Opacity = 0.8 });
                    var code = Factory.CreateViewElement<UIElement>(textView, step.Code);
                    if (code == null) continue;
                    if (code is FrameworkElement fe) System.Windows.Documents.TextElement.SetFontFamily(fe, Mono);
                    if (step.Label == null) { body.Children.Add(code); continue; }
                    body.Children.Add(new Border
                    {
                        Child = code, Background = CodeBackground, BorderBrush = CodeBorder, BorderThickness = new Thickness(1),
                        CornerRadius = new CornerRadius(4), Padding = new Thickness(8, 4, 8, 5),
                        HorizontalAlignment = HorizontalAlignment.Stretch, MaxWidth = 720,
                    });
                }
                body.Visibility = m.Expanded ? Visibility.Visible : Visibility.Collapsed;

                var header = new UiText(Arrow(m.Expanded) + " " + m.Header, TextVariant.Link) { Cursor = Cursors.Hand, VerticalAlignment = VerticalAlignment.Center };
                header.MouseLeftButtonUp += (s, e) =>
                {
                    bool show = body.Visibility != Visibility.Visible;
                    body.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
                    header.Text = Arrow(show) + " " + m.Header;
                    e.Handled = true;
                };
                var copy = new UiText(IntelliSenseStrings.MacroExpansionCopyLink, TextVariant.Link) { Cursor = Cursors.Hand, Margin = new Thickness(16, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center };
                UEToVS.UI.Controls.Tip.Set(copy, IntelliSenseStrings.MacroExpansionCopy);
                copy.MouseLeftButtonUp += (s, e) =>
                {
                    e.Handled = true;
                    try
                    {
                        Clipboard.SetText(m.CopyText ?? string.Empty);
                        copy.Text = IntelliSenseStrings.MacroExpansionCopied;
                        L.Info("is.quickInfo.macroCopied", null, ("chars", (m.CopyText ?? string.Empty).Length));
                    }
                    catch (Exception ex)
                    {
                        L.Warn("is.quickInfo.macroCopyFailed", "Clipboard busy", ex);
                    }
                };
                var head = new DockPanel { LastChildFill = true, Margin = new Thickness(0, 2, 0, 0) };
                DockPanel.SetDock(copy, Dock.Right);
                head.Children.Add(copy);
                head.Children.Add(header);

                var panel = new StackPanel();
                panel.Children.Add(head);
                panel.Children.Add(body);
                return panel as TView;
            }
            catch (Exception ex)
            {
                L.Error("is.quickInfo.macroElement.failed", "Rendering the macro expansion failed", ex);
                return null;
            }
        }

        private static System.Windows.Media.Brush Frozen(System.Windows.Media.Color c)
        {
            var b = new System.Windows.Media.SolidColorBrush(c);
            b.Freeze();
            return b;
        }

        private static string Arrow(bool expanded) => expanded ? "▾" : "▸";
    }
}
