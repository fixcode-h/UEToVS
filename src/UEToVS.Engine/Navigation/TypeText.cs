using System;
using System.Collections.Generic;
using System.Text;
using UEToVS.Contracts;
using UEToVS.Engine.Index;

namespace UEToVS.Engine.Navigation
{
    /// <summary>String helpers over declaration texts (types, signatures, containers, UE specifiers).</summary>
    public static class TypeText
    {
        /// <summary>Smart pointers / handles whose first template argument is the pointee for member access.</summary>
        private static readonly HashSet<string> Wrappers = new HashSet<string>(StringComparer.Ordinal)
        {
            "TObjectPtr", "TWeakObjectPtr", "TSoftObjectPtr", "TLazyObjectPtr", "TStrongObjectPtr", "TScriptInterface", "TWeakInterfacePtr",
            "TSharedPtr", "TSharedRef", "TWeakPtr", "TUniquePtr", "TOptional", "TNonNullPtr",
            "std::unique_ptr", "std::shared_ptr", "std::weak_ptr", "unique_ptr", "shared_ptr", "weak_ptr", "std::optional",
        };

        /// <summary>Containers whose element type is reached with <c>[]</c> or range-for.</summary>
        private static readonly HashSet<string> Containers = new HashSet<string>(StringComparer.Ordinal)
        {
            "TArray", "TInlineComponentArray", "TArrayView", "TConstArrayView", "TSet", "TSparseArray", "TStaticArray", "TIndirectArray",
            "std::vector", "vector", "std::array",
        };

        /// <summary>Template functions whose explicit argument is the result's pointee: <c>auto* X = Cast&lt;T&gt;(...)</c>.</summary>
        public static readonly HashSet<string> TypedFactories = new HashSet<string>(StringComparer.Ordinal)
        {
            "Cast", "CastChecked", "ExactCast", "NewObject", "CreateDefaultSubobject", "SpawnActor", "SpawnActorDeferred", "GetSubsystem",
            "FindComponentByClass", "GetComponentByClass", "FindObject", "LoadObject", "GetGameInstanceSubsystem", "GetWorldSubsystem",
            "GetLocalPlayerSubsystem", "GetEngineSubsystem", "GetOwner", "GetPawn", "GetController", "GetGameMode", "GetGameState",
            "GetPlayerState", "GetTypedOuter", "StaticCastSharedPtr", "StaticCastSharedRef", "MakeShared", "MakeUnique", "MakeShareable",
            "CreateWidget", "GetDefault", "GetMutableDefault", "FindFProperty", "DuplicateObject",
        };

        /// <summary>
        /// The class a value of this type gives access to with <c>.</c> / <c>-&gt;</c>:
        /// <c>const TObjectPtr&lt;UFoo&gt;&amp;</c> → <c>UFoo</c>, <c>class AActor*</c> → <c>AActor</c>, <c>TArray&lt;int&gt;</c> → <c>TArray</c>.
        /// Null for <c>auto</c>, built-in types or empty input.
        /// </summary>
        public static string CoreType(string typeText)
        {
            if (string.IsNullOrWhiteSpace(typeText)) return null;
            string t = RemoveWords(typeText, "const", "volatile", "class", "struct", "enum", "typename", "static", "mutable", "inline",
                "constexpr", "virtual", "extern", "FORCEINLINE", "unsigned", "signed").Replace("*", " ").Replace("&", " ").Trim();
            for (int guard = 0; guard < 4; guard++)
            {
                int lt = t.IndexOf('<');
                if (lt < 0) break;
                string head = t.Substring(0, lt).Trim();
                if (!Wrappers.Contains(head)) break;
                string arg = FirstTemplateArg(t);
                if (arg == null) break;
                t = RemoveWords(arg, "const", "class", "struct").Replace("*", " ").Replace("&", " ").Trim();
            }
            t = BaseNames.StripTemplateArgs(t);
            if (string.IsNullOrEmpty(t) || IsBuiltin(t)) return null;
            return t;
        }

        /// <summary>Element type for <c>[]</c> / range-for over a container type: <c>TArray&lt;AActor*&gt;</c> → <c>AActor*</c>.</summary>
        public static string ElementType(string typeText)
        {
            if (string.IsNullOrWhiteSpace(typeText)) return null;
            string t = RemoveWords(typeText, "const").Replace("&", " ").Trim();
            int lt = t.IndexOf('<');
            if (lt < 0) return null;
            string head = t.Substring(0, lt).Trim();
            if (Wrappers.Contains(head))
            {
                string inner = FirstTemplateArg(t);
                return inner == null ? null : ElementType(inner);
            }
            return Containers.Contains(head) || head == "TMap" ? FirstTemplateArg(t) : null;
        }

        /// <summary>First top-level template argument of <c>X&lt;A, B&gt;</c>.</summary>
        public static string FirstTemplateArg(string t)
        {
            int lt = t.IndexOf('<');
            if (lt < 0) return null;
            int depth = 0;
            for (int i = lt; i < t.Length; i++)
            {
                char c = t[i];
                if (c == '<' || c == '(') depth++;
                else if (c == '>' || c == ')')
                {
                    depth--;
                    if (depth == 0) return t.Substring(lt + 1, i - lt - 1).Trim();
                }
                else if (c == ',' && depth == 1) return t.Substring(lt + 1, i - lt - 1).Trim();
            }
            return null;
        }

        public static bool IsBuiltin(string t) => t switch
        {
            "auto" or "void" or "bool" or "char" or "short" or "int" or "long" or "float" or "double" or "int8" or "int16" or "int32" or "int64"
                or "uint8" or "uint16" or "uint32" or "uint64" or "TCHAR" or "ANSICHAR" or "WIDECHAR" or "SIZE_T" or "size_t" or "decltype" => true,
            _ => false,
        };

        private static string RemoveWords(string s, params string[] words)
        {
            var sb = new StringBuilder(s.Length);
            int i = 0;
            while (i < s.Length)
            {
                if (IsIdentStart(s[i]) && (i == 0 || !IsIdentPart(s[i - 1])))
                {
                    int j = i;
                    while (j < s.Length && IsIdentPart(s[j])) j++;
                    string w = s.Substring(i, j - i);
                    if (Array.IndexOf(words, w) < 0) sb.Append(w);
                    else sb.Append(' ');
                    i = j;
                    continue;
                }
                sb.Append(s[i++]);
            }
            return sb.ToString();
        }

        public static bool IsIdentStart(char c) => char.IsLetter(c) || c == '_';

        public static bool IsIdentPart(char c) => char.IsLetterOrDigit(c) || c == '_';

        /// <summary>
        /// Type part of a field / variable signature: <c>TObjectPtr&lt;UHealth&gt; Health</c> → <c>TObjectPtr&lt;UHealth&gt;</c>.
        /// </summary>
        public static string TypeFromSignature(string signature, string name)
        {
            if (string.IsNullOrEmpty(signature) || string.IsNullOrEmpty(name)) return null;
            int i = signature.LastIndexOf(name, StringComparison.Ordinal);
            while (i > 0)
            {
                bool startOk = !IsIdentPart(signature[i - 1]);
                bool endOk = i + name.Length >= signature.Length || !IsIdentPart(signature[i + name.Length]);
                if (startOk && endOk) return signature.Substring(0, i).Trim();
                i = signature.LastIndexOf(name, i - 1, StringComparison.Ordinal);
            }
            return null;
        }

        /// <summary>
        /// Normalised parameter types of a function signature, for matching a declaration with its definition among overloads:
        /// <c>void A::F(const FVector&amp; Dir, int32 N = 3)</c> → <c>constFVector&amp;|int32</c>. Null without a parameter list.
        /// </summary>
        public static string ParameterKey(string signature)
        {
            if (string.IsNullOrEmpty(signature)) return null;
            int open = signature.IndexOf('(');
            if (open < 0) return null;
            int depth = 0, close = -1;
            for (int i = open; i < signature.Length; i++)
            {
                if (signature[i] == '(') depth++;
                else if (signature[i] == ')' && --depth == 0) { close = i; break; }
            }
            if (close < 0) return null;
            string inner = signature.Substring(open + 1, close - open - 1);
            var parts = SplitTopLevel(inner);
            var sb = new StringBuilder();
            foreach (var raw in parts)
            {
                string p = raw;
                int eq = IndexOfTopLevel(p, '=');
                if (eq >= 0) p = p.Substring(0, eq);
                p = p.Trim();
                if (p.Length == 0 || p == "void") continue;
                // drop the parameter name: trailing identifier preceded by a type
                int end = p.Length;
                while (end > 0 && p[end - 1] == ']') { int lb = p.LastIndexOf('[', end - 1); if (lb < 0) break; end = lb; }
                int s = end;
                while (s > 0 && IsIdentPart(p[s - 1])) s--;
                string before = p.Substring(0, s).Trim();
                if (s < end && before.Length > 0 && !before.EndsWith("::", StringComparison.Ordinal) && before != "const" && before != "unsigned")
                    p = before;
                if (sb.Length > 0) sb.Append('|');
                foreach (char c in p) if (!char.IsWhiteSpace(c)) sb.Append(c);
            }
            return sb.ToString();
        }

        private static int IndexOfTopLevel(string s, char ch)
        {
            int depth = 0;
            for (int i = 0; i < s.Length; i++)
            {
                char c = s[i];
                if (c == '<' || c == '(' || c == '[' || c == '{') depth++;
                else if (c == '>' || c == ')' || c == ']' || c == '}') depth--;
                else if (c == ch && depth == 0) return i;
            }
            return -1;
        }

        private static List<string> SplitTopLevel(string s)
        {
            var parts = new List<string>();
            int depth = 0, start = 0;
            for (int i = 0; i < s.Length; i++)
            {
                char c = s[i];
                if (c == '<' || c == '(' || c == '[' || c == '{') depth++;
                else if (c == '>' || c == ')' || c == ']' || c == '}') depth--;
                else if (c == ',' && depth == 0)
                {
                    parts.Add(s.Substring(start, i - start));
                    start = i + 1;
                }
            }
            parts.Add(s.Substring(start));
            return parts;
        }

        /// <summary>Number of parameters in a signature (-1 when there is no parameter list).</summary>
        public static int ParameterCount(string signature)
        {
            string key = ParameterKey(signature);
            if (key == null) return -1;
            return key.Length == 0 ? 0 : key.Split('|').Length;
        }

        /// <summary>Whether a comma separated UE specifier list contains <paramref name="word"/> as a whole item (before any '=').</summary>
        public static bool HasSpecifier(string specifiers, string word)
        {
            if (string.IsNullOrEmpty(specifiers)) return false;
            int i = 0;
            while ((i = specifiers.IndexOf(word, i, StringComparison.OrdinalIgnoreCase)) >= 0)
            {
                bool startOk = i == 0 || !IsIdentPart(specifiers[i - 1]);
                int e = i + word.Length;
                bool endOk = e >= specifiers.Length || !IsIdentPart(specifiers[e]);
                if (startOk && endOk) return true;
                i = e;
            }
            return false;
        }

        /// <summary><c>container</c> equals <paramref name="wanted"/> or ends with <c>::wanted</c> (null/empty = global).</summary>
        public static bool ContainerMatches(string container, string wanted)
        {
            if (string.IsNullOrEmpty(wanted)) return string.IsNullOrEmpty(container);
            if (string.IsNullOrEmpty(container)) return false;
            if (string.Equals(container, wanted, StringComparison.Ordinal)) return true;
            return container.Length > wanted.Length + 2 && container.EndsWith(wanted, StringComparison.Ordinal) &&
                   container[container.Length - wanted.Length - 1] == ':' && container[container.Length - wanted.Length - 2] == ':';
        }

        public static string Qualified(string container, string name) => string.IsNullOrEmpty(container) ? name : container + "::" + name;

        public static string LastSegment(string qualified)
        {
            if (string.IsNullOrEmpty(qualified)) return qualified;
            int i = qualified.LastIndexOf("::", StringComparison.Ordinal);
            return i < 0 ? qualified : qualified.Substring(i + 2);
        }

        /// <summary>Qualifier part of a qualified name, or null: <c>A::B::C</c> → <c>A::B</c>.</summary>
        public static string Parent(string qualified)
        {
            if (string.IsNullOrEmpty(qualified)) return null;
            int i = qualified.LastIndexOf("::", StringComparison.Ordinal);
            return i <= 0 ? null : qualified.Substring(0, i);
        }

        public static bool IsTypeKind(SymbolKind k) => k == SymbolKind.Class || k == SymbolKind.Struct || k == SymbolKind.Union ||
            k == SymbolKind.Enum || k == SymbolKind.Typedef || k == SymbolKind.Delegate || k == SymbolKind.Namespace || k == SymbolKind.Concept;

        public static bool IsRecordKind(SymbolKind k) => k == SymbolKind.Class || k == SymbolKind.Struct || k == SymbolKind.Union;

        public static bool IsCallableKind(SymbolKind k) => k == SymbolKind.Function || k == SymbolKind.Method ||
            k == SymbolKind.Constructor || k == SymbolKind.Destructor;

        public static bool IsProject(ItemOrigin o) => o == ItemOrigin.Project || o == ItemOrigin.ProjectPlugin;
    }
}
