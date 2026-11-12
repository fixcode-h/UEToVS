using System;
using System.Collections.Generic;
using UEToVS.Contracts;
using UEToVS.Core.Diagnostics;
using UEToVS.Host.Logic.Actions;
using UEToVS.Host.Logic.Localization;

namespace UEToVS.Host.Navigation
{
    /// <summary>VSCT command ids of the M2 navigation commands (range 0x0200–0x02FF). Must match UEToVSHostPackage.vsct.</summary>
    internal static class NavigationCommandIds
    {
        public const int GoToDefinition = 0x0200;
        public const int GoToImplementation = 0x0201;
        public const int FindReferences = 0x0202;
        public const int TypeHierarchy = 0x0203;
        public const int ListMembersInFile = 0x0204;
        public const int ListTypeMembers = 0x0205;
        // ---- v0.4.5 navigation gaps (0x0210–0x022F)
        public const int CallHierarchy = 0x0210;
    }

    /// <summary>Stable action ids of the navigation features (§7.4).</summary>
    internal static class NavigationActionIds
    {
        public const string GoToDefinition = "uetovs.navigation.goToDefinition";
        public const string GoToImplementation = "uetovs.navigation.goToImplementation";
        public const string FindReferences = "uetovs.navigation.findReferences";
        public const string TypeHierarchy = "uetovs.navigation.typeHierarchy";
        public const string ListMembersInFile = "uetovs.navigation.listMembersInFile";
        public const string ListTypeMembers = "uetovs.navigation.listTypeMembers";
        // ---- v0.4.5 navigation gaps
        public const string CallHierarchy = "uetovs.navigation.callHierarchy";
        /// <summary>Find Action only (no VSCT command): Alt+F12 stays VS's Edit.PeekDefinition, which PeekDefinitionCommandFilter serves after the takeover.</summary>
        public const string PeekDefinition = "uetovs.navigation.peekDefinition";
    }

    /// <summary>
    /// Registers the navigation actions in the action hub (Find Action lists them) and maps the VSCT commands to them
    /// (<see cref="Map"/>, bound by CommandRouter). Available in Unreal solutions; the features report precisely in the status
    /// bar when the editor / engine cannot serve them.
    /// </summary>
    internal static class NavigationActions
    {
        private static readonly Logger L = Log.For("Navigation");

        /// <summary>command id → (action id, localized menu text). Default keys are in the VSCT (KeyBindings).</summary>
        public static IReadOnlyList<(int CommandId, string ActionId, Func<string> Text)> Map { get; } = new (int, string, Func<string>)[]
        {
            (NavigationCommandIds.GoToDefinition, NavigationActionIds.GoToDefinition, () => NavStrings.CmdGoToDefinition),
            (NavigationCommandIds.GoToImplementation, NavigationActionIds.GoToImplementation, () => NavStrings.CmdGoToImplementation),
            (NavigationCommandIds.FindReferences, NavigationActionIds.FindReferences, () => NavStrings.CmdFindReferences),
            (NavigationCommandIds.TypeHierarchy, NavigationActionIds.TypeHierarchy, () => NavStrings.CmdTypeHierarchy),
            (NavigationCommandIds.ListMembersInFile, NavigationActionIds.ListMembersInFile, () => NavStrings.CmdListMembersInFile),
            (NavigationCommandIds.ListTypeMembers, NavigationActionIds.ListTypeMembers, () => NavStrings.CmdListTypeMembers),
            // ---- v0.4.5 navigation gaps
            (NavigationCommandIds.CallHierarchy, NavigationActionIds.CallHierarchy, () => NavGapStrings.CmdCallHierarchy),
        };

        public static void Register(ActionRegistry registry)
        {
            NavigationContext.EnsureHooked();
            bool Available(ActionContext ctx) => ctx.IsUnrealSolution;
            int EditorFirst(ActionContext ctx) => ctx.Source == ActionSource.FindAction ? 5 : 0;
            string Src(ActionContext ctx) => ctx.Source.ToString().ToLowerInvariant();

            registry.Register(new DelegateAction(NavigationActionIds.GoToDefinition, NavStrings.CmdGoToDefinition, ActionCategory.Navigation,
                ctx => GoToDefinitionFeature.ExecuteAsync(NavigationKind.Toggle, Src(ctx)), Available, EditorFirst, NavStrings.KwGoToDefinition));
            registry.Register(new DelegateAction(NavigationActionIds.GoToImplementation, NavStrings.CmdGoToImplementation, ActionCategory.Navigation,
                ctx => GoToDefinitionFeature.ExecuteAsync(NavigationKind.Implementation, Src(ctx)), Available, EditorFirst, NavStrings.KwGoToImplementation));
            registry.Register(new DelegateAction(NavigationActionIds.FindReferences, NavStrings.CmdFindReferences, ActionCategory.Navigation,
                ctx => FindReferencesFeature.ExecuteAsync(Src(ctx)), Available, EditorFirst, NavStrings.KwFindReferences));
            registry.Register(new DelegateAction(NavigationActionIds.TypeHierarchy, NavStrings.CmdTypeHierarchy, ActionCategory.Navigation,
                ctx => TypeHierarchyFeature.ExecuteAsync(Src(ctx)), Available, EditorFirst, NavStrings.KwTypeHierarchy));
            registry.Register(new DelegateAction(NavigationActionIds.ListMembersInFile, NavStrings.CmdListMembersInFile, ActionCategory.Navigation,
                ctx => MemberListFeature.ListMembersInFileAsync(Src(ctx)), Available, EditorFirst, NavStrings.KwMembers));
            registry.Register(new DelegateAction(NavigationActionIds.ListTypeMembers, NavStrings.CmdListTypeMembers, ActionCategory.Navigation,
                ctx => MemberListFeature.ListTypeMembersAsync(Src(ctx)), Available, EditorFirst, NavStrings.KwMembers));
            // ---- v0.4.5 navigation gaps
            registry.Register(new DelegateAction(NavigationActionIds.CallHierarchy, NavGapStrings.CmdCallHierarchy, ActionCategory.Navigation,
                ctx => CallHierarchyFeature.ExecuteAsync(Src(ctx)), Available, EditorFirst, NavGapStrings.KwCallHierarchy));
            registry.Register(new DelegateAction(NavigationActionIds.PeekDefinition, NavGapStrings.CmdPeekDefinition, ActionCategory.Navigation,
                ctx => PeekDefinitionFeature.ExecuteAsync(Src(ctx)), Available, EditorFirst, NavGapStrings.KwPeekDefinition));
            L.Info("nav.actions.registered", null, ("count", Map.Count));
        }
    }
}
