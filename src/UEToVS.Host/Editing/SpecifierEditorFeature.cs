using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Text.Editor;
using UEToVS.Core.Diagnostics;
using UEToVS.Core.Localization;
using UEToVS.Host.Editor;
using UEToVS.Host.Integration;
using UEToVS.Host.Logic.Actions;
using UEToVS.Host.Logic.Editing;
using UEToVS.Host.Logic.Localization;
using UEToVS.Host.Logic.Refactoring;
using UEToVS.Host.Refactoring;
using UEToVS.Host.Shell;
using UEToVS.UI.Atoms;
using UEToVS.UI.Controls;
using UEToVS.UI.Shells;

namespace UEToVS.Host.Editing
{
    /// <summary>
    /// v0.9.12 灯泡 B 项：UPROPERTY / UFUNCTION / UCLASS / USTRUCT 说明符编辑器。光标在宏上时灯泡 / Alt+Enter 里出现
    /// “编辑说明符…”（也有命令和快捷键），弹窗勾选说明符和 meta，确定后一次改写括号里的文本（一次撤销）。
    /// 知识库是引擎同一份 UeMacros.json（嵌入资源），不需要引擎连着。
    /// </summary>
    internal static class SpecifierEditorFeature
    {
        private static readonly Logger L = Log.For("Editing");
        private static readonly Lazy<SpecifierCatalog> Catalog = new Lazy<SpecifierCatalog>(LoadCatalog);

        public static void Register(ActionRegistry registry)
        {
            registry.Register(new DelegateAction(SpecifierEditorIds.Edit, CodingAssistStrings.EditSpecifiers("UPROPERTY"), ActionCategory.Unreal, ExecuteAsync, null, null,
                CodingAssistStrings.KwEditSpecifiers));
            RefactoringFeature.Hub.Register(new SpecifierEditorProvider());
        }

        private static SpecifierCatalog LoadCatalog()
        {
            try
            {
                using (var s = typeof(SpecifierEditorFeature).Assembly.GetManifestResourceStream("UEToVS.Host.UeMacros.json"))
                {
                    if (s == null)
                    {
                        L.Error("editor.specifiers.kbMissing", "Embedded UeMacros.json not found", null);
                        return SpecifierCatalog.Parse("{}");
                    }
                    using (var r = new StreamReader(s)) return SpecifierCatalog.Parse(r.ReadToEnd());
                }
            }
            catch (Exception ex)
            {
                L.Error("editor.specifiers.kbFailed", "Reading UeMacros.json failed", ex);
                return SpecifierCatalog.Parse("{}");
            }
        }

        private static async Task ExecuteAsync(ActionContext ctx)
        {
            await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
            var sw = Stopwatch.StartNew();
            var view = ctx.Get<IWpfTextView>(RefactorKeys.View) ?? EditorServices.ActiveView();
            if (view == null || view.IsClosed || !EditorServices.IsCpp(view.TextBuffer))
            {
                VsStatusBar.SetText(CodingAssistStrings.NotOnMacro);
                return;
            }
            string file = EditorServices.FilePath(view.TextBuffer);
            var snapshot = view.TextBuffer.CurrentSnapshot;
            string text = snapshot.GetText();
            int caret = view.Caret.Position.BufferPosition.TranslateTo(snapshot, Microsoft.VisualStudio.Text.PointTrackingMode.Positive).Position;
            var call = ReflectionMacroText.FindAt(text, caret);
            string outcome = null;
            int before = 0, after = 0;
            try
            {
                if (call == null) { outcome = "not-on-macro"; VsStatusBar.SetText(CodingAssistStrings.NotOnMacro); return; }
                if (view.TextBuffer.IsReadOnly(call.OpenParen) || EngineReadOnlyState.For(view.TextBuffer).Locked)
                {
                    outcome = "readonly";
                    VsStatusBar.SetText(CodingAssistStrings.ReadOnly);
                    return;
                }
                var original = SpecifierSet.Parse(call.Arguments);
                before = original.Items.Count + original.Meta.Count;
                var dialog = new SpecifierEditorDialog(call.Name, Catalog.Value, original);
                var edited = dialog.ShowModal(VsMainWindow.Handle);
                if (edited == null) { outcome = "canceled"; return; }
                after = edited.Items.Count + edited.Meta.Count;
                var edit = SpecifierEditorPlanner.Edit(file, text, call, edited);
                if (edit == null) { outcome = "unchanged"; VsStatusBar.SetText(CodingAssistStrings.Unchanged); return; }
                if (view.TextBuffer.CurrentSnapshot.Version.VersionNumber != snapshot.Version.VersionNumber) { outcome = "stale"; VsStatusBar.SetText(CodingAssistStrings.Stale); return; }
                var result = RefactorEdits.Apply(CodingAssistStrings.EditorTitle(call.Name), new List<TextEdit> { edit });
                if (result.Applied == 0) { outcome = "stale"; VsStatusBar.SetText(CodingAssistStrings.Stale); return; }
                outcome = "applied";
                VsStatusBar.SetText(CodingAssistStrings.Changed(call.Name));
            }
            catch (Exception ex)
            {
                outcome = "failed";
                L.Warn("editor.specifiers.failed", "Editing specifiers failed", ex, ("file", Path.GetFileName(file ?? string.Empty)));
            }
            finally
            {
                L.Info("editor.specifiers", null, ("macro", call?.Name), ("outcome", outcome), ("before", before), ("after", after),
                       ("file", Path.GetFileName(file ?? string.Empty)), ("source", ctx.Source.ToString()), ("ms", sw.ElapsedMilliseconds));
            }
        }
    }

    /// <summary>
    /// 说明符编辑器弹窗：筛选框，说明符区和 meta 区（每行：勾选框 + 中英说明 + 带值的输入框），知识库里没有的项单独列出（默认勾着，
    /// 原样保留），下面是问题提示和结果预览。互斥 / 缺值时“确定”不可用。UI 线程。
    /// </summary>
    internal sealed class SpecifierEditorDialog
    {
        private sealed class Row
        {
            public SpecifierDef Def;
            public SpecifierEntry Unknown;
            public bool IsMeta;
            public UiCheckBox Box;
            public TextInput Value;
            public string InitialValueText;
            public string OriginalRaw;
            public FrameworkElement Element;
        }

        private readonly DialogShell _shell;
        private readonly string _macro;
        private readonly SpecifierCatalog _catalog;
        private readonly SpecifierSet _original;
        private readonly List<Row> _rows = new List<Row>();
        private readonly SearchInput _filter = new SearchInput(false);
        private readonly UiText _problems = new UiText((string)null, TextVariant.Secondary) { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 6, 0, 0) };
        private readonly UiText _preview = new UiText((string)null, TextVariant.Mono) { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 4, 0, 0) };
        private SpecifierSet _current;
        private bool _blocking;

        public SpecifierEditorDialog(string macro, SpecifierCatalog catalog, SpecifierSet original)
        {
            _macro = macro;
            _catalog = catalog;
            _original = original;
            _shell = new DialogShell(() => CodingAssistStrings.EditorTitle(macro), () => CodingAssistStrings.Apply)
            {
                VisibleSize = new Size(900, 640), PlacementId = "dialog.specifierEditor",
            };
            _shell.Description = CodingAssistStrings.EditorDescription;
            _filter.Placeholder = CodingAssistStrings.FilterPlaceholder;
            _filter.Margin = new Thickness(0, 0, 0, 8);

            var list = new StackPanel();
            list.Children.Add(Header(CodingAssistStrings.SpecifiersHeader));
            foreach (var def in catalog.Specifiers(macro)) list.Children.Add(Add(def, false));
            var metaKeys = catalog.MetaKeys(macro);
            if (metaKeys.Count > 0)
            {
                list.Children.Add(Header(CodingAssistStrings.MetaHeader));
                foreach (var def in metaKeys) list.Children.Add(Add(def, true));
            }
            var unknown = original.Items.Where(e => catalog.Find(macro, e.Key, false) == null).Select(e => (e, false))
                .Concat(original.Meta.Where(e => catalog.Find(macro, e.Key, true) == null).Select(e => (e, true))).ToList();
            if (unknown.Count > 0)
            {
                list.Children.Add(Header(CodingAssistStrings.UnknownHeader));
                foreach (var (e, meta) in unknown) list.Children.Add(AddUnknown(e, meta));
            }

            var form = new DockPanel { LastChildFill = true };
            DockPanel.SetDock(_filter, Dock.Top);
            form.Children.Add(_filter);
            var bottom = new StackPanel { Margin = new Thickness(0, 8, 0, 0) };
            bottom.Children.Add(_problems);
            bottom.Children.Add(new UiText(CodingAssistStrings.Preview, TextVariant.Secondary) { Margin = new Thickness(0, 6, 0, 0) });
            bottom.Children.Add(_preview);
            DockPanel.SetDock(bottom, Dock.Bottom);
            form.Children.Add(bottom);
            form.Children.Add(new ScrollViewer { Content = list, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, MaxHeight = 380 });
            _shell.Form = form;
            _filter.TextChanged += (s, t) => UEToVS.UI.Theming.Ui.Safe("editor.specifiers.filter", ApplyFilter);
            Refresh();
        }

        /// <summary>编辑后的参数表；取消时 null。</summary>
        public SpecifierSet ShowModal(IntPtr owner)
        {
            _shell.Validate = () => !_blocking;
            return _shell.ShowModal(owner) ? _current : null;
        }

        private static UiText Header(string text) => new UiText(text, TextVariant.Subtitle) { Margin = new Thickness(0, 10, 0, 4) };

        private FrameworkElement Add(SpecifierDef def, bool meta)
        {
            var existing = _original.Get(def.Name, meta);
            var row = new Row { Def = def, IsMeta = meta, OriginalRaw = existing?.Value };
            row.Box = new UiCheckBox(def.Name, existing != null) { VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(0, 2, 8, 2) };
            var doc = new UiText(def.Description(Loc.IsEnglish), TextVariant.Secondary) { TextWrapping = TextWrapping.Wrap, VerticalAlignment = VerticalAlignment.Center };
            doc.ToolTip = Loc.IsEnglish ? def.DocZh : def.Doc; // 另一种语言放在悬停提示里，两种说明都能看到
            if (def.HasValue)
            {
                row.InitialValueText = SpecifierSet.DisplayValue(existing?.Value);
                row.Value = new TextInput { Text = row.InitialValueText, Placeholder = CodingAssistStrings.ValuePlaceholder(def.ValueHint), Margin = new Thickness(8, 0, 0, 2) };
                row.Value.TextChanged += (s, e) => UEToVS.UI.Theming.Ui.Safe("editor.specifiers.value", () =>
                {
                    if (!string.IsNullOrWhiteSpace(row.Value.Text) && row.Box.IsChecked != true) row.Box.IsChecked = true; // 填了值就是要用
                    Refresh();
                });
            }
            row.Box.Checked += (s, e) => UEToVS.UI.Theming.Ui.Safe("editor.specifiers.toggle", Refresh);
            row.Box.Unchecked += (s, e) => UEToVS.UI.Theming.Ui.Safe("editor.specifiers.toggle", Refresh);
            row.Element = Line(row.Box, doc, row.Value);
            _rows.Add(row);
            return row.Element;
        }

        private FrameworkElement AddUnknown(SpecifierEntry e, bool meta)
        {
            var row = new Row { Unknown = e, IsMeta = meta };
            row.Box = new UiCheckBox((meta ? "meta: " : string.Empty) + e, true) { Margin = new Thickness(0, 2, 8, 2) };
            row.Box.Checked += (s, a) => UEToVS.UI.Theming.Ui.Safe("editor.specifiers.toggle", Refresh);
            row.Box.Unchecked += (s, a) => UEToVS.UI.Theming.Ui.Safe("editor.specifiers.toggle", Refresh);
            row.Element = Line(row.Box, null, null);
            _rows.Add(row);
            return row.Element;
        }

        /// <summary>勾选框 220 | 说明 * | 值 200。</summary>
        private static Grid Line(UIElement box, UIElement doc, UIElement value)
        {
            var g = new Grid { Margin = new Thickness(0, 0, 0, 2) };
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(220) });
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(200) });
            var cells = new[] { box, doc, value };
            for (int i = 0; i < cells.Length; i++)
            {
                if (cells[i] == null) continue;
                Grid.SetColumn(cells[i], i);
                g.Children.Add(cells[i]);
            }
            return g;
        }

        private void ApplyFilter()
        {
            string q = (_filter.Text ?? string.Empty).Trim();
            foreach (var r in _rows)
            {
                bool show = q.Length == 0 || r.Box.IsChecked == true ||
                            (r.Def != null && (Contains(r.Def.Name, q) || Contains(r.Def.Doc, q) || Contains(r.Def.DocZh, q))) ||
                            (r.Unknown != null && Contains(r.Unknown.ToString(), q));
                r.Element.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
            }
        }

        private static bool Contains(string s, string q) => s != null && s.IndexOf(q, StringComparison.OrdinalIgnoreCase) >= 0;

        /// <summary>按勾选状态从原参数表算出新的（没动过的值保留原文写法），刷新问题和预览。</summary>
        private void Refresh()
        {
            var set = _original.Clone();
            foreach (var r in _rows)
            {
                string key = r.Def?.Name ?? r.Unknown.Key;
                if (r.Box.IsChecked != true)
                {
                    set.Remove(key, r.IsMeta);
                    continue;
                }
                if (r.Unknown != null) continue; // 原样保留
                string value = null;
                if (r.Def.HasValue)
                {
                    string typed = r.Value.Text ?? string.Empty;
                    value = typed == r.InitialValueText && r.OriginalRaw != null ? r.OriginalRaw : SpecifierSet.NormalizeValue(r.Def.ValueHint, typed);
                    if (value.Length == 0) value = IsFlagLike(r.Def) ? null : string.Empty;
                }
                var existing = set.Get(key, r.IsMeta);
                if (existing != null && existing.Value == value) continue;
                set.Set(existing?.Key ?? key, value, r.IsMeta);
            }
            _current = set;
            var problems = set.Check(_catalog, _macro);
            _blocking = problems.Any(p => p.Blocking);
            _problems.Text = string.Join("\n", problems.Select(p => (p.Blocking ? "✕ " : "! ") + CodingAssistStrings.Problem(p)));
            _problems.Visibility = problems.Count == 0 ? Visibility.Collapsed : Visibility.Visible;
            _preview.Text = _macro + "(" + set.Format() + ")";
            _shell.IsPrimaryEnabled = !_blocking;
        }

        /// <summary>值可写可不写（meta 的 =true 提示）：空着就写成开关。</summary>
        private static bool IsFlagLike(SpecifierDef def)
        {
            string h = (def.ValueHint ?? string.Empty).TrimStart('=').Trim().Trim('"');
            return h == "true" || h == "false";
        }
    }
}
