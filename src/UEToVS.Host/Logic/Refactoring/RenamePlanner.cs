using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using UEToVS.Contracts;
using UEToVS.Core.Matching;

namespace UEToVS.Host.Logic.Refactoring
{
    /// <summary>One place the rename touches (from Find References), shown in the preview with a check box.</summary>
    public sealed class RenameLocation
    {
        public string FilePath { get; set; }
        public int Line { get; set; }
        public int Column { get; set; }
        public int Length { get; set; }
        public string LineText { get; set; }
        public int LineTextColumn { get; set; }
        public string Context { get; set; }
        public ReferenceKind Kind { get; set; }
        public ResolutionConfidence Confidence { get; set; }

        /// <summary>Project file: changed when checked. Engine / third-party: listed only.</summary>
        public bool IsEditable { get; set; }

        public bool Checked { get; set; }

        /// <summary>An #include line rewritten because the header file is renamed (not a symbol reference).</summary>
        public bool IsInclude { get; set; }

        /// <summary>For <see cref="IsInclude"/>: the replacement text of [Column, Column+Length).</summary>
        public string Replacement { get; set; }
    }

    /// <summary>What kind of UE Core Redirect a renamed symbol needs (null = none).</summary>
    public enum RedirectKind
    {
        None,
        Class,
        Struct,
        Enum,
        Property,
        Function,
    }

    /// <summary>
    /// Rename planning (pure): identifier validation, reference hits → locations (project editable, engine listed only),
    /// edits for the checked locations, Core Redirect lines and their insertion into DefaultEngine.ini, class file renames
    /// with #include rewrites.
    /// </summary>
    public static class RenamePlanner
    {
        private static readonly HashSet<string> Keywords = new HashSet<string>(StringComparer.Ordinal)
        {
            "alignas", "alignof", "and", "asm", "auto", "bool", "break", "case", "catch", "char", "char8_t", "char16_t", "char32_t", "class", "concept",
            "const", "consteval", "constexpr", "constinit", "const_cast", "continue", "co_await", "co_return", "co_yield", "decltype", "default", "delete",
            "do", "double", "dynamic_cast", "else", "enum", "explicit", "export", "extern", "false", "float", "for", "friend", "goto", "if", "inline",
            "int", "long", "mutable", "namespace", "new", "noexcept", "not", "nullptr", "operator", "or", "private", "protected", "public", "register",
            "reinterpret_cast", "requires", "return", "short", "signed", "sizeof", "static", "static_assert", "static_cast", "struct", "switch",
            "template", "this", "thread_local", "throw", "true", "try", "typedef", "typeid", "typename", "union", "unsigned", "using", "virtual",
            "void", "volatile", "wchar_t", "while", "xor",
        };

        public static bool IsValidIdentifier(string name)
        {
            if (string.IsNullOrEmpty(name) || Keywords.Contains(name)) return false;
            if (!(char.IsLetter(name[0]) || name[0] == '_')) return false;
            return name.All(c => char.IsLetterOrDigit(c) || c == '_') && name.All(c => c < 128);
        }

        public static bool IsProjectOrigin(ItemOrigin o) => o == ItemOrigin.Project || o == ItemOrigin.ProjectPlugin;

        /// <summary>Reference hits → locations; project hits checked, engine hits listed (unchecked, not editable). Duplicates removed.</summary>
        public static List<RenameLocation> FromHits(IEnumerable<ReferenceHit> hits, string oldName)
        {
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var list = new List<RenameLocation>();
            foreach (var h in hits ?? Enumerable.Empty<ReferenceHit>())
            {
                if (h?.Location?.FilePath == null) continue;
                if (!seen.Add(h.Location.FilePath + "|" + h.Location.Line + "|" + h.Location.Column)) continue;
                bool editable = IsProjectOrigin(h.Origin);
                list.Add(new RenameLocation
                {
                    FilePath = h.Location.FilePath, Line = h.Location.Line, Column = h.Location.Column, Length = h.Length > 0 ? h.Length : oldName.Length,
                    LineText = h.LineText, LineTextColumn = h.LineTextColumn, Context = h.Context, Kind = h.Kind, Confidence = h.Confidence,
                    IsEditable = editable,
                    // name-only matches are listed but start unchecked (they may be another symbol with the same name)
                    Checked = editable && h.Confidence != ResolutionConfidence.NameOnly,
                });
            }
            return list.OrderBy(l => l.IsEditable ? 0 : 1).ThenBy(l => l.FilePath, StringComparer.OrdinalIgnoreCase).ThenBy(l => l.Line).ThenBy(l => l.Column).ToList();
        }

        /// <summary>Edits for the checked, editable locations; each verifies that the old name is still there.</summary>
        public static List<TextEdit> Edits(IEnumerable<RenameLocation> locations, string oldName, string newName)
        {
            var edits = new List<TextEdit>();
            foreach (var l in locations)
            {
                if (!l.Checked || !l.IsEditable) continue;
                if (l.IsInclude)
                    edits.Add(new TextEdit { FilePath = l.FilePath, Line = l.Line, Column = l.Column, Length = l.Length, NewText = l.Replacement });
                else
                    edits.Add(new TextEdit { FilePath = l.FilePath, Line = l.Line, Column = l.Column, Length = oldName.Length, OldText = oldName, NewText = newName });
            }
            return edits;
        }

        // ---------------------------------------------------------------- Core Redirects

        public static RedirectKind RedirectFor(SymbolInfo s)
        {
            if (s == null) return RedirectKind.None;
            if ((s.Ue & UeFlags.UClass) != 0 || (s.Ue & UeFlags.UInterface) != 0) return RedirectKind.Class;
            if ((s.Ue & UeFlags.UStruct) != 0) return RedirectKind.Struct;
            if ((s.Ue & UeFlags.UEnum) != 0) return RedirectKind.Enum;
            if ((s.Ue & UeFlags.UProperty) != 0) return RedirectKind.Property;
            if ((s.Ue & UeFlags.UFunction) != 0) return RedirectKind.Function;
            return RedirectKind.None;
        }

        /// <summary>Name of a reflected type in /Script paths: the C++ prefix (A/U/F/I) dropped; enums keep their name.</summary>
        public static string ScriptName(string cppName, RedirectKind kind)
        {
            if (kind == RedirectKind.Enum) return cppName;
            return UnrealNaming.StripTypePrefix(cppName) ?? cppName;
        }

        /// <summary>
        /// The [CoreRedirects] line: <c>+ClassRedirects=(OldName="/Script/Mod.Old",NewName="/Script/Mod.New")</c>;
        /// properties / functions: <c>+PropertyRedirects=(OldName="/Script/Mod.Owner.Old",NewName="New")</c>. Null when not reflected.
        /// <paramref name="ownerClass"/> is the C++ owner of a property / function.
        /// </summary>
        public static string RedirectLine(RedirectKind kind, string module, string oldName, string newName, string ownerClass = null)
        {
            if (kind == RedirectKind.None || string.IsNullOrEmpty(module)) return null;
            switch (kind)
            {
                case RedirectKind.Class:
                case RedirectKind.Struct:
                case RedirectKind.Enum:
                    string key = kind == RedirectKind.Class ? "ClassRedirects" : kind == RedirectKind.Struct ? "StructRedirects" : "EnumRedirects";
                    return $"+{key}=(OldName=\"/Script/{module}.{ScriptName(oldName, kind)}\",NewName=\"/Script/{module}.{ScriptName(newName, kind)}\")";
                default:
                    if (string.IsNullOrEmpty(ownerClass)) return null;
                    string owner = UnrealNaming.StripTypePrefix(ownerClass) ?? ownerClass;
                    string k = kind == RedirectKind.Property ? "PropertyRedirects" : "FunctionRedirects";
                    return $"+{k}=(OldName=\"/Script/{module}.{owner}.{oldName}\",NewName=\"{newName}\")";
            }
        }

        /// <summary>
        /// DefaultEngine.ini text with <paramref name="lines"/> appended to [CoreRedirects] (created at the end when missing);
        /// lines already present are not repeated. Returns the input when nothing changes.
        /// </summary>
        public static string AddRedirects(string iniText, IEnumerable<string> lines)
        {
            string text = iniText ?? string.Empty;
            string nl = CppTextScan.NewlineOf(text.Length > 0 ? text : "\r\n");
            var all = text.Length == 0 ? new List<string>() : text.Replace("\r\n", "\n").Split('\n').ToList();
            var add = lines.Where(l => !string.IsNullOrEmpty(l) && !all.Any(a => string.Equals(a.Trim(), l, StringComparison.Ordinal))).Distinct().ToList();
            if (add.Count == 0) return iniText;
            int header = all.FindIndex(l => string.Equals(l.Trim(), "[CoreRedirects]", StringComparison.OrdinalIgnoreCase));
            if (header < 0)
            {
                while (all.Count > 0 && all[all.Count - 1].Trim().Length == 0) all.RemoveAt(all.Count - 1);
                if (all.Count > 0) all.Add(string.Empty);
                all.Add("[CoreRedirects]");
                all.AddRange(add);
                all.Add(string.Empty);
                return string.Join(nl, all);
            }
            int end = header + 1;
            while (end < all.Count && !all[end].TrimStart().StartsWith("[", StringComparison.Ordinal)) end++;
            int insertAt = end;
            while (insertAt - 1 > header && all[insertAt - 1].Trim().Length == 0) insertAt--;
            all.InsertRange(insertAt, add);
            return string.Join(nl, all);
        }

        // ---------------------------------------------------------------- files

        /// <summary>
        /// Base file name (no extension) the renamed type's files get when they are named after it (AMyActor in MyActor.h or
        /// AMyActor.h); null when the file is not named after the type.
        /// </summary>
        public static string RenamedFileBase(string headerPath, string oldName, string newName)
        {
            string baseName = Path.GetFileNameWithoutExtension(headerPath ?? string.Empty);
            if (string.IsNullOrEmpty(baseName)) return null;
            if (string.Equals(baseName, oldName, StringComparison.Ordinal)) return newName;
            string oldStripped = UnrealNaming.StripTypePrefix(oldName);
            if (oldStripped != null && string.Equals(baseName, oldStripped, StringComparison.Ordinal))
                return UnrealNaming.StripTypePrefix(newName) ?? newName;
            return null;
        }

        /// <summary>
        /// #include rewrites in one file for a renamed header (<c>Old.h</c> → <c>New.h</c>, keeping the directory part) and its
        /// generated header (<c>Old.generated.h</c> → <c>New.generated.h</c>). Locations cover just the file name in the quotes.
        /// </summary>
        public static List<RenameLocation> IncludeRewrites(string filePath, string text, string oldBase, string newBase)
        {
            var list = new List<RenameLocation>();
            var lines = CppTextScan.Lines(text);
            var rx = new Regex(@"^(\s*#\s*include\s*"")([^""]*?)(" + Regex.Escape(oldBase) + @")(\.generated)?\.h""");
            for (int i = 0; i < lines.Length; i++)
            {
                var m = rx.Match(lines[i]);
                if (!m.Success) continue;
                string dir = m.Groups[2].Value;
                if (dir.Length > 0 && !dir.EndsWith("/", StringComparison.Ordinal) && !dir.EndsWith("\\", StringComparison.Ordinal)) continue; // "XOld.h"
                var g = m.Groups[3];
                list.Add(new RenameLocation
                {
                    FilePath = filePath, Line = i, Column = g.Index, Length = g.Length, LineText = lines[i].Trim(), IsInclude = true,
                    Replacement = newBase, IsEditable = true, Checked = true, Kind = ReferenceKind.None, Confidence = ResolutionConfidence.Exact,
                });
            }
            return list;
        }
    }
}
