using System;
using System.Collections.Generic;
using UEToVS.Contracts;
using UEToVS.Host.Logic.Localization;
using UEToVS.Host.Logic.Refactoring;

namespace UEToVS.Host.Logic.Editing
{
    /// <summary>One Alt+Enter fix of a squiggle: replace <see cref="Old"/> at (Line, Column) with <see cref="New"/>.</summary>
    public sealed class DiagnosticFix
    {
        public string Code { get; set; }
        public string Old { get; set; }
        public string New { get; set; }
        public int Line { get; set; }
        public int Column { get; set; }
        public int Length { get; set; }

        public static DiagnosticFix From(CodeDiagnostic d, string replacement) => new DiagnosticFix
        {
            Code = d.Code, Old = d.Args != null && d.Args.Count > 0 ? d.Args[0] : null, New = replacement,
            Line = d.Line, Column = d.Column, Length = d.Length,
        };
    }

    /// <summary>ActionContext keys of the v0.4.5 editing fixes.</summary>
    public static class EditorFeatureKeys
    {
        /// <summary>Name the Rename dialog / inline rename starts with instead of the old one (string).</summary>
        public const string PresetName = "uetovs.refactor.presetName";
    }

    /// <summary>
    /// Alt+Enter fixes of the v0.4.5 squiggles (<see cref="RefactorContext.Diagnostics"/>):
    /// UE0011 → "Replace with X" when the engine found a mechanical replacement (Args[3]); UE0012 → "Rename to X" through the
    /// existing Rename flow (project symbols only).
    /// </summary>
    public sealed class EditorFixProvider : IContextActionProvider
    {
        public string Id => "editor.fixes";

        public IEnumerable<ContextActionItem> GetActions(RefactorContext c)
        {
            if (c?.Diagnostics == null || !c.IsProjectFile) yield break;
            foreach (var d in c.Diagnostics)
            {
                if (d?.Args == null || d.Args.Count == 0 || string.IsNullOrEmpty(d.Args[0])) continue;
                if (d.Code == CodeDiagnosticCodes.DeprecatedApi)
                {
                    string replacement = d.Args.Count > 3 ? d.Args[3] : null;
                    if (!IsIdentifier(replacement) || replacement == d.Args[0]) continue;
                    yield return new ContextActionItem(EditorFeatureIds.ReplaceDeprecated, EditorFeatureStrings.ReplaceDeprecated(d.Args[0], replacement), 90,
                        DiagnosticFix.From(d, replacement)) { Group = EditorFeatureStrings.GroupFix };
                }
                else if (d.Code == CodeDiagnosticCodes.NamingConvention)
                {
                    string expected = d.Args.Count > 1 ? d.Args[1] : null;
                    if (!IsIdentifier(expected) || expected == d.Args[0]) continue;
                    yield return new ContextActionItem(EditorFeatureIds.FixNaming, EditorFeatureStrings.FixNaming(d.Args[0], expected), 35,
                        DiagnosticFix.From(d, expected)) { Group = EditorFeatureStrings.GroupFix };
                }
            }
        }

        public static bool IsIdentifier(string s)
        {
            if (string.IsNullOrEmpty(s) || !(char.IsLetter(s[0]) || s[0] == '_')) return false;
            foreach (char ch in s)
                if (!(char.IsLetterOrDigit(ch) || ch == '_') || ch > 127) return false;
            return true;
        }

        /// <summary>The diagnostics of <paramref name="items"/> whose span contains (line, column) (end inclusive).</summary>
        public static List<CodeDiagnostic> At(IEnumerable<CodeDiagnostic> items, int line, int column)
        {
            var list = new List<CodeDiagnostic>();
            if (items == null) return list;
            foreach (var d in items)
                if (d != null && d.Line == line && column >= d.Column && column <= d.Column + Math.Max(0, d.Length)) list.Add(d);
            return list;
        }
    }
}
