using System;
using System.Collections.Generic;
using System.Linq;
using UEToVS.Contracts;
using UEToVS.Host.Logic.Localization;

namespace UEToVS.Host.Logic.Navigation
{
    /// <summary>An entry of the navigation bar's type list: a type declared in the file, or a class whose members are defined here.</summary>
    public sealed class ScopeEntry
    {
        /// <summary>Type name ("" = global scope).</summary>
        public string Name { get; set; }
        public string Container { get; set; }
        public SymbolInfo Symbol { get; set; }

        /// <summary>Where selecting it jumps (the declaration, or the first member defined in this file).</summary>
        public SourceLocation Location { get; set; }

        public bool IsGlobal => string.IsNullOrEmpty(Name);

        public string Display => IsGlobal ? NavStrings.NavBarGlobalScope : Name;

        /// <summary>Qualified key matching SymbolInfo.Container of its members ("Outer::Inner" or "Name").</summary>
        public string Key => string.IsNullOrEmpty(Container) || IsGlobal ? Name ?? string.Empty : Container + "::" + Name;
    }

    /// <summary>
    /// Navigation bar model (left: current type, right: current function) and the member lists (Alt+M), computed from
    /// <see cref="CaretContextResponse"/> / <see cref="MembersResponse"/>. Pure.
    /// </summary>
    public static class ScopeModel
    {
        /// <summary>Innermost enclosing type of the caret, or null.</summary>
        public static SymbolInfo CurrentType(CaretContextResponse ctx) =>
            ctx?.Scopes?.LastOrDefault(s => s != null && SymbolPresentation.IsType(s.Kind) && s.Kind != SymbolKind.Typedef);

        /// <summary>Innermost enclosing function of the caret, or null.</summary>
        public static SymbolInfo CurrentFunction(CaretContextResponse ctx) =>
            ctx?.Scopes?.LastOrDefault(s => s != null && SymbolPresentation.IsFunction(s.Kind));

        /// <summary>
        /// Key of the type the caret is in: the enclosing type, else the container of the enclosing out-of-line function
        /// ("AMyActor" for AMyActor::BeginPlay in a .cpp), else "" (global).
        /// </summary>
        public static string CurrentTypeKey(CaretContextResponse ctx)
        {
            var t = CurrentType(ctx);
            if (t != null) return Qualify(t);
            return CurrentFunction(ctx)?.Container ?? string.Empty;
        }

        /// <summary>Types for the left drop-down: declared types + containers of members defined here + global (if used).</summary>
        public static List<ScopeEntry> TypeEntries(CaretContextResponse ctx)
        {
            var result = new List<ScopeEntry>();
            var byKey = new Dictionary<string, ScopeEntry>(StringComparer.Ordinal);
            bool hasGlobal = false;
            foreach (var s in ctx?.FileSymbols ?? Enumerable.Empty<SymbolInfo>())
            {
                if (s == null) continue;
                if (SymbolPresentation.IsType(s.Kind) && s.Kind != SymbolKind.Typedef)
                {
                    string key = Qualify(s);
                    if (byKey.TryGetValue(key, out var existing))
                    {
                        if (existing.Symbol == null) { existing.Symbol = s; existing.Location = s.Location; }
                        continue;
                    }
                    var e = new ScopeEntry { Name = s.Name, Container = s.Container, Symbol = s, Location = s.Location };
                    byKey.Add(key, e);
                    result.Add(e);
                }
                else if (IsMember(s))
                {
                    string c = s.Container ?? string.Empty;
                    if (c.Length == 0)
                    {
                        hasGlobal = true;
                        continue;
                    }
                    if (byKey.ContainsKey(c)) continue;
                    int sep = c.LastIndexOf("::", StringComparison.Ordinal);
                    var e = new ScopeEntry
                    {
                        Name = sep < 0 ? c : c.Substring(sep + 2),
                        Container = sep < 0 ? null : c.Substring(0, sep),
                        Location = s.Location,
                    };
                    byKey.Add(c, e);
                    result.Add(e);
                }
            }
            if (hasGlobal) result.Insert(0, new ScopeEntry { Name = string.Empty });
            return result;
        }

        /// <summary>Members (functions, fields, enum values, delegates) of the scope with <paramref name="typeKey"/>, in source order.</summary>
        public static List<SymbolInfo> MembersOf(CaretContextResponse ctx, string typeKey)
        {
            typeKey = typeKey ?? string.Empty;
            return (ctx?.FileSymbols ?? Enumerable.Empty<SymbolInfo>())
                .Where(s => s != null && IsMember(s) && string.Equals(s.Container ?? string.Empty, typeKey, StringComparison.Ordinal))
                .ToList();
        }

        public static bool IsMember(SymbolInfo s) =>
            s != null && (SymbolPresentation.IsFunction(s.Kind) || s.Kind == SymbolKind.Field || s.Kind == SymbolKind.Variable ||
                          s.Kind == SymbolKind.EnumValue || s.Kind == SymbolKind.Delegate);

        public static string Qualify(SymbolInfo s) =>
            s == null ? string.Empty : string.IsNullOrEmpty(s.Container) ? s.Name ?? string.Empty : s.Container + "::" + s.Name;

        /// <summary>Text for the right box: "BeginPlay()" style name of the current function, or the "(no function)" hint.</summary>
        public static string FunctionDisplay(SymbolInfo f) => f == null ? NavStrings.NavBarNoFunction : f.Name ?? string.Empty;

        /// <summary>Alt+M (file): declarations in source order, macros and namespaces excluded, deduplicated by name+line.</summary>
        public static List<SymbolInfo> FileMemberList(IEnumerable<SymbolInfo> fileSymbols)
        {
            var seen = new HashSet<(string, int)>();
            var list = new List<SymbolInfo>();
            foreach (var s in fileSymbols ?? Enumerable.Empty<SymbolInfo>())
            {
                if (s == null || s.Kind == SymbolKind.Namespace || s.Kind == SymbolKind.Macro || string.IsNullOrEmpty(s.Name)) continue;
                if (!seen.Add((Qualify(s), s.Location?.Line ?? -1))) continue;
                list.Add(s);
            }
            return list;
        }

        /// <summary>
        /// Alt+Shift+M (type): own members first (source order of the engine), then inherited ones grouped by declaring
        /// type in the engine's base order.
        /// </summary>
        public static List<SymbolInfo> TypeMemberList(MembersResponse r)
        {
            if (r?.Members == null) return new List<SymbolInfo>();
            string own = r.Type == null ? null : Qualify(r.Type);
            var order = new List<string>();
            var groups = new Dictionary<string, List<SymbolInfo>>(StringComparer.Ordinal);
            foreach (var m in r.Members)
            {
                if (m == null || string.IsNullOrEmpty(m.Name)) continue;
                string c = m.Container ?? string.Empty;
                if (!groups.TryGetValue(c, out var g)) { g = new List<SymbolInfo>(); groups.Add(c, g); order.Add(c); }
                g.Add(m);
            }
            var result = new List<SymbolInfo>();
            if (own != null && groups.TryGetValue(own, out var ownList)) result.AddRange(ownList);
            else if (own != null && r.Type != null && groups.TryGetValue(r.Type.Name ?? string.Empty, out var byName)) { result.AddRange(byName); own = r.Type.Name; }
            foreach (var c in order)
                if (!string.Equals(c, own, StringComparison.Ordinal)) result.AddRange(groups[c]);
            return result;
        }

        /// <summary>True when a member came from a base type (shown with "from X").</summary>
        public static bool IsInherited(SymbolInfo member, SymbolInfo type)
        {
            if (member == null || type == null) return false;
            string c = member.Container ?? string.Empty;
            return !string.Equals(c, Qualify(type), StringComparison.Ordinal) && !string.Equals(c, type.Name ?? string.Empty, StringComparison.Ordinal);
        }

        /// <summary>Filter text for member lists (the chooser matches the typed text as a subsequence of the name).</summary>
        public static string FilterText(SymbolInfo s) => s == null ? string.Empty : s.Name ?? string.Empty;
    }
}
