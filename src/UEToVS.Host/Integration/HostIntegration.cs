using System;
using System.Collections.Generic;
using System.Windows;
using UEToVS.Host.Logic.Actions;
using UEToVS.Host.Logic.Performance;

namespace UEToVS.Host.Integration
{
    // =====================================================================================================================
    // Integration points for components built in other workstreams (UEToVS.UI component library, UEToVS.Engine).
    // The host compiles and runs without them (placeholders); at integration, UEToVS.Host references UEToVS.UI and
    // assigns the slots in HostIntegration (one place: HostPackage.InitializeIntegrations).
    // =====================================================================================================================

    /// <summary>
    /// Supplies the content of Tools > Options > UEToVS > 外观 (§8.2.1).
    /// TODO(integration/UI): implement with UEToVS.UI's AppearanceSettingsPanel (bind to the design-token settings,
    /// apply live; Apply/Cancel come from the VS options dialog).
    /// </summary>
    public interface IAppearancePageContentProvider
    {
        /// <summary>Creates the page content (called once per options-dialog session, on the UI thread).</summary>
        UIElement CreateContent();

        /// <summary>User pressed OK/Apply.</summary>
        void Apply();

        /// <summary>User pressed Cancel: revert live previews.</summary>
        void Cancel();

        /// <summary>"恢复默认".</summary>
        void ResetToDefaults();
    }

    /// <summary>
    /// Find Action / command palette popup (§7.4). The host owns the data (ActionRegistry); the UI owns the popup.
    /// TODO(integration/UI): implement with UEToVS.UI's CommandPalette; call registry.Query(text, context) per keystroke
    /// and registry.ExecuteAsync(id, context) on Enter.
    /// </summary>
    public interface ICommandPaletteProvider
    {
        void Show(ActionRegistry registry, ActionContext context, string initialText);
    }

    /// <summary>
    /// Component gallery (§8.7): a window listing every UI component in all states.
    /// TODO(integration/UI): implement with UEToVS.UI's gallery window.
    /// </summary>
    public interface IComponentGalleryProvider
    {
        void Show();
    }

    /// <summary>
    /// Content of the "UEToVS 性能" tool window (§9.8). The UI library's PerformancePanel has its own view-data type;
    /// <see cref="IPerformancePanelContent.Update"/> is THE mapping point from the host's <see cref="PerformanceData"/> to it.
    /// TODO(integration/UI): implement with UEToVS.UI's PerformancePanel:
    ///   - Engine metric card  ← data.Engine.Resources.PrivateBytes / WorkingSetBytes / ManagedHeapBytes, data.Engine.CpuPercent
    ///   - VS-side card        ← data.Host.PrivateBytes, data.Host.PrivateBytesDelta (since load), data.Host.ManagedHeapBytes
    ///   - Index state card    ← data.Engine.Status (Phase, FilesParsed/FilesToParse, SymbolCount)
    ///   - Trend charts        ← data.Engine.History (engine) and data.HostHistory (host)
    ///   - 正在进行            ← data.Engine.Active (names are engine codes: localize with EngineCodes.WorkName),
    ///                            ScheduledInteractive/Background, BackgroundPaused (pause button state)
    ///   - Errors / details    ← EngineCodes.Error(data.Engine.Status.LastError), EngineCodes.Detail(phase.Detail)
    ///   - 索引阶段            ← data.Engine.Phases;  索引内存构成 ← data.Engine.IndexParts
    ///   - 操作延迟            ← data.Engine.Operations + data.HostOperations (highlight PerformanceDataBuilder.IsOverBudget)
    ///   - Engine unavailable  ← data.Engine == null, show data.EngineState / data.EngineError
    ///   - Buttons             → <see cref="PerformancePanelCommands"/>
    /// </summary>
    public interface IPerformancePanelContentProvider
    {
        IPerformancePanelContent Create(PerformancePanelCommands commands);
    }

    public interface IPerformancePanelContent
    {
        UIElement Element { get; }

        /// <summary>Called on the UI thread about once per second while the window is visible.</summary>
        void Update(PerformanceData data);
    }

    /// <summary>Buttons of the performance panel, routed through the action hub.</summary>
    public sealed class PerformancePanelCommands
    {
        public Action OpenLog { get; set; }
        public Action CreateDiagnosticsPackage { get; set; }

        /// <summary>Pause/resume background indexing (IEngineService.SetBackgroundPausedAsync).</summary>
        public Action<bool> SetBackgroundPaused { get; set; }
    }

    /// <summary>
    /// Banner (InfoBar) presenter. Defaults to the VS InfoBar; TODO(integration/UI): may be replaced by UEToVS.UI's Banner.
    /// </summary>
    public interface IBannerPresenter
    {
        /// <param name="id">Stable id; showing the same id again replaces the previous banner.</param>
        void Show(string id, string text, BannerKind kind, IReadOnlyList<KeyValuePair<string, Action>> actions);

        void Close(string id);
    }

    public enum BannerKind
    {
        Info,
        Warning,
        Error,
    }

    /// <summary>The slots. Assigned once at package initialization (UI thread); read anywhere.</summary>
    public static class HostIntegration
    {
        public static IAppearancePageContentProvider Appearance { get; set; }
        public static ICommandPaletteProvider CommandPalette { get; set; }
        public static IComponentGalleryProvider ComponentGallery { get; set; }
        public static IPerformancePanelContentProvider PerformancePanel { get; set; }
        public static IBannerPresenter Banner { get; set; }
    }
}
