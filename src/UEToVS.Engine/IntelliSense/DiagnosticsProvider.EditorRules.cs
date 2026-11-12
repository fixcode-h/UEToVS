using System;
using System.Collections.Generic;
using System.Linq;
using UEToVS.Contracts;
using UEToVS.Engine.Index;
using UEToVS.Engine.Navigation;
using UEToVS.Engine.Syntax;

namespace UEToVS.Engine.IntelliSense
{
    /// <summary>Opt-in rules of a diagnostics request (v0.4.5 编辑体验; off unless the host asks).</summary>
    public sealed class DiagnosticsOptions
    {
        public static DiagnosticsOptions Default { get; } = new DiagnosticsOptions();

        public bool CheckDeprecated { get; set; }
        public bool CheckNaming { get; set; }
    }

    /// <summary>
    /// v0.4.5 编辑体验 rules, opt-in per request (<see cref="DiagnosticsOptions"/>), same contract as the others (certain or silent):
    /// <list type="bullet">
    /// <item>UE0011 use of a deprecated API in the range (<see cref="DeprecationInfo"/> decides): reported only when every candidate the
    /// resolver keeps (after overload filtering by the call's arguments) is deprecated. The replacement is offered only when the
    /// message names an identifier that exists in the same scope (a mechanical rename). Names are pre-filtered: an identifier
    /// whose index candidates are all non-deprecated is never resolved. Only the current file's visible lines, no project scan.</item>
    /// <item>UE0012 UE naming conventions of the types / enums / bool members declared in this (project) file: A for AActor
    /// subclasses, U for other UObject subclasses, S for Slate widgets, I for UINTERFACE companion classes, T for templates, F for
    /// other structs / classes, E for enums, b for bool fields. Types whose bases are not all known are skipped.</item>
    /// </list>
    /// </summary>
    public sealed partial class DiagnosticsProvider
    {
        /// <summary>Most identifier resolutions UE0011 makes per request (keeps a huge viewport bounded).</summary>
        public const int MaxDeprecationResolutions = 400;

        /// <summary>More index candidates than this and the name is resolved per occurrence instead of pre-filtered.</summary>
        private const int MaxPrefilterCandidates = 12;

        private sealed partial class Pass
        {
            // ============================================================ UE0011 deprecated API

            public void Deprecated(int start, int end, List<CodeDiagnostic> result)
            {
                var doc = _doc;
                var maybe = new Dictionary<string, bool>(StringComparer.Ordinal);
                int resolutions = 0;
                for (int k = doc.TokenIndexFrom(start); k < doc.Tokens.Length && doc.Tokens[k].Start < end; k++)
                {
                    if (!doc.IsIdentifier(k)) continue;
                    var t = doc.Tokens[k];
                    if (_caret >= t.Start && _caret <= t.End) continue; // being typed
                    if (doc.DeclAtOffset(t.Start) >= 0) continue;      // the declaration itself
                    string name = doc.TokenText(k);
                    if (TypeWords.Contains(name) || (IsMacroSpelling(name) && !name.EndsWith(DeprecationInfo.MacroSuffix, StringComparison.Ordinal))) continue;
                    if (!maybe.TryGetValue(name, out bool may)) maybe[name] = may = MaybeDeprecated(name);
                    if (!may) continue;
                    _s.Ct.ThrowIfCancellationRequested();
                    if (InInactive(doc, t.Start)) continue;
                    if (++resolutions > MaxDeprecationResolutions)
                    {
                        _stats.Skip("deprecated-budget");
                        break;
                    }
                    _stats.Checked++;
                    var r = SymbolResolver.ResolveToken(_s.Ctx, doc, k, 0);
                    if (r.Local != null) continue;
                    var cands = r.Candidates;
                    if (r.CallArguments != null && cands.Count > 1)
                    {
                        var filtered = SymbolResolver.FilterByCall(cands, r.CallArguments);
                        if (filtered.Count > 0) cands = filtered;
                    }
                    if (cands.Count == 0) continue;
                    DeprecationInfo info = null;
                    bool all = true;
                    foreach (var c in cands)
                    {
                        var di = DeprecationInfo.Of(_s, c);
                        if (di == null) { all = false; break; }
                        info ??= di;
                    }
                    if (!all)
                    {
                        _stats.Skip("deprecated-overload");
                        continue;
                    }
                    string replacement = info.Replacement != null && ReplacementExists(cands[0], info.Replacement) ? info.Replacement : string.Empty;
                    _stats.Hit(CodeDiagnosticCodes.DeprecatedApi);
                    string message = $"'{name}' is deprecated" + (info.Version != null ? " since " + info.Version : string.Empty) + (info.Message != null ? ": " + info.Message : string.Empty);
                    result.Add(Make(t.Start, t.Length, CodeDiagnosticSeverity.Warning, CodeDiagnosticCodes.DeprecatedApi, message,
                        name, info.Version ?? string.Empty, info.Message ?? string.Empty, replacement));
                }
            }

            /// <summary>Some declaration named <paramref name="name"/> may be deprecated (true when there are too many to check cheaply).</summary>
            private bool MaybeDeprecated(string name)
            {
                if (name.EndsWith(DeprecationInfo.MacroSuffix, StringComparison.Ordinal) && name.Length > DeprecationInfo.MacroSuffix.Length) return true;
                var cands = _s.Ctx.ByName(name);
                if (cands.Count == 0) return false;
                if (cands.Count > MaxPrefilterCandidates) return true;
                foreach (var c in cands)
                {
                    if (c.Kind == SymbolKind.Namespace) continue;
                    if (DeprecationInfo.Of(_s, c) != null) return true;
                }
                return false;
            }

            /// <summary>The replacement is a symbol of the same scope and the same family (callable / variable / type).</summary>
            private bool ReplacementExists(SymbolInfo old, string replacement)
            {
                if (string.Equals(old.Name, replacement, StringComparison.Ordinal)) return false;
                foreach (var c in _s.Ctx.ByName(replacement))
                {
                    if (Family(c.Kind) != Family(old.Kind)) continue;
                    bool sameScope = string.IsNullOrEmpty(old.Container) ? string.IsNullOrEmpty(c.Container) : NavContext.SameScope(c.Container, old.Container);
                    if (sameScope && DeprecationInfo.Of(_s, c) == null) return true;
                }
                return false;
            }

            private static int Family(SymbolKind k) =>
                TypeText.IsCallableKind(k) ? 1 : k == SymbolKind.Field || k == SymbolKind.Variable || k == SymbolKind.EnumValue ? 2 : TypeText.IsTypeKind(k) ? 3 : 4;

            // ============================================================ UE0012 naming conventions

            public void Naming(int start, int end, List<CodeDiagnostic> result)
            {
                var doc = _doc;
                for (int i = 0; i < doc.Decls.Count; i++)
                {
                    var d = doc.Decls[i];
                    if (d.IsImplicit || d.Offset < start || d.Offset >= end || string.IsNullOrEmpty(d.Name)) continue;
                    if (_caret >= d.Offset && _caret <= d.Offset + Math.Max(d.NameLength, d.Name.Length)) continue; // being typed
                    _s.Ct.ThrowIfCancellationRequested();
                    string rule = null, kind = null;
                    if (TypeText.IsRecordKind(d.Kind))
                    {
                        if (d.Body.IsNone || !d.IsDefinition) continue; // forward declaration
                        _stats.Checked++;
                        if (d.IsConditional || InInactive(doc, d.Offset)) { _stats.Skip("naming-conditional"); continue; }
                        rule = TypeRule(i, out string why);
                        if (rule == null) { if (why != null) _stats.Skip(why); continue; }
                        kind = "type";
                    }
                    else if (d.Kind == SymbolKind.Enum)
                    {
                        if (d.Body.IsNone || d.Name == "Type") continue; // namespace EFoo { enum Type { … } } idiom
                        _stats.Checked++;
                        rule = "E";
                        kind = "enum";
                    }
                    else if (d.Kind == SymbolKind.Field && IsBool(d.Type) && d.Parent >= 0 && TypeText.IsRecordKind(doc.Decls[d.Parent].Kind))
                    {
                        _stats.Checked++;
                        if (d.Name.Length >= 2 && d.Name[0] == 'b' && char.IsUpper(d.Name[1])) continue;
                        result.Add(Naming(d, "b" + Capitalize(d.Name), "b", "bool"));
                        continue;
                    }
                    else continue;
                    if (HasPrefix(d.Name, rule[0])) continue;
                    result.Add(Naming(d, rule + Capitalize(StripPrefix(d.Name)), rule, kind));
                }
            }

            private CodeDiagnostic Naming(Declaration d, string expected, string rule, string kind)
            {
                _stats.Hit(CodeDiagnosticCodes.NamingConvention);
                int len = d.NameLength > 0 ? d.NameLength : System.Text.Encoding.UTF8.GetByteCount(d.Name);
                return Make(d.Offset, len, CodeDiagnosticSeverity.Info, CodeDiagnosticCodes.NamingConvention,
                    $"UE naming convention: '{d.Name}' should be '{expected}'", d.Name, expected, rule, kind);
            }

            /// <summary>Prefix letter a record declaration should have, or null (with the reason when uncertain).</summary>
            private string TypeRule(int index, out string why)
            {
                why = null;
                var d = _doc.Decls[index];
                if (d.Name.IndexOf('<') >= 0) { why = "naming-specialization"; return null; }
                if (!string.IsNullOrEmpty(d.TemplateParameters)) return "T";
                // UINTERFACE companion: GENERATED_BODY() without a reflection macro of its own
                if ((d.Ue & UeFlags.GeneratedBody) != 0 && (d.Ue & (UeFlags.UClass | UeFlags.UStruct | UeFlags.UInterface)) == 0) return "I";
                var lineage = Lineage(d, out why);
                if (lineage == null) return null;
                if (lineage.Contains("AActor")) return "A";
                if (lineage.Contains("UObject") || lineage.Contains("UObjectBase") || lineage.Contains("UInterface")) return "U";
                if (lineage.Contains("SWidget")) return "S";
                if ((d.Ue & (UeFlags.UClass | UeFlags.UInterface)) != 0) { why = "naming-uclass-unknown-base"; return null; } // a UCLASS must derive from UObject
                return "F";
            }

            /// <summary>Last segments of the type's bases, transitively; null when some base is not in the index (uncertain).</summary>
            private HashSet<string> Lineage(Declaration d, out string why)
            {
                why = null;
                var names = new HashSet<string>(StringComparer.Ordinal);
                var queue = new Queue<string>();
                foreach (var b in d.Bases ?? Array.Empty<string>()) queue.Enqueue(b);
                while (queue.Count > 0)
                {
                    if (names.Count > MaxLineage) { why = "naming-deep-lineage"; return null; }
                    string b = queue.Dequeue();
                    string simple = TypeText.LastSegment(BaseNames.StripTemplateArgs(b) ?? b);
                    if (string.IsNullOrEmpty(simple)) continue;
                    if (!names.Add(simple)) continue;
                    var t = _s.FindRecord(b, d.Offset) ?? _s.FindRecord(BaseNames.StripTemplateArgs(b));
                    if (t == null)
                    {
                        // template parameters / dependent bases (T, Super<T>) and unindexed bases: uncertain
                        why = "naming-unknown-base";
                        return null;
                    }
                    foreach (var bb in t.Bases) queue.Enqueue(bb);
                }
                return names;
            }

            private static bool IsBool(string type)
            {
                if (string.IsNullOrEmpty(type)) return false;
                string t = type.Trim();
                foreach (var w in new[] { "static ", "mutable ", "inline ", "constexpr " })
                    if (t.StartsWith(w, StringComparison.Ordinal)) t = t.Substring(w.Length).Trim();
                return t == "bool";
            }

            private static bool HasPrefix(string name, char prefix) => name.Length >= 2 && name[0] == prefix && (char.IsUpper(name[1]) || char.IsDigit(name[1]));

            /// <summary>"UMyActor" → "MyActor" (a wrong UE prefix is dropped); "Inventory" stays.</summary>
            private static string StripPrefix(string name) =>
                name.Length > 2 && "AUFESTI".IndexOf(name[0]) >= 0 && char.IsUpper(name[1]) && char.IsLower(name[2]) ? name.Substring(1) : name;

            private static string Capitalize(string name) => string.IsNullOrEmpty(name) || char.IsUpper(name[0]) ? name : char.ToUpperInvariant(name[0]) + name.Substring(1);
        }
    }
}
