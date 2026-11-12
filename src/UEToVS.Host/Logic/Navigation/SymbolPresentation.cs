using System;
using UEToVS.Contracts;
using UEToVS.Host.Logic.Localization;
using BadgeKind = UEToVS.UI.Logic.BadgeKind;
using Glyph = UEToVS.UI.Logic.SymbolKind;

namespace UEToVS.Host.Logic.Navigation
{
    /// <summary>
    /// How contract symbols look in UEToVS.UI rows: glyph by kind, UE badge, "Container::Name" title, "File.h:120"
    /// location, role / origin / confidence texts. Pure (the UI enums it maps to are WPF-free).
    /// </summary>
    public static class SymbolPresentation
    {
        public static Glyph GlyphFor(SymbolInfo s)
        {
            if (s == null) return Glyph.Unknown;
            if ((s.Ue & UeFlags.UInterface) != 0 && (s.Kind == SymbolKind.Class || s.Kind == SymbolKind.Struct)) return Glyph.Interface;
            return GlyphFor(s.Kind);
        }

        public static Glyph GlyphFor(SymbolKind kind)
        {
            switch (kind)
            {
                case SymbolKind.Namespace: return Glyph.Namespace;
                case SymbolKind.Class: return Glyph.Class;
                case SymbolKind.Struct:
                case SymbolKind.Union: return Glyph.Struct;
                case SymbolKind.Enum: return Glyph.Enum;
                case SymbolKind.EnumValue: return Glyph.EnumMember;
                case SymbolKind.Typedef:
                case SymbolKind.Concept: return Glyph.Typedef;
                case SymbolKind.Function: return Glyph.Function;
                case SymbolKind.Method:
                case SymbolKind.Constructor:
                case SymbolKind.Destructor: return Glyph.Method;
                case SymbolKind.Field: return Glyph.Field;
                case SymbolKind.Variable: return Glyph.Variable;
                case SymbolKind.Macro: return Glyph.Macro;
                case SymbolKind.Delegate: return Glyph.Delegate;
                default: return Glyph.Unknown;
            }
        }

        /// <summary>Most specific UE reflection badge (UCLASS, UFUNCTION, …) or null.</summary>
        public static (string Text, BadgeKind Kind)? UeBadge(UeFlags ue)
        {
            if ((ue & UeFlags.UInterface) != 0) return ("UINTERFACE", BadgeKind.UInterface);
            if ((ue & UeFlags.UClass) != 0) return ("UCLASS", BadgeKind.UClass);
            if ((ue & UeFlags.UStruct) != 0) return ("USTRUCT", BadgeKind.UStruct);
            if ((ue & UeFlags.UEnum) != 0) return ("UENUM", BadgeKind.UEnum);
            if ((ue & UeFlags.UFunction) != 0) return ("UFUNCTION", BadgeKind.UFunction);
            if ((ue & UeFlags.UProperty) != 0) return ("UPROPERTY", BadgeKind.UProperty);
            if ((ue & UeFlags.UDelegate) != 0) return ("DELEGATE", BadgeKind.Delegate);
            return null;
        }

        public static bool IsType(SymbolKind k) =>
            k == SymbolKind.Class || k == SymbolKind.Struct || k == SymbolKind.Union || k == SymbolKind.Enum || k == SymbolKind.Typedef;

        public static bool IsFunction(SymbolKind k) =>
            k == SymbolKind.Function || k == SymbolKind.Method || k == SymbolKind.Constructor || k == SymbolKind.Destructor;

        /// <summary>"AMyActor::BeginPlay", or the name alone at global scope.</summary>
        public static string QualifiedName(SymbolInfo s)
        {
            if (s == null) return string.Empty;
            string name = s.Name ?? string.Empty;
            return string.IsNullOrEmpty(s.Container) ? name : s.Container + "::" + name;
        }

        /// <summary>"Actor.h:120" (1-based line), or the file name alone when there is no location.</summary>
        public static string ShortLocation(SourceLocation loc)
        {
            if (loc == null || string.IsNullOrEmpty(loc.FilePath)) return string.Empty;
            return SafeFileName(loc.FilePath) + ":" + (loc.Line + 1);
        }

        private static readonly char[] Separators = { '\\', '/' };

        /// <summary>Directory part (both separators, never throws on odd paths).</summary>
        public static string SafeDirectory(string path)
        {
            if (string.IsNullOrEmpty(path)) return string.Empty;
            int sep = path.LastIndexOfAny(Separators);
            return sep <= 0 ? string.Empty : path.Substring(0, sep);
        }

        /// <summary>File name part (both separators, never throws on odd paths).</summary>
        public static string SafeFileName(string path)
        {
            if (string.IsNullOrEmpty(path)) return string.Empty;
            int sep = path.LastIndexOfAny(Separators);
            return sep < 0 ? path : path.Substring(sep + 1);
        }

        public static string RoleText(NavigationTargetRole role)
        {
            switch (role)
            {
                case NavigationTargetRole.Declaration: return NavStrings.RoleDeclaration;
                case NavigationTargetRole.Definition: return NavStrings.RoleDefinition;
                case NavigationTargetRole.Override: return NavStrings.RoleOverride;
                case NavigationTargetRole.UeCounterpart: return NavStrings.RoleUeCounterpart;
                case NavigationTargetRole.DerivedType: return NavStrings.RoleDerived;
                default: return role.ToString();
            }
        }

        public static string OriginText(ItemOrigin origin)
        {
            switch (origin)
            {
                case ItemOrigin.Project: return NavStrings.OriginProject;
                case ItemOrigin.ProjectPlugin: return NavStrings.OriginProjectPlugin;
                case ItemOrigin.Engine: return NavStrings.OriginEngine;
                case ItemOrigin.EnginePlugin: return NavStrings.OriginEnginePlugin;
                case ItemOrigin.ThirdParty: return NavStrings.OriginThirdParty;
                default: return origin.ToString();
            }
        }

        public static string ConfidenceText(ResolutionConfidence c)
        {
            switch (c)
            {
                case ResolutionConfidence.Exact: return NavStrings.ConfidenceExact;
                case ResolutionConfidence.Scoped: return NavStrings.ConfidenceScoped;
                case ResolutionConfidence.NameOnly: return NavStrings.ConfidenceNameOnly;
                default: return NavStrings.ConfidenceNone;
            }
        }

        public static bool IsNonProject(ItemOrigin origin) => origin == ItemOrigin.Engine || origin == ItemOrigin.EnginePlugin || origin == ItemOrigin.ThirdParty;

        /// <summary>"Module · Engine" style secondary text: module and, for non-project items, the origin.</summary>
        public static string ModuleAndOrigin(string module, ItemOrigin origin)
        {
            string o = origin == ItemOrigin.Project ? null : OriginText(origin);
            if (string.IsNullOrEmpty(module)) return o ?? string.Empty;
            return o == null ? module : module + " · " + o;
        }
    }
}
