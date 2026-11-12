using System;
using System.Collections.Generic;
using System.Threading;
using UEToVS.Contracts;
using UEToVS.Engine.Index.Segments;
using UEToVS.Engine.Index.Storage;

namespace UEToVS.Engine.Index.Query
{
    /// <summary>Materialises result DTOs. This is the only place strings leave the mapped segments.</summary>
    internal static class Dto
    {
        public static SymbolInfo Symbol(Segment seg, uint symbolIndex)
        {
            ref readonly var r = ref seg.Symbols[(int)symbolIndex];
            ref readonly var f = ref seg.Files[(int)r.FileId];
            var st = seg.Strings;
            return new SymbolInfo
            {
                Name = st.GetString(r.NameId),
                Container = st.GetString(r.ContainerId),
                Kind = r.SymbolKind,
                Ue = r.UeFlags,
                Specifiers = st.GetString(r.SpecifiersId),
                Signature = st.GetString(r.SignatureId),
                Location = new SourceLocation { FilePath = st.GetString(f.PathId), Line = r.Line, Column = r.Column },
                Module = st.GetString(f.ModuleId),
                Origin = f.ItemOrigin,
                IsDefinition = r.IsDefinition,
            };
        }

        public static SymbolInfo Symbol(DeltaFile df, int declIndex)
        {
            ref readonly var d = ref df.Decls[declIndex];
            return new SymbolInfo
            {
                Name = d.Name,
                Container = string.IsNullOrEmpty(d.Container) ? null : d.Container,
                Kind = d.Kind,
                Ue = d.Ue,
                Specifiers = string.IsNullOrEmpty(d.Specifiers) ? null : d.Specifiers,
                Signature = string.IsNullOrEmpty(d.Signature) ? null : d.Signature,
                Location = new SourceLocation
                {
                    FilePath = df.Source.Path,
                    Line = Math.Clamp(d.Line, 0, SymbolRecord.MaxLine),
                    Column = Math.Clamp(d.Column, 0, SymbolRecord.MaxColumn),
                },
                Module = string.IsNullOrEmpty(df.Source.Module) ? null : df.Source.Module,
                Origin = df.Source.Origin,
                IsDefinition = d.IsDefinition,
            };
        }

        public static IndexedFile File(Segment seg, int fileIndex)
        {
            ref readonly var f = ref seg.Files[fileIndex];
            var st = seg.Strings;
            var inc = seg.Includes.Slice((int)f.FirstInclude, (int)f.IncludeCount);
            var includes = new string[inc.Length];
            for (int i = 0; i < inc.Length; i++) includes[i] = st.GetString(inc[i]);
            return new IndexedFile
            {
                Path = st.GetString(f.PathId),
                Module = st.GetString(f.ModuleId),
                Origin = f.ItemOrigin,
                MtimeTicks = f.MtimeTicks,
                Length = f.Length,
                ContentHash = f.ContentHash,
                SymbolCount = (int)f.SymbolCount,
                Includes = includes,
                InDelta = false,
            };
        }
    }

    /// <summary>Reconstructs <see cref="FileSymbols"/> from a segment (used by compaction to re-emit live files).</summary>
    internal static class SegmentExport
    {
        public static IEnumerable<FileSymbols> LiveFiles(SegmentView view, CancellationToken ct)
        {
            var seg = view.Segment;
            var occByFile = DecodeOccurrences(view, ct);
            for (int fi = 0; fi < seg.FileCount; fi++)
            {
                if ((fi & 255) == 0) ct.ThrowIfCancellationRequested();
                if (view.IsDead((uint)fi)) continue;
                yield return ReadFile(seg, fi, occByFile[fi]);
            }
        }

        private static List<OccurrenceInput>[] DecodeOccurrences(SegmentView view, CancellationToken ct)
        {
            var seg = view.Segment;
            var result = new List<OccurrenceInput>[seg.FileCount];
            for (int n = 0; n < seg.NameCount; n++)
            {
                if ((n & 1023) == 0) ct.ThrowIfCancellationRequested();
                if (seg.Names[n].OccCount == 0) continue;
                string name = null;
                var reader = seg.ReadOccurrences(n);
                while (reader.Next(out uint file, out int offset, out byte role))
                {
                    if (view.IsDead(file)) continue;
                    name ??= seg.GetString((uint)n + 1);
                    (result[file] ??= new List<OccurrenceInput>()).Add(new OccurrenceInput { Name = name, Offset = offset, Role = role });
                }
            }
            return result;
        }

        private static FileSymbols ReadFile(Segment seg, int fi, List<OccurrenceInput> occ)
        {
            ref readonly var f = ref seg.Files[fi];
            var st = seg.Strings;
            var syms = seg.Symbols.Slice((int)f.FirstSymbol, (int)f.SymbolCount);
            var decls = new DeclInput[syms.Length];
            for (int i = 0; i < syms.Length; i++)
            {
                ref readonly var r = ref syms[i];
                decls[i] = new DeclInput
                {
                    Name = st.GetString(r.NameId),
                    Container = st.GetString(r.ContainerId),
                    Kind = r.SymbolKind,
                    Ue = r.UeFlags,
                    Specifiers = st.GetString(r.SpecifiersId),
                    Signature = st.GetString(r.SignatureId),
                    Offset = (int)r.Offset,
                    Line = r.Line,
                    Column = r.Column,
                    IsDefinition = r.IsDefinition,
                    IsConditional = r.IsConditional,
                };
            }
            var inc = seg.Includes.Slice((int)f.FirstInclude, (int)f.IncludeCount);
            var includes = new string[inc.Length];
            for (int i = 0; i < inc.Length; i++) includes[i] = st.GetString(inc[i]);
            if (occ != null) occ.Sort((a, b) => a.Offset.CompareTo(b.Offset));
            return new FileSymbols
            {
                Path = st.GetString(f.PathId),
                Module = st.GetString(f.ModuleId),
                Origin = f.ItemOrigin,
                MtimeTicks = f.MtimeTicks,
                Length = f.Length,
                ContentHash = f.ContentHash,
                Decls = decls,
                Occurrences = occ?.ToArray() ?? Array.Empty<OccurrenceInput>(),
                Includes = includes,
            };
        }
    }
}
