using System;
using System.Threading.Tasks;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Text;
using Microsoft.VisualStudio.Text.Editor;
using UEToVS.Core.Diagnostics;
using UEToVS.Host.Editor;
using UEToVS.Host.Logic.Actions;
using UEToVS.Host.Logic.Editing;
using UEToVS.Host.Logic.Refactoring;
using UEToVS.Host.Refactoring;
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
            registry.Register(new DelegateAction(EditorFeatureIds.ShowHashtags, EditorFeatureStrings.CmdShowHashtags, ActionCategory.Navigation,
                ctx => HashtagsFeature.ShowAsync(), null, null, EditorFeatureStrings.KwHashtags));
            registry.Register(new DelegateAction(EditorFeatureIds.PasteFromHistory, EditorFeatureStrings.CmdPasteFromHistory, ActionCategory.Refactoring,
                ctx => ClipboardHistoryFeature.PasteAsync(), ctx => ctx.ActiveDocumentPath != null, null, EditorFeatureStrings.KwClipboard));
            UEToVS.Host.Diagnostics.Safe.Execute(L, "editor.clipboard.init", ClipboardHistoryFeature.Initialize);
            // Alt+Enter fixes (offered by EditorFixProvider from the squiggles under the caret)
            registry.Register(new DelegateAction(EditorFeatureIds.ReplaceDeprecated, EditorFeatureStrings.GroupFix, ActionCategory.Fix,
                ReplaceDeprecatedAsync, ctx => ctx.Get<DiagnosticFix>(RefactorKeys.Argument) != null, null));
            registry.Register(new DelegateAction(EditorFeatureIds.FixNaming, EditorFeatureStrings.GroupFix, ActionCategory.Fix,
                FixNamingAsync, ctx => ctx.Get<DiagnosticFix>(RefactorKeys.Argument) != null, null));
            RefactoringFeature.Hub.Register(new EditorFixProvider());
            LightBulbActions.Register(registry); // v0.5.12 灯泡菜单：UE 规则修复、生成、导航、翻译、包围
            L.Debug("editor.actions.registered", null, ("count", 6));
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

        /// <summary>UE0011 fix: replaces the deprecated name under the squiggle with the replacement the message names (one undo step).</summary>
        private static async Task ReplaceDeprecatedAsync(ActionContext ctx)
        {
            await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
            var fix = ctx.Get<DiagnosticFix>(RefactorKeys.Argument);
            var view = ctx.Get<IWpfTextView>(RefactorKeys.View) ?? EditorServices.ActiveView();
            if (fix == null || view == null || view.IsClosed) return;
            var snapshot = view.TextBuffer.CurrentSnapshot;
            string result = "applied";
            try
            {
                if (fix.Line < 0 || fix.Line >= snapshot.LineCount) { result = "stale"; return; }
                var line = snapshot.GetLineFromLineNumber(fix.Line);
                if (fix.Column < 0 || fix.Column + fix.Length > line.Length) { result = "stale"; return; }
                var span = new Span(line.Start.Position + fix.Column, fix.Length);
                if (!string.Equals(snapshot.GetText(span), fix.Old, StringComparison.Ordinal)) { result = "stale"; return; }
                if (view.TextBuffer.IsReadOnly(span)) { result = "readonly"; VsStatusBar.SetText(EditorFeatureStrings.SurroundReadOnly); return; }
                using (var edit = view.TextBuffer.CreateEdit())
                {
                    edit.Replace(span, fix.New);
                    edit.Apply();
                }
                VsStatusBar.SetText(EditorFeatureStrings.ReplacedDeprecated(fix.Old, fix.New));
            }
            catch (Exception ex)
            {
                result = "failed";
                L.Warn("editor.deprecation.fix.failed", "Replacing a deprecated API failed", ex, ("old", fix.Old), ("new", fix.New));
            }
            finally
            {
                L.Info("editor.deprecation.fix", null, ("old", fix.Old), ("new", fix.New), ("result", result));
            }
        }

        /// <summary>UE0012 fix: the existing Rename (project-wide preview) with the conventional name pre-filled.</summary>
        private static Task FixNamingAsync(ActionContext ctx)
        {
            var fix = ctx.Get<DiagnosticFix>(RefactorKeys.Argument);
            if (fix == null) return Task.CompletedTask;
            ctx.Properties[EditorFeatureKeys.PresetName] = fix.New;
            L.Info("editor.naming.fix", null, ("name", fix.Old), ("expected", fix.New));
            return RenameFeature.ExecuteAsync(ctx);
        }
    }
}
