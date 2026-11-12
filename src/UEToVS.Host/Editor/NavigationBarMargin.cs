using System;
using System.Collections.Generic;
using System.ComponentModel.Composition;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Threading;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Text.Editor;
using Microsoft.VisualStudio.Utilities;
using UEToVS.Contracts;
using UEToVS.Core.Diagnostics;
using UEToVS.Host.Diagnostics;
using UEToVS.Host.Integration;
using UEToVS.Host.Logic.Localization;
using UEToVS.Host.Logic.Navigation;
using UEToVS.Host.Navigation;
using UEToVS.UI.Molecules;
using UEToVS.UI.Patterns;
using UEToVS.UI.Shells;
using Glyph = UEToVS.UI.Logic.SymbolKind;

namespace UEToVS.Host.Editor
{
    [Export(typeof(IWpfTextViewMarginProvider))]
    [Name(NavigationBarMargin.MarginName)]
    [MarginContainer(PredefinedMarginNames.Top)]
    [ContentType(EditorContentTypes.Cpp)]
    [TextViewRole(PredefinedTextViewRoles.Document)]
    internal sealed class NavigationBarMarginProvider : IWpfTextViewMarginProvider
    {
        public IWpfTextViewMargin CreateMargin(IWpfTextViewHost wpfTextViewHost, IWpfTextViewMargin marginContainer) =>
            new NavigationBarMargin(wpfTextViewHost.TextView);
    }

    /// <summary>
    /// Navigation bar at the top of C++ editors (UEToVS.UI <see cref="NavigationBar"/>): left = current type, right =
    /// current function, refreshed from GetCaretContextAsync after the caret rests ~250 ms (deduplicated per caret and
    /// buffer version, newest request wins). Clicking a box opens a fuzzy-filtered chooser (types in the file / members of
    /// the selected type); picking one jumps there. Hidden when the setting is off or no Unreal solution is open.
    /// </summary>
    internal sealed class NavigationBarMargin : IWpfTextViewMargin
    {
        public const string MarginName = "UEToVS.NavigationBar";

        private static readonly Logger L = Log.For("NavigationBar");
        private static ChooserPopup<ScopeEntry> _typeChooser;
        private static ChooserPopup<SymbolInfo> _memberChooser;

        private readonly IWpfTextView _view;
        private readonly NavigationBar _bar = new NavigationBar();
        private readonly DispatcherTimer _timer;
        private readonly LatestRequestGate _gate = new LatestRequestGate();
        private CaretRequest _lastRequest;
        private CaretContextResponse _context;
        private string _selectedTypeKey;
        private bool _disposed;

        public NavigationBarMargin(IWpfTextView view)
        {
            _view = view;
            _timer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(250) };
            _timer.Tick += (s, e) => Safe.Execute(L, "navbar.tick", Refresh);
            _bar.SetLeft(Glyph.Class, string.Empty, () => NavStrings.NavBarTypesTip, true);
            _bar.SetRight(Glyph.Method, string.Empty, () => NavStrings.NavBarMembersTip, true);
            _bar.LeftClicked += (s, e) => Safe.RunAsync(L, "navbar.types", ShowTypesAsync);
            _bar.RightClicked += (s, e) => Safe.RunAsync(L, "navbar.members", ShowMembersAsync);
            _view.Caret.PositionChanged += OnCaretMoved;
            _view.TextBuffer.PostChanged += OnBufferChanged;
            _view.GotAggregateFocus += OnFocus;
            NavigationContext.SettingsChanged += OnSettingsChanged;
            UpdateVisibility();
            _timer.Start();
        }

        // ---------------------------------------------------------------- IWpfTextViewMargin

        public FrameworkElement VisualElement => _bar;

        public double MarginSize => _bar.ActualHeight;

        public bool Enabled => !_disposed;

        public ITextViewMargin GetTextViewMargin(string marginName) =>
            string.Equals(marginName, MarginName, StringComparison.OrdinalIgnoreCase) ? this : null;

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            _timer.Stop();
            _gate.Dispose();
            _view.Caret.PositionChanged -= OnCaretMoved;
            _view.TextBuffer.PostChanged -= OnBufferChanged;
            _view.GotAggregateFocus -= OnFocus;
            NavigationContext.SettingsChanged -= OnSettingsChanged;
        }

        // ---------------------------------------------------------------- refresh

        private static bool FeatureOn => NavigationContext.IsUnrealSolution && NavigationContext.Settings.NavigationBar;

        private void OnCaretMoved(object sender, CaretPositionChangedEventArgs e) => Restart();

        private void OnBufferChanged(object sender, EventArgs e) => Restart();

        // The package / UE solution may come up after the view was created: re-check when the editor gets focus.
        private void OnFocus(object sender, EventArgs e) => Safe.Execute(L, "navbar.focus", () => { UpdateVisibility(); Restart(); });

        private void Restart()
        {
            if (_disposed) return;
            _timer.Stop();
            _timer.Start();
        }

        private void OnSettingsChanged(object sender, Options.SettingsChangedEventArgs e) => Safe.Execute(L, "navbar.settings", () =>
        {
            UpdateVisibility();
            if (FeatureOn) Restart();
        });

        private void UpdateVisibility()
        {
            var want = FeatureOn ? Visibility.Visible : Visibility.Collapsed;
            if (_bar.Visibility != want) _bar.Visibility = want;
        }

        private void Refresh()
        {
            _timer.Stop();
            UpdateVisibility();
            if (_disposed || _view.IsClosed || !FeatureOn) return;
            var engine = NavigationContext.Engine;
            if (engine == null) return;
            var capture = EditorServices.CaptureCaret(_view);
            if (capture == null) return;
            var (gen, ct) = _gate.Begin(NavigationContext.Disposal);
            Safe.RunAsync(L, "navbar.refresh", async () =>
            {
                var request = await Task.Run(capture).ConfigureAwait(false);
                if (CaretRequestBuilder.SameCaret(request, _lastRequest) && _context != null) return;
                var r = await engine.GetCaretContextAsync(request, ct).ConfigureAwait(false);
                await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
                if (!_gate.IsCurrent(gen) || _disposed || !r.Ok) return;
                _lastRequest = request;
                _context = r.Value;
                Render();
            });
        }

        private void Render()
        {
            var ctx = _context;
            var type = ScopeModel.CurrentType(ctx);
            var fn = ScopeModel.CurrentFunction(ctx);
            _selectedTypeKey = ScopeModel.CurrentTypeKey(ctx);
            if (type != null) _bar.SetLeft(SymbolPresentation.GlyphFor(type), SymbolPresentation.QualifiedName(type));
            else if (!string.IsNullOrEmpty(_selectedTypeKey)) _bar.SetLeft(Glyph.Class, _selectedTypeKey);
            else _bar.SetLeft(Glyph.Namespace, NavStrings.NavBarGlobalScope, null, true);
            if (fn != null) _bar.SetRight(SymbolPresentation.GlyphFor(fn), ScopeModel.FunctionDisplay(fn));
            else _bar.SetRight(Glyph.Method, NavStrings.NavBarNoFunction, null, true);
        }

        // ---------------------------------------------------------------- drop-downs

        private async Task ShowTypesAsync()
        {
            await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
            var entries = ScopeModel.TypeEntries(_context);
            if (entries.Count == 0) return;
            if (_typeChooser == null) _typeChooser = new ChooserPopup<ScopeEntry>("uetovs.navbar.types", NavigationPresenters.Scope, new Size(420, 360));
            _typeChooser.OwnerHandle = VsMainWindow.Handle;
            var picked = await _typeChooser.ShowAsync(entries, PopupAnchor.Element(_bar.LeftBox), NavStrings.NavBarTypesTitle, e => e.Display);
            L.Info("navbar.types", null, ("count", entries.Count), ("chosen", picked.Chosen ? picked.Item.Key : null));
            if (!picked.Chosen) return;
            _selectedTypeKey = picked.Item.Key;
            var loc = NavigationPresenters.ToLocation(picked.Item.Location);
            if (loc != null) await NavigationService.Instance.JumpToAsync(loc, "navigationBar");
        }

        private async Task ShowMembersAsync()
        {
            await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
            List<SymbolInfo> members = ScopeModel.MembersOf(_context, _selectedTypeKey);
            if (members.Count == 0) members = ScopeModel.FileMemberList(_context?.FileSymbols);
            if (members.Count == 0) return;
            if (_memberChooser == null) _memberChooser = new ChooserPopup<SymbolInfo>("uetovs.navbar.members", NavigationPresenters.Member(null), new Size(480, 400));
            _memberChooser.OwnerHandle = VsMainWindow.Handle;
            var picked = await _memberChooser.ShowAsync(members, PopupAnchor.Element(_bar.RightBox), NavStrings.NavBarMembersTitle, ScopeModel.FilterText);
            L.Info("navbar.members", null, ("count", members.Count), ("type", _selectedTypeKey), ("chosen", picked.Chosen ? picked.Item.Name : null));
            if (!picked.Chosen) return;
            var loc = NavigationPresenters.ToLocation(picked.Item.Location);
            if (loc != null) await NavigationService.Instance.JumpToAsync(loc, "navigationBar");
        }
    }
}
