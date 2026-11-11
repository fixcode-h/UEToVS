using System;
using System.Collections.Generic;
using System.Text;
using UEToVS.Core.Protocol;

namespace UEToVS.Indexer.Parsing
{
    /// <summary>A declaration found in a source file. FilePath/Module/Origin are filled in by the index.</summary>
    internal sealed class ScannedSymbol
    {
        public string Name;
        public string Container;
        public SymbolKind Kind;
        public UeFlags Ue;
        public string Specifiers;
        public int Line;
        public int Column;
        public bool IsDefinition;
        public string Signature;
    }

    /// <summary>
    /// Heuristic, compiler-free declaration scanner for UE C++. Finds namespaces, classes/structs/enums,
    /// functions and methods (declarations and definitions), fields, typedefs/usings, enum values,
    /// macros, delegate declarations and UE reflection markers (UCLASS, UFUNCTION, UPROPERTY...).
    /// It does not try to be a C++ parser: it only needs good names, kinds and line numbers.
    /// </summary>
    internal sealed class CppSymbolScanner
    {
        private enum ScopeKind { Namespace, Class, Enum, Function, Block }

        private sealed class Scope
        {
            public ScopeKind Kind;
            public string Name;      // class/namespace/enum name
            public string FullName;  // A::B
        }

        private static readonly HashSet<string> NonNameKeywords = new HashSet<string>(StringComparer.Ordinal)
        {
            "if", "for", "while", "switch", "return", "sizeof", "decltype", "static_assert", "alignas", "alignof",
            "catch", "throw", "new", "delete", "case", "do", "else", "noexcept", "requires", "typeid", "co_await",
            "check", "checkf", "verify", "ensure", "ensureMsgf", "TEXT",
            "void", "bool", "auto", "int", "char", "float", "double", "unsigned", "signed", "long", "short", "const", "volatile",
            "UE_DEPRECATED", "UPARAM", "GCC_ALIGN", "MS_ALIGN", "PREPROCESSOR_JOIN", "TSAN_ATOMIC", "UE_STRING_CLASS",
            "ANSITEXTVIEW", "WIDETEXTVIEW", "UTF8TEXTVIEW", "UE_NODISCARD", "DEPRECATED", "LIKELY", "UNLIKELY",
        };

        private static readonly HashSet<string> DeclSkipWords = new HashSet<string>(StringComparer.Ordinal)
        {
            "final", "alignas", "UE_DEPRECATED", "DEPRECATED", "MS_ALIGN", "GCC_ALIGN", "abstract", "sealed",
        };

        private readonly List<Token> _t;
        private readonly List<ScannedSymbol> _out = new List<ScannedSymbol>();
        private readonly Stack<Scope> _scopes = new Stack<Scope>();
        private readonly List<int> _stmt = new List<int>(); // token indices of the current statement
        private UeFlags _pendingUe;
        private string _pendingSpecifiers;

        private CppSymbolScanner(List<Token> tokens)
        {
            _t = tokens;
        }

        public static List<ScannedSymbol> Scan(string source)
        {
            var s = new CppSymbolScanner(CppLexer.Tokenize(source));
            s.Run();
            return s._out;
        }

        private Scope Current => _scopes.Count > 0 ? _scopes.Peek() : null;

        private bool InFunction
        {
            get
            {
                foreach (var s in _scopes) if (s.Kind == ScopeKind.Function) return true;
                return false;
            }
        }

        private string CurrentContainer
        {
            get
            {
                foreach (var s in _scopes)
                    if (s.Kind == ScopeKind.Class || s.Kind == ScopeKind.Namespace || s.Kind == ScopeKind.Enum)
                        return s.FullName;
                return null;
            }
        }

        private void Run()
        {
            int i = 0;
            while (i < _t.Count)
            {
                var tok = _t[i];

                if (tok.Kind == TokKind.Define)
                {
                    if (!InFunction) Add(tok.Text, null, SymbolKind.Macro, tok, false, "#define " + tok.Text);
                    i++;
                    continue;
                }

                if (InFunction)
                {
                    // Inside a function body only braces matter.
                    if (tok.Text == "{") Push(ScopeKind.Block, null);
                    else if (tok.Text == "}") Pop();
                    i++;
                    continue;
                }

                if (Current?.Kind == ScopeKind.Enum)
                {
                    i = ScanEnumBody(i);
                    continue;
                }

                // A bare ALL_CAPS macro alone on its line (PRAGMA_DISABLE_DEPRECATION_WARNINGS, FORCEINLINE...) carries no name.
                if (tok.Kind == TokKind.Ident && IsMacroName(tok.Text) && i + 1 < _t.Count && _t[i + 1].Line > tok.Line
                    && _t[i + 1].Text != "(" && _t[i + 1].Text != "::" && _t[i + 1].Text != ";" && OnlyMacrosInStatement())
                {
                    _stmt.Clear();
                    i++;
                    continue;
                }

                // ALL_CAPS(...) at statement start: UE markers / declaration macros, often without ';'
                if (tok.Kind == TokKind.Ident && IsMacroCallName(tok.Text) && i + 1 < _t.Count && _t[i + 1].Text == "(" && OnlyMacrosInStatement())
                {
                    _stmt.Clear();
                    int close = MatchParen(i + 1);
                    HandleMacroCall(i, close);
                    i = close + 1;
                    continue;
                }

                switch (tok.Text)
                {
                    case "{":
                        OnOpenBrace(i);
                        _stmt.Clear();
                        i++;
                        continue;
                    case "}":
                        _stmt.Clear();
                        Pop();
                        i++;
                        continue;
                    case ";":
                        OnSemicolon();
                        _stmt.Clear();
                        ClearPending();
                        i++;
                        continue;
                    case ":":
                        // access specifier "public:" inside a class
                        if (_stmt.Count == 1 && Current?.Kind == ScopeKind.Class)
                        {
                            string w = _t[_stmt[0]].Text;
                            if (w == "public" || w == "protected" || w == "private")
                            {
                                _stmt.Clear();
                                i++;
                                continue;
                            }
                        }
                        break;
                    case "(":
                        {
                            // keep the parenthesised group in the statement, but jump over it
                            int close = MatchParen(i);
                            for (int k = i; k <= close && k < _t.Count; k++) _stmt.Add(k);
                            i = close + 1;
                            continue;
                        }
                    case "=" when IsOperatorEquals(i):
                        break;
                    case "=":
                        // default member initializers / variable initializers may contain braces: "int X = {1};" and lambdas.
                        {
                            _stmt.Add(i);
                            int k = i + 1;
                            int depth = 0;
                            for (; k < _t.Count; k++)
                            {
                                string s = _t[k].Text;
                                if (s == "(" || s == "{" || s == "[") depth++;
                                else if (s == ")" || s == "}" || s == "]") { if (depth == 0) break; depth--; }
                                else if ((s == ";" || s == ",") && depth == 0) break;
                            }
                            i = k;
                            continue;
                        }
                }

                _stmt.Add(i);
                i++;
            }
        }

        private bool OnlyMacrosInStatement()
        {
            foreach (int k in _stmt)
                if (!(_t[k].Kind == TokKind.Ident && IsMacroName(_t[k].Text))) return false;
            return true;
        }

        // "operator==", "operator=", "a <= b" style tokens: '=' that is part of an operator name, not an initializer.
        private bool IsOperatorEquals(int i)
        {
            if (i == 0) return false;
            string prev = _t[i - 1].Text;
            if (prev == "operator") return true;
            if (prev.Length == 1 && "=!<>+-*/%&|^".IndexOf(prev[0]) >= 0 && i >= 2 && (_t[i - 2].Text == "operator" || _t[i - 2].Text == prev && i >= 3 && _t[i - 3].Text == "operator")) return true;
            return false;
        }

        private void HandleMacroCall(int nameIdx, int close)
        {
            string name = _t[nameIdx].Text;
            UeFlags flag = UeFlags.None;
            switch (name)
            {
                case "UCLASS": flag = UeFlags.UClass; break;
                case "USTRUCT": flag = UeFlags.UStruct; break;
                case "UENUM": flag = UeFlags.UEnum; break;
                case "UINTERFACE": flag = UeFlags.UInterface; break;
                case "UFUNCTION": flag = UeFlags.UFunction; break;
                case "UPROPERTY": flag = UeFlags.UProperty; break;
                case "UDELEGATE": flag = UeFlags.UDelegate; break;
            }
            if (flag != UeFlags.None)
            {
                _pendingUe |= flag;
                _pendingSpecifiers = JoinTokens(nameIdx + 2, close - 1, 200);
                return;
            }

            if (name.StartsWith("DECLARE_", StringComparison.Ordinal) && nameIdx + 2 < close)
            {
                var first = _t[nameIdx + 2];
                if (first.Kind != TokKind.Ident) return;
                if (name.IndexOf("DELEGATE", StringComparison.Ordinal) >= 0 || name.IndexOf("EVENT", StringComparison.Ordinal) >= 0)
                {
                    // DECLARE_EVENT_xxx(OwningType, EventName, ...) names the event second
                    var nameTok = first;
                    if (name.StartsWith("DECLARE_EVENT", StringComparison.Ordinal) || name.StartsWith("DECLARE_DERIVED_EVENT", StringComparison.Ordinal)
                        || name.IndexOf("_RetVal", StringComparison.Ordinal) >= 0)
                    {
                        int k = nameIdx + 3;
                        while (k < close && _t[k].Text != ",") k++;
                        if (k + 1 < close && _t[k + 1].Kind == TokKind.Ident) nameTok = _t[k + 1];
                    }
                    var flags = _pendingUe;
                    if (name.IndexOf("DYNAMIC", StringComparison.Ordinal) >= 0) flags |= UeFlags.UDelegate;
                    AddWithUe(nameTok.Text, CurrentContainer, SymbolKind.Delegate, nameTok, true, name + "(" + nameTok.Text + ")", flags, _pendingSpecifiers);
                }
                else if (name.StartsWith("DECLARE_LOG_CATEGORY", StringComparison.Ordinal) || name.StartsWith("DECLARE_STATS_GROUP", StringComparison.Ordinal)
                         || name.StartsWith("DECLARE_CYCLE_STAT", StringComparison.Ordinal))
                {
                    Add(first.Text, CurrentContainer, SymbolKind.Variable, first, true, name + "(" + first.Text + ")");
                }
                ClearPending();
                return;
            }
            if (name == "DEFINE_LOG_CATEGORY_STATIC" && nameIdx + 2 < close && _t[nameIdx + 2].Kind == TokKind.Ident)
            {
                var first = _t[nameIdx + 2];
                Add(first.Text, CurrentContainer, SymbolKind.Variable, first, true, name + "(" + first.Text + ")");
            }
            // GENERATED_BODY(), IMPLEMENT_MODULE(...), UE_DEPRECATED(...) etc: ignore. Keep pending UE flags for
            // UE_DEPRECATED which sits between UFUNCTION() and the declaration.
            if (!name.StartsWith("UE_DEPRECATED", StringComparison.Ordinal)) { /* nothing */ }
        }

        private int ScanEnumBody(int i)
        {
            // Enum values: "A, B = 3, C UMETA(DisplayName="x"), }"
            bool expectName = true;
            int depth = 0;
            while (i < _t.Count)
            {
                var tok = _t[i];
                if (tok.Kind == TokKind.Define) { i++; continue; }
                if (depth == 0 && tok.Text == "}") { Pop(); return i + 1; }
                if (tok.Text == "(" || tok.Text == "{" || tok.Text == "[") depth++;
                else if (tok.Text == ")" || tok.Text == "]" || tok.Text == "}") depth--;
                else if (depth == 0 && tok.Text == ",") expectName = true;
                else if (depth == 0 && expectName && tok.Kind == TokKind.Ident && !IsMacroName(tok.Text))
                {
                    Add(tok.Text, Current.FullName, SymbolKind.EnumValue, tok, true, Current.Name + "::" + tok.Text);
                    expectName = false;
                }
                else if (depth == 0 && expectName && tok.Kind == TokKind.Ident && IsMacroName(tok.Text) && i + 1 < _t.Count && _t[i + 1].Text == "(")
                {
                    i = MatchParen(i + 1) + 1;
                    continue;
                }
                i++;
            }
            return i;
        }

        private void OnOpenBrace(int braceIdx)
        {
            if (_stmt.Count == 0)
            {
                Push(ScopeKind.Block, null);
                ClearPending();
                return;
            }

            int first = _stmt[0];
            // template<...> prefix
            int start = SkipTemplatePrefix(0);

            // namespace X { / namespace A::B { / namespace {
            int nsPos = FindWord(start, "namespace");
            if (nsPos >= 0)
            {
                string name = null;
                for (int k = nsPos + 1; k < _stmt.Count; k++)
                {
                    var t = _t[_stmt[k]];
                    if (t.Kind == TokKind.Ident) name = name == null ? t.Text : name;
                    if (t.Text == "::" && k + 1 < _stmt.Count) { name += "::" + _t[_stmt[k + 1]].Text; k++; }
                }
                Push(ScopeKind.Namespace, name ?? "(anonymous)");
                ClearPending();
                return;
            }

            if (Word(start) == "extern" && start + 1 < _stmt.Count && _t[_stmt[start + 1]].Kind == TokKind.String)
            {
                Push(ScopeKind.Block, null); // extern "C" { ... } - transparent
                _scopes.Peek().Kind = ScopeKind.Namespace;
                _scopes.Peek().FullName = CurrentContainerOuter();
                ClearPending();
                return;
            }

            int typeKw = FindTypeKeyword(start);
            if (typeKw >= 0 && !ContainsParenBefore(start, typeKw) && !IsFunctionWithElaboratedType(start, typeKw))
            {
                string kw = Word(typeKw);
                if (kw == "enum")
                {
                    int nameAt = typeKw + 1;
                    if (nameAt < _stmt.Count && (Word(nameAt) == "class" || Word(nameAt) == "struct")) nameAt++;
                    var nameTok = nameAt < _stmt.Count && _t[_stmt[nameAt]].Kind == TokKind.Ident ? _t[_stmt[nameAt]] : default;
                    string enumName = nameTok.Text ?? "(anonymous enum)";
                    if (nameTok.Text != null)
                        AddWithUe(enumName, CurrentContainer, SymbolKind.Enum, nameTok, true, Signature(start, _stmt.Count - 1), _pendingUe, _pendingSpecifiers);
                    Push(ScopeKind.Enum, enumName);
                    ClearPending();
                    return;
                }

                // class/struct/union NAME [final] [: bases] {
                var name = FindTypeName(typeKw);
                if (name.Text != null)
                {
                    var kind = kw == "struct" ? SymbolKind.Struct : kw == "union" ? SymbolKind.Union : SymbolKind.Class;
                    AddWithUe(name.Text, CurrentContainer, kind, name, true, Signature(start, _stmt.Count - 1), _pendingUe, _pendingSpecifiers);
                    Push(ScopeKind.Class, name.Text);
                }
                else
                {
                    Push(ScopeKind.Class, "(anonymous)");
                }
                ClearPending();
                return;
            }

            // function definition: "... Name(...) [const] [override] [-> T] [: init] {"
            int paren = FirstTopLevelParen(start);
            if (paren >= 0)
            {
                if (TryRecordFunction(start, paren, true))
                {
                    Push(ScopeKind.Function, null);
                    ClearPending();
                    return;
                }
            }

            // "= {" initializers are consumed earlier; anything else is an opaque block (e.g. lambdas at file scope)
            Push(InFunction ? ScopeKind.Block : (paren >= 0 ? ScopeKind.Function : ScopeKind.Block), null);
            ClearPending();
        }

        // "FORCEINLINE class USkeletalMeshComponent* GetMesh() const {": a function whose return type says "class".
        private bool IsFunctionWithElaboratedType(int start, int typeKw)
        {
            int paren = FirstTopLevelParen(typeKw);
            if (paren < 0) return false;
            // parens directly after the keyword belong to alignas()/MS_ALIGN()/UE_DEPRECATED(): still a type
            string before = Word(paren - 1);
            if (before == "alignas" || (before != null && IsMacroName(before))) return false;
            // a ':' (base list) before the paren means a class declaration with odd bases
            for (int k = typeKw + 1; k < paren; k++) if (Word(k) == ":") return false;
            return true;
        }

        private string CurrentContainerOuter() => CurrentContainer;

        private void OnSemicolon()
        {
            if (_stmt.Count == 0) return;
            int start = SkipTemplatePrefix(0);
            if (start >= _stmt.Count) return;
            string w0 = Word(start);

            if (w0 == "using")
            {
                // using X = ...;   (skip "using namespace" and using-declarations)
                if (start + 2 < _stmt.Count && _t[_stmt[start + 1]].Kind == TokKind.Ident && Word(start + 2) == "=")
                    Add(_t[_stmt[start + 1]].Text, CurrentContainer, SymbolKind.Typedef, _t[_stmt[start + 1]], true, Signature(start, _stmt.Count - 1));
                return;
            }
            if (w0 == "typedef")
            {
                // typedef A B;  typedef void (*Fn)(int);
                Token name = default;
                for (int k = _stmt.Count - 1; k > start; k--)
                {
                    var t = _t[_stmt[k]];
                    if (t.Kind == TokKind.Ident && !IsApiMacro(t.Text) && !NonNameKeywords.Contains(t.Text)) { name = t; break; }
                }
                int fp = FindPunct(start, "(");
                if (fp >= 0 && fp + 2 < _stmt.Count && Word(fp + 1) == "*" && _t[_stmt[fp + 2]].Kind == TokKind.Ident)
                    name = _t[_stmt[fp + 2]];
                if (name.Text != null) Add(name.Text, CurrentContainer, SymbolKind.Typedef, name, true, Signature(start, _stmt.Count - 1));
                return;
            }
            if (w0 == "friend" || w0 == "return" || w0 == "static_assert" || w0 == "goto" || w0 == "break" || w0 == "continue") return;

            int typeKw = FindTypeKeyword(start);
            if (typeKw >= 0 && typeKw == start && IsForwardDeclaration(start))
                return; // "class Foo;", "class ENGINE_API Foo;", "enum class E : uint8;" are not symbols

            int paren = FirstTopLevelParen(start);
            int eq = FindPunct(start, "=");
            while (eq >= 0 && IsOperatorEquals(_stmt[eq]))
            {
                int next = FindPunct(eq + 1, "=");
                eq = next;
            }
            if (paren >= 0 && (eq < 0 || paren < eq))
            {
                TryRecordFunction(start, paren, false);
                return;
            }

            // Field / variable: last identifier before '=', '[', ':' (bitfield), '{' or end.
            int end = _stmt.Count;
            for (int k = start; k < _stmt.Count; k++)
            {
                string s = Word(k);
                if (s == "=" || s == "[" || (s == ":" && k > start)) { end = k; break; }
            }
            if (end - start < 2) return; // need at least a type and a name
            var nameTok = _t[_stmt[end - 1]];
            if (nameTok.Kind != TokKind.Ident || NonNameKeywords.Contains(nameTok.Text) || IsApiMacro(nameTok.Text)) return;
            if (w0 == "extern" && end - start < 3) return;
            var scope = Current;
            var kind = scope?.Kind == ScopeKind.Class ? SymbolKind.Field : SymbolKind.Variable;
            AddWithUe(nameTok.Text, CurrentContainer, kind, nameTok, true, Signature(start, end - 1), _pendingUe, _pendingSpecifiers);
        }

        private bool IsForwardDeclaration(int start)
        {
            int k = start + 1;
            if (Word(start) == "enum" && (Word(k) == "class" || Word(k) == "struct")) k++;
            while (k < _stmt.Count && IsApiMacro(Word(k))) k++;
            if (k >= _stmt.Count || _t[_stmt[k]].Kind != TokKind.Ident) return false;
            k++;
            while (k + 1 < _stmt.Count && Word(k) == "::") k += 2;
            return k == _stmt.Count || Word(k) == ":"; // ": uint8" for enums
        }

        private bool TryRecordFunction(int start, int paren, bool isDefinition)
        {
            if (paren <= start) return false;
            int nameIdx = paren - 1;
            var nameTok = _t[_stmt[nameIdx]];
            string name;
            int qualStart;

            // operator overloads: operator==, operator(), operator new
            int op = -1;
            for (int k = Math.Max(start, paren - 4); k < paren; k++) if (Word(k) == "operator") op = k;
            if (op >= 0)
            {
                var sb = new StringBuilder("operator");
                for (int k = op + 1; k < paren; k++) sb.Append(Word(k) == "(" ? "()" : Word(k));
                if (op + 1 == paren && paren + 1 < _stmt.Count && Word(paren + 1) == ")")
                {
                    // operator()(...): first parens are the name
                    sb.Append("()");
                }
                name = sb.ToString();
                nameTok = _t[_stmt[op]];
                qualStart = op;
            }
            else
            {
                if (nameTok.Kind != TokKind.Ident) return false;
                name = nameTok.Text;
                if (NonNameKeywords.Contains(name) || IsMacroName(name) && !LooksLikeFunctionAfterMacro(start, paren)) return false;
                qualStart = nameIdx;
                if (nameIdx - 1 >= start && Word(nameIdx - 1) == "~")
                {
                    name = "~" + name;
                    qualStart = nameIdx - 1;
                }
            }

            // Qualification A::B::Name
            string container = CurrentContainer;
            var quals = new List<string>();
            int q = qualStart - 1;
            while (q - 1 >= start && Word(q) == "::" && _t[_stmt[q - 1]].Kind == TokKind.Ident)
            {
                quals.Insert(0, Word(q - 1));
                q -= 2;
                // skip template args A<T>::B
            }
            if (quals.Count > 0)
                container = container == null ? string.Join("::", quals) : container + "::" + string.Join("::", quals);

            // A call statement like "Foo(1);" at file scope has no return type. Declarations have one, except
            // constructors/destructors whose name equals the class name.
            bool hasType = q >= start;
            string className = Current?.Kind == ScopeKind.Class ? Current.Name : (quals.Count > 0 ? quals[quals.Count - 1] : null);
            bool isCtor = className != null && (name == className || name == "~" + className);
            if (!hasType && !isCtor && op < 0) return false;

            var kind = (Current?.Kind == ScopeKind.Class || quals.Count > 0) ? SymbolKind.Method : SymbolKind.Function;
            AddWithUe(name, container, kind, nameTok, isDefinition, Signature(start, ClosingParenIndex(paren)), _pendingUe, _pendingSpecifiers);
            return true;
        }

        private bool LooksLikeFunctionAfterMacro(int start, int paren) => paren - start >= 1 && _t[_stmt[paren - 1]].Text.Length > 0 && paren - 1 > start;

        private int ClosingParenIndex(int paren)
        {
            int depth = 0;
            for (int k = paren; k < _stmt.Count; k++)
            {
                string s = Word(k);
                if (s == "(") depth++;
                else if (s == ")" && --depth == 0)
                {
                    // include trailing const/override for the signature
                    int e = k;
                    while (e + 1 < _stmt.Count && (Word(e + 1) == "const" || Word(e + 1) == "override" || Word(e + 1) == "final" || Word(e + 1) == "noexcept")) e++;
                    return e;
                }
            }
            return _stmt.Count - 1;
        }

        private Token FindTypeName(int typeKw)
        {
            Token name = default;
            for (int k = typeKw + 1; k < _stmt.Count; k++)
            {
                var t = _t[_stmt[k]];
                if (t.Text == ":" || t.Text == "{") break;
                if (t.Text == "<") { k = SkipAngles(k); continue; }
                if (t.Text == "(") { k = SkipParensInStmt(k); continue; }
                if (t.Kind != TokKind.Ident) continue;
                if (IsApiMacro(t.Text) || DeclSkipWords.Contains(t.Text)) continue;
                if (k + 1 < _stmt.Count && Word(k + 1) == "::") continue; // class Outer::Inner
                name = t;
            }
            return name;
        }

        private int FindTypeKeyword(int start)
        {
            for (int k = start; k < _stmt.Count; k++)
            {
                string s = Word(k);
                if (s == "(" || s == "=") return -1;
                if (s == "class" || s == "struct" || s == "union" || s == "enum")
                {
                    // "enum class" counts once
                    return k;
                }
                if (s == "<") { k = SkipAngles(k); continue; }
            }
            return -1;
        }

        private bool ContainsParenBefore(int start, int idx)
        {
            for (int k = start; k < idx; k++) if (Word(k) == "(") return true;
            return false;
        }

        private int SkipTemplatePrefix(int k)
        {
            while (k < _stmt.Count && Word(k) == "template" && k + 1 < _stmt.Count && Word(k + 1) == "<")
                k = SkipAngles(k + 1) + 1;
            return k;
        }

        private int SkipAngles(int k)
        {
            int depth = 0;
            for (; k < _stmt.Count; k++)
            {
                string s = Word(k);
                if (s == "<") depth++;
                else if (s == ">" && --depth == 0) return k;
                else if (s == "(") k = SkipParensInStmt(k);
                else if (s == ";" || s == "{") return k - 1;
            }
            return _stmt.Count - 1;
        }

        private int SkipParensInStmt(int k)
        {
            int depth = 0;
            for (; k < _stmt.Count; k++)
            {
                string s = Word(k);
                if (s == "(") depth++;
                else if (s == ")" && --depth == 0) return k;
            }
            return _stmt.Count - 1;
        }

        private int FirstTopLevelParen(int start)
        {
            for (int k = start; k < _stmt.Count; k++)
            {
                string s = Word(k);
                if (s == "<" && k > start && _t[_stmt[k - 1]].Kind == TokKind.Ident && !(k >= 1 && Word(k - 1) == "operator"))
                {
                    int close = SkipAngles(k);
                    if (close < _stmt.Count && Word(close) == ">") { k = close; continue; }
                }
                if (s == "(") return k;
            }
            return -1;
        }

        private int FindPunct(int start, string p)
        {
            int depth = 0;
            for (int k = start; k < _stmt.Count; k++)
            {
                string s = Word(k);
                if (s == "(") depth++;
                else if (s == ")") depth--;
                else if (depth == 0 && s == p) return k;
            }
            return -1;
        }

        private int FindWord(int start, string w)
        {
            for (int k = start; k < _stmt.Count; k++) if (Word(k) == w) return k;
            return -1;
        }

        private string Word(int stmtIndex) => stmtIndex < _stmt.Count ? _t[_stmt[stmtIndex]].Text : null;

        private int MatchParen(int open)
        {
            int depth = 0;
            for (int k = open; k < _t.Count; k++)
            {
                string s = _t[k].Text;
                if (s == "(") depth++;
                else if (s == ")" && --depth == 0) return k;
                else if (s == ";" && depth > 0 && k > open + 2000) return k; // runaway guard
            }
            return _t.Count - 1;
        }

        private string Signature(int fromStmt, int toStmt)
        {
            var sb = new StringBuilder();
            Token prev = default;
            for (int k = fromStmt; k <= toStmt && k < _stmt.Count; k++)
            {
                var t = _t[_stmt[k]];
                if (sb.Length > 0 && NeedsSpace(prev, t)) sb.Append(' ');
                sb.Append(t.Text);
                prev = t;
                if (sb.Length > 160) { sb.Append("…"); break; }
            }
            return sb.ToString();
        }

        private string JoinTokens(int from, int to, int max)
        {
            var sb = new StringBuilder();
            Token prev = default;
            for (int k = from; k <= to && k < _t.Count; k++)
            {
                var t = _t[k];
                if (sb.Length > 0 && NeedsSpace(prev, t)) sb.Append(' ');
                sb.Append(t.Kind == TokKind.String ? "\"…\"" : t.Text);
                prev = t;
                if (sb.Length > max) break;
            }
            return sb.ToString();
        }

        private static bool NeedsSpace(Token a, Token b)
        {
            if (a.Text == null) return false;
            if (b.Text == "," || b.Text == ")" || b.Text == "(" || b.Text == "::" || b.Text == ">" || b.Text == "]" || b.Text == "[" || b.Text == ";") return false;
            if (a.Text == "(" || a.Text == "::" || a.Text == "<" || a.Text == "~" || a.Text == "[") return false;
            if ((b.Text == "*" || b.Text == "&") && a.Kind == TokKind.Ident) return false;
            if (b.Text == "<" && a.Kind == TokKind.Ident) return false;
            return true;
        }

        private void Push(ScopeKind kind, string name)
        {
            string parent = CurrentContainer;
            string full = name == null ? parent : (parent == null ? name : parent + "::" + name);
            if (kind == ScopeKind.Block || kind == ScopeKind.Function) full = parent;
            _scopes.Push(new Scope { Kind = kind, Name = name, FullName = full });
        }

        private void Pop()
        {
            if (_scopes.Count > 0) _scopes.Pop();
        }

        private void ClearPending()
        {
            _pendingUe = UeFlags.None;
            _pendingSpecifiers = null;
        }

        private void Add(string name, string container, SymbolKind kind, Token at, bool def, string sig) =>
            AddWithUe(name, container, kind, at, def, sig, UeFlags.None, null);

        private void AddWithUe(string name, string container, SymbolKind kind, Token at, bool def, string sig, UeFlags ue, string spec)
        {
            _out.Add(new ScannedSymbol
            {
                Name = name,
                Container = container,
                Kind = kind,
                Ue = ue,
                Specifiers = ue == UeFlags.None ? null : spec,
                Line = at.Line,
                Column = at.Col,
                IsDefinition = def,
                Signature = sig,
            });
        }

        internal static bool IsMacroName(string s)
        {
            if (s.Length < 3) return false;
            bool hasLetter = false;
            foreach (char c in s)
            {
                if (c >= 'a' && c <= 'z') return false;
                if (c >= 'A' && c <= 'Z') hasLetter = true;
            }
            return hasLetter;
        }

        /// <summary>ALL_CAPS or UE-style "DECLARE_DYNAMIC_DELEGATE_OneParam": an upper-case prefix before the first '_'.</summary>
        internal static bool IsMacroCallName(string s)
        {
            if (IsMacroName(s)) return true;
            int u = s.IndexOf('_');
            if (u < 2) return false;
            for (int k = 0; k < u; k++)
                if (!(s[k] >= 'A' && s[k] <= 'Z')) return false;
            return true;
        }

        internal static bool IsApiMacro(string s) => s.EndsWith("_API", StringComparison.Ordinal) && IsMacroName(s);
    }
}
