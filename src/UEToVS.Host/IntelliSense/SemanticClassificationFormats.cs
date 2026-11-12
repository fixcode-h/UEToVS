using System.ComponentModel.Composition;
using System.Windows.Media;
using Microsoft.VisualStudio.Text.Classification;
using Microsoft.VisualStudio.Utilities;
using UEToVS.Contracts;
using UEToVS.Host.Logic.IntelliSense;
using N = UEToVS.Host.Logic.IntelliSense.SemanticClassificationNames;

namespace UEToVS.Host.IntelliSense
{
    /// <summary>
    /// Classification types of the VA-style semantic colouring (§7.7), one per <see cref="SemanticKind"/>. Each also has a
    /// format definition below, listed in Tools &gt; Options &gt; Environment &gt; Fonts and Colors as "UEToVS …".
    /// </summary>
    internal static class SemanticClassificationTypes
    {
#pragma warning disable 169, 649 // MEF exports
        [Export, Name(N.Class)] internal static ClassificationTypeDefinition Class;
        [Export, Name(N.Struct)] internal static ClassificationTypeDefinition Struct;
        [Export, Name(N.Enum)] internal static ClassificationTypeDefinition Enum;
        [Export, Name(N.EnumValue)] internal static ClassificationTypeDefinition EnumValue;
        [Export, Name(N.Typedef)] internal static ClassificationTypeDefinition Typedef;
        [Export, Name(N.TemplateParameter)] internal static ClassificationTypeDefinition TemplateParameter;
        [Export, Name(N.Namespace)] internal static ClassificationTypeDefinition Namespace;
        [Export, Name(N.Function)] internal static ClassificationTypeDefinition Function;
        [Export, Name(N.Method)] internal static ClassificationTypeDefinition Method;
        [Export, Name(N.StaticMethod)] internal static ClassificationTypeDefinition StaticMethod;
        [Export, Name(N.VirtualMethod)] internal static ClassificationTypeDefinition VirtualMethod;
        [Export, Name(N.Field)] internal static ClassificationTypeDefinition Field;
        [Export, Name(N.StaticField)] internal static ClassificationTypeDefinition StaticField;
        [Export, Name(N.LocalVariable)] internal static ClassificationTypeDefinition LocalVariable;
        [Export, Name(N.Parameter)] internal static ClassificationTypeDefinition Parameter;
        [Export, Name(N.GlobalVariable)] internal static ClassificationTypeDefinition GlobalVariable;
        [Export, Name(N.Macro)] internal static ClassificationTypeDefinition Macro;
        [Export, Name(N.UeMacro)] internal static ClassificationTypeDefinition UeMacro;
        [Export, Name(N.UeType)] internal static ClassificationTypeDefinition UeType;
        [Export, Name(N.Delegate)] internal static ClassificationTypeDefinition Delegate;
        [Export, Name(N.UeSpecifier)] internal static ClassificationTypeDefinition UeSpecifier;
#pragma warning restore 169, 649
    }

    /// <summary>
    /// Default look of one semantic classification: the dark-theme colour of §7.7 (VS's default theme); on a light editor
    /// background <see cref="SemanticColors"/> swaps in the light defaults at runtime unless the user changed the colour.
    /// Ordered above VS's own C++ semantic colours so ours win while both are active.
    /// </summary>
    internal abstract class SemanticFormat : ClassificationFormatDefinition
    {
        protected SemanticFormat(SemanticKind kind)
        {
            var info = SemanticClassifications.Get(kind);
            DisplayName = info.DisplayName;
            ForegroundColor = SemanticColors.ToColor(info.Dark);
            if (kind == SemanticKind.UeSpecifier) ForegroundOpacity = SemanticClassifications.UeSpecifierOpacity;
        }
    }

#pragma warning disable SA1402 // one tiny class per classification (MEF needs a type per export)
    [Export(typeof(EditorFormatDefinition)), ClassificationType(ClassificationTypeNames = N.Class), Name(N.Class), UserVisible(true), Order(After = Priority.High)]
    internal sealed class ClassFormat : SemanticFormat { public ClassFormat() : base(SemanticKind.Class) { } }

    [Export(typeof(EditorFormatDefinition)), ClassificationType(ClassificationTypeNames = N.Struct), Name(N.Struct), UserVisible(true), Order(After = Priority.High)]
    internal sealed class StructFormat : SemanticFormat { public StructFormat() : base(SemanticKind.Struct) { } }

    [Export(typeof(EditorFormatDefinition)), ClassificationType(ClassificationTypeNames = N.Enum), Name(N.Enum), UserVisible(true), Order(After = Priority.High)]
    internal sealed class EnumFormat : SemanticFormat { public EnumFormat() : base(SemanticKind.Enum) { } }

    [Export(typeof(EditorFormatDefinition)), ClassificationType(ClassificationTypeNames = N.EnumValue), Name(N.EnumValue), UserVisible(true), Order(After = Priority.High)]
    internal sealed class EnumValueFormat : SemanticFormat { public EnumValueFormat() : base(SemanticKind.EnumValue) { } }

    [Export(typeof(EditorFormatDefinition)), ClassificationType(ClassificationTypeNames = N.Typedef), Name(N.Typedef), UserVisible(true), Order(After = Priority.High)]
    internal sealed class TypedefFormat : SemanticFormat { public TypedefFormat() : base(SemanticKind.Typedef) { } }

    [Export(typeof(EditorFormatDefinition)), ClassificationType(ClassificationTypeNames = N.TemplateParameter), Name(N.TemplateParameter), UserVisible(true), Order(After = Priority.High)]
    internal sealed class TemplateParameterFormat : SemanticFormat { public TemplateParameterFormat() : base(SemanticKind.TemplateParameter) { } }

    [Export(typeof(EditorFormatDefinition)), ClassificationType(ClassificationTypeNames = N.Namespace), Name(N.Namespace), UserVisible(true), Order(After = Priority.High)]
    internal sealed class NamespaceFormat : SemanticFormat { public NamespaceFormat() : base(SemanticKind.Namespace) { } }

    [Export(typeof(EditorFormatDefinition)), ClassificationType(ClassificationTypeNames = N.Function), Name(N.Function), UserVisible(true), Order(After = Priority.High)]
    internal sealed class FunctionFormat : SemanticFormat { public FunctionFormat() : base(SemanticKind.Function) { } }

    [Export(typeof(EditorFormatDefinition)), ClassificationType(ClassificationTypeNames = N.Method), Name(N.Method), UserVisible(true), Order(After = Priority.High)]
    internal sealed class MethodFormat : SemanticFormat { public MethodFormat() : base(SemanticKind.Method) { } }

    [Export(typeof(EditorFormatDefinition)), ClassificationType(ClassificationTypeNames = N.StaticMethod), Name(N.StaticMethod), UserVisible(true), Order(After = Priority.High)]
    internal sealed class StaticMethodFormat : SemanticFormat { public StaticMethodFormat() : base(SemanticKind.StaticMethod) { } }

    [Export(typeof(EditorFormatDefinition)), ClassificationType(ClassificationTypeNames = N.VirtualMethod), Name(N.VirtualMethod), UserVisible(true), Order(After = Priority.High)]
    internal sealed class VirtualMethodFormat : SemanticFormat { public VirtualMethodFormat() : base(SemanticKind.VirtualMethod) { } }

    [Export(typeof(EditorFormatDefinition)), ClassificationType(ClassificationTypeNames = N.Field), Name(N.Field), UserVisible(true), Order(After = Priority.High)]
    internal sealed class FieldFormat : SemanticFormat { public FieldFormat() : base(SemanticKind.Field) { } }

    [Export(typeof(EditorFormatDefinition)), ClassificationType(ClassificationTypeNames = N.StaticField), Name(N.StaticField), UserVisible(true), Order(After = Priority.High)]
    internal sealed class StaticFieldFormat : SemanticFormat { public StaticFieldFormat() : base(SemanticKind.StaticField) { } }

    [Export(typeof(EditorFormatDefinition)), ClassificationType(ClassificationTypeNames = N.LocalVariable), Name(N.LocalVariable), UserVisible(true), Order(After = Priority.High)]
    internal sealed class LocalVariableFormat : SemanticFormat { public LocalVariableFormat() : base(SemanticKind.LocalVariable) { } }

    [Export(typeof(EditorFormatDefinition)), ClassificationType(ClassificationTypeNames = N.Parameter), Name(N.Parameter), UserVisible(true), Order(After = Priority.High)]
    internal sealed class ParameterFormat : SemanticFormat { public ParameterFormat() : base(SemanticKind.Parameter) { } }

    [Export(typeof(EditorFormatDefinition)), ClassificationType(ClassificationTypeNames = N.GlobalVariable), Name(N.GlobalVariable), UserVisible(true), Order(After = Priority.High)]
    internal sealed class GlobalVariableFormat : SemanticFormat { public GlobalVariableFormat() : base(SemanticKind.GlobalVariable) { } }

    [Export(typeof(EditorFormatDefinition)), ClassificationType(ClassificationTypeNames = N.Macro), Name(N.Macro), UserVisible(true), Order(After = Priority.High)]
    internal sealed class MacroFormat : SemanticFormat { public MacroFormat() : base(SemanticKind.Macro) { } }

    [Export(typeof(EditorFormatDefinition)), ClassificationType(ClassificationTypeNames = N.UeMacro), Name(N.UeMacro), UserVisible(true), Order(After = Priority.High)]
    internal sealed class UeMacroFormat : SemanticFormat { public UeMacroFormat() : base(SemanticKind.UeMacro) { } }

    [Export(typeof(EditorFormatDefinition)), ClassificationType(ClassificationTypeNames = N.UeType), Name(N.UeType), UserVisible(true), Order(After = Priority.High)]
    internal sealed class UeTypeFormat : SemanticFormat { public UeTypeFormat() : base(SemanticKind.UeType) { } }

    [Export(typeof(EditorFormatDefinition)), ClassificationType(ClassificationTypeNames = N.Delegate), Name(N.Delegate), UserVisible(true), Order(After = Priority.High)]
    internal sealed class DelegateFormat : SemanticFormat { public DelegateFormat() : base(SemanticKind.Delegate) { } }

    [Export(typeof(EditorFormatDefinition)), ClassificationType(ClassificationTypeNames = N.UeSpecifier), Name(N.UeSpecifier), UserVisible(true), Order(After = Priority.High)]
    internal sealed class UeSpecifierFormat : SemanticFormat { public UeSpecifierFormat() : base(SemanticKind.UeSpecifier) { } }
#pragma warning restore SA1402
}
