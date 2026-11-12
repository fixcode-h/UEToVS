using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using Microsoft.VisualStudio.Imaging;
using UEToVS.Contracts;
using UEToVS.Core.Diagnostics;
using UEToVS.Host.Diagnostics;
using UEToVS.Host.Engine;
using UEToVS.Host.Logic.Localization;
using UEToVS.Host.Logic.Refactoring;
using UEToVS.Host.Logic.Unreal;
using UEToVS.Host.Search;
using UEToVS.UI.Atoms;
using UEToVS.UI.Controls;
using UEToVS.UI.Logic;
using UEToVS.UI.Molecules;
using UEToVS.UI.Shells;

namespace UEToVS.Host.Build
{
    /// <summary>What the New UE Class dialog decided.</summary>
    internal sealed class NewClassChoice
    {
        public NewClassSpec Spec { get; set; }
        public NewClassTarget Target { get; set; }
        public ModuleInfo Module { get; set; }
        public bool IsPublic { get; set; }
        public string Subdirectory { get; set; }
    }

    /// <summary>
    /// New UE Class (UEToVS.UI DialogShell): parent picker on the right (common parents pinned, then classes from the index as
    /// you type), name with the A/U/F prefix shown live, module, Public / Private (or the module folder when it has no Public
    /// folder), subfolder, BeginPlay / Tick for actors and components, and the two paths that will be written. UI thread.
    /// </summary>
    internal sealed class NewClassDialog
    {
        private static readonly Logger L = Log.For("NewClass");
        private readonly DialogShell _shell;
        private readonly SearchInput _search = new SearchInput(true);
        private readonly VirtualList<ParentClassInfo> _parents;
        private readonly TextInput _name = new TextInput();
        private readonly UiText _prefixed = new UiText((string)null, TextVariant.Secondary);
        private readonly UiText _nameError = new UiText((string)null, TextVariant.Secondary) { Visibility = Visibility.Collapsed };
        private readonly Dropdown _module = new Dropdown();
        private readonly SegmentedControl _visibility = new SegmentedControl(BuildStrings.VisibilityPublic, BuildStrings.VisibilityPrivate);
        private readonly UiText _visibilityNote = new UiText((string)null, TextVariant.Secondary) { Visibility = Visibility.Collapsed };
        private readonly TextInput _subfolder = new TextInput();
        private readonly UiCheckBox _tick = new UiCheckBox(() => BuildStrings.BeginPlayTick, true);
        private readonly UiText _paths = new UiText((string)null, TextVariant.Secondary) { TextWrapping = TextWrapping.Wrap, TextTrimming = TextTrimming.None };
        private readonly DispatcherTimer _searchTimer;
        private readonly IReadOnlyList<ModuleInfo> _modules;
        private readonly EngineClient _engine;
        private readonly string _copyright;
        private CancellationTokenSource _searchCts;
        private ParentClassInfo _parent;
        private NewClassChoice _result;

        public NewClassDialog(IReadOnlyList<ModuleInfo> modules, EngineClient engine, string copyright)
        {
            _modules = modules;
            _engine = engine;
            _copyright = copyright;
            _shell = new DialogShell(() => BuildStrings.NewClassTitle, () => BuildStrings.NewClassCreate)
            {
                VisibleSize = new Size(860, 540),
                PreviewPosition = PreviewPosition.Right,
                Description = BuildStrings.NewClassDescription,
            };
            _parents = new VirtualList<ParentClassInfo>(new DelegatePresenter<ParentClassInfo>(PresentParent, p => true)) { AlwaysActive = true, MinHeight = 200 };
            _search.Placeholder = BuildStrings.ParentSearchPlaceholder;
            _name.Placeholder = BuildStrings.ClassNamePlaceholder;
            _module.Items = modules.Select(m => m.DisplayName).ToList();

            var form = new StackPanel { Margin = new Thickness(0, 0, 4, 0) };
            form.Children.Add(Label(BuildStrings.ClassName));
            form.Children.Add(_name);
            _prefixed.Margin = new Thickness(0, 4, 0, 0);
            form.Children.Add(_prefixed);
            form.Children.Add(_nameError);
            form.Children.Add(Label(BuildStrings.Module));
            form.Children.Add(_module);
            form.Children.Add(Label(BuildStrings.Visibility));
            _visibility.HorizontalAlignment = HorizontalAlignment.Left;
            form.Children.Add(_visibility);
            form.Children.Add(_visibilityNote);
            form.Children.Add(Label(BuildStrings.Subfolder));
            form.Children.Add(_subfolder);
            _tick.Margin = new Thickness(0, 12, 0, 0);
            form.Children.Add(_tick);
            _paths.Margin = new Thickness(0, 14, 0, 0);
            form.Children.Add(_paths);
            _shell.Form = form;

            var right = new DockPanel { LastChildFill = true, Margin = new Thickness(8) };
            var header = Label(BuildStrings.ParentClass);
            header.Margin = new Thickness(0, 0, 0, 4);
            DockPanel.SetDock(header, Dock.Top);
            DockPanel.SetDock(_search, Dock.Top);
            right.Children.Add(header);
            right.Children.Add(_search);
            _parents.Margin = new Thickness(0, 6, 0, 0);
            right.Children.Add(_parents);
            _shell.Preview = right;

            _searchTimer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(150) };
            _searchTimer.Tick += (s, e) => UEToVS.UI.Theming.Ui.Safe("newClass.search", RunSearch);
            _search.TextChanged += (s, t) => UEToVS.UI.Theming.Ui.Safe("newClass.filter", () =>
            {
                ShowParents(FilterCommon(t), null);
                _searchTimer.Stop();
                if (!string.IsNullOrWhiteSpace(t)) _searchTimer.Start();
            });
            _parents.SelectionChanged += (s, e) => UEToVS.UI.Theming.Ui.Safe("newClass.parent", () =>
            {
                if (_parents.HasSelection && _parents.SelectedItem != null) SelectParent(_parents.SelectedItem);
            });
            _name.TextChanged += (s, e) => UEToVS.UI.Theming.Ui.Safe("newClass.name", Update);
            _subfolder.TextChanged += (s, e) => UEToVS.UI.Theming.Ui.Safe("newClass.subfolder", Update);
            _module.SelectionChanged += (s, e) => UEToVS.UI.Theming.Ui.Safe("newClass.module", UpdateModule);
            _visibility.SelectionChanged += (s, e) => UEToVS.UI.Theming.Ui.Safe("newClass.visibility", Update);
            _shell.Validate = Accept;
        }

        private static UiText Label(string text) => new UiText(text, TextVariant.Body) { Margin = new Thickness(0, 12, 0, 4), FontWeight = FontWeights.SemiBold };

        /// <summary>Shows the dialog with a preselected module / location; null when cancelled.</summary>
        public NewClassChoice ShowModal(ModuleInfo module, bool isPublic, string subdir, IntPtr owner)
        {
            int index = module == null ? 0 : Math.Max(0, _modules.ToList().FindIndex(m => string.Equals(m.Directory, module.Directory, StringComparison.OrdinalIgnoreCase)));
            _module.SelectedIndex = _modules.Count > 0 ? index : -1;
            _visibility.SelectedIndex = isPublic ? 0 : 1;
            _subfolder.Text = subdir ?? string.Empty;
            ShowParents(FilterCommon(null), null);
            _parents.SelectedIndex = 0;
            SelectParent(NewClassPlanner.CommonParents[0]);
            UpdateModule();
            _shell.Loaded += (s, e) => UEToVS.UI.Theming.Ui.Safe("newClass.focus", () => _name.Focus());
            bool ok = _shell.ShowModal(owner);
            _searchTimer.Stop();
            _searchCts?.Cancel();
            return ok ? _result : null;
        }

        // ---------------------------------------------------------------- parents

        private static List<ParentClassInfo> FilterCommon(string q)
        {
            q = (q ?? string.Empty).Trim();
            return NewClassPlanner.CommonParents
                .Where(p => q.Length == 0 || (p.Name ?? p.DisplayName).IndexOf(q, StringComparison.OrdinalIgnoreCase) >= 0)
                .ToList();
        }

        private void ShowParents(List<ParentClassInfo> common, List<ParentClassInfo> indexed)
        {
            var all = new List<ParentClassInfo>(common);
            if (indexed != null)
                foreach (var p in indexed)
                    if (!all.Any(c => string.Equals(c.Name, p.Name, StringComparison.Ordinal))) all.Add(p);
            _parents.Items = all;
            int keep = _parent == null ? -1 : all.FindIndex(p => ReferenceEquals(p, _parent) || (p.Name != null && p.Name == _parent.Name && p.Kind == _parent.Kind));
            if (keep >= 0) _parents.SelectedIndex = keep;
        }

        private void RunSearch()
        {
            _searchTimer.Stop();
            string q = (_search.Text ?? string.Empty).Trim();
            _searchCts?.Cancel();
            if (q.Length == 0 || _engine == null || !_engine.IsConnected) return;
            var cts = _searchCts = new CancellationTokenSource();
            var request = new SearchRequest
            {
                Pattern = q, IncludeTypes = true, IncludeSymbols = false, IncludeFiles = false, IncludeAssets = false,
                IncludeNonProject = true, MaxResultsPerCategory = 60,
            };
            Safe.RunAsync(L, "newClass.search", async () =>
            {
                SearchResponse resp;
                try
                {
                    resp = await EngineQueries.SearchAsync(_engine, request, cts.Token).ConfigureAwait(true);
                }
                catch (OperationCanceledException)
                {
                    return;
                }
                if (cts.IsCancellationRequested) return;
                var found = resp.Hits.Where(h => h.Symbol != null && (h.Symbol.Kind == UEToVS.Contracts.SymbolKind.Class || h.Symbol.Kind == UEToVS.Contracts.SymbolKind.Struct))
                    .Select(h => h.Symbol)
                    .Where(sym => sym.Location?.FilePath != null && IsHeader(sym.Location.FilePath))
                    .GroupBy(sym => sym.Name).Select(g => g.First())
                    .Select(sym => new ParentClassInfo
                    {
                        Name = sym.Name,
                        Kind = NewClassPlanner.KindForParent(sym.Name, (sym.Ue & UeFlags.UClass) != 0, (sym.Ue & UeFlags.UStruct) != 0),
                        Include = IncludePlanner.UeIncludePath(sym.Location.FilePath),
                        Module = sym.Module,
                    }).ToList();
                ShowParents(FilterCommon(q), found);
            }, ("query", q));
        }

        private static bool IsHeader(string path)
        {
            string ext = Path.GetExtension(path).ToLowerInvariant();
            return ext == ".h" || ext == ".hpp";
        }

        private void PresentParent(ParentClassInfo p, RowData d)
        {
            d.Primary = p.Name ?? (p.Kind == NewClassKind.Struct ? "USTRUCT" : BuildStrings.NoParentRow);
            d.Moniker = p.Kind == NewClassKind.Struct || p.Kind == NewClassKind.Plain ? KnownMonikers.Structure : KnownMonikers.Class;
            d.Secondary = p.Include;
            d.Right = p.IsCommon ? BuildStrings.CommonParentGroup : p.Module;
        }

        private void SelectParent(ParentClassInfo p)
        {
            _parent = p;
            Update();
        }

        // ---------------------------------------------------------------- form

        private ModuleInfo SelectedModule => _module.SelectedIndex >= 0 && _module.SelectedIndex < _modules.Count ? _modules[_module.SelectedIndex] : null;

        private void UpdateModule()
        {
            var m = SelectedModule;
            bool split = m?.CanSplit == true;
            _visibility.Visibility = split ? Visibility.Visible : Visibility.Collapsed;
            _visibilityNote.Text = BuildStrings.VisibilitySame;
            _visibilityNote.Visibility = split ? Visibility.Collapsed : Visibility.Visible;
            Update();
        }

        private void Update()
        {
            var kind = _parent?.Kind ?? NewClassKind.Object;
            string typed = _name.Text ?? string.Empty;
            string error = NewClassPlanner.ValidateName(typed, kind, _parent?.Name);
            string full = NewClassPlanner.ApplyPrefix(typed, kind);
            _prefixed.Text = full.Length == 0 ? string.Empty : (NewClassPlanner.IsReflected(kind) ? (kind == NewClassKind.Struct ? "USTRUCT  " : "UCLASS  ") : "") + full +
                                                                (_parent?.Name != null ? " : public " + _parent.Name : string.Empty);
            _nameError.Text = error != null && typed.Length > 0 ? BuildStrings.NameError(error) : string.Empty;
            _nameError.Visibility = _nameError.Text.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
            _tick.Visibility = NewClassPlanner.HasBeginPlayAndTick(kind) ? Visibility.Visible : Visibility.Collapsed;
            var target = Target(full, kind, out string sub);
            _paths.Text = target == null ? string.Empty : BuildStrings.WillCreate(target.HeaderPath, target.SourcePath);
            _shell.IsPrimaryEnabled = error == null && target != null && sub != null;
        }

        private NewClassTarget Target(string fullName, NewClassKind kind, out string sub)
        {
            sub = NewClassPlanner.NormalizeSubdirectory(_subfolder.Text);
            var m = SelectedModule;
            if (m == null || sub == null || string.IsNullOrEmpty(fullName)) return null;
            return NewClassPlanner.Target(m, _visibility.SelectedIndex != 1, sub, NewClassPlanner.FileBaseName(fullName, kind));
        }

        private bool Accept()
        {
            var kind = _parent?.Kind ?? NewClassKind.Object;
            string typed = _name.Text ?? string.Empty;
            if (NewClassPlanner.ValidateName(typed, kind, _parent?.Name) != null) return false;
            string full = NewClassPlanner.ApplyPrefix(typed, kind);
            var target = Target(full, kind, out string sub);
            var m = SelectedModule;
            if (target == null || m == null) return false;
            string exists = File.Exists(target.HeaderPath) ? target.HeaderPath : File.Exists(target.SourcePath) ? target.SourcePath : null;
            if (exists != null)
            {
                _nameError.Text = BuildStrings.FileExists(exists);
                _nameError.Visibility = Visibility.Visible;
                return false;
            }
            _result = new NewClassChoice
            {
                Module = m,
                IsPublic = _visibility.SelectedIndex != 1,
                Subdirectory = sub,
                Target = target,
                Spec = new NewClassSpec
                {
                    ClassName = full,
                    Kind = kind,
                    ParentName = _parent?.Name,
                    ParentInclude = _parent?.Include,
                    ApiMacro = m.ApiMacro,
                    WithBeginPlayAndTick = _tick.IsChecked == true && NewClassPlanner.HasBeginPlayAndTick(kind),
                    HeaderInclude = target.HeaderInclude,
                    Copyright = _copyright,
                },
            };
            return true;
        }
    }
}
