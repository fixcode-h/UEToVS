using System;
using System.Collections.Generic;
using System.Threading;
using UEToVS.Contracts;
using UEToVS.Engine.Index;
using UEToVS.Engine.Syntax;
using UEToVS.Engine.Unreal;

namespace UEToVS.Engine.Indexing
{
    /// <summary>
    /// <see cref="IFileParser"/> backed by the real tolerant C++ parser (UEToVS.Engine.Syntax). Produces display
    /// symbols for the engine plus a <see cref="FileSymbols"/> payload (declarations with byte offsets, occurrences,
    /// includes) for the segmented index.
    /// </summary>
    public sealed class SyntaxFileParser : IFileParser
    {
        private readonly ParseOptions _options;

        public SyntaxFileParser(ParseOptions options = null)
        {
            _options = options ?? new ParseOptions();
            var kb = _options.Knowledge ?? UeMacroKnowledge.Default;
            Version = "syntax1+kb" + (kb.Version ?? "0");
        }

        public string Name => "syntax";

        public string Version { get; }

        public bool CanParse(FileContext file) => file.Kind == SourceFileKind.Header || file.Kind == SourceFileKind.Source;

        public ParseResult Parse(FileContext file, ReadOnlyMemory<byte> utf8, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            var summary = CppParser.Parse(utf8.Span, _options);
            var decls = summary.Declarations;
            var symbols = new List<SymbolInfo>(decls.Count);
            var inputs = new List<DeclInput>(decls.Count);
            foreach (var d in decls)
            {
                // GENERATED_BODY-injected members (StaticClass, Super…) are for member completion, not search.
                if (d.IsImplicit || string.IsNullOrEmpty(d.Name)) continue;
                symbols.Add(new SymbolInfo
                {
                    Name = d.Name,
                    Container = d.Container,
                    Kind = d.Kind,
                    Ue = d.Ue,
                    Specifiers = d.Specifiers,
                    Signature = d.Signature,
                    IsDefinition = d.IsDefinition,
                    Module = file.Module,
                    Origin = file.Origin,
                    Location = new SourceLocation { FilePath = file.Path, Line = d.Line, Column = d.Column },
                });
                inputs.Add(new DeclInput
                {
                    Name = d.Name,
                    Container = d.Container,
                    Kind = d.Kind,
                    Ue = d.Ue,
                    Specifiers = d.Specifiers,
                    Signature = d.Signature,
                    Offset = d.Offset,
                    Line = d.Line,
                    Column = d.Column,
                    IsDefinition = d.IsDefinition,
                    IsConditional = d.IsConditional,
                });
            }

            var occ = summary.Occurrences;
            var occInputs = new OccurrenceInput[occ.Count];
            for (int i = 0; i < occ.Count; i++)
                occInputs[i] = new OccurrenceInput { Name = occ[i].Name, Offset = occ[i].Offset, Role = (byte)occ[i].Role };

            var includes = new string[summary.Includes.Count];
            for (int i = 0; i < includes.Length; i++) includes[i] = summary.Includes[i].Path;

            return new ParseResult
            {
                Symbols = symbols,
                Warning = summary.Diagnostics.Count > 0 ? summary.Diagnostics.Count + " recovery point(s), first: " + summary.Diagnostics[0] : null,
                Payload = new FileSymbols
                {
                    Path = file.Path,
                    Module = file.Module,
                    Origin = file.Origin,
                    Decls = inputs.ToArray(),
                    Occurrences = occInputs,
                    Includes = includes,
                },
            };
        }
    }
}
