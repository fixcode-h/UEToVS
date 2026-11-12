using System;
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using Microsoft.VisualStudio.Shell;
using UEToVS.Core.Diagnostics;
using UEToVS.Host.Diagnostics;
using UEToVS.Host.Integration;
using UEToVS.Host.Logic.Localization;
using UEToVS.Host.Logic.Settings;

namespace UEToVS.Host.Options
{
    /// <summary>Tools > Options > UEToVS > General (常规). Page names are fixed English (ProvideOptionPage); content is localized via Ui* attributes. Persisted in the VS settings store (DialogPage).</summary>
    [Guid("e3b2a2f4-6a55-4c33-9a4e-1f3d6f0c8a01")]
    public sealed class GeneralOptionsPage : DialogPage
    {
        private static readonly Logger L = Log.For("Options");

        [UiCategory(nameof(Strings.CatLogging))]
        [UiDisplayName(nameof(Strings.OptLogLevel))]
        [UiDescription(nameof(Strings.OptLogLevelDesc))]
        public LogLevel LogLevel { get; set; } = LogLevel.Info;

        [UiCategory(nameof(Strings.CatEngine))]
        [UiDisplayName(nameof(Strings.OptEnginePath))]
        [UiDescription(nameof(Strings.OptEnginePathDesc))]
        public string EnginePathOverride { get; set; } = string.Empty;

        [UiCategory(nameof(Strings.CatEngine))]
        [UiDisplayName(nameof(Strings.OptUeRoot))]
        [UiDescription(nameof(Strings.OptUeRootDesc))]
        public string UnrealEngineRootOverride { get; set; } = string.Empty;

        [UiCategory(nameof(Strings.CatEngine))]
        [UiDisplayName(nameof(Strings.OptTimeout))]
        [UiDescription(nameof(Strings.OptTimeoutDesc))]
        public int EngineRequestTimeoutSeconds { get; set; } = 30;

        [UiCategory(nameof(Strings.CatCppInsight))]
        [UiDisplayName(nameof(Strings.OptTakeover))]
        [UiDescription(nameof(Strings.OptTakeoverDesc))]
        public bool IntelliSenseTakeover { get; set; } = true;

        [UiCategory(nameof(Strings.CatCppInsight))]
        [UiDisplayName(nameof(Strings.OptTakeoverMode))]
        [UiDescription(nameof(Strings.OptTakeoverModeDesc))]
        public IntelliSenseTakeoverMode IntelliSenseTakeoverMode { get; set; } = IntelliSenseTakeoverMode.Global;

        [UiCategory(nameof(Strings.CatCppInsight))]
        [UiDisplayName(nameof(Strings.OptDisableDb))]
        [UiDescription(nameof(Strings.OptDisableDbDesc))]
        public bool DisableDatabase { get; set; } = true;

        [Browsable(false)]
        public bool TakeoverNoticeShown { get; set; }

        [Browsable(false)]
        public bool VisualAssistNoticeDismissed { get; set; }

        /// <summary>Raised after the user pressed OK/Apply (UI thread).</summary>
        public event EventHandler Applied;

        protected override void OnApply(PageApplyEventArgs e)
        {
            base.OnApply(e);
            if (e.ApplyBehavior != ApplyKind.Apply) return;
            L.Info("options.applied", null, ("page", "General"));
            Safe.Execute(L, "options.general.applied", () => Applied?.Invoke(this, EventArgs.Empty));
        }

        internal void Fill(HostSettings s)
        {
            s.LogLevel = LogLevel;
            s.EnginePathOverride = EnginePathOverride ?? string.Empty;
            s.UnrealEngineRootOverride = UnrealEngineRootOverride ?? string.Empty;
            s.EngineRequestTimeoutSeconds = EngineRequestTimeoutSeconds > 0 ? EngineRequestTimeoutSeconds : 30;
            s.IntelliSenseTakeover = IntelliSenseTakeover;
            s.IntelliSenseTakeoverMode = IntelliSenseTakeoverMode;
            s.DisableDatabase = DisableDatabase;
        }
    }

    /// <summary>
    /// Tools > Options > UEToVS > 外观. The content comes from <see cref="HostIntegration.Appearance"/> (UEToVS.UI's
    /// AppearanceSettingsPanel); the settings are persisted by the provider (not as DialogPage properties). A placeholder
    /// is shown only when the slot is empty.
    /// </summary>
    [Guid("e3b2a2f4-6a55-4c33-9a4e-1f3d6f0c8a02")]
    public sealed class AppearanceOptionsPage : UIElementDialogPage
    {
        private static readonly Logger L = Log.For("Options");
        private UIElement _child;
        private bool _applied;

        protected override UIElement Child => _child ?? (_child = Safe.Execute(L, "options.appearance.create", CreateChild, Placeholder(Strings.AppearanceLoadFailed)));

        private static UIElement CreateChild()
        {
            var provider = HostIntegration.Appearance;
            if (provider != null) return provider.CreateContent();
            return Placeholder(Strings.AppearancePlaceholder);
        }

        private static UIElement Placeholder(string text) =>
            new TextBlock { Text = text, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(8) };

        protected override void OnActivate(CancelEventArgs e)
        {
            base.OnActivate(e);
            _applied = false;
            if (_child != null) Safe.Execute(L, "options.appearance.activate", () => HostIntegration.Appearance?.Activate());
        }

        protected override void OnApply(PageApplyEventArgs e)
        {
            base.OnApply(e);
            if (e.ApplyBehavior != ApplyKind.Apply) return;
            _applied = true;
            Safe.Execute(L, "options.appearance.apply", () => HostIntegration.Appearance?.Apply());
        }

        protected override void OnClosed(EventArgs e)
        {
            base.OnClosed(e);
            if (!_applied) Safe.Execute(L, "options.appearance.cancel", () => HostIntegration.Appearance?.Cancel());
        }

        public override void ResetSettings()
        {
            base.ResetSettings();
            Safe.Execute(L, "options.appearance.reset", () => HostIntegration.Appearance?.ResetToDefaults());
        }
    }

    /// <summary>Tools > Options > UEToVS > 日志: retention and a button to open the log folder.</summary>
    [Guid("e3b2a2f4-6a55-4c33-9a4e-1f3d6f0c8a03")]
    public sealed class LoggingOptionsPage : UIElementDialogPage
    {
        private static readonly Logger L = Log.For("Options");
        private StackPanel _panel;
        private TextBox _days;
        private TextBox _maxMb;
        private TextBlock _where;

        /// <summary>Days of logs kept in .vs/UEToVS/Logs.</summary>
        public int LogRetentionDays { get; set; } = 7;

        /// <summary>Total size cap of the log folder, in MB.</summary>
        public int LogMaxTotalMB { get; set; } = 200;

        /// <summary>Raised after OK/Apply (UI thread).</summary>
        public event EventHandler Applied;

        /// <summary>Set by the package: opens the log folder.</summary>
        internal Action OpenLogFolder { get; set; }

        protected override UIElement Child
        {
            get
            {
                if (_panel != null) return _panel;
                _panel = new StackPanel { Margin = new Thickness(8) };
                _panel.Children.Add(new TextBlock { Text = Strings.LoggingIntro, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 8) });
                _where = new TextBlock { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 8), Opacity = 0.8 };
                _panel.Children.Add(_where);
                _days = AddRow(Strings.LoggingRetentionDays, LogRetentionDays);
                _maxMb = AddRow(Strings.LoggingMaxTotal, LogMaxTotalMB);
                var open = new Button { Content = Strings.CmdOpenLogFolder, HorizontalAlignment = HorizontalAlignment.Left, Padding = new Thickness(12, 2, 12, 2), Margin = new Thickness(0, 8, 0, 0) };
                open.Click += (s, e) => Safe.Execute(L, "options.logging.openFolder", () => OpenLogFolder?.Invoke());
                _panel.Children.Add(open);
                return _panel;
            }
        }

        private TextBox AddRow(string label, int value)
        {
            var row = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 2, 0, 2) };
            row.Children.Add(new TextBlock { Text = label, Width = 140, VerticalAlignment = VerticalAlignment.Center });
            var box = new TextBox { Text = value.ToString(), Width = 80 };
            row.Children.Add(box);
            _panel.Children.Add(row);
            return box;
        }

        protected override void OnActivate(CancelEventArgs e)
        {
            base.OnActivate(e);
            if (_days != null) _days.Text = LogRetentionDays.ToString();
            if (_maxMb != null) _maxMb.Text = LogMaxTotalMB.ToString();
            if (_where != null) _where.Text = Strings.LoggingCurrent(HostLog.LogDirectory);
        }

        protected override void OnApply(PageApplyEventArgs e)
        {
            if (e.ApplyBehavior == ApplyKind.Apply)
            {
                if (_days != null && int.TryParse(_days.Text, out int d) && d > 0) LogRetentionDays = d;
                if (_maxMb != null && int.TryParse(_maxMb.Text, out int m) && m > 0) LogMaxTotalMB = m;
            }
            base.OnApply(e);
            if (e.ApplyBehavior != ApplyKind.Apply) return;
            L.Info("options.applied", null, ("page", "Logging"), ("days", LogRetentionDays), ("maxMB", LogMaxTotalMB));
            Safe.Execute(L, "options.logging.applied", () => Applied?.Invoke(this, EventArgs.Empty));
        }

        internal void Fill(HostSettings s)
        {
            s.LogRetentionDays = LogRetentionDays > 0 ? LogRetentionDays : 7;
            s.LogMaxTotalMB = LogMaxTotalMB > 0 ? LogMaxTotalMB : 200;
        }
    }
}
