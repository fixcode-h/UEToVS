using System;
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using Microsoft.VisualStudio.Shell;
using UEToVS.Core.Diagnostics;
using UEToVS.Host.Diagnostics;
using UEToVS.Host.Logic.Localization;
using UEToVS.Host.Logic.Settings;
using UEToVS.Host.Shell;
using UEToVS.UI.Controls;
using UEToVS.UI.Theming;

namespace UEToVS.Host.Options
{
    /// <summary>
    /// Tools &gt; Options &gt; UEToVS &gt; General. Since v0.3.0 every setting lives in the UEToVS settings window; this page
    /// only explains that and has one button that opens it (kept so users who look in Tools &gt; Options find the way).
    /// The old properties stay as hidden, persisted DialogPage values: they are read once to migrate into the settings
    /// window's store (<see cref="SettingsStore"/>), and the two notice flags are still used.
    /// </summary>
    [Guid("e3b2a2f4-6a55-4c33-9a4e-1f3d6f0c8a01")]
    public sealed class GeneralOptionsPage : UIElementDialogPage
    {
        private static readonly Logger L = Log.For("Options");
        private UIElement _child;

        [Browsable(false)] public LogLevel LogLevel { get; set; } = LogLevel.Info;
        [Browsable(false)] public string EnginePathOverride { get; set; } = string.Empty;
        [Browsable(false)] public string UnrealEngineRootOverride { get; set; } = string.Empty;
        [Browsable(false)] public int EngineRequestTimeoutSeconds { get; set; } = 30;
        [Browsable(false)] public bool IntelliSenseTakeover { get; set; } = false;
        [Browsable(false)] public IntelliSenseTakeoverMode IntelliSenseTakeoverMode { get; set; } = IntelliSenseTakeoverMode.Global;
        [Browsable(false)] public bool DisableDatabase { get; set; } = true;
        [Browsable(false)] public bool DisableBackgroundCodeAnalysis { get; set; } = false;
        [Browsable(false)] public bool TakeoverNoticeShown { get; set; }
        [Browsable(false)] public bool VisualAssistNoticeDismissed { get; set; }

        protected override UIElement Child => _child ?? (_child = Safe.Execute(L, "options.general.create", Create,
            new TextBlock { Text = SettingsText.OptionsMovedIntro, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(8) }));

        private static UIElement Create()
        {
            var panel = new StackPanel { Margin = new Thickness(12) };
            ThemeManager.Instance.Attach(panel);
            panel.Children.Add(new TextBlock { Text = SettingsText.OptionsMovedIntro, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 12) });
            var open = new UiButton(() => SettingsText.OpenUEToVSSettings, ButtonKind.Primary) { HorizontalAlignment = HorizontalAlignment.Left, MinWidth = 160 };
            open.Click += (s, e) => Safe.Execute(L, "options.general.openSettings", () => HostPackage.Instance?.ExecuteAction(ActionIds.Settings));
            panel.Children.Add(open);
            return panel;
        }

        /// <summary>Legacy values (migration only).</summary>
        internal void Fill(HostSettings s)
        {
            s.LogLevel = LogLevel;
            s.EnginePathOverride = EnginePathOverride ?? string.Empty;
            s.UnrealEngineRootOverride = UnrealEngineRootOverride ?? string.Empty;
            s.EngineRequestTimeoutSeconds = EngineRequestTimeoutSeconds > 0 ? EngineRequestTimeoutSeconds : 30;
            s.IntelliSenseTakeover = IntelliSenseTakeover;
            s.IntelliSenseTakeoverMode = IntelliSenseTakeoverMode;
            s.DisableDatabase = DisableDatabase;
            s.DisableBackgroundCodeAnalysis = DisableBackgroundCodeAnalysis;
        }
    }

    /// <summary>
    /// The old Tools &gt; Options &gt; UEToVS &gt; Logging values. No longer registered as an options page (log settings are in
    /// the settings window); still loaded through GetDialogPage so <see cref="SettingsStore"/> can migrate them once.
    /// </summary>
    [Guid("e3b2a2f4-6a55-4c33-9a4e-1f3d6f0c8a03")]
    public sealed class LoggingOptionsPage : DialogPage
    {
        public int LogRetentionDays { get; set; } = 7;

        public int LogMaxTotalMB { get; set; } = 200;

        internal void Fill(HostSettings s)
        {
            s.LogRetentionDays = LogRetentionDays > 0 ? LogRetentionDays : 7;
            s.LogMaxTotalMB = LogMaxTotalMB > 0 ? LogMaxTotalMB : 200;
        }
    }
}
