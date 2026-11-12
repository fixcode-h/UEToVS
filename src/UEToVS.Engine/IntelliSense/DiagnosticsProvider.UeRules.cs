using System;
using System.Collections.Generic;
using System.Linq;
using UEToVS.Contracts;
using UEToVS.Core.Unreal;
using UEToVS.Engine.Navigation;
using UEToVS.Engine.Syntax;

namespace UEToVS.Engine.IntelliSense
{
    /// <summary>
    /// v0.4.2 UE rules, same contract as the v0.4.1 ones (report only what is certain, count everything else as uncertain):
    /// <list type="bullet">
    /// <item>UE0004 a Server / Client / NetMulticast / BlueprintNativeEvent / BlueprintImplementableEvent UFUNCTION defined by hand (UHT generates that body).</item>
    /// <item>UE0005 <c>ReplicatedUsing=X</c> with no X in the class or its (fully known) bases, or X not a UFUNCTION.</item>
    /// <item>UE0006 a Replicated property of the class missing from its GetLifetimeReplicatedProps, when that definition is in this file and contains nothing but registrations.</item>
    /// <item>UE0007 RequestGameplayTag("A.B.X") where A.B is a known tag with children and A.B.X is not one (warning).</item>
    /// <item>UE0008 "/Game/..." asset path neither in the asset table nor on disk (warning).</item>
    /// <item>UE0009 UPROPERTY of TSharedPtr / std:: / pointer to struct or builtin.</item>
    /// <item>UE0010 UFUNCTION parameter of those types; int8/int16/uint16/uint32/uint64 in BlueprintCallable / BlueprintPure.</item>
    /// </list>
    /// </summary>
    public sealed partial class DiagnosticsProvider
    {
        /// <summary>Gameplay tags and ini files (UE0007); null switches the rule off.</summary>
        public StringCompletionSources Strings { get; set; }

        /// <summary>/Game/ package existence (UE0008): true / false / null = unknown. Null switches the rule off.</summary>
        public Func<string, bool?> GamePackageExists { get; set; }

        /// <summary>Whether the index (and the asset listing) has finished; UE0007 / UE0008 only run when it has.</summary>
        public Func<bool> IndexReady { get; set; }

        private sealed partial class Pass
        {
            private int _caretLine = -2;

            private int CaretLine
            {
                get
                {
                    if (_caretLine == -2) _caretLine = _caret >= 0 && _caret <= _doc.Text.Length ? _doc.PositionOf(_caret).Line : -1;
                    return _caretLine;
                }
            }

            /// <summary>The caret is on the declaration's line or the two lines above (its UPROPERTY / UFUNCTION macro being edited).</summary>
            private bool Editing(Declaration d) => CaretLine >= 0 && CaretLine >= d.Line - 2 && CaretLine <= d.Line;

            public void UeRules(int start, int end, List<CodeDiagnostic> result)
            {
                var doc = _doc;
                for (int i = 0; i < doc.Decls.Count; i++)
                {
                    var d = doc.Decls[i];
                    if (d.IsImplicit || d.Offset < start || d.Offset >= end) continue;
                    _s.Ct.ThrowIfCancellationRequested();
                    if (TypeText.IsCallableKind(d.Kind))
                    {
                        if (d.IsDefinition && !d.Body.IsNone) GeneratedFunctionBody(i, result);
                        if ((d.Ue & UeFlags.UFunction) != 0) ParameterTypes(i, result);
                        if (d.IsDefinition && !d.Body.IsNone && d.Name == "GetLifetimeReplicatedProps") Registrations(i, result);
                    }
                    else if (d.Kind == SymbolKind.Field && (d.Ue & UeFlags.UProperty) != 0)
                    {
                        PropertyType(i, result);
                        RepNotify(i, result);
                    }
                }
                StringLiterals(start, end, result);
            }

            private void Report(List<CodeDiagnostic> result, CodeDiagnostic d)
            {
                _stats.Hit(d.Code);
                result.Add(d);
            }

            private int NameLength(Declaration d) => d.NameLength > 0 ? d.NameLength : System.Text.Encoding.UTF8.GetByteCount(d.Name ?? string.Empty);

            /// <summary>Class declaring a member: the parent record (in-class) or the record named by the definition's qualifier.</summary>
            private (DocumentModel Doc, int Index, string Reason) OwnerClass(int declIndex)
            {
                var d = _doc.Decls[declIndex];
                if (d.Parent >= 0 && TypeText.IsRecordKind(_doc.Decls[d.Parent].Kind)) return (_doc, d.Parent, null);
                if (string.IsNullOrEmpty(d.Container)) return (null, -1, "free-function");
                if (d.Parent >= 0 && _doc.Decls[d.Parent].Kind != SymbolKind.Namespace) return (null, -1, "nested-definition");
                var t = Record(d.Container, d.Offset, out string why);
                if (t == null) return (null, -1, why);
                var (cdoc, ci) = _s.ClassDecl(t);
                if (cdoc == null) return (null, -1, "class-body-unavailable");
                if (!ReferenceEquals(cdoc, _doc) && _unsaved.Contains(NavContext.NormalizePath(cdoc.Path))) return (null, -1, "unsaved-declaration");
                return (cdoc, ci, null);
            }

            private static IEnumerable<int> MembersNamed(DocumentModel doc, int classIndex, string name)
            {
                var body = doc.Decls[classIndex].Body;
                for (int i = classIndex + 1; i < doc.Decls.Count; i++)
                {
                    var m = doc.Decls[i];
                    if (m.Offset >= body.End) break;
                    if (m.Parent == classIndex && m.Name == name) yield return i;
                }
            }

            private bool Uncertain(DocumentModel doc, Declaration d) =>
                d.IsConditional || InInactive(doc, d.Offset) || (d.Modifiers & DeclModifiers.Template) != 0 || !string.IsNullOrEmpty(d.TemplateParameters);

            // ============================================================ UE0004

            private void GeneratedFunctionBody(int i, List<CodeDiagnostic> result)
            {
                var d = _doc.Decls[i];
                if (d.Name.EndsWith("_Implementation", StringComparison.Ordinal) || d.Name.EndsWith("_Validate", StringComparison.Ordinal)) return;
                bool inClass = d.Parent >= 0 && TypeText.IsRecordKind(_doc.Decls[d.Parent].Kind);
                if (!inClass && string.IsNullOrEmpty(d.Container)) return;
                Declaration decl;
                if (inClass)
                {
                    if ((d.Ue & UeFlags.UFunction) == 0) return;
                    decl = d;
                }
                else
                {
                    var (cdoc, ci, why) = OwnerClass(i);
                    _stats.Checked++;
                    if (cdoc == null) { _stats.Skip("ue0004:" + why); return; }
                    var named = MembersNamed(cdoc, ci, d.Name).ToList();
                    if (named.Count == 0) { _stats.Skip("ue0004:no-declaration"); return; }
                    if (named.All(n => (cdoc.Decls[n].Ue & UeFlags.UFunction) == 0)) return;
                    if (named.Count != 1) { _stats.Skip("ue0004:overloads"); return; }
                    decl = cdoc.Decls[named[0]];
                    if (Uncertain(cdoc, decl)) { _stats.Skip("ue0004:conditional"); return; }
                }
                var specs = UhtRules.Specifiers(decl.Specifiers);
                string spec = UhtRules.GeneratedBodySpecifier(specs);
                if (spec == null) return;
                if (UhtRules.HasKey(specs, "CustomThunk")) { _stats.Skip("ue0004:custom-thunk"); return; }
                if (Uncertain(_doc, d)) { _stats.Skip("ue0004:conditional"); return; }
                if (Editing(d)) { _stats.Skip("ue0004:typing"); return; }
                string cls = inClass ? _doc.Decls[d.Parent].Name : TypeText.LastSegment(d.Container);
                Report(result, Make(d.Offset, NameLength(d), CodeDiagnosticSeverity.Error, CodeDiagnosticCodes.GeneratedFunctionBody,
                    $"'{d.Name}' is a {spec} UFUNCTION: UHT generates its body, implement '{d.Name}_Implementation' instead", d.Name, spec, cls));
            }

            // ============================================================ UE0010

            private void ParameterTypes(int i, List<CodeDiagnostic> result)
            {
                var d = _doc.Decls[i];
                if (d.Parent < 0 || !TypeText.IsRecordKind(_doc.Decls[d.Parent].Kind)) return; // the declaration carries the UFUNCTION
                var specs = UhtRules.Specifiers(d.Specifiers);
                if (UhtRules.HasKey(specs, "CustomThunk")) return;
                bool blueprint = UhtRules.HasKey(specs, "BlueprintCallable") || UhtRules.HasKey(specs, "BlueprintPure");
                foreach (var p in UhtRules.Parameters(d.Parameters))
                {
                    _stats.Checked++;
                    string kind = UhtRules.ReflectedTypeProblem(p.Type);
                    if (kind == null && blueprint && UhtRules.IsNonBlueprintNumber(p.Type)) kind = "blueprint";
                    if (kind == null) continue;
                    if (Uncertain(_doc, d) || Editing(d)) { _stats.Skip("ue0010:conditional-or-typing"); return; }
                    if (p.Type.IndexOf("::", StringComparison.Ordinal) >= 0 && kind != "unsupported") { _stats.Skip("ue0010:qualified"); continue; }
                    int at = ParameterNameOffset(d, p.Name);
                    int len = at == d.Offset ? NameLength(d) : System.Text.Encoding.UTF8.GetByteCount(p.Name);
                    string pname = p.Name ?? "?";
                    string msg = kind == "blueprint"
                        ? $"Parameter '{pname}' of type '{p.Type}' is not supported by Blueprint (UHT error in '{d.Name}')"
                        : $"Parameter '{pname}' of type '{p.Type}' cannot be used in a UFUNCTION (UHT error in '{d.Name}')";
                    Report(result, Make(at, len, CodeDiagnosticSeverity.Error, CodeDiagnosticCodes.UnsupportedParameterType, msg, pname, p.Type, d.Name, kind));
                }
            }

            /// <summary>Offset of the parameter's name token in the declaration's parameter list, or the function name.</summary>
            private int ParameterNameOffset(Declaration d, string name)
            {
                if (string.IsNullOrEmpty(name)) return d.Offset;
                var doc = _doc;
                int k = doc.TokenAt(d.Offset);
                if (k < 0) return d.Offset;
                int open = k + 1;
                if (!doc.Is(open, Punct.LParen)) return d.Offset;
                int depth = 0;
                for (int j = open; j < doc.Tokens.Length && j < open + 400; j++)
                {
                    if (doc.Is(j, Punct.LParen)) depth++;
                    else if (doc.Is(j, Punct.RParen) && --depth == 0) break;
                    else if (depth == 1 && doc.IsIdentifier(j) && doc.TokenText(j) == name) return doc.Tokens[j].Start;
                }
                return d.Offset;
            }

            // ============================================================ UE0009

            private void PropertyType(int i, List<CodeDiagnostic> result)
            {
                var d = _doc.Decls[i];
                if (d.Parent < 0 || !TypeText.IsRecordKind(_doc.Decls[d.Parent].Kind)) return;
                _stats.Checked++;
                string kind = UhtRules.ReflectedTypeProblem(d.Type);
                if (kind == null) return;
                if (Uncertain(_doc, d) || Editing(d)) { _stats.Skip("ue0009:conditional-or-typing"); return; }
                string type = UhtRules.Normalize(d.Type);
                Report(result, Make(d.Offset, NameLength(d), CodeDiagnosticSeverity.Error, CodeDiagnosticCodes.UnsupportedPropertyType,
                    $"UPROPERTY '{d.Name}' has type '{type}', which UHT does not support", d.Name, type));
            }

            // ============================================================ UE0005

            private void RepNotify(int i, List<CodeDiagnostic> result)
            {
                var d = _doc.Decls[i];
                if (d.Parent < 0 || !TypeText.IsRecordKind(_doc.Decls[d.Parent].Kind)) return;
                var specs = UhtRules.Specifiers(d.Specifiers);
                string fn = UhtRules.Value(specs, "ReplicatedUsing");
                if (string.IsNullOrEmpty(fn)) return;
                _stats.Checked++;
                if (!IsIdentifierText(fn)) { _stats.Skip("ue0005:value"); return; }
                if (Uncertain(_doc, d) || Editing(d)) { _stats.Skip("ue0005:conditional-or-typing"); return; }
                var cls = _doc.Decls[d.Parent];
                var root = Record(TypeText.Qualified(cls.Container, cls.Name), cls.Offset, out string why);
                if (root == null) { _stats.Skip("ue0005:" + why); return; }
                string c = Certain(root);
                if (c != null) { _stats.Skip("ue0005:" + c); return; }
                var hits = _s.MembersOf(root).Members.Where(m => m.Decl.Name == fn).ToList();
                string state;
                if (hits.Count == 0) state = "missing";
                else if (hits.Any(h => (h.Decl.Ue & UeFlags.UFunction) != 0)) return;
                else if (hits.All(h => TypeText.IsCallableKind(h.Decl.Kind) && !h.Decl.IsConditional)) state = "not-ufunction";
                else { _stats.Skip("ue0005:not-a-function"); return; }
                string msg = state == "missing"
                    ? $"ReplicatedUsing function '{fn}' does not exist in '{cls.Name}'"
                    : $"ReplicatedUsing function '{fn}' must be a UFUNCTION()";
                Report(result, Make(d.Offset, NameLength(d), CodeDiagnosticSeverity.Error, CodeDiagnosticCodes.MissingRepNotify, msg, d.Name, fn, cls.Name, state));
            }

            private static bool IsIdentifierText(string s)
            {
                if (string.IsNullOrEmpty(s) || !TypeText.IsIdentStart(s[0])) return false;
                foreach (char ch in s)
                    if (!TypeText.IsIdentPart(ch)) return false;
                return true;
            }

            // ============================================================ UE0006

            private static readonly string[] RegistrationMacros = { "DOREPLIFETIME", "DISABLE_REPLICATED_PROPERTY", "DISABLE_REPLICATED_PRIVATE_PROPERTY", "RESET_REPLIFETIME" };

            private static readonly HashSet<string> HarmlessMacros = new HashSet<string>(StringComparer.Ordinal)
            {
                "TEXT", "check", "checkf", "checkSlow", "ensure", "ensureMsgf", "UE_LOG", "SCOPE_CYCLE_COUNTER", "QUICK_SCOPE_CYCLE_COUNTER", "TRACE_CPUPROFILER_EVENT_SCOPE",
            };

            private void Registrations(int i, List<CodeDiagnostic> result)
            {
                var d = _doc.Decls[i];
                _stats.Checked++;
                var (cdoc, ci, why) = OwnerClass(i);
                if (cdoc == null) { _stats.Skip("ue0006:" + why); return; }
                var cls = cdoc.Decls[ci];
                if ((cls.Ue & UeFlags.UClass) == 0) { _stats.Skip("ue0006:not-uclass"); return; }
                if (Uncertain(_doc, d) || cls.IsConditional) { _stats.Skip("ue0006:conditional"); return; }
                var replicated = new List<Declaration>();
                var body = cls.Body;
                for (int m = ci + 1; m < cdoc.Decls.Count && cdoc.Decls[m].Offset < body.End; m++)
                {
                    var x = cdoc.Decls[m];
                    if (x.Parent != ci || x.Kind != SymbolKind.Field || (x.Ue & UeFlags.UProperty) == 0) continue;
                    var specs = UhtRules.Specifiers(x.Specifiers);
                    if (!UhtRules.HasKey(specs, "Replicated") && !UhtRules.HasKey(specs, "ReplicatedUsing")) continue;
                    if (x.IsConditional || InInactive(cdoc, x.Offset)) continue;
                    replicated.Add(x);
                }
                if (replicated.Count == 0) return;
                string param = UhtRules.Parameters(d.Parameters).Select(p => p.Name).FirstOrDefault();
                string reason = ScanRegistrations(d, param, out var registered);
                if (reason != null) { _stats.Skip("ue0006:" + reason); return; }
                if (CaretLine >= 0 && _caret >= d.Body.Start && _caret <= d.Body.End) { _stats.Skip("ue0006:typing"); return; }
                foreach (var p in replicated)
                {
                    if (registered.Contains(p.Name)) continue;
                    Report(result, Make(d.Offset, NameLength(d), CodeDiagnosticSeverity.Error, CodeDiagnosticCodes.UnregisteredReplicatedProperty,
                        $"Replicated property '{p.Name}' is not registered in {cls.Name}::GetLifetimeReplicatedProps (DOREPLIFETIME)", p.Name, cls.Name));
                }
            }

            /// <summary>The second argument of every DOREPLIFETIME* / DISABLE_REPLICATED_* call, or why the body cannot be trusted.</summary>
            private string ScanRegistrations(Declaration fn, string param, out HashSet<string> registered)
            {
                registered = new HashSet<string>(StringComparer.Ordinal);
                var doc = _doc;
                if (string.IsNullOrEmpty(param)) return "no-parameter";
                foreach (var r in doc.Summary.InactiveRanges)
                    if (r.Start < fn.Body.End && r.End > fn.Body.Start) return "inactive-branch";
                int first = doc.TokenIndexFrom(fn.Body.Start) + 1;
                for (int k = first; k < doc.Tokens.Length && doc.Tokens[k].Start < fn.Body.End; k++)
                {
                    var t = doc.Tokens[k];
                    if (t.Kind == TokenKind.Directive) return "directive";
                    if (t.Is(Keyword.For) || t.Is(Keyword.While) || t.Is(Keyword.Do) || t.Is(Keyword.Goto)) return "loop";
                    if (t.Kind != TokenKind.Identifier) continue;
                    string name = doc.TokenText(k);
                    if (name == param)
                    {
                        // only Super::GetLifetimeReplicatedProps(OutLifetimeProps) may see the array
                        if (!(doc.Is(k - 1, Punct.LParen) && doc.IsIdentifier(k - 2) && doc.TokenText(k - 2) == "GetLifetimeReplicatedProps")) return "array-escapes";
                        continue;
                    }
                    if (!doc.Is(k + 1, Punct.LParen)) continue;
                    if (RegistrationMacros.Any(m => name.StartsWith(m, StringComparison.Ordinal)))
                    {
                        string prop = SecondArgument(k + 1);
                        if (prop == null) return "complex-registration";
                        registered.Add(prop);
                        continue;
                    }
                    if (name.StartsWith("DISABLE_ALL_CLASS_REPLICATED", StringComparison.Ordinal)) return "disable-all";
                    if (HarmlessMacros.Contains(name) || name == "GetLifetimeReplicatedProps") continue;
                    if (IsMacro(k, name)) return "unknown-macro";
                }
                return null;
            }

            /// <summary>The single identifier that is the second argument of the call whose '(' is at <paramref name="open"/>.</summary>
            private string SecondArgument(int open)
            {
                var doc = _doc;
                int depth = 0, arg = 0, start = -1;
                for (int j = open; j < doc.Tokens.Length && j < open + 200; j++)
                {
                    if (doc.Is(j, Punct.LParen)) { depth++; if (depth == 1) { start = j + 1; } continue; }
                    bool close = doc.Is(j, Punct.RParen);
                    bool comma = doc.Is(j, Punct.Comma) && depth == 1;
                    if (close) depth--;
                    if (comma || close && depth == 0)
                    {
                        if (arg == 1) return j - start == 1 && doc.IsIdentifier(start) ? doc.TokenText(start) : null;
                        arg++;
                        start = j + 1;
                        if (close) return null;
                    }
                }
                return null;
            }

            // ============================================================ UE0007 / UE0008

            private (bool Done, string Reason, Dictionary<string, List<TagNode>> Tree) _tags;

            private string TagGuard(out Dictionary<string, List<TagNode>> tree)
            {
                if (_tags.Done)
                {
                    tree = _tags.Tree;
                    return _tags.Reason;
                }
                string reason = ComputeTagGuard(out tree);
                _tags = (true, reason, tree);
                return reason;
            }

            private string ComputeTagGuard(out Dictionary<string, List<TagNode>> tree)
            {
                tree = null;
                var src = _p.Strings;
                if (src == null) return "no-tag-source";
                if (_p.IndexReady != null && !_p.IndexReady()) return "index-not-ready";
                foreach (var f in src.Config.Files(src.Files))
                    foreach (var sec in f.Sections)
                        if (sec.Keys.ContainsKey("GameplayTagTableList") || sec.Keys.ContainsKey("GameplayTagRedirects"))
                            return "tag-tables-or-redirects";
                if (src.Backend != null)
                    foreach (var macro in new[] { "AddNativeGameplayTag", "FNativeGameplayTag", "AddTagIniSearchPath" })
                        foreach (var path in src.Backend.FindOccurrenceFiles(macro, _s.Ct))
                            if (TypeText.IsProject(_s.Ctx.FileOrigin(path).Origin)) return "native-tags";
                tree = src.Tags.Tree(src.Backend, src.Files, _s.Ct);
                if (src.Tags.Count == 0 || tree.Count == 0) return "no-tags";
                return null;
            }

            private static bool TagKnown(Dictionary<string, List<TagNode>> tree, string tag, out bool parentKnown)
            {
                int dot = tag.LastIndexOf('.');
                string parent = dot < 0 ? string.Empty : tag.Substring(0, dot);
                parentKnown = false;
                if (!tree.TryGetValue(parent, out var children)) return false;
                parentKnown = dot >= 0;
                return children.Any(c => string.Equals(c.FullTag, tag, StringComparison.OrdinalIgnoreCase));
            }

            private void StringLiterals(int start, int end, List<CodeDiagnostic> result)
            {
                var doc = _doc;
                bool tagsOn = _p.Strings != null, assetsOn = _p.GamePackageExists != null;
                if (!tagsOn && !assetsOn) return;
                int lastLine = -1;
                for (int k = doc.TokenIndexFrom(start); k < doc.Tokens.Length && doc.Tokens[k].Start < end; k++)
                {
                    var t = doc.Tokens[k];
                    if (t.Kind != TokenKind.String) continue;
                    var (line, col) = doc.PositionOf(t.Start);
                    if (line == lastLine) continue; // one literal of interest per line is plenty; avoids re-analysing long lines
                    string text = doc.LineText(line);
                    int quote = text.IndexOf('"', Math.Min(col, text.Length));
                    if (quote < 0) continue;
                    var site = StringLiteralContext.Analyze(text, quote + 1);
                    if (!site.InString || site.Kind == StringLiteralKind.None) continue;
                    if (site.ContentEnd >= text.Length || text[site.ContentEnd] != '"') continue; // unterminated
                    string content = text.Substring(site.ContentStart, site.ContentEnd - site.ContentStart);
                    int cStart = doc.OffsetOf(line, site.ContentStart), cEnd = doc.OffsetOf(line, site.ContentEnd);
                    if (_caret >= cStart && _caret <= cEnd) continue;
                    if (InInactive(doc, t.Start)) continue;
                    lastLine = line;
                    if (site.Kind == StringLiteralKind.GameplayTag && tagsOn) Tag(site, text, line, content, result);
                    else if (site.Kind == StringLiteralKind.AssetPath && assetsOn) Asset(site, text, line, quote, content, result);
                }
            }

            private void Tag(StringLiteralSite site, string text, int line, string content, List<CodeDiagnostic> result)
            {
                if (site.Callee != "RequestGameplayTag" || site.ArgumentIndex != 0) return;
                _stats.Checked++;
                if (!UhtRules.IsPlainTag(content)) { _stats.Skip("ue0007:not-a-tag"); return; }
                // RequestGameplayTag(Tag, /*ErrorIfNotFound*/ false): a second argument means the caller handles a missing tag
                int after = site.ContentEnd + 1;
                while (after < text.Length && (text[after] == ')' || char.IsWhiteSpace(text[after]))) after++;
                if (after < text.Length && text[after] == ',') { _stats.Skip("ue0007:second-argument"); return; }
                string guard = TagGuard(out var tree);
                if (guard != null) { _stats.Skip("ue0007:" + guard); return; }
                if (TagKnown(tree, content, out bool parentKnown)) return;
                if (!parentKnown) { _stats.Skip("ue0007:unknown-parent"); return; }
                Report(result, new CodeDiagnostic
                {
                    Line = line, Column = site.ContentStart, Length = Math.Max(1, content.Length), Severity = CodeDiagnosticSeverity.Warning,
                    Code = CodeDiagnosticCodes.UnknownGameplayTag, Message = $"Gameplay tag '{content}' is not defined", Args = new List<string> { content },
                });
            }

            private void Asset(StringLiteralSite site, string text, int line, int quote, string content, List<CodeDiagnostic> result)
            {
                string pkg = UhtRules.GamePackagePath(content);
                if (pkg == null) return;
                _stats.Checked++;
                if (_p.IndexReady != null && !_p.IndexReady()) { _stats.Skip("ue0008:index-not-ready"); return; }
                // concatenation: "/Game/A/" + Name, Prefix + "/Game/A"
                int after = site.ContentEnd + 1;
                while (after < text.Length && (text[after] == ')' || char.IsWhiteSpace(text[after]))) after++;
                if (after < text.Length && (text[after] == '+' || text[after] == '/' || text[after] == '%')) { _stats.Skip("ue0008:concatenated"); return; }
                int before = quote - 1;
                while (before >= 0 && (char.IsWhiteSpace(text[before]) || text[before] == '(' || TypeText.IsIdentPart(text[before]))) before--;
                if (before >= 0 && (text[before] == '+' || text[before] == '/')) { _stats.Skip("ue0008:concatenated"); return; }
                bool? exists = _p.GamePackageExists(pkg);
                if (exists == null) { _stats.Skip("ue0008:unknown"); return; }
                if (exists.Value) return;
                Report(result, new CodeDiagnostic
                {
                    Line = line, Column = site.ContentStart, Length = Math.Max(1, content.Length), Severity = CodeDiagnosticSeverity.Warning,
                    Code = CodeDiagnosticCodes.UnknownAssetPath, Message = $"Asset '{pkg}' does not exist", Args = new List<string> { pkg },
                });
            }
        }
    }
}
