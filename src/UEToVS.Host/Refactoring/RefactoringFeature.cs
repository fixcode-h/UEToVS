using System;
using System.Collections.Generic;
using System.ComponentModel.Design;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Text.Editor;
using UEToVS.Contracts;
using UEToVS.Core.Diagnostics;
using UEToVS.Host.Diagnostics;
using UEToVS.Host.Editor;
using UEToVS.Host.IntelliSense;
using UEToVS.Host.Logic.Actions;
using UEToVS.Host.Logic.Commands;
using UEToVS.Host.Logic.Localization;
using UEToVS.Host.Logic.Refactoring;
using UEToVS.Host.Navigation;
using UEToVS.Host.Shell;
using UEToVS.UI.Patterns;

namespace UEToVS.Host.Refactoring
{
    /// <summary>
    /// M4 refactoring entry point (§6.6, §7.4). Registers the refactoring actions in the action hub (Find Action lists
    /// them), the context-action providers (<see cref="Hub"/>: Alt+Enter menu and the VS light bulb show the same items),
    /// and the commands (Alt+Enter, Rename, Implement Virtual; 0x0700–0x07FF). Everything is enabled only while UEToVS
    /// IntelliSense is in effect (Unreal solution + VS C++ IntelliSense taken over); otherwise Alt+Enter falls back to VS's
    /// own quick actions so the key keeps working in every editor.
    /// </summary>
    internal static class RefactoringFeature
    {
        private static readonly Logger L = Log.For("Refactor");
        private static ActionRegistry _registry;
        private static ContextMenuPopup _menu;

        /// <summary>Context-action providers (Alt+Enter / light bulb). Other features may register more.</summary>
        public static ContextActionHub Hub { get; } = new ContextActionHub();

        public static IReadOnlyList<(int CommandId, string ActionId, Func<string> Text)> Commands { get; } = new (int, string, Func<string>)[]
        {
            (CommandCatalog.QuickActions, RefactorActionIds.QuickActions, () => RefactorStrings.CmdQuickActions),
            (CommandCatalog.Rename, RefactorActionIds.Rename, () => RefactorStrings.CmdRename),
            (CommandCatalog.ImplementVirtual, RefactorActionIds.ImplementVirtual, () => RefactorStrings.CmdImplementVirtual),
        };

        /// <summary>UEToVS IntelliSense is in effect: Unreal solution and VS's own C++ IntelliSense taken over.</summary>
        public static bool IsActive => IntelliSenseContext.IsUnrealSolution && IntelliSenseContext.TakenOver;

        public static void Initialize(HostPackage package, ActionRegistry registry, CommandRouter router, OleMenuCommandService mcs)
        {
            _registry = registry;
            foreach (var p in RefactorProviders.All) Hub.Register(p);

            bool Editing(ActionContext ctx) => IsActive;
            registry.Register(new DelegateAction(RefactorActionIds.QuickActions, RefactorStrings.CmdQuickActions, ActionCategory.Refactoring,
                ctx => ShowQuickActionsAsync(ctx.Source), null, null, RefactorStrings.KwQuickActions));
            registry.Register(new DelegateAction(RefactorActionIds.CreateImplementation, RefactorStrings.CmdCreateImplementation, ActionCategory.Generate,
                CreateMemberFeature.CreateImplementationAsync, Editing, null, RefactorStrings.KwCreateImplementation));
            registry.Register(new DelegateAction(RefactorActionIds.CreateDeclaration, RefactorStrings.CmdCreateDeclaration, ActionCategory.Generate,
                CreateMemberFeature.CreateDeclarationAsync, Editing, null, RefactorStrings.KwCreateDeclaration));
            registry.Register(new DelegateAction(RefactorActionIds.ImplementVirtual, RefactorStrings.CmdImplementVirtual, ActionCategory.Generate,
                ImplementVirtualFeature.ExecuteAsync, Editing, null, RefactorStrings.KwImplementVirtual));
            registry.Register(new DelegateAction(RefactorActionIds.AddInclude, RefactorStrings.CmdAddInclude, ActionCategory.Fix,
                AddIncludeFeature.ExecuteAsync, Editing, null, RefactorStrings.KwAddInclude));
            registry.Register(new DelegateAction(RefactorActionIds.Rename, RefactorStrings.CmdRename, ActionCategory.Refactoring,
                RenameFeature.ExecuteAsync, Editing, null, RefactorStrings.KwRename));
            if (mcs != null && router != null) router.RegisterMap(mcs, Commands);
            // ---- v0.4.5 refactoring batch 2 (Change Signature, Move Implementation, Forward Declaration, Introduce Variable, Extract Method, Encapsulate Field)
            Batch2.RefactoringBatch2.Initialize(registry, Hub, router, mcs);
            L.Info("refactor.registered", null, ("actions", 6), ("providers", Hub.Count), ("commands", Commands.Count));
        }

        // ---------------------------------------------------------------- context

        /// <summary>Engine context for the caret of <paramref name="view"/> (null when the view is not a C++ editor or the engine is down).</summary>
        public static async Task<RefactorContext> BuildContextAsync(IWpfTextView view, bool checkIncludes, CancellationToken ct,
                                                                    Action<RefactoringContextRequest> configure = null)
        {
            await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync(ct);
            if (view == null || view.IsClosed || !EditorServices.IsCpp(view.TextBuffer)) return null;
            var loc = EditorServices.Location(view);
            if (loc == null) return null;
            var snapshot = view.TextBuffer.CurrentSnapshot;
            long version = snapshot.Version.VersionNumber;
            var selection = Batch2.RefactoringBatch2.Selection(view, snapshot); // v0.4.5 batch 2
            var caret = await EditorServices.CaretRequestAsync(view);
            if (caret == null) return null;
            var engine = IntelliSenseContext.Engine;
            RefactoringContextResponse resp = null;
            if (engine != null && engine.IsConnected)
            {
                var request = new RefactoringContextRequest { Caret = caret, CheckIncludes = checkIncludes };
                Batch2.RefactoringBatch2.Prepare(request, snapshot, selection);
                configure?.Invoke(request);
                var r = await engine.GetRefactoringContextAsync(request, ct);
                if (r.Ok) resp = r.Value;
            }
            var context = new RefactorContext
            {
                FilePath = loc.FilePath, Line = loc.Line, Column = loc.Column, Version = version, Engine = resp,
                IsProjectFile = IsProjectPath(loc.FilePath),
                Diagnostics = CaretDiagnostics(view, loc.Line, loc.Column), // v0.4.5 编辑体验
            };
            Batch2.RefactoringBatch2.Attach(context, view, snapshot, selection);
            return context;
        }

        /// <summary>Squiggles that have an Alt+Enter / light bulb fix (v0.4.5 UE0011 / UE0012, v0.5.12 UE0002–UE0006).</summary>
        private static readonly string[] FixableCodes =
        {
            CodeDiagnosticCodes.DeprecatedApi, CodeDiagnosticCodes.NamingConvention,
            CodeDiagnosticCodes.MissingModuleDependency, CodeDiagnosticCodes.MissingGeneratedBody, CodeDiagnosticCodes.GeneratedFunctionBody,
            CodeDiagnosticCodes.MissingRepNotify, CodeDiagnosticCodes.UnregisteredReplicatedProperty,
        };

        /// <summary>v0.4.5: the squiggles under the caret (current snapshot coordinates) for the Alt+Enter fixes.</summary>
        private static List<CodeDiagnostic> CaretDiagnostics(IWpfTextView view, int line, int column)
        {
            try
            {
                var tagger = CodeDiagnosticsTagger.For(view);
                if (tagger == null) return new List<CodeDiagnostic>();
                var snapshot = view.TextBuffer.CurrentSnapshot;
                var moved = new List<CodeDiagnostic>();
                foreach (var (item, span) in tagger.Spans(snapshot, FixableCodes))
                {
                    var l = span.Start.GetContainingLine();
                    moved.Add(new CodeDiagnostic
                    {
                        Code = item.Code, Severity = item.Severity, Args = item.Args, Message = item.Message,
                        Line = l.LineNumber, Column = span.Start.Position - l.Start.Position, Length = span.Length,
                    });
                }
                return Logic.Editing.EditorFixProvider.At(moved, line, column);
            }
            catch (Exception ex)
            {
                L.Warn("refactor.context.diagnostics.failed", "Reading the squiggles at the caret failed", ex);
                return new List<CodeDiagnostic>();
            }
        }

        /// <summary>Editable = inside the solution / project folder and not under the engine root.</summary>
        public static bool IsProjectPath(string path)
        {
            var package = HostPackage.Instance;
            if (string.IsNullOrEmpty(path) || package == null) return true;
            string engineRoot = package.Engine?.LastStatus?.EngineRoot;
            if (!string.IsNullOrEmpty(engineRoot) && path.StartsWith(engineRoot.TrimEnd('\\', '/') + "\\", StringComparison.OrdinalIgnoreCase)) return false;
            if (!string.IsNullOrEmpty(engineRoot) && path.StartsWith(engineRoot.TrimEnd('\\', '/') + "/", StringComparison.OrdinalIgnoreCase)) return false;
            return true;
        }

        /// <summary>
        /// The view and context an action runs on: the ones it was offered for (Alt+Enter / light bulb), else the active C++
        /// editor's caret (Find Action, key binding). Reports why when there is none.
        /// </summary>
        public static async Task<(IWpfTextView View, RefactorContext Context)> ResolveAsync(ActionContext ctx, bool checkIncludes = false)
        {
            await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
            var view = ctx?.Get<IWpfTextView>(RefactorKeys.View);
            var rc = ctx?.Get<RefactorContext>(RefactorKeys.Context);
            if (view != null && rc != null && !view.IsClosed && rc.Version == view.TextBuffer.CurrentSnapshot.Version.VersionNumber) return (view, rc);
            view = EditorServices.ActiveView();
            if (view == null || !EditorServices.IsCpp(view.TextBuffer))
            {
                VsStatusBar.SetText(NavStrings.NoActiveCppEditor);
                return (null, null);
            }
            var engine = IntelliSenseContext.Engine;
            if (engine == null || !engine.IsConnected)
            {
                VsStatusBar.SetText(NavStrings.EngineUnavailable);
                return (null, null);
            }
            rc = await BuildContextAsync(view, checkIncludes, ctx?.CancellationToken ?? CancellationToken.None);
            if (rc?.Engine == null) VsStatusBar.SetText(NavStrings.EngineUnavailable);
            return (view, rc?.Engine == null ? null : rc);
        }

        /// <summary>Runs a context item through the action hub (logged and timed there).</summary>
        public static void Execute(ContextActionItem item, IWpfTextView view, RefactorContext context, ActionSource source)
        {
            var package = HostPackage.Instance;
            if (package == null || _registry == null || item == null) return;
            var ctx = package.CreateActionContext(source);
            ctx.ActiveDocumentPath = context?.FilePath;
            ctx.CaretLine = context?.Line ?? -1;
            ctx.CaretColumn = context?.Column ?? -1;
            ctx.Properties[RefactorKeys.Context] = context;
            ctx.Properties[RefactorKeys.View] = view;
            ctx.Properties[RefactorKeys.Argument] = item.Argument;
            L.Info("refactor.item.invoked", null, ("action", item.ActionId), ("provider", item.Provider), ("source", source.ToString()), ("title", item.Title));
            Safe.RunAsync(L, "refactor.item", async () =>
            {
                bool ok = await _registry.ExecuteAsync(item.ActionId, ctx);
                if (!ok) VsStatusBar.SetText(Strings.ActionFailed(item.Title));
            }, ("action", item.ActionId));
        }

        // ---------------------------------------------------------------- Alt+Enter

        private static async Task ShowQuickActionsAsync(ActionSource source)
        {
            await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
            var view = EditorServices.ActiveView();
            if (view == null || !EditorServices.IsCpp(view.TextBuffer) || !IsActive)
            {
                // VS's own quick actions (C#, XAML, or C++ while VS IntelliSense is still on)
                bool fallback = VsEditor.TryExecute("View.QuickActions");
                L.Info("refactor.quickActions.fallback", null, ("cpp", view != null && EditorServices.IsCpp(view.TextBuffer)), ("active", IsActive), ("vsQuickActions", fallback));
                if (!fallback && view != null && EditorServices.IsCpp(view.TextBuffer)) VsStatusBar.SetText(RefactorStrings.NotActive);
                return;
            }
            var sw = Stopwatch.StartNew();
            var context = await BuildContextAsync(view, true, NavigationContext.Disposal);
            await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
            if (context?.Engine == null)
            {
                VsStatusBar.SetText(NavStrings.EngineUnavailable);
                return;
            }
            var items = Hub.Collect(context);
            L.Info("refactor.quickActions", null, ("source", source.ToString()), ("word", context.Word), ("items", items.Count),
                ("actions", string.Join(",", items.Select(i => i.ActionId.Substring(i.ActionId.LastIndexOf('.') + 1)))), ("ms", sw.ElapsedMilliseconds),
                ("file", System.IO.Path.GetFileName(context.FilePath)), ("line", context.Line));
            if (items.Count == 0)
            {
                VsStatusBar.SetText(RefactorStrings.NoActions);
                return;
            }
            if (context.Version != view.TextBuffer.CurrentSnapshot.Version.VersionNumber) return; // typed meanwhile
            // one header per group (groups in order of their best item)
            var entries = items.GroupBy(i => i.Group ?? string.Empty).SelectMany(g => g).Select(i => new MenuEntry(i.ActionId + "|" + i.Title, () => i.Title)
            {
                Group = string.IsNullOrEmpty(i.Group) ? null : (Func<string>)(() => i.Group),
                Shortcut = ShortcutOf(i.ActionId),
                Tag = i,
            }).ToList();
            if (_menu == null) _menu = new ContextMenuPopup("uetovs.refactor.quickActions");
            _menu.OwnerHandle = UEToVS.Host.Integration.VsMainWindow.Handle;
            var chosen = await _menu.ShowAsync(entries, EditorServices.AnchorAt(view));
            if (chosen?.Tag is ContextActionItem item) Execute(item, view, context, ActionSource.AltEnter);
        }

        internal static string ShortcutOf(string actionId)
        {
            var c = CommandCatalog.All.FirstOrDefault(x => x.ActionId == actionId);
            return c == null ? null : Shortcut.Display(c.VaKeys);
        }
    }
}
