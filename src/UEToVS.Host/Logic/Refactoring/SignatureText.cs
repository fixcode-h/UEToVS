using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using UEToVS.Contracts;

namespace UEToVS.Host.Logic.Refactoring
{
    /// <summary>What Create Implementation must generate for a UFUNCTION (UHT declares Name itself; we write the bodies).</summary>
    public sealed class UeImplementationKinds
    {
        /// <summary>Name_Implementation instead of Name (Server / Client / NetMulticast / BlueprintNativeEvent).</summary>
        public bool Implementation { get; set; }

        /// <summary>bool Name_Validate(...) { return true; } (Server + WithValidation).</summary>
        public bool Validate { get; set; }
    }

    /// <summary>
    /// Code text for declarations and definitions generated from the engine's parse (<see cref="FunctionSignature"/>):
    /// default arguments only in declarations; virtual / override / static / explicit / inline only in declarations;
    /// const / noexcept on both; UE RPC / BlueprintNativeEvent bodies become Name_Implementation (+ Name_Validate).
    /// Pure string work, unit-tested.
    /// </summary>
    public static class SignatureText
    {
        private static readonly string[] ImplementationSpecifiers = { "Server", "Client", "NetMulticast", "BlueprintNativeEvent" };

        public static UeImplementationKinds UeKinds(FunctionSignature f)
        {
            var k = new UeImplementationKinds();
            if (f == null || (f.Ue & UeFlags.UFunction) == 0 || string.IsNullOrEmpty(f.Specifiers)) return k;
            k.Implementation = ImplementationSpecifiers.Any(s => HasSpecifier(f.Specifiers, s));
            k.Validate = HasSpecifier(f.Specifiers, "Server") && HasSpecifier(f.Specifiers, "WithValidation");
            return k;
        }

        /// <summary>Whole-item match in "A, B=C, meta=(X)" (before '='; parenthesised groups skipped).</summary>
        public static bool HasSpecifier(string specifiers, string word)
        {
            foreach (var item in SplitTopLevel(specifiers ?? string.Empty, ','))
            {
                string s = item.Trim();
                int eq = s.IndexOf('=');
                if (eq >= 0) s = s.Substring(0, eq).Trim();
                if (string.Equals(s, word, StringComparison.OrdinalIgnoreCase)) return true;
            }
            return false;
        }

        // ---------------------------------------------------------------- parameters

        /// <summary>Parameters split at top-level commas (template arguments, parentheses and braces respected).</summary>
        public static List<string> SplitParameters(string parameters)
        {
            var list = new List<string>();
            if (string.IsNullOrWhiteSpace(parameters)) return list;
            foreach (var p in SplitTopLevel(parameters, ','))
            {
                string t = p.Trim();
                if (t.Length > 0) list.Add(t);
            }
            if (list.Count == 1 && list[0] == "void") list.Clear();
            return list;
        }

        /// <summary>The parameter list without default arguments (for definitions): <c>int32 A = 1, FString B = TEXT("x")</c> → <c>int32 A, FString B</c>.</summary>
        public static string StripDefaults(string parameters)
        {
            var parts = SplitParameters(parameters).Select(p =>
            {
                int eq = IndexOfTopLevel(p, '=');
                return eq >= 0 ? p.Substring(0, eq).TrimEnd() : p;
            });
            return string.Join(", ", parts);
        }

        /// <summary>Gives unnamed parameters a name (Arg0, Arg1…) so an override implementation can forward them.</summary>
        public static string NameParameters(string parameters)
        {
            var parts = SplitParameters(parameters);
            for (int i = 0; i < parts.Count; i++)
            {
                string p = parts[i];
                int eq = IndexOfTopLevel(p, '=');
                string decl = eq >= 0 ? p.Substring(0, eq).TrimEnd() : p;
                string rest = eq >= 0 ? " " + p.Substring(eq).TrimStart() : string.Empty;
                if (decl == "..." || ParameterName(decl) != null) continue;
                parts[i] = decl + " Arg" + i + rest;
            }
            return string.Join(", ", parts);
        }

        private static readonly HashSet<string> TypeWords = new HashSet<string>(StringComparer.Ordinal)
        {
            "const", "volatile", "unsigned", "signed", "short", "long", "int", "char", "bool", "float", "double", "void", "wchar_t", "auto",
            "struct", "class", "enum", "typename", "char8_t", "char16_t", "char32_t",
        };

        /// <summary>Name of one parameter declaration (no default), or null when it is unnamed (<c>const FVector&amp;</c>, <c>int32</c>).</summary>
        public static string ParameterName(string declaration)
        {
            string p = (declaration ?? string.Empty).Trim();
            // array parameters: Name[N]
            while (p.EndsWith("]", StringComparison.Ordinal))
            {
                int lb = p.LastIndexOf('[');
                if (lb < 0) break;
                p = p.Substring(0, lb).TrimEnd();
            }
            int end = p.Length, s = end;
            while (s > 0 && IsIdentPart(p[s - 1])) s--;
            if (s == end) return null; // ends with * & > ) …
            string name = p.Substring(s, end - s);
            if (TypeWords.Contains(name) || char.IsDigit(name[0])) return null;
            string before = p.Substring(0, s).TrimEnd();
            if (before.Length == 0) return null; // just a type name
            if (before.EndsWith("::", StringComparison.Ordinal)) return null;
            // "struct FDamageEvent" / "const FVector" with no declarator: the word before is a type keyword only
            var words = before.Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);
            if (words.All(w => w == "const" || w == "volatile" || w == "struct" || w == "class" || w == "enum" || w == "typename")) return null;
            return name;
        }

        /// <summary>Argument list forwarding every parameter: <c>A, B</c> (unnamed ones as ArgN — call <see cref="NameParameters"/> first).</summary>
        public static string ForwardArguments(string parameters)
        {
            var names = new List<string>();
            var parts = SplitParameters(parameters);
            for (int i = 0; i < parts.Count; i++)
            {
                string p = parts[i];
                int eq = IndexOfTopLevel(p, '=');
                string decl = eq >= 0 ? p.Substring(0, eq) : p;
                if (decl.Trim() == "...") continue;
                names.Add(ParameterName(decl) ?? "Arg" + i);
            }
            return string.Join(", ", names);
        }

        // ---------------------------------------------------------------- generation

        /// <summary>Qualifiers after the parameter list that belong to both declaration and definition.</summary>
        private static string TrailingQualifiers(FunctionModifiers m)
        {
            var sb = new StringBuilder();
            if ((m & FunctionModifiers.Const) != 0) sb.Append(" const");
            if ((m & FunctionModifiers.Noexcept) != 0) sb.Append(" noexcept");
            return sb.ToString();
        }

        /// <summary>Simple class name of a qualified owner (namespaces are written by the enclosing namespace block, if any).</summary>
        public static string ClassName(string container)
        {
            if (string.IsNullOrEmpty(container)) return null;
            int i = container.LastIndexOf("::", StringComparison.Ordinal);
            return i < 0 ? container : container.Substring(i + 2);
        }

        private static string TemplateLine(FunctionSignature f, string indent) =>
            string.IsNullOrEmpty(f.TemplateParameters) ? string.Empty : indent + "template<" + f.TemplateParameters + ">" + "\n";

        private static bool HasReturnType(FunctionSignature f) =>
            f.Kind != SymbolKind.Constructor && f.Kind != SymbolKind.Destructor && !string.IsNullOrEmpty(f.ReturnType);

        /// <summary>
        /// Out-of-line definition for the .cpp: <c>Ret Class::Name(params-without-defaults) const\n{\n}\n</c>.
        /// <paramref name="nameOverride"/> replaces the function name (Name_Implementation); <paramref name="body"/> lines go inside.
        /// </summary>
        public static string Definition(FunctionSignature f, string className, string nameOverride = null, string returnOverride = null,
                                        IReadOnlyList<string> body = null, string newline = "\n")
        {
            var sb = new StringBuilder();
            sb.Append(TemplateLine(f, string.Empty));
            string ret = returnOverride ?? (HasReturnType(f) ? f.ReturnType : null);
            if (!string.IsNullOrEmpty(ret)) sb.Append(ret).Append(' ');
            if (!string.IsNullOrEmpty(className)) sb.Append(className).Append("::");
            sb.Append(nameOverride ?? f.Name).Append('(').Append(StripDefaults(f.Parameters)).Append(')');
            sb.Append(TrailingQualifiers(f.Modifiers)).Append(newline);
            sb.Append('{').Append(newline);
            if (body != null) foreach (var line in body) sb.Append('\t').Append(line).Append(newline);
            sb.Append('}').Append(newline);
            return sb.ToString();
        }

        /// <summary>
        /// Bodies Create Implementation writes for a header declaration: the function itself, or for UE RPC /
        /// BlueprintNativeEvent functions Name_Implementation (and Name_Validate returning true for Server + WithValidation).
        /// Each entry: (generated function name, text).
        /// </summary>
        public static List<(string Name, string Text)> Implementations(FunctionSignature f, string newline = "\n")
        {
            var result = new List<(string, string)>();
            string cls = ClassName(f.Container);
            var ue = UeKinds(f);
            if (!ue.Implementation)
            {
                result.Add((f.Name, Definition(f, cls, newline: newline)));
                return result;
            }
            result.Add((f.Name + "_Implementation", Definition(f, cls, f.Name + "_Implementation", newline: newline)));
            if (ue.Validate)
            {
                result.Add((f.Name + "_Validate", Definition(f, cls, f.Name + "_Validate", "bool", new[] { "return true;" }, newline)));
            }
            return result;
        }

        /// <summary>
        /// In-class declaration for a definition found in a .cpp: <c>\tRet Name(params) const;</c>. Default arguments are
        /// not invented (the definition has none); virtual/static are unknown at the definition and left out.
        /// </summary>
        public static string Declaration(FunctionSignature f, string indent = "\t", string newline = "\n")
        {
            var sb = new StringBuilder();
            sb.Append(TemplateLine(f, indent));
            sb.Append(indent);
            if (HasReturnType(f)) sb.Append(f.ReturnType).Append(' ');
            sb.Append(f.Name).Append('(').Append(f.Parameters ?? string.Empty).Append(')');
            sb.Append(TrailingQualifiers(f.Modifiers)).Append(';').Append(newline);
            return sb.ToString();
        }

        /// <summary>UE interfaces (IFoo) have no Super; their virtuals are usually pure.</summary>
        public static bool IsInterfaceName(string type)
        {
            string n = ClassName(type);
            return n != null && n.Length > 1 && n[0] == 'I' && char.IsUpper(n[1]);
        }

        /// <summary>Override declaration: <c>\tvirtual Ret Name(params) const override;</c> (defaults stripped, unnamed parameters named).</summary>
        public static string OverrideDeclaration(FunctionSignature f, string indent = "\t", string newline = "\n")
        {
            var sb = new StringBuilder();
            sb.Append(TemplateLine(f, indent)).Append(indent).Append("virtual ");
            if (HasReturnType(f)) sb.Append(f.ReturnType).Append(' ');
            sb.Append(f.Name).Append('(').Append(NameParameters(StripDefaults(f.Parameters))).Append(')');
            sb.Append(TrailingQualifiers(f.Modifiers)).Append(" override;").Append(newline);
            return sb.ToString();
        }

        /// <summary>Override implementation calling <c>Super::Name(args)</c> (no call for pure virtuals or interface methods).</summary>
        public static string OverrideDefinition(FunctionSignature f, string className, string newline = "\n")
        {
            var named = Copy(f);
            named.Parameters = NameParameters(StripDefaults(f.Parameters));
            bool returns = HasReturnType(f) && f.ReturnType.Trim() != "void";
            var body = new List<string>();
            bool callSuper = (f.Modifiers & FunctionModifiers.Pure) == 0 && !IsInterfaceName(f.DeclaringType);
            if (callSuper)
                body.Add((returns ? "return " : string.Empty) + "Super::" + f.Name + "(" + ForwardArguments(named.Parameters) + ");");
            else if (returns) body.Add("return {};");
            return Definition(named, className, body: body, newline: newline);
        }

        public static FunctionSignature Copy(FunctionSignature f) => new FunctionSignature
        {
            Name = f.Name, Container = f.Container, Kind = f.Kind, ReturnType = f.ReturnType, Parameters = f.Parameters,
            TemplateParameters = f.TemplateParameters, Modifiers = f.Modifiers, Access = f.Access, Specifiers = f.Specifiers, Ue = f.Ue,
            Location = f.Location, IsDefinition = f.IsDefinition, DeclaringType = f.DeclaringType, Module = f.Module, Origin = f.Origin, EndLine = f.EndLine,
        };

        /// <summary>Display text for lists: <c>Ret Name(params) const</c>.</summary>
        public static string Display(FunctionSignature f)
        {
            string ret = HasReturnType(f) ? f.ReturnType + " " : string.Empty;
            return ret + f.Name + "(" + StripDefaults(f.Parameters) + ")" + TrailingQualifiers(f.Modifiers);
        }

        // ---------------------------------------------------------------- helpers

        private static bool IsIdentPart(char c) => char.IsLetterOrDigit(c) || c == '_';

        internal static int IndexOfTopLevel(string s, char ch)
        {
            int depth = 0;
            bool inString = false;
            char quote = '\0';
            for (int i = 0; i < s.Length; i++)
            {
                char c = s[i];
                if (inString)
                {
                    if (c == '\\') i++;
                    else if (c == quote) inString = false;
                    continue;
                }
                if (c == '"' || c == '\'') { inString = true; quote = c; }
                else if (c == '<' || c == '(' || c == '[' || c == '{') depth++;
                else if (c == '>' || c == ')' || c == ']' || c == '}') depth = Math.Max(0, depth - 1);
                else if (c == ch && depth == 0)
                {
                    // "operator<=" style and comparisons inside defaults are rare; '=' of "==" / "<=" / ">=" / "!=" is not a default
                    if (ch == '=' && ((i + 1 < s.Length && s[i + 1] == '=') || (i > 0 && "=!<>".IndexOf(s[i - 1]) >= 0))) continue;
                    return i;
                }
            }
            return -1;
        }

        internal static List<string> SplitTopLevel(string s, char separator)
        {
            var parts = new List<string>();
            int depth = 0, start = 0;
            bool inString = false;
            char quote = '\0';
            for (int i = 0; i < s.Length; i++)
            {
                char c = s[i];
                if (inString)
                {
                    if (c == '\\') i++;
                    else if (c == quote) inString = false;
                    continue;
                }
                if (c == '"' || c == '\'') { inString = true; quote = c; }
                else if (c == '<' || c == '(' || c == '[' || c == '{') depth++;
                else if (c == '>' || c == ')' || c == ']' || c == '}') depth = Math.Max(0, depth - 1);
                else if (c == separator && depth == 0)
                {
                    parts.Add(s.Substring(start, i - start));
                    start = i + 1;
                }
            }
            parts.Add(s.Substring(start));
            return parts;
        }
    }
}
