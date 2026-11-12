using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using UEToVS.Core.Localization;
using UEToVS.UI.Controls;
using UEToVS.UI.Logic;
using UEToVS.UI.Molecules;
using UEToVS.UI.Patterns;
using UEToVS.UI.Shells;
using UEToVS.UI.Theming;

namespace UEToVS.UI.Gallery
{
    /// <summary>Gallery page for the M2 navigation components: NavigationBar, ResultsPopup (Find References), TabbedTreePopup (Type Hierarchy).</summary>
    public partial class ComponentGallery
    {
        private ResultsPopup<SampleSymbol> _referencesPopup;
        private TabbedTreePopup<SampleSymbol> _hierarchyPopup;
        private ChooserPopup<SampleSymbol> _navChooser;

        private void CloseNavigationPopups()
        {
            _referencesPopup?.Shell.CloseForReal();
            _hierarchyPopup?.Shell.CloseForReal();
            _navChooser?.Shell.CloseForReal();
        }

        private UIElement BuildNavigation()
        {
            var sp = Stack();
            sp.Children.Add(Heading(() => Loc.T("导航栏（编辑器顶部）", "Navigation bar (editor top margin)")));
            var bar = new NavigationBar { Width = 640, HorizontalAlignment = HorizontalAlignment.Left };
            bar.SetLeft(SymbolKind.Class, "AMyCharacter", () => Loc.T("当前类型", "Current type"));
            bar.SetRight(SymbolKind.Method, "BeginPlay", () => Loc.T("当前函数", "Current function"));
            var members = SampleData.Symbols(40).Where(s => s.Kind == SymbolKind.Method || s.Kind == SymbolKind.Function || s.Kind == SymbolKind.Field).ToList();
            var types = SampleData.Symbols(40).Where(s => s.Kind == SymbolKind.Class || s.Kind == SymbolKind.Struct).ToList();
            bar.LeftClicked += async (s, e) => await PickAsync(bar.LeftBox, types, Loc.T("文件中的类型", "Types in file"), x => bar.SetLeft(x.Kind, x.Name));
            bar.RightClicked += async (s, e) => await PickAsync(bar.RightBox, members, Loc.T("成员", "Members"), x => bar.SetRight(x.Kind, x.Name));
            sp.Children.Add(bar);

            sp.Children.Add(Heading(() => Loc.T("结果弹窗（查找引用）", "Results popup (Find References)")));
            var refs = new UiButton(() => Loc.T("打开“查找引用”弹窗", "Open the Find References popup"), ButtonKind.Primary);
            refs.Click += (s, e) => Ui.Safe("ui.gallery.references", ShowReferencesSample);
            sp.Children.Add(Row(refs));

            sp.Children.Add(Heading(() => Loc.T("带标签的树形弹窗（类型层次）", "Tabbed tree popup (Type Hierarchy)")));
            var hier = new UiButton(() => Loc.T("打开“类型层次”弹窗", "Open the Type Hierarchy popup"), ButtonKind.Primary);
            hier.Click += (s, e) => Ui.Safe("ui.gallery.hierarchy", ShowHierarchySample);
            sp.Children.Add(Row(hier));
            return Scroll(sp);
        }

        private async System.Threading.Tasks.Task PickAsync(FrameworkElement anchor, List<SampleSymbol> items, string title, Action<SampleSymbol> chosen)
        {
            try
            {
                if (_navChooser == null) _navChooser = new ChooserPopup<SampleSymbol>("gallery.navbar", new SamplePresenter()) { OwnerHandle = Hwnd };
                var r = await _navChooser.ShowAsync(items, PopupAnchor.Element(anchor), title, x => x.Name);
                if (r.Chosen) chosen(r.Item);
            }
            catch (Exception ex)
            {
                Ui.Logger.Error("ui.gallery.navbar_failed", "Navigation bar sample failed", ex);
            }
        }

        private void ShowReferencesSample()
        {
            if (_referencesPopup == null)
            {
                _referencesPopup = new ResultsPopup<SampleSymbol>("gallery.references", new SamplePresenter()) { OwnerHandle = Hwnd };
                var writes = new Chip(() => Loc.T("只看写入", "Writes only"));
                var engine = new Chip(() => Loc.T("包含引擎代码", "Include engine code"));
                var rerun = new IconButton(Ui.FrozenGeometry("M 10,2 A 5,5 0 1 0 11,7 M 10,0 L 10,3 L 7,3"), () => Loc.T("重新搜索", "Search again"), null, 11);
                _referencesPopup.AddToolbarItem(rerun);
                _referencesPopup.AddToolbarItem(writes);
                _referencesPopup.AddToolbarItem(engine);
                _referencesPopup.Panel.PreviewProvider = x => new CodePreviewRequest { FilePath = x.File, Line = x.Line, Column = 6, Length = x.Name.Length, Text = SampleData.FakeSource(x, Math.Max(200, x.Line + 20)) };
                _referencesPopup.ItemInvoked += (s, e) => Toast.Show(_page, e.Item.Name);
            }
            var roots = new List<TreeNode<SampleSymbol>>();
            foreach (var g in SampleData.Symbols(120).GroupBy(x => x.File))
            {
                var children = g.Select(x => new TreeNode<SampleSymbol>(x)).ToList();
                roots.Add(new TreeNode<SampleSymbol>(new SampleSymbol { Name = g.Key, Kind = SymbolKind.File, Container = "Source/MyGame", File = g.Key, Line = 1 }, children));
            }
            _referencesPopup.Title = Loc.T("“Health”的引用", "References of 'Health'");
            _referencesPopup.SetResults(roots, Loc.F("{0} 处引用 · {1} 个文件 · 18 ms", "{0} references · {1} files · 18 ms", roots.Sum(r => r.LeafCount), roots.Count));
            _referencesPopup.ShowAt(PopupAnchor.CenterOf(Hwnd));
        }

        private void ShowHierarchySample()
        {
            if (_hierarchyPopup == null)
            {
                var tabs = new[]
                {
                    new TabDefinition("bases", () => Loc.T("基类", "Base types")),
                    new TabDefinition("derived", () => Loc.T("派生类", "Derived types")),
                };
                _hierarchyPopup = new TabbedTreePopup<SampleSymbol>("gallery.hierarchy", new SamplePresenter(), tabs) { OwnerHandle = Hwnd };
                _hierarchyPopup.ItemInvoked += (s, e) => Toast.Show(_page, e.Item.Name);
            }
            SampleSymbol T(string name, bool nonProject = false) => new SampleSymbol { Name = name, Kind = SymbolKind.Class, Badge = BadgeKind.UClass, File = name + ".h", Line = 10, NonProject = nonProject };
            TreeNode<SampleSymbol> N(SampleSymbol s, params TreeNode<SampleSymbol>[] children) => new TreeNode<SampleSymbol>(s, children);
            var bases = new[] { N(T("AMyCharacter"), N(T("ACharacter", true), N(T("APawn", true), N(T("AActor", true), N(T("UObject", true))))), N(T("IAbilitySystemInterface", true))) };
            var derived = new[] { N(T("AMyCharacter"), N(T("AMyHero"), N(T("AMyHero_Mage"))), N(T("AMyEnemy"))) };
            _hierarchyPopup.Title = Loc.T("“AMyCharacter”的类型层次", "Type hierarchy of 'AMyCharacter'");
            _hierarchyPopup.SetTabRoots("bases", bases, 6);
            _hierarchyPopup.SetTabRoots("derived", derived, 4);
            _hierarchyPopup.Footer.Status = Loc.T("10 个类型 · 3 ms", "10 types · 3 ms");
            _hierarchyPopup.ShowAt(PopupAnchor.CenterOf(Hwnd));
        }
    }
}
