using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using UEToVS.Contracts;
using UEToVS.Host.Logic.Localization;

namespace UEToVS.Host.Logic.Refactoring
{
    /// <summary>
    /// Context-action providers of the v0.4.5 refactorings (batch 2). Pure: they read the <see cref="RefactorContext"/>, and
    /// at most tokenize the current snapshot text (lazy, only when the engine answer says the operation may apply). The
    /// actions re-check everything (Find Action and key bindings call them without a provider). When in doubt: not offered.
    /// </summary>
    public static class RefactorProviders2
    {
        public static IEnumerable<IContextActionProvider> All { get; } = new IContextActionProvider[]
        {
            new ChangeSignatureProvider(),
            new MoveImplementationProvider(),
            new ForwardDeclarationProvider(),
            new IntroduceVariableProvider(),
            new ExtractMethodProvider(),
            new EncapsulateFieldProvider(),
        };

        // ---------------------------------------------------------------- Change Signature

        /// <summary>The caret is on the name of a project function / method declaration or definition whose parameters parse.</summary>
        public static bool CanChangeSignature(RefactorContext c, out string problem)
        {
            problem = "not-function";
            var f = c?.Function;
            if (f == null || !c.IsProjectFile || string.IsNullOrEmpty(c.Word) || !string.Equals(f.Name, c.Word, StringComparison.Ordinal)) return false;
            if (f.Kind != SymbolKind.Method && f.Kind != SymbolKind.Function) return false;
            if (f.Name.StartsWith("operator", StringComparison.Ordinal) || RefactorProviders.IsUeGenerated(f.Name)) return false;
            if (!RenamePlanner.IsProjectOrigin(f.Origin)) { problem = "engine-base"; return false; }
            if ((f.Modifiers & FunctionModifiers.Template) != 0 || !string.IsNullOrEmpty(f.TemplateParameters)) { problem = "template"; return false; }
            if (ChangeSignaturePlanner.Parse(f.Parameters ?? string.Empty) == null) { problem = "unparsable"; return false; }
            problem = null;
            return true;
        }

        private sealed class ChangeSignatureProvider : IContextActionProvider
        {
            public string Id => "changeSignature";

            public IEnumerable<ContextActionItem> GetActions(RefactorContext c)
            {
                if (!CanChangeSignature(c, out _)) yield break;
                yield return new ContextActionItem(RefactorActionIds.ChangeSignature, Refactor2Strings.ChangeSignatureOf(c.Function.Name), 45)
                    { Group = RefactorStrings.GroupRefactor };
            }
        }

        // ---------------------------------------------------------------- Move Implementation

        private sealed class MoveImplementationProvider : IContextActionProvider
        {
            public string Id => "moveImplementation";

            public IEnumerable<ContextActionItem> GetActions(RefactorContext c)
            {
                if (c == null || !c.IsProjectFile || c.CaretOffset < 0) yield break;
                var f = c.Function;
                if (c.IsHeader)
                {
                    // an inline member body at the caret, or the class name: everything inline in the class
                    var cls = c.Engine?.Class?.Symbol;
                    if (cls == null || !RefactorProviders.IsProject(cls)) yield break;
                    bool onClassName = c.Symbol != null && c.Engine.OnDeclaration && string.Equals(c.Symbol.Name, cls.Name, StringComparison.Ordinal) &&
                                       string.Equals(c.Word, cls.Name, StringComparison.Ordinal);
                    bool onFunction = f != null && f.IsDefinition && string.Equals(c.Word, f.Name, StringComparison.Ordinal);
                    if (!onClassName && !onFunction) yield break;
                    string text = c.Text;
                    if (text == null) yield break;
                    if (onFunction)
                    {
                        var m = MoveImplementationPlanner.InlineMemberAt(text, c.CaretOffset, out _);
                        if (m != null) yield return new ContextActionItem(RefactorActionIds.MoveImplementation, Refactor2Strings.MoveToSource(m.Name), 42)
                            { Group = RefactorStrings.GroupRefactor };
                    }
                    int count = MoveImplementationPlanner.InlineMembersOf(text, cls.Name, c.Engine.Class.BodyStartLine).Count;
                    if (count > (onFunction ? 1 : 0))
                        yield return new ContextActionItem(RefactorActionIds.MoveImplementationClass, Refactor2Strings.MoveClassToSource(cls.Name, count), 41)
                            { Group = RefactorStrings.GroupRefactor };
                    yield break;
                }
                if (f == null || !f.IsDefinition || string.IsNullOrEmpty(f.Container) || !string.Equals(c.Word, f.Name, StringComparison.Ordinal)) yield break;
                if (RefactorProviders.IsUeGenerated(f.Name)) yield break;
                if (c.Text == null || MoveImplementationPlanner.DefinitionAt(c.Text, c.CaretOffset) == null) yield break;
                yield return new ContextActionItem(RefactorActionIds.MoveImplementation, Refactor2Strings.MoveToHeader(f.Name), 42) { Group = RefactorStrings.GroupRefactor };
            }
        }

        // ---------------------------------------------------------------- Add Forward Declaration

        private static readonly Regex FixedEnum = new Regex(@"\benum\s+(class|struct)\s+\w+\s*:\s*\w", RegexOptions.CultureInvariant);

        /// <summary>
        /// A header uses a class / struct / scoped enum (with a fixed underlying type) only through pointers, references and
        /// handle templates, without including it, and has no declaration of it yet.
        /// </summary>
        public static bool NeedsForwardDeclaration(RefactorContext c, out string declaration)
        {
            declaration = null;
            var t = c?.Engine?.Type;
            var s = t?.Symbol;
            if (s == null || !c.IsHeader || !c.IsProjectFile || string.IsNullOrEmpty(s.Name)) return false;
            if (!string.IsNullOrEmpty(s.Container)) return false; // nested or namespaced: not offered
            if (t.Include != IncludeState.Missing && !(t.Include == IncludeState.Unknown && t.FilesVisited > 0)) return false;
            if (!string.IsNullOrEmpty(t.HeaderPath) && string.Equals(t.HeaderPath, c.FilePath, StringComparison.OrdinalIgnoreCase)) return false;
            string keyword;
            if (s.Kind == SymbolKind.Enum)
            {
                // only "enum class E : uint8" can be declared without the header; the action reads the exact underlying type
                if (s.Signature == null || !FixedEnum.IsMatch(s.Signature)) return false;
                keyword = "enum class";
            }
            else keyword = ForwardDeclarationPlanner.KeywordFor(s.Kind, false);
            if (keyword == null) return false;
            string text = c.Text;
            if (text == null) return false;
            if (ForwardDeclarationPlanner.DeclaresType(text, s.Name) || ForwardDeclarationPlanner.HasForwardDeclaration(text, s.Name)) return false;
            if (!ForwardDeclarationPlanner.OnlyIndirectUses(text, s.Name, out _)) return false;
            declaration = ForwardDeclarationPlanner.Declaration(keyword, s.Name);
            return true;
        }

        private sealed class ForwardDeclarationProvider : IContextActionProvider
        {
            public string Id => "addForwardDeclaration";

            public IEnumerable<ContextActionItem> GetActions(RefactorContext c)
            {
                if (!NeedsForwardDeclaration(c, out string decl)) yield break;
                // above Add #include (110): in a header a forward declaration is the better fix
                var s = c.Engine.Type.Symbol;
                string title = Refactor2Strings.AddForwardDeclarationOf(s.Kind == SymbolKind.Enum ? "enum class " + s.Name + " : …;" : decl);
                yield return new ContextActionItem(RefactorActionIds.AddForwardDeclaration, title, 120) { Group = RefactorStrings.GroupFix };
            }
        }

        // ---------------------------------------------------------------- Introduce Variable / Extract Method

        private sealed class IntroduceVariableProvider : IContextActionProvider
        {
            public string Id => "introduceVariable";

            public IEnumerable<ContextActionItem> GetActions(RefactorContext c)
            {
                if (c == null || !c.HasSelection || !c.IsProjectFile || c.Text == null) yield break;
                if (!IntroduceVariablePlanner.Analyze(c.Text, c.SelectionStart, c.SelectionEnd).Ok) yield break;
                yield return new ContextActionItem(RefactorActionIds.IntroduceVariable, Refactor2Strings.IntroduceVariableItem, 38) { Group = RefactorStrings.GroupRefactor };
            }
        }

        /// <summary>Engine locals as offsets in <paramref name="text"/> (positions outside the text are dropped).</summary>
        public static List<ExtractLocal> LocalsOf(string text, IEnumerable<LocalSymbol> locals)
        {
            var result = new List<ExtractLocal>();
            if (text == null || locals == null) return result;
            var starts = TextEditApplier.LineStarts(text);
            foreach (var l in locals)
            {
                if (l == null || string.IsNullOrEmpty(l.Name)) continue;
                int offset = TextEditApplier.OffsetOf(text, starts, l.Line, l.Column);
                if (offset < 0) continue;
                int end = l.ScopeEndLine < 0 ? int.MaxValue : TextEditApplier.OffsetOf(text, starts, l.ScopeEndLine, Math.Max(0, l.ScopeEndColumn));
                result.Add(new ExtractLocal { Name = l.Name, Type = l.Type, Offset = offset, IsParameter = l.IsParameter, ScopeEnd = end < 0 ? int.MaxValue : end });
            }
            return result;
        }

        private sealed class ExtractMethodProvider : IContextActionProvider
        {
            public string Id => "extractMethod";

            public IEnumerable<ContextActionItem> GetActions(RefactorContext c)
            {
                if (c == null || !c.HasSelection || !c.IsProjectFile || c.IsHeader || c.Engine?.EnclosingFunction == null || c.Text == null) yield break;
                var a = ExtractMethodPlanner.Analyze(c.Text, c.SelectionStart, c.SelectionEnd, LocalsOf(c.Text, c.Engine.Locals));
                if (!a.Ok) yield break;
                yield return new ContextActionItem(RefactorActionIds.ExtractMethod, Refactor2Strings.ExtractMethodItem, 37) { Group = RefactorStrings.GroupRefactor };
            }
        }

        // ---------------------------------------------------------------- Encapsulate Field

        private sealed class EncapsulateFieldProvider : IContextActionProvider
        {
            public string Id => "encapsulateField";

            public IEnumerable<ContextActionItem> GetActions(RefactorContext c)
            {
                if (c == null || !c.IsHeader || !c.IsProjectFile || c.CaretOffset < 0 || c.HasSelection) yield break;
                var cls = c.Engine?.Class?.Symbol;
                var s = c.Symbol;
                if (cls == null || !RefactorProviders.IsProject(cls) || s == null || !c.Engine.OnDeclaration) yield break;
                if (s.Kind != SymbolKind.Field && s.Kind != SymbolKind.Variable) yield break;
                if (!string.Equals(c.Word, s.Name, StringComparison.Ordinal) || c.Text == null) yield break;
                var f = EncapsulateFieldPlanner.FieldAt(c.Text, c.CaretOffset, out _);
                if (f == null) yield break;
                var plan = EncapsulateFieldPlanner.Plan(f, new EncapsulateOptions(), n => EncapsulateFieldPlanner.ClassDeclares(c.Text, f, n));
                if (!plan.Ok) yield break;
                yield return new ContextActionItem(RefactorActionIds.EncapsulateField, Refactor2Strings.EncapsulateFieldOf(f.Name), 39) { Group = RefactorStrings.GroupGenerate };
            }
        }
    }
}
