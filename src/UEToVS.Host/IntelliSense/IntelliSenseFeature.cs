using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.VisualStudio;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Shell.Interop;
using Microsoft.VisualStudio.TextManager.Interop;
using UEToVS.Core.Diagnostics;
using UEToVS.Host.Diagnostics;
using UEToVS.Host.Editor;
using UEToVS.Host.Integration;
using UEToVS.Host.Logic.Actions;
using UEToVS.Host.Logic.IntelliSense;
using UEToVS.Host.Logic.Localization;
using UEToVS.Host.Shell;

namespace UEToVS.Host.IntelliSense
{
    /// <summary>VSCT command ids of the M3 IntelliSense commands (range 0x0600–0x06FF; no menu placement, keyboard / Find Action only).</summary>
    internal static class IntelliSenseCommandIds
    {
        public const int ImportVaColors = 0x0600;
        public const int ToggleSemanticColoring = 0x0601;
        public const int ResetSemanticColors = 0x0602;
    }

    /// <summary>Stable action ids of the M3 IntelliSense features (§7.4).</summary>
    internal static class IntelliSenseActionIds
    {
        public const string ImportVaColors = "uetovs.intellisense.importVaColors";
        public const string ToggleSemanticColoring = "uetovs.intellisense.toggleSemanticColoring";
        public const string ResetSemanticColors = "uetovs.intellisense.resetSemanticColors";
    }

    /// <summary>
    /// M3 wiring from <see cref="HostPackage"/>: registers the IntelliSense actions (Find Action, bindable commands
    /// 0x0600–0x06FF, not on the top-level menu — settings-like switches live in the settings dialog) and implements
    /// "Import VA colours" / "Reset semantic colours" / "Toggle semantic colouring". The editor parts themselves are MEF
    /// components (completion, Quick Info, parameter info, colouring) that read <see cref="IntelliSenseContext"/>.
    /// </summary>
    internal static class IntelliSenseFeature
    {
        private static readonly Logger L = Log.For("IntelliSense");

        /// <summary>Fonts and Colors "Text Editor" category (where MEF classification formats live).</summary>
        private static readonly Guid TextEditorCategory = new Guid("A27B4E24-A735-4D1D-B8E7-9716E1E3D8E0");

        public static IReadOnlyList<(int CommandId, string ActionId, Func<string> Text)> Commands { get; } = new (int, string, Func<string>)[]
        {
            (IntelliSenseCommandIds.ImportVaColors, IntelliSenseActionIds.ImportVaColors, () => IntelliSenseStrings.CmdImportVaColors),
            (IntelliSenseCommandIds.ToggleSemanticColoring, IntelliSenseActionIds.ToggleSemanticColoring, () => IntelliSenseStrings.CmdToggleSemanticColoring),
            (IntelliSenseCommandIds.ResetSemanticColors, IntelliSenseActionIds.ResetSemanticColors, () => IntelliSenseStrings.CmdResetSemanticColors),
        };

        public static void Initialize(HostPackage package, ActionRegistry registry, CommandRouter router, OleMenuCommandService mcs)
        {
            registry.Register(new DelegateAction(IntelliSenseActionIds.ImportVaColors, IntelliSenseStrings.CmdImportVaColors, ActionCategory.Settings,
                ctx => ImportVaColorsAsync(package), null, null, IntelliSenseStrings.KwColors));
            registry.Register(new DelegateAction(IntelliSenseActionIds.ToggleSemanticColoring, IntelliSenseStrings.CmdToggleSemanticColoring, ActionCategory.Settings,
                ctx => ToggleColoringAsync(), null, null, IntelliSenseStrings.KwSemanticColoring));
            registry.Register(new DelegateAction(IntelliSenseActionIds.ResetSemanticColors, IntelliSenseStrings.CmdResetSemanticColors, ActionCategory.Settings,
                ctx => ResetColorsAsync(), null, null, IntelliSenseStrings.KwColors));
            if (mcs != null && router != null) router.RegisterMap(mcs, Commands);
            L.Info("is.actions.registered", null, ("count", Commands.Count));
        }

        // ---------------------------------------------------------------- toggle

        private static async Task ToggleColoringAsync()
        {
            await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
            if (!IntelliSenseContext.Settings.SemanticColoring)
            {
                // Off in the settings: the session toggle cannot turn it on (the settings dialog owns the persistent switch).
                IntelliSenseContext.ColoringSuspended = false;
                VsStatusBar.SetText(IntelliSenseStrings.SemanticColoringOff);
                L.Info("is.colors.toggle", null, ("settingOn", false));
                return;
            }
            IntelliSenseContext.ColoringSuspended = !IntelliSenseContext.ColoringSuspended;
            bool on = !IntelliSenseContext.ColoringSuspended;
            VsStatusBar.SetText(on ? IntelliSenseStrings.SemanticColoringOn : IntelliSenseStrings.SemanticColoringOff);
            L.Info("is.colors.toggle", null, ("on", on));
        }

        // ---------------------------------------------------------------- reset

        private static async Task ResetColorsAsync()
        {
            await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
            var provider = SemanticClassificationTaggerProvider.Instance;
            var view = EditorServices.ActiveView();
            bool dark = view == null || SemanticColors.IsDark(view.Background);
            string overrides = IntelliSenseContext.Settings.SemanticColorOverrides;
            var parsed = SemanticClassifications.ParseOverrides(overrides);
            // Persist: Fonts and Colors store first (survives restarts), then the live format map.
            var writes = SemanticClassifications.All.Select(i => new VaColorAssignment
            {
                Kind = i.Kind, ClassificationName = i.Name, Foreground = parsed.TryGetValue(i.Kind, out var o) ? o : i.Default(dark), Source = "reset",
            }).ToList();
            int stored = await WriteFontsAndColorsAsync(writes);
            int live = provider == null ? 0 : SemanticColors.Reset(provider.TextFormatMap, provider.Registry, dark, overrides);
            L.Info("is.colors.reset.done", null, ("dark", dark), ("stored", stored), ("live", live));
            Banners.Show("intellisense.colors", IntelliSenseStrings.ResetColorsDone, BannerKind.Info);
        }

        // ---------------------------------------------------------------- VA import

        private static async Task ImportVaColorsAsync(HostPackage package)
        {
            await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
            var dialog = new Microsoft.Win32.OpenFileDialog
            {
                Title = IntelliSenseStrings.ImportVaDialogTitle,
                Filter = IntelliSenseStrings.ImportVaFilter,
                CheckFileExists = true,
                Multiselect = false,
            };
            if (dialog.ShowDialog() != true) return;
            string path = dialog.FileName;
            L.Info("is.colors.vaImport.start", null, ("file", path));
            VaImportPlan plan;
            try
            {
                plan = await Task.Run(() =>
                {
                    using (var reader = new StreamReader(path, true)) return VaColorImport.Map(VaColorImport.Parse(reader));
                }).ConfigureAwait(true);
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException || ex is System.Xml.XmlException)
            {
                L.Error("is.colors.vaImport.failed", "Reading the .vssettings file failed", ex, ("file", path));
                Banners.Show("intellisense.colors", IntelliSenseStrings.ImportVaFailed(ex.Message), BannerKind.Error);
                return;
            }
            L.Info("is.colors.vaImport.plan", null, ("file", path), ("vaItems", plan.VaItems), ("withoutColor", plan.WithoutColor), ("ignored", plan.Ignored),
                   ("assignments", plan.Assignments.Select(a => a.Kind + "=" + SemanticClassifications.Hex(a.Foreground) + (a.Bold ? " bold" : "") + " (" + a.Source + ")").ToList()));
            await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
            if (plan.IsEmpty)
            {
                Banners.Show("intellisense.colors", IntelliSenseStrings.ImportVaNoItems, BannerKind.Warning);
                return;
            }
            int stored = await WriteFontsAndColorsAsync(plan.Assignments);
            var provider = SemanticClassificationTaggerProvider.Instance;
            if (provider != null) SemanticColors.SetImported(provider.TextFormatMap, provider.Registry, plan.Assignments);
            L.Info("is.colors.vaImport.done", null, ("stored", stored), ("assignments", plan.Assignments.Count), ("liveMap", provider != null));
            Banners.Show("intellisense.colors", IntelliSenseStrings.ImportVaDone(plan.Assignments.Count, plan.VaItems.Count), BannerKind.Info);
        }

        /// <summary>
        /// Writes foreground (and bold) of UEToVS classifications into Tools &gt; Options &gt; Fonts and Colors (Text Editor
        /// category) through IVsFontAndColorStorage, propagating to open editors. Returns the number of items written.
        /// </summary>
        private static async Task<int> WriteFontsAndColorsAsync(IEnumerable<VaColorAssignment> items)
        {
            await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
            if (!(Package.GetGlobalService(typeof(SVsFontAndColorStorage)) is IVsFontAndColorStorage storage))
            {
                L.Warn("is.colors.storage.unavailable", "IVsFontAndColorStorage unavailable; colours applied to this session only");
                return 0;
            }
            var category = TextEditorCategory;
            int n = 0;
            using (UiThread.Measure(L, "is.colors.storage.write"))
            {
                int hr = storage.OpenCategory(ref category, (uint)(__FCSTORAGEFLAGS.FCSF_LOADDEFAULTS | __FCSTORAGEFLAGS.FCSF_PROPAGATECHANGES));
                if (ErrorHandler.Failed(hr))
                {
                    L.Warn("is.colors.storage.openFailed", null, ("hr", hr));
                    return 0;
                }
                try
                {
                    foreach (var a in items)
                    {
                        var info = new ColorableItemInfo[1];
                        if (ErrorHandler.Failed(storage.GetItem(a.ClassificationName, info))) info[0] = new ColorableItemInfo();
                        info[0].crForeground = VaColorImport.ToColorRef(a.Foreground);
                        info[0].bForegroundValid = 1;
                        info[0].dwFontFlags = a.Bold ? (uint)FONTFLAGS.FF_BOLD : 0;
                        info[0].bFontFlagsValid = 1;
                        hr = storage.SetItem(a.ClassificationName, info);
                        if (ErrorHandler.Succeeded(hr)) n++;
                        else L.Warn("is.colors.storage.setFailed", null, ("item", a.ClassificationName), ("hr", hr));
                    }
                }
                finally
                {
                    storage.CloseCategory();
                }
            }
            return n;
        }
    }
}
