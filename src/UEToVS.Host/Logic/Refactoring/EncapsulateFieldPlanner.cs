using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace UEToVS.Host.Logic.Refactoring
{
    /// <summary>A member variable declaration in a class body (header text offsets).</summary>
    public sealed class FieldDeclaration
    {
        public string ClassName { get; set; }
        public int ClassHintLine { get; set; }
        public string Name { get; set; }

        /// <summary>Type as written (normalised spaces), e.g. <c>TObjectPtr&lt;UStaticMeshComponent&gt;</c>.</summary>
        public string Type { get; set; }

        public bool IsBitfield { get; set; }
        public bool IsConst { get; set; }

        /// <summary>UPROPERTY(...) arguments, or null without UPROPERTY.</summary>
        public string Specifiers { get; set; }

        /// <summary>Offsets of the text between the UPROPERTY parentheses (-1 without UPROPERTY).</summary>
        public int SpecifiersStart { get; set; } = -1;

        public int SpecifiersEnd { get; set; } = -1;
        public int Line { get; set; }

        public bool IsBool => IsBitfield || Type == "bool";
        public bool HasUProperty => Specifiers != null;
        public override string ToString() => ClassName + "::" + Name + " : " + Type;
    }

    /// <summary>Options of Encapsulate Field / Generate Getter / Setter.</summary>
    public sealed class EncapsulateOptions
    {
        public bool Getter { get; set; } = true;
        public bool Setter { get; set; } = true;

        /// <summary>UPROPERTY(BlueprintGetter=GetX) + UFUNCTION(BlueprintGetter) on the getter.</summary>
        public bool BlueprintGetter { get; set; }

        public bool BlueprintSetter { get; set; }
    }

    /// <summary>Result: the class-body block (indented by CodePlacement) and the UPROPERTY edit, or a problem.</summary>
    public sealed class EncapsulatePlan
    {
        public string Problem { get; set; }
        public string GetterName { get; set; }
        public string SetterName { get; set; }

        /// <summary>Lines of the generated members (no indentation).</summary>
        public List<string> Lines { get; } = new List<string>();

        /// <summary>The new UPROPERTY(...) arguments, or null when unchanged.</summary>
        public string NewSpecifiers { get; set; }

        public List<string> Generated { get; } = new List<string>();
        public bool Ok => Problem == null && Lines.Count > 0;
    }

    /// <summary>
    /// Encapsulate Field / Generate Getter / Setter (pure, v0.4.5). For a member variable: <c>T GetX() const</c> (const T&amp; for
    /// structs, the raw pointer for TObjectPtr), bool fields <c>IsX()</c> / <c>SetX(bool)</c> (b prefix dropped; IsX / HasX / CanX names
    /// kept), <c>void SetX(T InX)</c>; inline in the class's public section. On a UPROPERTY, BlueprintGetter / BlueprintSetter can be
    /// added to the property and the functions become UFUNCTION(BlueprintGetter / BlueprintSetter) (BlueprintReadOnly is removed with a
    /// setter). Problem codes: "no-field", "static", "array", "several", "reference", "exists", "nothing".
    /// </summary>
    public static class EncapsulateFieldPlanner
    {
        private static readonly string[] BoolVerbs = { "Is", "Has", "Can", "Should", "Was", "Will", "Does", "Did", "Allow", "Needs", "Use" };

        /// <summary>The member variable declared on the caret's line(s) in a class body, or null with a reason.</summary>
        public static FieldDeclaration FieldAt(string headerText, int offset, out string problem)
        {
            problem = "no-field";
            string text = headerText ?? string.Empty;
            var t = CppTextScan.Tokenize(text);
            var cls = Generate.UeCodeGenerator.FindClass(text, offset);
            if (cls == null) return null;
            int open = CodeScan.IndexAtOrAfter(t, cls.BodyOpen), close = CodeScan.IndexAtOrAfter(t, cls.BodyClose);
            if (open >= t.Count || close >= t.Count || !t[open].Is("{") || !t[close].Is("}")) return null;
            // member statement around the offset at class depth
            int stmt = open + 1, depth = 0;
            for (int k = open + 1; k < close; k++)
            {
                var tok = t[k];
                if (tok.Is("{") || tok.Is("(") || tok.Is("[")) { depth++; continue; }
                if (tok.Is("}") || tok.Is(")") || tok.Is("]"))
                {
                    depth--;
                    if (depth == 0 && tok.Is("}") && !(k + 1 < close && t[k + 1].Is(";")) && !InitializerBrace(t, k)) stmt = k + 1;
                    continue;
                }
                if (depth != 0) continue;
                if (tok.Is(":") && k > 0 && (t[k - 1].Is("public") || t[k - 1].Is("protected") || t[k - 1].Is("private"))) { stmt = k + 1; continue; }
                if (!tok.Is(";")) continue;
                int startOff = stmt < t.Count ? CodeScan.LineStart(text, t[stmt].Offset) : int.MaxValue;
                if (offset >= startOff && offset <= CodeScan.LineEnd(text, tok.Offset))
                    return Parse(text, t, stmt, k, cls.Name, out problem, cls.BodyOpen);
                stmt = k + 1;
            }
            return null;
        }

        private static bool InitializerBrace(List<ScanToken> t, int close)
        {
            int o = CodeScan.MatchOpen(t, close);
            return o > 0 && (t[o - 1].IsIdent || t[o - 1].Is("=") || t[o - 1].Is(">"));
        }

        private static FieldDeclaration Parse(string text, List<ScanToken> t, int first, int semi, string cls, out string problem, int classOpen)
        {
            problem = "no-field";
            var f = new FieldDeclaration { ClassName = cls, Line = t[first].Line, ClassHintLine = MoveImplementationPlanner.LineOfOffset(text, classOpen) };
            int k = first;
            if (t[k].Is("UPROPERTY") && k + 1 < semi && t[k + 1].Is("("))
            {
                int c = CppTextScan.MatchClose(t, k + 1);
                f.SpecifiersStart = CodeScan.End(t[k + 1]);
                f.SpecifiersEnd = t[c].Offset;
                f.Specifiers = text.Substring(f.SpecifiersStart, f.SpecifiersEnd - f.SpecifiersStart);
                k = c + 1;
            }
            // other UE macros with arguments before the type (UE_DEPRECATED(...)) are not fields we touch
            if (k < semi && t[k].IsIdent && MoveImplementationPlanner.IsMacroName(t[k].Text) && k + 1 < semi && t[k + 1].Is("(")) return null;
            if (k >= semi) return null;
            if (t[k].Is("static") || t[k].Is("constexpr") || t[k].Is("friend") || t[k].Is("using") || t[k].Is("typedef") || t[k].Is("template") ||
                t[k].Is("class") || t[k].Is("struct") || t[k].Is("enum") || t[k].Is("virtual"))
            {
                problem = t[k].Is("static") || t[k].Is("constexpr") ? "static" : "no-field";
                return null;
            }
            if (t[k].Is("mutable")) k++;
            // name: the identifier before '=' / '{' / ':' / '[' / ';' at depth 0
            int end = semi, depth = 0;
            for (int m = k; m < semi; m++)
            {
                var tok = t[m];
                if (tok.Is("<")) depth++;
                else if (tok.Is(">")) depth--;
                else if (depth == 0 && tok.Is("(")) return null; // a function
                else if (depth == 0 && tok.Is(",")) { problem = "several"; return null; }
                else if (depth == 0 && (tok.Is("=") || tok.Is("{") || tok.Is("[") || (tok.Is(":") && !tok.Is("::")))) { end = m; break; }
            }
            if (end < semi && t[end].Is("[")) { problem = "array"; return null; }
            int nameTok = end - 1;
            if (nameTok <= k || !t[nameTok].IsIdent) return null;
            f.Name = t[nameTok].Text;
            if (end < semi && t[end].Is(":")) f.IsBitfield = true;
            string type = text.Substring(t[k].Offset, t[nameTok].Offset - t[k].Offset);
            f.Type = ChangeSignaturePlanner.NormalizeSpaces(type);
            if (f.Type.EndsWith("&", StringComparison.Ordinal)) { problem = "reference"; return null; }
            if (f.Type.StartsWith("const ", StringComparison.Ordinal) && !f.Type.EndsWith("*", StringComparison.Ordinal)) f.IsConst = true;
            if (f.Type.Length == 0 || !RenamePlanner.IsValidIdentifier(f.Name)) return null;
            problem = null;
            return f;
        }

        /// <summary>Base name for accessors: <c>bIsDead</c> → <c>IsDead</c>, <c>bVisible</c> → <c>Visible</c>, <c>Health</c> → <c>Health</c>.</summary>
        public static string BaseName(FieldDeclaration f)
        {
            string n = f.Name;
            if (f.IsBool && n.Length > 1 && n[0] == 'b' && char.IsUpper(n[1])) n = n.Substring(1);
            else if (n.Length > 2 && (n.StartsWith("m_", StringComparison.Ordinal))) n = n.Substring(2);
            if (n.Length > 0 && char.IsLower(n[0])) n = char.ToUpperInvariant(n[0]) + n.Substring(1);
            return n;
        }

        /// <summary>Getter / setter names (UE style).</summary>
        public static (string Getter, string Setter) Names(FieldDeclaration f)
        {
            string b = BaseName(f);
            if (!f.IsBool) return ("Get" + b, "Set" + b);
            bool verb = BoolVerbs.Any(v => b.Length > v.Length && b.StartsWith(v, StringComparison.Ordinal) && char.IsUpper(b[v.Length]));
            return (verb ? b : "Is" + b, "Set" + b);
        }

        /// <summary>Type returned by the getter / taken by the setter.</summary>
        public static string ValueType(FieldDeclaration f, out bool byConstRef)
        {
            byConstRef = false;
            if (f.IsBool) return "bool";
            string t = f.Type;
            if (t.StartsWith("const ", StringComparison.Ordinal) && !t.EndsWith("*", StringComparison.Ordinal)) t = t.Substring(6).Trim();
            if (t.StartsWith("TObjectPtr<", StringComparison.Ordinal) && t.EndsWith(">", StringComparison.Ordinal))
                return t.Substring("TObjectPtr<".Length, t.Length - "TObjectPtr<".Length - 1).Trim() + "*";
            if (ExtractMethodPlanner.IsCheap(t)) return t;
            byConstRef = true;
            return t;
        }

        public static EncapsulatePlan Plan(FieldDeclaration f, EncapsulateOptions o, Func<string, bool> classDeclares)
        {
            var plan = new EncapsulatePlan();
            if (f == null) { plan.Problem = "no-field"; return plan; }
            var (getter, setter) = Names(f);
            plan.GetterName = getter;
            plan.SetterName = setter;
            bool wantGetter = o.Getter && !(classDeclares?.Invoke(getter) ?? false);
            bool wantSetter = o.Setter && !f.IsConst && !(classDeclares?.Invoke(setter) ?? false);
            if (!wantGetter && !wantSetter)
            {
                plan.Problem = o.Getter || o.Setter ? "exists" : "nothing";
                return plan;
            }
            string type = ValueType(f, out bool byRef);
            bool bpGetter = wantGetter && o.BlueprintGetter && f.HasUProperty;
            bool bpSetter = wantSetter && o.BlueprintSetter && f.HasUProperty;
            if (wantGetter)
            {
                // a BlueprintGetter returns by value (UHT wants the property type)
                string ret = byRef && !bpGetter ? "const " + type + "&" : type;
                if (bpGetter) plan.Lines.Add("UFUNCTION(BlueprintGetter)");
                plan.Lines.Add(ret + " " + getter + "() const { return " + f.Name + "; }");
                plan.Generated.Add(getter);
            }
            if (wantSetter)
            {
                string param = f.IsBool ? "bIn" + BaseName(f) : "In" + BaseName(f);
                string ptype = byRef ? "const " + type + "&" : type;
                if (bpSetter) plan.Lines.Add("UFUNCTION(BlueprintSetter)");
                plan.Lines.Add("void " + setter + "(" + ptype + " " + param + ") { " + f.Name + " = " + param + "; }");
                plan.Generated.Add(setter);
            }
            if (bpGetter || bpSetter) plan.NewSpecifiers = WithBlueprintAccessors(f.Specifiers, bpGetter ? getter : null, bpSetter ? setter : null);
            return plan;
        }

        /// <summary>UPROPERTY arguments with BlueprintGetter=… / BlueprintSetter=… (replacing existing ones; BlueprintReadOnly dropped with a setter).</summary>
        public static string WithBlueprintAccessors(string specifiers, string getter, string setter)
        {
            var items = SignatureText.SplitTopLevel(specifiers ?? string.Empty, ',').Select(s => s.Trim()).Where(s => s.Length > 0).ToList();
            bool Key(string item, string key)
            {
                int eq = item.IndexOf('=');
                string k = (eq >= 0 ? item.Substring(0, eq) : item).Trim();
                return string.Equals(k, key, StringComparison.OrdinalIgnoreCase);
            }
            if (getter != null) items.RemoveAll(i => Key(i, "BlueprintGetter"));
            if (setter != null)
            {
                items.RemoveAll(i => Key(i, "BlueprintSetter"));
                items.RemoveAll(i => Key(i, "BlueprintReadOnly"));
            }
            // before meta=(...) when present
            int meta = items.FindIndex(i => Key(i, "meta"));
            var add = new List<string>();
            if (getter != null) add.Add("BlueprintGetter=" + getter);
            if (setter != null) add.Add("BlueprintSetter=" + setter);
            if (meta >= 0) items.InsertRange(meta, add);
            else items.AddRange(add);
            return string.Join(", ", items);
        }

        /// <summary>Whether the class body declares a member function named <paramref name="name"/> (text scan).</summary>
        public static bool ClassDeclares(string headerText, FieldDeclaration f, string name)
        {
            var t = CppTextScan.Tokenize(headerText ?? string.Empty);
            var body = CppTextScan.FindClassBody(t, f.ClassName, f.ClassHintLine);
            if (body == null) return false;
            int depth = 0;
            for (int k = body.OpenToken + 1; k < body.CloseToken; k++)
            {
                if (t[k].Is("{")) depth++;
                else if (t[k].Is("}")) depth--;
                else if (depth == 0 && t[k].Is(name) && k + 1 < body.CloseToken && t[k + 1].Is("(")) return true;
            }
            return false;
        }

        /// <summary>Edits: the generated members in the class's public section, and the UPROPERTY change.</summary>
        public static List<TextEdit> Edits(string filePath, string headerText, FieldDeclaration f, EncapsulatePlan plan, out InsertPoint point)
        {
            var edits = new List<TextEdit>();
            point = CodePlacement.Declaration(headerText, f.ClassName, f.ClassHintLine, null, AccessKind.Public, plan.Lines);
            if (point == null) return edits;
            edits.Add(TextEdit.Insert(filePath, point.Line, point.Text));
            if (plan.NewSpecifiers != null && f.SpecifiersStart >= 0)
            {
                var starts = TextEditApplier.LineStarts(headerText);
                int line = StructuredEditComposer.LineOf(starts, f.SpecifiersStart);
                edits.Add(new TextEdit
                {
                    FilePath = filePath, Line = line, Column = f.SpecifiersStart - starts[line], Length = f.SpecifiersEnd - f.SpecifiersStart,
                    OldText = f.Specifiers, NewText = plan.NewSpecifiers,
                });
            }
            return edits;
        }
    }
}
