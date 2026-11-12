using System.Threading.Tasks;
using UEToVS.Core.Diagnostics;
using UEToVS.Host.Editor;
using UEToVS.Host.Logic.Actions;
using UEToVS.Host.Logic.Localization;
using UEToVS.Host.Shell;

namespace UEToVS.Host.Editing
{
    /// <summary>
    /// v0.4.5 编辑体验 actions (commands 0x0180–0x019F in CommandCatalog dispatch to them; Find Action lists them too).
    /// </summary>
    internal static class EditorFeatureActions
    {
        private static readonly Logger L = Log.For("Editing");

        public static void Register(ActionRegistry registry)
        {
            registry.Register(new DelegateAction(EditorFeatureIds.SurroundWith, EditorFeatureStrings.CmdSurroundWith, ActionCategory.Refactoring,
                ctx => SurroundWithFeature.ExecuteAsync(), ctx => ctx.ActiveDocumentPath != null, null, EditorFeatureStrings.KwSurround));
            registry.Register(new DelegateAction(EditorFeatureIds.ToggleInlayHints, EditorFeatureStrings.CmdToggleInlayHints, ActionCategory.Settings,
                ctx => ToggleInlayHintsAsync(), null, null, EditorFeatureStrings.KwInlay));
            L.Debug("editor.actions.registered", null, ("count", 2));
        }

        private static Task ToggleInlayHintsAsync()
        {
            if (!InlayHintsTagger.AnyEnabledInSettings)
            {
                VsStatusBar.SetText(EditorFeatureStrings.InlayAllOff);
                return Task.CompletedTask;
            }
            InlayHintsTagger.Hidden = !InlayHintsTagger.Hidden;
            VsStatusBar.SetText(InlayHintsTagger.Hidden ? EditorFeatureStrings.InlayOff : EditorFeatureStrings.InlayOn);
            L.Info("editor.inlayHints.toggle", null, ("hidden", InlayHintsTagger.Hidden));
            return Task.CompletedTask;
        }
    }
}
