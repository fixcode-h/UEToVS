using System.Windows;
using Microsoft.VisualStudio.Shell;
using UEToVS.Core.Diagnostics;
using UEToVS.UI.Logic;
using UEToVS.UI.Settings;
using UEToVS.UI.Theming;

namespace UEToVS.Host.Integration
{
    /// <summary>
    /// Tools > Options > UEToVS > Appearance backed by UEToVS.UI's <see cref="AppearanceSettingsPanel"/> (§8.2.1).
    /// Settings persist as <see cref="AppearanceSettings.ToStorageString"/> text in the VS per-user settings store
    /// (UEToVS\Appearance, "Settings"); <see cref="ApplyStored"/> applies them to <see cref="ThemeManager"/> at package
    /// initialization. Every method runs on the UI thread and is called through Safe by AppearanceOptionsPage.
    /// </summary>
    internal sealed class UiAppearancePage : IAppearancePageContentProvider
    {
        private const string Collection = "UEToVS\\Appearance";
        private const string Property = "Settings";
        private static readonly Logger L = Log.For("Appearance");

        private AppearanceSettingsPanel _panel;

        /// <summary>Package init (after Loc is set): stored text → ThemeManager. Problems in the text are logged, never fatal.</summary>
        public static void ApplyStored()
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            string stored = VsUserSettings.Get(Collection, Property);
            var settings = AppearanceSettings.Parse(stored, out var problems);
            foreach (var p in problems) L.Warn("appearance.stored.problem", p);
            ThemeManager.Instance.Apply(settings);
            L.Info("appearance.applied", null, ("source", stored == null ? "defaults" : "settingsStore"), ("preset", settings.Preset.ToString()),
                   ("fontScale", settings.FontScale), ("problems", problems.Count));
        }

        public UIElement CreateContent()
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            _panel = new AppearanceSettingsPanel();
            _panel.Load(ThemeManager.Instance.Settings);
            return _panel;
        }

        public void Activate()
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            _panel?.Load(ThemeManager.Instance.Settings);
        }

        public void Apply()
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            if (_panel == null) return;
            _panel.Commit();
            Persist(ThemeManager.Instance.Settings, "apply");
        }

        public void Cancel()
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            _panel?.Revert();
        }

        /// <summary>DialogPage.ResetSettings (Import and Export Settings > Reset): defaults are committed and saved at once.
        /// The panel's own "reset" button only previews defaults until OK/Apply.</summary>
        public void ResetToDefaults()
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            var defaults = AppearanceSettings.CreateDefault();
            ThemeManager.Instance.Apply(defaults);
            _panel?.Load(ThemeManager.Instance.Settings);
            Persist(ThemeManager.Instance.Settings, "reset");
        }

        private static void Persist(AppearanceSettings settings, string reason)
        {
            bool ok = VsUserSettings.Set(Collection, Property, settings.ToStorageString());
            L.Info("appearance.saved", null, ("reason", reason), ("ok", ok), ("preset", settings.Preset.ToString()));
        }
    }
}
