using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using System.Windows;
using Microsoft.VisualStudio.Shell;
using UEToVS.Contracts;
using UEToVS.Core.Diagnostics;
using UEToVS.Host.Diagnostics;
using UEToVS.Host.Engine;
using UEToVS.Host.Integration;
using UEToVS.Host.Logic.Localization;
using UEToVS.Host.Logic.Settings;
using UEToVS.UI.Logic;

namespace UEToVS.Host.Settings
{
    /// <summary>
    /// Opens the single <see cref="SettingsWindow"/> (Extensions &gt; UEToVS &gt; 设置…, the toolbar brand button, Find
    /// Action, the Tools &gt; Options stub page, banner links) and adapts the package to <see cref="ISettingsWindowHost"/>.
    /// </summary>
    internal sealed class SettingsWindowService : ISettingsWindowHost
    {
        private static readonly Logger L = Log.For("SettingsWindow");
        private readonly HostPackage _package;
        private SettingsWindow _window;

        public SettingsWindowService(HostPackage package)
        {
            _package = package;
        }

        public void Show(string category = null)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            if (_window == null)
            {
                var window = new SettingsWindow(this, category);
                window.Closed += (s, e) => Safe.Execute(L, "settings.window.closed", () =>
                {
                    if (ReferenceEquals(_window, window)) _window = null;
                    L.Info("settings.window.closed", null);
                });
                _window = window;
            }
            else if (category != null) _window.ShowCategory(category);
            using (UiThread.Measure(L, "settings.window.show"))
                _window.ShowOwned(VsMainWindow.Handle);
        }

        // ------------------------------------------------------------------ ISettingsWindowHost

        public IReadOnlyList<SettingDescriptor> Schema => _package.Settings.Schema;

        public string ProjectDirectory
        {
            get
            {
                string project = _package.Unreal?.ProjectFile;
                if (!string.IsNullOrEmpty(project)) return System.IO.Path.GetDirectoryName(project);
                return _package.SolutionDirectory;
            }
        }

        public Dictionary<string, string> LoadValues() => _package.Settings.UserValues();

        public void SaveValues(IReadOnlyDictionary<string, string> values) => _package.Settings.ApplyUserValues(values, "settingsWindow");

        public void PreviewFonts(IReadOnlyDictionary<string, string> values)
        {
            var fonts = _package.Fonts;
            if (fonts == null) return;
            // Saved settings with only the font rows taken from the window (the window shows just the user's values).
            var s = _package.Settings.Effective.Clone();
            if (values != null)
                foreach (var kv in values)
                    if (UEToVS.Host.Logic.Fonts.FontSettingsSchema.IsFontKey(kv.Key)) UEToVS.Host.Logic.Fonts.FontSettingsSchema.TrySet(s, kv.Key, kv.Value);
            fonts.Preview(s, values == null ? "previewRevert" : "preview");
        }

        public async Task<string> RunActionAsync(string actionId)
        {
            var engine = _package.Engine;
            switch (actionId)
            {
                case HostSettingsSchema.WindowActions.RebuildProjectIndex:
                    return await RebuildAsync(engine, EngineClient.ScopeProject);
                case HostSettingsSchema.WindowActions.RebuildEngineIndex:
                    return await RebuildAsync(engine, EngineClient.ScopeEngine);
                case HostSettingsSchema.WindowActions.RebuildBlueprintIndex:
                    return await RebuildAsync(engine, RebuildScope.Assets);
                case HostSettingsSchema.WindowActions.RebuildAllIndexes:
                    return await RebuildAsync(engine, RebuildScope.All);
                case HostSettingsSchema.WindowActions.IndexLua:
                {
                    if (HostSettingsSchema.Codec.SplitPaths(_package.Settings.Effective.LuaScriptPaths).Count == 0) return SettingsText.LuaIndexDisabled;
                    // Make sure the engine has the folders the user sees (settings are pushed on save, asynchronously).
                    if (engine != null && engine.IsConnected)
                        await engine.SetIndexingSettingsAsync(HostSettingsSchema.Codec.ToIndexing(_package.Settings.Effective)).ConfigureAwait(true);
                    return await RebuildAsync(engine, RebuildScope.Lua);
                }
                case HostSettingsSchema.WindowActions.CheckForUpdates:
                    return await Update.UpdateService.CheckAndInstallAsync(HostPackage.HostVersion).ConfigureAwait(true);
                case HostSettingsSchema.WindowActions.ToggleIntelliSense:
                {
                    var svc = _package.IntelliSense;
                    if (svc == null) return SettingsText.ActionUnavailable;
                    var report = svc.BackupExists ? await svc.RestoreAsync("settingsWindow") : await svc.TakeOverAsync("settingsWindow");
                    return report != null ? SettingsText.Done : SettingsText.Failed;
                }
                default:
                    if (_package.Actions.Get(actionId) == null) return SettingsText.ActionUnavailable;
                    _package.ExecuteAction(actionId);
                    return null;
            }
        }

        private static async Task<string> RebuildAsync(EngineClient engine, RebuildScope scope)
        {
            if (engine == null || !engine.IsConnected) return SettingsText.EngineNotConnected;
            bool ok = await engine.RebuildIndexAsync(scope).ConfigureAwait(true);
            return ok ? SettingsText.Requested : SettingsText.Failed;
        }

        public string InfoText(string infoId)
        {
            switch (infoId)
            {
                case HostSettingsSchema.WindowActions.IndexStatus:
                {
                    var s = _package.Settings.Effective;
                    int cores = Environment.ProcessorCount;
                    string threads = SettingsText.IndexThreadsEffective(HostSettingsSchema.EffectiveThreads(s.CppIndexThreads, cores),
                                                                        HostSettingsSchema.EffectiveThreads(s.BlueprintIndexThreads, cores), cores);
                    var engine = _package.Engine;
                    var st = engine?.LastStatus;
                    if (engine == null) return SettingsText.IndexStatusNone + "\n" + threads;
                    if (st == null) return Strings.EngineStateName(engine.State.ToString()) + "\n" + threads;
                    return SettingsText.IndexStatus(Strings.PhaseName((int)st.Phase), st.FilesParsed, st.FilesToParse, st.SymbolCount, st.AssetCount) + "\n" + threads;
                }
                case HostSettingsSchema.WindowActions.LuaIndexStatus:
                {
                    var st = _package.Engine?.LastStatus;
                    var configured = HostSettingsSchema.Codec.SplitPaths(_package.Settings.Effective.LuaScriptPaths);
                    if (configured.Count == 0) return SettingsText.LuaIndexDisabled;
                    if (st == null) return SettingsText.EngineNotConnected;
                    string roots = st.LuaRoots == null || st.LuaRoots.Count == 0 ? string.Join("; ", configured) : string.Join("; ", st.LuaRoots);
                    string text = SettingsText.LuaIndexStatus(SettingsText.LuaStateName((int)st.LuaState), st.LuaFileCount, st.LuaSymbolCount, roots);
                    return string.IsNullOrEmpty(st.LuaLastError) ? text : text + "\n" + st.LuaLastError;
                }
                case HostSettingsSchema.WindowActions.IntelliSenseStatus:
                    return _package.IntelliSense?.BackupExists == true ? SettingsText.IntelliSenseTakenOver : SettingsText.IntelliSenseNotTakenOver;
                case UEToVS.Host.Logic.Fonts.FontSettingsSchema.VsStatusInfoId:
                    return _package.Fonts?.VsStatusText(_package.Settings.Effective) ?? SettingsText.ActionUnavailable;
                case HostSettingsSchema.WindowActions.LogDirectory:
                    return SettingsText.LogDirectory(HostLog.LogDirectory);
                case HostSettingsSchema.WindowActions.VsUnrealIntegration:
                {
                    var found = _package.IntelliSense?.VsBlueprintOptions;
                    if (found == null) return SettingsText.VsUnrealIntegration;
                    return SettingsText.VsUnrealIntegration + "\n" + (found.Count == 0 ? SettingsText.VsBlueprintOptionsNone : SettingsText.VsBlueprintOptionsFound(string.Join(", ", found)));
                }
                default:
                    return null;
            }
        }

        public IndexStatusInput IndexStatus()
        {
            var s = _package.Settings.Effective;
            int cores = Environment.ProcessorCount;
            var engine = _package.Engine;
            var st = engine?.LastStatus;
            return new IndexStatusInput
            {
                HasEngine = engine != null,
                EngineState = engine?.State.ToString(),
                EngineError = engine?.LastError,
                HasStatus = st != null,
                Phase = st == null ? 0 : (int)st.Phase,
                FilesParsed = st?.FilesParsed ?? 0,
                FilesToParse = st?.FilesToParse ?? 0,
                SymbolCount = st?.SymbolCount ?? 0,
                AssetCount = st?.AssetCount ?? 0,
                LastError = st?.LastError,
                CppThreads = HostSettingsSchema.EffectiveThreads(s.CppIndexThreads, cores),
                BlueprintThreads = HostSettingsSchema.EffectiveThreads(s.BlueprintIndexThreads, cores),
                Cores = cores,
            };
        }

        public ISettingsCustomPage CreateCustomPage(string categoryId)
        {
            switch (categoryId)
            {
                case HostSettingsSchema.Appearance:
                    var provider = HostIntegration.Appearance;
                    if (provider == null) L.Warn("settings.custom.noProvider", "appearance provider slot is empty (integration.slots failed?)", ("category", categoryId));
                    return provider == null ? null : new AppearancePage(provider);
                case HostSettingsSchema.Keymap:
                    return new KeymapPage(_package);
                case HostSettingsSchema.Agent:
                    return _package.Agent == null ? null : new AgentApiPage(_package.Agent);
                default:
                    return null;
            }
        }

        /// <summary>The existing appearance panel (UEToVS.UI AppearanceSettingsPanel) as a settings-window page.</summary>
        private sealed class AppearancePage : ISettingsCustomPage
        {
            private readonly IAppearancePageContentProvider _provider;

            public AppearancePage(IAppearancePageContentProvider provider)
            {
                _provider = provider;
                Element = provider.CreateContent() ?? throw new InvalidOperationException("appearance provider returned no content");
                provider.Activate();
                // Inside the settings window the footer owns "恢复默认"; the panel's own reset button would duplicate it.
                if (Element is UEToVS.UI.Settings.AppearanceSettingsPanel panel) panel.EmbeddedInSettingsWindow = true;
            }

            public UIElement Element { get; }
            public void Apply() => _provider.Apply();
            public void Cancel() => _provider.Cancel();
            // Preview only (the panel's own reset); committed with Apply / OK like every other page.
            public void ResetToDefaults()
            {
                if (Element is UEToVS.UI.Settings.AppearanceSettingsPanel panel) panel.ResetToDefaults();
            }
        }
    }
}
