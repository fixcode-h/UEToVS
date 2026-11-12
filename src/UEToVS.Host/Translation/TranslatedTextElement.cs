using System;
using System.Collections.Generic;
using System.ComponentModel.Composition;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Microsoft.VisualStudio.Language.StandardClassification;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Text.Adornments;
using Microsoft.VisualStudio.Text.Editor;
using Microsoft.VisualStudio.Utilities;
using UEToVS.Core.Diagnostics;
using UEToVS.Host.Logic.Localization;
using UEToVS.Host.Logic.Translation;
using UEToVS.UI.Atoms;

namespace UEToVS.Host.Translation
{
    /// <summary>One unit of a translated comment: the original, and the translation (already known, or still on its way).</summary>
    internal sealed class TranslatedUnit
    {
        public TranslatedUnit(TextUnit unit, string cached, Task<TranslationResult> pending)
        {
            Unit = unit;
            Cached = cached;
            Pending = pending;
        }

        public TextUnit Unit { get; }
        public string Cached { get; }
        public Task<TranslationResult> Pending { get; }
    }

    /// <summary>
    /// A comment with its translation, as Quick Info / tooltip content (rendered by <see cref="TranslatedTextViewElementFactory"/>).
    /// Requests start when the element is created (off the UI thread); cached units are filled in at once.
    /// </summary>
    internal sealed class TranslatedTextElement
    {
        private TranslatedTextElement(IReadOnlyList<TranslatedUnit> units, TranslationDisplay display, bool showOriginal, string header)
        {
            Units = units;
            Display = display;
            ShowOriginal = showOriginal;
            Header = header;
        }

        public IReadOnlyList<TranslatedUnit> Units { get; }
        public TranslationDisplay Display { get; }

        /// <summary>False when the original is already on screen elsewhere (VS's own Quick Info before the takeover).</summary>
        public bool ShowOriginal { get; }

        /// <summary>Optional caption on top ("翻译 · 谷歌翻译" in the selection popup).</summary>
        public string Header { get; }

        public bool HasTranslation
        {
            get
            {
                foreach (var u in Units) if (u.Unit.Translate) return true;
                return false;
            }
        }

        /// <summary>Null when translation is off or <paramref name="text"/> has nothing to translate (unless <paramref name="always"/>).</summary>
        public static TranslatedTextElement Create(TranslationService service, string text, bool showOriginal = true, string header = null, bool always = false)
        {
            if (service == null || string.IsNullOrWhiteSpace(text)) return null;
            var o = service.Options;
            var units = new List<TranslatedUnit>();
            bool any = false;
            foreach (var u in TranslationText.Split(text, o.Target))
            {
                if (!u.Translate)
                {
                    units.Add(new TranslatedUnit(u, null, null));
                    continue;
                }
                any = true;
                units.Add(service.TryGetCached(u.Text, out var cached)
                    ? new TranslatedUnit(u, cached, null)
                    : new TranslatedUnit(u, null, service.TranslateAsync(u.Text)));
            }
            if (!any && !always) return null;
            return new TranslatedTextElement(units, o.Display, showOriginal, header);
        }
    }

    /// <summary>
    /// Renders <see cref="TranslatedTextElement"/>: each paragraph as VS renders Quick Info text (same font and colours,
    /// through VS's own view element factory), its translation below with a thin clay accent bar (bilingual), or in place of
    /// the original (translation only). Pending translations are filled in on the UI thread when they arrive; the popup grows
    /// with its content. A caption at the bottom says "翻译中…" or why it failed.
    /// </summary>
    [Export(typeof(IViewElementFactory))]
    [Name("UEToVS Translated Text Element Factory")]
    [TypeConversion(from: typeof(TranslatedTextElement), to: typeof(UIElement))]
    [Order(Before = "default")]
    internal sealed class TranslatedTextViewElementFactory : IViewElementFactory
    {
        private static readonly Logger L = Log.For("Translation");
        private static readonly Brush Bar = Frozen(Color.FromArgb(0xB0, 0xD9, 0x77, 0x57));

        [Import]
        internal IViewElementFactoryService Factory { get; set; }

        public TView CreateViewElement<TView>(ITextView textView, object model) where TView : class
        {
            if (!(model is TranslatedTextElement m) || typeof(TView) != typeof(UIElement)) return null;
            try
            {
                return Render(textView, m) as TView;
            }
            catch (Exception ex)
            {
                L.Error("translate.render.failed", "Rendering the translation failed", ex);
                return null;
            }
        }

        private UIElement Render(ITextView view, TranslatedTextElement m)
        {
            var panel = new StackPanel();
            if (m.Header != null) panel.Children.Add(new UiText(m.Header, TextVariant.Caption) { Margin = new Thickness(0, 0, 0, 4), Opacity = 0.8 });
            var status = new UiText(TranslationStrings.Translating, TextVariant.Caption) { Margin = new Thickness(0, 6, 0, 0), Opacity = 0.7, Visibility = Visibility.Collapsed };
            int pending = 0;
            string firstError = null;
            bool first = true;

            foreach (var u in m.Units)
            {
                var slot = new StackPanel { Margin = new Thickness(0, !first && u.Unit.GapBefore ? 8 : (first ? 0 : 2), 0, 0) };
                first = false;
                panel.Children.Add(slot);
                var original = m.ShowOriginal ? Text(view, u.Unit.Text) : null;
                if (!u.Unit.Translate)
                {
                    if (original != null) slot.Children.Add(original);
                    else slot.Visibility = Visibility.Collapsed;
                    continue;
                }
                if (u.Cached != null)
                {
                    Fill(view, m, slot, original, u.Cached, null);
                    continue;
                }
                // still on its way: the original (if any) now, the translation when it arrives
                if (original != null) slot.Children.Add(original);
                else slot.Visibility = Visibility.Collapsed;
                pending++;
                status.Visibility = Visibility.Visible;
                var task = u.Pending;
                _ = ThreadHelper.JoinableTaskFactory.RunAsync(async () =>
                {
                    TranslationResult r;
                    try
                    {
#pragma warning disable VSTHRD003 // the translation runs on the thread pool (TranslationService) and never needs the UI thread
                        r = await task.ConfigureAwait(false);
#pragma warning restore VSTHRD003
                    }
                    catch (Exception ex)
                    {
                        r = TranslationResult.Failure(ex.Message);
                    }
                    await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
                    try
                    {
                        Fill(view, m, slot, original, r.Ok ? r.Text : null, r.Ok ? null : r.Error);
                        if (!r.Ok && firstError == null) firstError = r.Error;
                        pending--;
                        if (pending > 0) return;
                        if (firstError == null) status.Visibility = Visibility.Collapsed;
                        else status.Text = TranslationStrings.Failed(firstError);
                    }
                    catch (Exception ex)
                    {
                        L.Error("translate.render.failed", "Showing the translation failed", ex);
                    }
                });
            }
            panel.Children.Add(status);
            return panel;
        }

        /// <summary>Puts the translation into <paramref name="slot"/>; on failure keeps / restores the original.</summary>
        private void Fill(ITextView view, TranslatedTextElement m, StackPanel slot, UIElement original, string translation, string error)
        {
            slot.Children.Clear();
            if (translation == null)
            {
                if (original != null)
                {
                    slot.Children.Add(original);
                    slot.Visibility = Visibility.Visible;
                }
                return;
            }
            slot.Visibility = Visibility.Visible;
            var text = Text(view, translation);
            if (m.Display == TranslationDisplay.TranslationOnly || original == null)
            {
                slot.Children.Add(text);
                return;
            }
            slot.Children.Add(original);
            slot.Children.Add(new Border
            {
                Child = text, BorderBrush = Bar, BorderThickness = new Thickness(2, 0, 0, 0), Padding = new Thickness(8, 1, 0, 1),
                Margin = new Thickness(0, 3, 0, 0),
            });
        }

        private UIElement Text(ITextView view, string text) =>
            Factory.CreateViewElement<UIElement>(view, new ClassifiedTextElement(new ClassifiedTextRun(PredefinedClassificationTypeNames.NaturalLanguage, text)))
            ?? new TextBlock { Text = text, TextWrapping = TextWrapping.Wrap };

        private static Brush Frozen(Color c)
        {
            var b = new SolidColorBrush(c);
            b.Freeze();
            return b;
        }
    }
}
