using System;
using Microsoft.VisualStudio.Core.Imaging;
using Microsoft.VisualStudio.Imaging;
using Microsoft.VisualStudio.Imaging.Interop;
using Microsoft.VisualStudio.Language.StandardClassification;
using Microsoft.VisualStudio.Text;
using Microsoft.VisualStudio.Text.Adornments;
using UEToVS.Contracts;
using UEToVS.Host.Editor;
using UEToVS.Host.Logic.IntelliSense;
using UEToVS.Host.Logic.Navigation;

namespace UEToVS.Host.IntelliSense
{
    /// <summary>VS-side helpers shared by the M3 editor parts: requests at a snapshot point, icons, classification names.</summary>
    internal static class IntelliSenseEditor
    {
        /// <summary>
        /// Captures a CaretRequest for <paramref name="point"/> of its own snapshot (not the buffer's current one: async
        /// completion / Quick Info hand us the snapshot they were triggered on). Cheap; the returned factory materializes a
        /// dirty buffer's text and may run on any thread.
        /// </summary>
        public static Func<CaretRequest> CaptureAt(ITextBuffer buffer, SnapshotPoint point)
        {
            var doc = EditorServices.Document(buffer);
            // Code preview buffers have no ITextDocument: they show the file on disk unchanged (not dirty).
            string path = doc?.FilePath ?? Search.EditorCodePreview.PathOf(buffer);
            if (string.IsNullOrEmpty(path)) return null;
            var snapshot = point.Snapshot;
            var line = point.GetContainingLine();
            bool dirty = doc?.IsDirty ?? false;
            int lineNumber = line.LineNumber, lineStart = line.Start.Position, lineLength = line.Length, position = point.Position;
            return () => CaretRequestBuilder.Build(path, lineNumber, lineStart, lineLength, position, dirty, () => snapshot.GetText(), snapshot.Length,
                                                   snapshot.Version.VersionNumber);
        }

        public static ImageMoniker MonikerFor(SymbolKind kind, UeFlags ue = UeFlags.None)
        {
            if ((ue & UeFlags.UInterface) != 0 && (kind == SymbolKind.Class || kind == SymbolKind.Struct)) return KnownMonikers.InterfacePublic;
            switch (kind)
            {
                case SymbolKind.Namespace: return KnownMonikers.Namespace;
                case SymbolKind.Class: return KnownMonikers.ClassPublic;
                case SymbolKind.Struct: return KnownMonikers.StructurePublic;
                case SymbolKind.Union: return KnownMonikers.UnionPublic;
                case SymbolKind.Enum: return KnownMonikers.EnumerationPublic;
                case SymbolKind.EnumValue: return KnownMonikers.EnumerationItemPublic;
                case SymbolKind.Typedef: return KnownMonikers.TypeDefinition;
                case SymbolKind.Concept: return KnownMonikers.TemplatePublic;
                case SymbolKind.Function:
                case SymbolKind.Method:
                case SymbolKind.Constructor:
                case SymbolKind.Destructor: return KnownMonikers.MethodPublic;
                case SymbolKind.Field: return KnownMonikers.FieldPublic;
                case SymbolKind.Variable: return KnownMonikers.LocalVariable;
                case SymbolKind.Macro: return KnownMonikers.MacroPublic;
                case SymbolKind.Delegate: return KnownMonikers.DelegatePublic;
                default: return KnownMonikers.QuestionMark;
            }
        }

        /// <summary>Icon of a completion item: symbol kind, keyword, reflection specifier, header / folder.</summary>
        public static ImageMoniker MonikerFor(CompletionItem item, CompletionContextKind context)
        {
            if (item == null) return KnownMonikers.QuestionMark;
            if (context == CompletionContextKind.Include)
            {
                string t = item.InsertText ?? item.Label ?? string.Empty;
                return t.EndsWith("/", StringComparison.Ordinal) || t.EndsWith("\\", StringComparison.Ordinal) ? KnownMonikers.FolderClosed : KnownMonikers.CPPHeaderFile;
            }
            if (context == CompletionContextKind.Specifier && item.Kind == SymbolKind.Unknown) return KnownMonikers.Attribute;
            if (item.IsKeyword && item.Kind == SymbolKind.Unknown) return KnownMonikers.IntellisenseKeyword;
            return MonikerFor(item.Kind, item.Ue);
        }

        public static ImageElement Image(ImageMoniker m, string automation = null) =>
            automation == null ? new ImageElement(m.ToImageId()) : new ImageElement(m.ToImageId(), automation);

        /// <summary>Classification for a declaration token in Quick Info / completion tooltips (same names as the editor colouring).</summary>
        public static string ClassificationFor(DeclToken t)
        {
            switch (t.Class)
            {
                case DeclTokenClass.Keyword: return PredefinedClassificationTypeNames.Keyword;
                case DeclTokenClass.Number: return PredefinedClassificationTypeNames.Number;
                case DeclTokenClass.String: return PredefinedClassificationTypeNames.String;
                case DeclTokenClass.Comment: return PredefinedClassificationTypeNames.Comment;
                case DeclTokenClass.Punctuation: return PredefinedClassificationTypeNames.Punctuation;
                case DeclTokenClass.Semantic: return SemanticClassifications.NameFor(t.Kind) ?? PredefinedClassificationTypeNames.Identifier;
                case DeclTokenClass.Whitespace: return PredefinedClassificationTypeNames.WhiteSpace;
                default: return PredefinedClassificationTypeNames.Identifier;
            }
        }
    }
}
