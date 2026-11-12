using System;
using System.Collections.Generic;
using System.Linq;
using UEToVS.Contracts;
using UEToVS.Host.Logic.Localization;

namespace UEToVS.Host.Logic.Refactoring
{
    /// <summary>
    /// The built-in context-action providers (pure: they read the <see cref="RefactorContext"/> only). Each offers one
    /// refactoring action of the hub; the action itself does the work (and re-checks, since Find Action calls it without a
    /// provider).
    /// </summary>
    public static class RefactorProviders
    {
        public static IEnumerable<IContextActionProvider> All { get; } = new IContextActionProvider[]
        {
            new CreateImplementationProvider(),
            new CreateDeclarationProvider(),
            new AddIncludeProvider(),
            new ImplementVirtualProvider(),
            new RenameProvider(),
        };

        internal static bool IsProject(SymbolInfo s) => s != null && RenamePlanner.IsProjectOrigin(s.Origin);

        internal static bool IsUeGenerated(string name) =>
            name != null && (name.EndsWith("_Implementation", StringComparison.Ordinal) || name.EndsWith("_Validate", StringComparison.Ordinal));

        /// <summary>A header declaration without a body anywhere (or, for RPC / BlueprintNativeEvent, missing _Implementation / _Validate).</summary>
        public static bool NeedsImplementation(RefactorContext c, out UeImplementationKinds ue)
        {
            ue = null;
            var f = c?.Function;
            if (f == null || f.IsDefinition || !c.IsProjectFile) return false;
            if ((f.Modifiers & FunctionModifiers.Pure) != 0) return false;
            if (f.Kind != SymbolKind.Method && f.Kind != SymbolKind.Function && f.Kind != SymbolKind.Constructor && f.Kind != SymbolKind.Destructor) return false;
            ue = SignatureText.UeKinds(f);
            if (ue.Implementation)
            {
                var defs = c.Engine.UeCounterpartDefinitions ?? new List<SymbolInfo>();
                bool hasImpl = defs.Any(d => d.Name == f.Name + "_Implementation");
                bool hasValidate = defs.Any(d => d.Name == f.Name + "_Validate");
                return !hasImpl || (ue.Validate && !hasValidate);
            }
            if ((f.Ue & UeFlags.UFunction) != 0 && SignatureText.HasSpecifier(f.Specifiers, "BlueprintImplementableEvent")) return false;
            return (c.Engine.Definitions?.Count ?? 0) == 0;
        }

        private sealed class CreateImplementationProvider : IContextActionProvider
        {
            public string Id => "createImplementation";

            public IEnumerable<ContextActionItem> GetActions(RefactorContext c)
            {
                if (!NeedsImplementation(c, out var ue)) yield break;
                string title = ue.Implementation
                    ? RefactorStrings.CreateUeImplementationOf(c.Function.Name, ue.Validate ? "_Implementation / _Validate" : "_Implementation")
                    : RefactorStrings.CreateImplementationOf(c.Function.Name);
                yield return new ContextActionItem(RefactorActionIds.CreateImplementation, title, 100) { Group = RefactorStrings.GroupGenerate };
            }
        }

        /// <summary>An out-of-line member definition in a source file whose class declares no such member.</summary>
        public static bool NeedsDeclaration(RefactorContext c)
        {
            var f = c?.Function;
            if (f == null || !f.IsDefinition || string.IsNullOrEmpty(f.Container)) return false;
            if (c.IsHeader && c.Engine.Class != null && c.Engine.Class.Symbol?.Location?.FilePath != null &&
                string.Equals(c.Engine.Class.Symbol.Location.FilePath, c.FilePath, StringComparison.OrdinalIgnoreCase) && f.Location != null &&
                f.Location.Line > c.Engine.Class.BodyStartLine && f.Location.Line < c.Engine.Class.BodyEndLine) return false; // inline in the class
            if (IsUeGenerated(f.Name)) return false;
            if ((c.Engine.Declarations?.Count ?? 0) > 0) return false;
            var cls = c.Engine.Class;
            return cls?.Symbol != null && IsProject(cls.Symbol);
        }

        private sealed class CreateDeclarationProvider : IContextActionProvider
        {
            public string Id => "createDeclaration";

            public IEnumerable<ContextActionItem> GetActions(RefactorContext c)
            {
                if (!NeedsDeclaration(c)) yield break;
                yield return new ContextActionItem(RefactorActionIds.CreateDeclaration, RefactorStrings.CreateDeclarationOf(c.Function.Name), 100)
                    { Group = RefactorStrings.GroupGenerate };
            }
        }

        /// <summary>The type at the caret lives in a header this file does not (provably) include.</summary>
        public static bool NeedsInclude(RefactorContext c, out string include)
        {
            include = null;
            var t = c?.Engine?.Type;
            if (t == null || string.IsNullOrEmpty(t.HeaderPath) || !c.IsProjectFile) return false;
            if (t.Include != IncludeState.Missing && t.Include != IncludeState.Unknown) return false;
            if (t.Include == IncludeState.Unknown && t.FilesVisited == 0) return false; // not checked at all
            if (string.Equals(t.HeaderPath, c.FilePath, StringComparison.OrdinalIgnoreCase)) return false;
            if (!Core.Unreal.RelatedNames.IsHeader(t.HeaderPath)) return false;
            include = IncludePlanner.UeIncludePath(t.HeaderPath);
            return !string.IsNullOrEmpty(include);
        }

        private sealed class AddIncludeProvider : IContextActionProvider
        {
            public string Id => "addInclude";

            public IEnumerable<ContextActionItem> GetActions(RefactorContext c)
            {
                if (!NeedsInclude(c, out var include)) yield break;
                // Unknown (walk bound reached) ranks lower than a known-missing include
                int prio = c.Engine.Type.Include == IncludeState.Missing ? 110 : 60;
                yield return new ContextActionItem(RefactorActionIds.AddInclude, RefactorStrings.AddIncludeFor(include), prio, include) { Group = RefactorStrings.GroupFix };
            }
        }

        private sealed class ImplementVirtualProvider : IContextActionProvider
        {
            public string Id => "implementVirtual";

            public IEnumerable<ContextActionItem> GetActions(RefactorContext c)
            {
                var cls = c?.Engine?.Class;
                if (cls?.Symbol == null || !IsProject(cls.Symbol) || cls.Bases == null || cls.Bases.Count == 0) yield break;
                yield return new ContextActionItem(RefactorActionIds.ImplementVirtual, RefactorStrings.ImplementVirtualIn(cls.Symbol.Name), 50)
                    { Group = RefactorStrings.GroupGenerate };
            }
        }

        /// <summary>A project symbol (type, function, variable, field, enum value) resolved at the caret.</summary>
        public static bool CanRename(RefactorContext c)
        {
            var s = c?.Symbol;
            if (s == null || string.IsNullOrEmpty(c.Word) || !string.Equals(s.Name, c.Word, StringComparison.Ordinal)) return false;
            if (!IsProject(s) || s.Kind == SymbolKind.Macro || s.Kind == SymbolKind.Namespace || s.Kind == SymbolKind.Unknown) return false;
            if (IsUeGenerated(s.Name)) return false;
            return RenamePlanner.IsValidIdentifier(s.Name);
        }

        private sealed class RenameProvider : IContextActionProvider
        {
            public string Id => "rename";

            public IEnumerable<ContextActionItem> GetActions(RefactorContext c)
            {
                if (!CanRename(c)) yield break;
                yield return new ContextActionItem(RefactorActionIds.Rename, RefactorStrings.RenameSymbol(c.Symbol.Name), 40) { Group = RefactorStrings.GroupRefactor };
            }
        }
    }
}
