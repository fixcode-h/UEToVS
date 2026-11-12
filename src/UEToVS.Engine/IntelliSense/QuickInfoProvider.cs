using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using UEToVS.Contracts;
using UEToVS.Engine.Navigation;
using UEToVS.Engine.Syntax;

namespace UEToVS.Engine.IntelliSense
{
    /// <summary>Formats declarations for Quick Info: modifiers, return type, qualified name, parameters, const / override / = 0.</summary>
    public static class DeclarationFormatter
    {
        public static string Format(Declaration d)
        {
            if (d == null) return null;
            var m = d.Modifiers;
            string qualified = d.QualifiedName;
            var sb = new StringBuilder();
            if ((m & DeclModifiers.Template) != 0 && !string.IsNullOrEmpty(d.TemplateParameters)) sb.Append("template<").Append(d.TemplateParameters).Append("> ");
            switch (d.Kind)
            {
                case SymbolKind.Function:
                case SymbolKind.Method:
                case SymbolKind.Constructor:
                case SymbolKind.Destructor:
                    if ((m & DeclModifiers.Static) != 0) sb.Append("static ");
                    if ((m & DeclModifiers.Virtual) != 0) sb.Append("virtual ");
                    if ((m & DeclModifiers.Explicit) != 0) sb.Append("explicit ");
                    if ((m & DeclModifiers.Constexpr) != 0) sb.Append("constexpr ");
                    if ((m & DeclModifiers.Inline) != 0) sb.Append("inline ");
                    if (!string.IsNullOrEmpty(d.Type) && d.Kind != SymbolKind.Constructor && d.Kind != SymbolKind.Destructor) sb.Append(d.Type).Append(' ');
                    sb.Append(qualified).Append('(').Append(d.Parameters ?? string.Empty).Append(')');
                    if ((m & DeclModifiers.Const) != 0) sb.Append(" const");
                    if ((m & DeclModifiers.Noexcept) != 0) sb.Append(" noexcept");
                    if ((m & DeclModifiers.Override) != 0) sb.Append(" override");
                    if ((m & DeclModifiers.Final) != 0) sb.Append(" final");
                    if ((m & DeclModifiers.Pure) != 0) sb.Append(" = 0");
                    if ((m & DeclModifiers.Defaulted) != 0) sb.Append(" = default");
                    if ((m & DeclModifiers.Deleted) != 0) sb.Append(" = delete");
                    break;
                case SymbolKind.Field:
                case SymbolKind.Variable:
                    if ((m & DeclModifiers.Static) != 0) sb.Append("static ");
                    if ((m & DeclModifiers.Extern) != 0) sb.Append("extern ");
                    if ((m & DeclModifiers.Constexpr) != 0) sb.Append("constexpr ");
                    if ((m & DeclModifiers.Mutable) != 0) sb.Append("mutable ");
                    if (!string.IsNullOrEmpty(d.Type)) sb.Append(d.Type).Append(' ');
                    else if (d.Signature != null && d.Signature.StartsWith("DECLARE", StringComparison.Ordinal)) return d.Signature;
                    sb.Append(qualified);
                    break;
                case SymbolKind.EnumValue:
                    sb.Append(qualified);
                    if (!string.IsNullOrEmpty(d.Value)) sb.Append(" = ").Append(d.Value);
                    break;
                case SymbolKind.Class:
                case SymbolKind.Struct:
                case SymbolKind.Union:
                    {
                        sb.Append(d.Kind == SymbolKind.Class ? "class " : d.Kind == SymbolKind.Struct ? "struct " : "union ").Append(qualified);
                        if (d.Bases != null && d.Bases.Length > 0) sb.Append(" : ").Append(string.Join(", ", d.Bases.Select(b => "public " + b)));
                        break;
                    }
                case SymbolKind.Enum:
                    sb.Append((m & DeclModifiers.Scoped) != 0 ? "enum class " : "enum ").Append(qualified);
                    if (!string.IsNullOrEmpty(d.Value)) sb.Append(" : ").Append(d.Value);
                    break;
                case SymbolKind.Namespace:
                    sb.Append("namespace ").Append(qualified);
                    break;
                case SymbolKind.Typedef:
                    if (d.Signature != null && d.Signature.StartsWith("using ", StringComparison.Ordinal)) sb.Append("using ").Append(qualified).Append(" = ").Append(d.Type);
                    else sb.Append("typedef ").Append(d.Type).Append(' ').Append(qualified);
                    break;
                default:
                    return d.Signature ?? qualified;
            }
            return sb.ToString();
        }

        /// <summary>UPROPERTY(...) / UFUNCTION(...) etc. with a line of readable tags; null for non-reflected declarations.</summary>
        public static string Reflection(Declaration d)
        {
            if (d == null || d.Ue == UeFlags.None || d.Ue == UeFlags.GeneratedBody) return null;
            string macro = (d.Ue & UeFlags.UFunction) != 0 ? "UFUNCTION" : (d.Ue & UeFlags.UProperty) != 0 ? "UPROPERTY" : (d.Ue & UeFlags.UClass) != 0 ? "UCLASS" :
                (d.Ue & UeFlags.UStruct) != 0 ? "USTRUCT" : (d.Ue & UeFlags.UEnum) != 0 ? "UENUM" : (d.Ue & UeFlags.UInterface) != 0 ? "UINTERFACE" :
                (d.Ue & UeFlags.UMeta) != 0 ? "UMETA" : (d.Ue & UeFlags.UDelegate) != 0 ? "UDELEGATE" : null;
            if (macro == null) return null;
            if (d.Kind == SymbolKind.Delegate) return "Dynamic delegate (Blueprint-compatible)";
            var sb = new StringBuilder(macro).Append('(').Append(d.Specifiers ?? string.Empty).Append(')');
            var tags = Tags(d.Specifiers);
            if (tags.Count > 0) sb.Append('\n').Append(string.Join(" · ", tags));
            string meta = Meta(d.Specifiers);
            if (meta != null) sb.Append("\nmeta: ").Append(meta);
            return sb.ToString();
        }

        private static readonly (string Spec, string Tag)[] TagMap =
        {
            ("BlueprintCallable", "Blueprint callable"), ("BlueprintPure", "Blueprint pure"), ("BlueprintImplementableEvent", "Blueprint event"),
            ("BlueprintNativeEvent", "Blueprint native event (_Implementation)"), ("BlueprintReadWrite", "Blueprint read/write"),
            ("BlueprintReadOnly", "Blueprint read-only"), ("BlueprintAssignable", "Blueprint assignable"), ("EditAnywhere", "Editable"),
            ("EditDefaultsOnly", "Editable (defaults)"), ("EditInstanceOnly", "Editable (instances)"), ("VisibleAnywhere", "Visible"),
            ("VisibleDefaultsOnly", "Visible (defaults)"), ("VisibleInstanceOnly", "Visible (instances)"), ("Replicated", "Replicated"),
            ("ReplicatedUsing", "Replicated (OnRep)"), ("Server", "Server RPC"), ("Client", "Client RPC"), ("NetMulticast", "Multicast RPC"),
            ("Reliable", "Reliable"), ("Unreliable", "Unreliable"), ("WithValidation", "Validated"), ("Exec", "Console command"),
            ("Transient", "Transient"), ("SaveGame", "SaveGame"), ("Config", "Config"), ("Blueprintable", "Blueprintable"),
            ("BlueprintType", "Blueprint type"), ("Abstract", "Abstract"), ("CallInEditor", "Call in editor"),
        };

        private static List<string> Tags(string specifiers)
        {
            var list = new List<string>();
            if (string.IsNullOrEmpty(specifiers)) return list;
            string top = specifiers;
            int meta = IndexOfMeta(specifiers);
            if (meta >= 0) top = specifiers.Substring(0, meta);
            foreach (var (spec, tag) in TagMap)
                if (TypeText.HasSpecifier(top, spec)) list.Add(tag);
            int cat = top.IndexOf("Category", StringComparison.OrdinalIgnoreCase);
            if (cat >= 0)
            {
                int eq = top.IndexOf('=', cat);
                if (eq > 0)
                {
                    int end = eq + 1;
                    while (end < top.Length && top[end] == ' ') end++;
                    int stop = top[end < top.Length ? end : top.Length - 1] == '"' ? top.IndexOf('"', end + 1) + 1 : top.IndexOf(',', end);
                    if (stop <= end) stop = top.Length;
                    list.Add("Category: " + top.Substring(end, stop - end).Trim().Trim('"'));
                }
            }
            return list;
        }

        private static int IndexOfMeta(string s)
        {
            int i = s.IndexOf("meta", StringComparison.OrdinalIgnoreCase);
            while (i >= 0)
            {
                int j = i + 4;
                while (j < s.Length && s[j] == ' ') j++;
                if (j < s.Length && s[j] == '=' && (i == 0 || !TypeText.IsIdentPart(s[i - 1]))) return i;
                i = s.IndexOf("meta", i + 4, StringComparison.OrdinalIgnoreCase);
            }
            return -1;
        }

        private static string Meta(string specifiers)
        {
            if (string.IsNullOrEmpty(specifiers)) return null;
            int i = IndexOfMeta(specifiers);
            if (i < 0) return null;
            int open = specifiers.IndexOf('(', i);
            if (open < 0) return null;
            int depth = 0;
            for (int k = open; k < specifiers.Length; k++)
            {
                if (specifiers[k] == '(') depth++;
                else if (specifiers[k] == ')' && --depth == 0) return specifiers.Substring(open + 1, k - open - 1).Trim();
            }
            return specifiers.Substring(open + 1).Trim();
        }
    }

    /// <summary>Quick Info: declaration, documentation, UE reflection summary, macro expansion, overload count.</summary>
    public static class QuickInfoProvider
    {
        public static QuickInfoResponse Get(IsSession s, int offset)
        {
            var resp = new QuickInfoResponse();
            var doc = s.Doc;
            int ti = doc.TokenAt(offset);
            if (ti < 0 || doc.Tokens[ti].Kind != TokenKind.Identifier) return resp;
            var tok = doc.Tokens[ti];
            var (line, col) = doc.PositionOf(tok.Start);
            resp.Line = line;
            resp.Column = col;
            resp.Length = LineMap.Utf16Length(new ReadOnlySpan<byte>(doc.Text, tok.Start, tok.Length));
            string word = doc.TokenText(ti);

            // a reflection specifier inside UPROPERTY(...) / meta=(...)
            if (CompletionSiteAnalyzer.SpecifierContextAt(doc, ti, out string macro, out bool meta, out bool valuePos) && !valuePos)
            {
                var spec = s.Knowledge.FindSpecifier(macro, word, meta);
                if (spec != null)
                {
                    resp.Declaration = (meta ? macro + " meta " : macro + " specifier ") + spec.Name + (spec.Value ?? string.Empty);
                    resp.Documentation = spec.Doc + (spec.Conflicts.Count > 0 ? "\nCannot be combined with: " + string.Join(", ", spec.Conflicts) : string.Empty);
                    resp.Confidence = ResolutionConfidence.Exact;
                    return resp;
                }
            }

            var r = SymbolResolver.ResolveToken(s.Ctx, doc, ti, 0);
            if (r.Local != null)
            {
                var l = r.Local;
                resp.Symbol = r.Candidates.FirstOrDefault();
                resp.Declaration = (l.IsParameter ? "(parameter) " : "(local) ") + (l.Type + " " + l.Name).Trim();
                resp.Confidence = ResolutionConfidence.Exact;
                return resp;
            }

            // member access through a chain the resolver could not type: use the IntelliSense inference
            var cands = r.Candidates;
            var confidence = r.Confidence;
            int before = ti - 1;
            if ((doc.Is(before, Punct.Dot) || doc.Is(before, Punct.Arrow)) && confidence <= ResolutionConfidence.NameOnly)
            {
                var types = new ExpressionTypes(s, doc.EnclosingClass(tok.Start, out _));
                string left = types.TypeOf(before - 1);
                if (left != null)
                {
                    var shape = TypeShape.Parse(left);
                    string target = shape.IsPointerLike ? shape.Pointee() : left;
                    var rec = s.FindRecord(TypeShape.Parse(target).Head);
                    if (rec != null)
                    {
                        var hits = s.Ctx.MemberLookup(word, TypeText.Qualified(rec.Symbol.Container, rec.Symbol.Name), out _);
                        if (hits.Count > 0)
                        {
                            cands = hits;
                            confidence = hits.Select(SymbolResolver.EntityKey).Distinct().Count() == 1 ? ResolutionConfidence.Exact : ResolutionConfidence.Scoped;
                        }
                    }
                }
            }

            // macros: knowledge-base doc + expansion
            var expander = new MacroExpander(s);
            var macroSym = cands.FirstOrDefault(c => c.Kind == SymbolKind.Macro);
            bool ueMacro = r.Reason == "ue-macro" || s.Knowledge.Lookup(word) != null && cands.Count == 0;
            if (macroSym != null || ueMacro || cands.Count == 0 && s.Knowledge.GetMacroDoc(word) != null)
            {
                var def = expander.Find(word);
                resp.Symbol = def?.Symbol ?? macroSym;
                resp.Declaration = def != null ? "#define " + def.Name + (def.IsFunctionLike ? "(" + string.Join(", ", def.Parameters) + ")" : string.Empty) + " " + Short(def.Body)
                                               : "macro " + word;
                resp.Documentation = s.Knowledge.GetMacroDoc(word);
                if (resp.Documentation == null && macroSym != null)
                {
                    var (md, mi) = s.DeclOf(macroSym);
                    if (md != null) resp.Documentation = DocComments.Get(md, mi);
                }
                if (def != null)
                {
                    var args = def.IsFunctionLike ? MacroExpander.InvocationArgs(doc, ti) : null;
                    var (one, full) = expander.Expand(def, args);
                    resp.MacroExpansion = one == full ? one : one + "\n\n" + full;
                }
                resp.Confidence = def != null || ueMacro ? ResolutionConfidence.Exact : ResolutionConfidence.NameOnly;
                if (def != null || ueMacro || resp.Documentation != null) return resp;
            }
            if (cands.Count == 0)
            {
                resp.Confidence = ResolutionConfidence.None;
                return resp;
            }

            // pick the best symbol: on a call the fitting overloads, declarations (headers) before definitions, project first
            var pool = cands;
            if (r.CallArguments != null) pool = SymbolResolver.FilterByCall(cands, r.CallArguments);
            var primary = r.OnDeclaration ?? pool.OrderBy(c => c.IsDefinition && TypeText.IsCallableKind(c.Kind) ? 1 : 0)
                .ThenBy(c => TypeText.IsProject(c.Origin) ? 0 : 1).First();
            resp.Symbol = primary;
            resp.Confidence = confidence;
            var (pd, pi) = s.DeclOf(primary);
            Declaration decl = pd != null ? pd.Decls[pi] : null;
            resp.Declaration = decl != null ? DeclarationFormatter.Format(decl) : primary.Signature ?? TypeText.Qualified(primary.Container, primary.Name);

            // docs and reflection usually live on the header declaration; a definition borrows them
            string key = SymbolResolver.EntityKey(primary);
            string pkey = TypeText.ParameterKey(primary.Signature);
            string docs = pd != null ? DocComments.Get(pd, pi) : null;
            string reflection = DeclarationFormatter.Reflection(decl);
            if (docs == null || reflection == null)
            {
                foreach (var other in cands.Where(c => c != primary && SymbolResolver.EntityKey(c) == key && TypeText.ParameterKey(c.Signature) == pkey))
                {
                    var (od, oi) = s.DeclOf(other);
                    if (od == null) continue;
                    docs ??= DocComments.Get(od, oi);
                    reflection ??= DeclarationFormatter.Reflection(od.Decls[oi]);
                    if (docs != null && reflection != null) break;
                }
            }
            resp.Documentation = docs;
            resp.Reflection = reflection;
            if (TypeText.IsCallableKind(primary.Kind))
            {
                int overloads = cands.Where(c => SymbolResolver.EntityKey(c) == key).Select(c => TypeText.ParameterKey(c.Signature) ?? string.Empty).Distinct().Count();
                resp.OverloadCount = Math.Max(0, overloads - 1);
            }
            return resp;
        }

        private static string Short(string body)
        {
            if (string.IsNullOrEmpty(body)) return string.Empty;
            return body.Length <= 200 ? body : body.Substring(0, 199) + "…";
        }
    }
}
