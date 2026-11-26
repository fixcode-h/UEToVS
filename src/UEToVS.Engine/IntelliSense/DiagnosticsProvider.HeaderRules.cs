using System;
using System.Collections.Generic;
using System.Linq;
using UEToVS.Contracts;
using UEToVS.Engine.Includes;
using UEToVS.Engine.Navigation;
using UEToVS.Engine.Syntax;

namespace UEToVS.Engine.IntelliSense
{
    /// <summary>
    /// v0.11.0 include-line and syntax rules (design: 后续规划-代码诊断与提示 §3.4–3.5, §4.7). Same contract as the other rules:
    /// report only when certain. The include rules need the <see cref="IncludeModel"/> (the module catalog and the engine index);
    /// without it (loose files, tests without a workspace) they stay silent.
    /// <list type="bullet">
    /// <item>UE0017 include that resolves nowhere: no file anywhere, or a file exists but not below the module's include roots
    /// (written relative to the wrong folder). The fix rewrites it when exactly one header has that name.</item>
    /// <item>UE0018 include of another module's Private header (never on the include path, whatever Build.cs says).</item>
    /// <item>UE0019 X.generated.h missing / misnamed / not the last include in a header with reflected types (UHT errors).</item>
    /// <item>UE0020 (info) include found only through the legacy include paths UE5 switches off module by module; the fix writes
    /// the spelling from the module's own roots.</item>
    /// <item>UE0021 syntax errors: unbalanced braces from the parser, a missing ';' after a type body or before a block's '}',
    /// unbalanced parentheses in a function body. Brace errors are skipped when unknown #if/#else branches are both parsed.</item>
    /// </list>
    /// </summary>
    public sealed partial class DiagnosticsProvider
    {
        /// <summary>Headers of the C runtime / Windows SDK written with quotes now and then: never indexed, never "not found".</summary>
        private static readonly HashSet<string> SystemHeaders = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "windows.h", "winsock2.h", "ws2tcpip.h", "winbase.h", "winuser.h", "windef.h", "winnt.h", "shlobj.h", "shellapi.h", "objbase.h",
            "combaseapi.h", "commctrl.h", "dbghelp.h", "psapi.h", "tlhelp32.h", "d3d11.h", "d3d12.h", "dxgi.h", "dxgi1_6.h", "d3dcompiler.h",
            "tchar.h", "intrin.h", "immintrin.h", "xmmintrin.h", "emmintrin.h", "smmintrin.h", "stdio.h", "stdlib.h", "string.h", "math.h",
            "assert.h", "stdint.h", "stddef.h", "time.h", "ctype.h", "wchar.h", "limits.h", "float.h", "errno.h", "signal.h", "setjmp.h",
            "stdarg.h", "malloc.h", "memory.h", "io.h", "direct.h", "process.h", "unistd.h", "pthread.h", "fcntl.h", "locale.h", "wctype.h",
        };

        private sealed partial class Pass
        {
            // ============================================================ UE0017 / UE0018 / UE0020 include lines, UE0019 generated header

            public void IncludeLines(int start, int end, List<CodeDiagnostic> result, DiagnosticsOptions options)
            {
                var doc = _doc;
                var includes = doc.Summary.Includes;
                if (includes.Count == 0 && !IsHeader(doc.Path)) return;
                var model = _s.Ctx.Files?.Includes;
                if (options.On(CodeDiagnosticCodes.GeneratedInclude)) GeneratedInclude(start, end, result);
                if (model?.Catalog == null || model.ModuleOf(doc.Path) == null) return;
                bool notFound = options.On(CodeDiagnosticCodes.IncludeNotFound), priv = options.On(CodeDiagnosticCodes.IncludeOfPrivateHeader);
                bool spelling = options.On(CodeDiagnosticCodes.IncludeSpelling);
                if (!notFound && !priv && !spelling) return;
                foreach (var inc in includes)
                {
                    if (inc.Offset < start || inc.Offset >= end || inc.IsMacro || string.IsNullOrWhiteSpace(inc.Path)) continue;
                    if (inc.Path.EndsWith(".generated.h", StringComparison.OrdinalIgnoreCase)) continue;
                    if (InInactive(doc, inc.Offset)) continue;
                    var (spanStart, spanLen) = IncludeSpan(inc);
                    if (spanStart < 0) continue;
                    if (_caret >= inc.Offset && _caret <= spanStart + spanLen) { _stats.Skip("include:typing"); continue; }
                    _s.Ct.ThrowIfCancellationRequested();
                    _stats.Checked++;
                    var r = model.Resolve(inc.Path, inc.IsAngled, doc.Path);
                    switch (r.Status)
                    {
                        case IncludeStatus.NotFound:
                            if (!notFound) break;
                            if (!r.Certain) { _stats.Skip("include:uncertain"); break; }
                            if (IsSystemHeaderName(inc.Path)) { _stats.Skip("include:system"); break; }
                            {
                                string fix = UniqueHeaderSpelling(model, System.IO.Path.GetFileName(inc.Path.Replace('\\', '/')));
                                var d = Make(spanStart, spanLen, CodeDiagnosticSeverity.Error, CodeDiagnosticCodes.IncludeNotFound,
                                    $"cannot open include file '{inc.Path}'", inc.Path, fix ?? string.Empty);
                                if (fix != null) AddReplaceFix(d, spanStart, spanLen, Quote(fix, inc.IsAngled));
                                Report(result, d);
                            }
                            break;
                        case IncludeStatus.NotOnIncludePath:
                            if (!r.Certain) { _stats.Skip("include:uncertain"); break; }
                            if (r.CandidatesPrivate)
                            {
                                if (!priv) break;
                                Report(result, Make(spanStart, spanLen, CodeDiagnosticSeverity.Error, CodeDiagnosticCodes.IncludeOfPrivateHeader,
                                    $"'{inc.Path}' is a private header of module '{r.CandidateModule}'", inc.Path, r.CandidateModule ?? string.Empty));
                                break;
                            }
                            if (!notFound) break;
                            {
                                var module = model.ModuleOf(doc.Path);
                                // a public header of a module that is not a dependency yet: UE0002 (host) asks for the Build.cs line
                                var owner = model.Catalog.FindForFile(r.Candidates[0]);
                                if (owner != null && !model.IsVisible(module.Name, owner.Name)) { _stats.Skip("include:not-a-dependency"); break; }
                                string fix = SameFileSpelling(model, r.Candidates, inc.Path);
                                var d = Make(spanStart, spanLen, CodeDiagnosticSeverity.Error, CodeDiagnosticCodes.IncludeNotFound,
                                    $"cannot open include file '{inc.Path}' (not below an include path of the module)", inc.Path, fix ?? string.Empty);
                                if (fix != null) AddReplaceFix(d, spanStart, spanLen, Quote(fix, inc.IsAngled));
                                Report(result, d);
                            }
                            break;
                        case IncludeStatus.Found:
                            if (!spelling || r.Reason != "legacy-public" && r.Reason != "legacy-parent") break;
                            {
                                string shorter = model.Spelling(r.Path, doc.Path);
                                if (string.IsNullOrEmpty(shorter) || string.Equals(shorter, inc.Path.Replace('\\', '/'), StringComparison.OrdinalIgnoreCase)) break;
                                var back = model.Resolve(shorter, false, doc.Path);
                                if (back.Status != IncludeStatus.Found || back.Reason == "legacy-public" || back.Reason == "legacy-parent" ||
                                    NavContext.NormalizePath(back.Path) != NavContext.NormalizePath(r.Path)) break;
                                var d = Make(spanStart, spanLen, CodeDiagnosticSeverity.Info, CodeDiagnosticCodes.IncludeSpelling,
                                    $"'{inc.Path}' is found through a legacy include path; write \"{shorter}\"", inc.Path, shorter);
                                AddReplaceFix(d, spanStart, spanLen, Quote(shorter, inc.IsAngled));
                                Report(result, d);
                            }
                            break;
                    }
                }
            }

            private void GeneratedInclude(int start, int end, List<CodeDiagnostic> result)
            {
                var doc = _doc;
                if (!IsHeader(doc.Path)) return;
                int firstReflected = -1;
                for (int i = 0; i < doc.Decls.Count; i++)
                {
                    var d = doc.Decls[i];
                    if (d.IsImplicit || (d.Ue & (UeFlags.UClass | UeFlags.UStruct | UeFlags.UEnum | UeFlags.UInterface)) == 0) continue;
                    if (!TypeText.IsRecordKind(d.Kind) && d.Kind != SymbolKind.Enum) continue;
                    if (d.IsConditional || InInactive(doc, d.Offset)) continue;
                    firstReflected = i;
                    break;
                }
                if (firstReflected < 0) return;
                var includes = doc.Summary.Includes;
                if (includes.Any(x => x.IsMacro)) { _stats.Skip("generated:macro-include"); return; }
                string expected = System.IO.Path.GetFileNameWithoutExtension(doc.Path) + ".generated.h";
                IncludeDirective gen = null;
                foreach (var inc in includes)
                    if (!InInactive(doc, inc.Offset) && inc.Path.EndsWith(".generated.h", StringComparison.OrdinalIgnoreCase)) gen = inc;
                _stats.Checked++;
                if (gen == null)
                {
                    var rd = doc.Decls[firstReflected];
                    if (rd.Offset < start || rd.Offset >= end) return;
                    if (_caret >= 0 && includes.Any(x => _caret >= x.Offset && _caret <= x.Offset + x.Path.Length + 12)) { _stats.Skip("generated:typing"); return; }
                    int len = rd.NameLength > 0 ? rd.NameLength : System.Text.Encoding.UTF8.GetByteCount(rd.Name);
                    var d = Make(rd.Offset, len, CodeDiagnosticSeverity.Error, CodeDiagnosticCodes.GeneratedInclude,
                        $"'{expected}' must be included (the last #include of the header)", "missing", expected, string.Empty);
                    var last = includes.Where(x => !x.IsConditional && !InInactive(doc, x.Offset)).LastOrDefault();
                    int line = last != null ? last.Line + 1 : PragmaOnceLine() + 1;
                    if (line > 0)
                        (d.Fixes ??= new List<CodeDiagnosticFix>()).Add(new CodeDiagnosticFix
                        {
                            Kind = "insert-line",
                            Args = { "#include \"" + expected + "\"" },
                            Edits = { new FileTextEdit { FilePath = doc.Path, Line = line, Column = 0, Length = 0, OldText = string.Empty, NewText = "#include \"" + expected + "\"\n" } },
                        });
                    Report(result, d);
                    return;
                }
                var (gs, gl) = IncludeSpan(gen);
                string written = System.IO.Path.GetFileName(gen.Path.Replace('\\', '/'));
                if (gs >= 0 && gen.Offset >= start && gen.Offset < end && !string.Equals(written, expected, StringComparison.OrdinalIgnoreCase) &&
                    !(_caret >= gen.Offset && _caret <= gs + gl))
                {
                    var d = Make(gs, gl, CodeDiagnosticSeverity.Error, CodeDiagnosticCodes.GeneratedInclude,
                        $"the generated header of this file is '{expected}'", "name", expected, gen.Path);
                    AddReplaceFix(d, gs, gl, "\"" + expected + "\"");
                    Report(result, d);
                }
                foreach (var inc in includes)
                {
                    if (inc.Offset <= gen.Offset || inc.IsConditional || InInactive(doc, inc.Offset) || inc.Offset < start || inc.Offset >= end) continue;
                    var (s2, l2) = IncludeSpan(inc);
                    if (s2 < 0) continue;
                    Report(result, Make(s2, l2, CodeDiagnosticSeverity.Error, CodeDiagnosticCodes.GeneratedInclude,
                        $"#include after '{written}': the generated header must be the last #include", "not-last", written, inc.Path));
                }
            }

            private int PragmaOnceLine()
            {
                var doc = _doc;
                foreach (var t in doc.Tokens)
                {
                    if (t.Kind != TokenKind.Directive) { if (t.Kind != TokenKind.DocComment) return -1; continue; }
                    if (t.Directive == DirectiveKind.Pragma) return doc.PositionOf(t.Start).Line;
                }
                return -1;
            }

            /// <summary>Byte range of <c>"path"</c> / <c>&lt;path&gt;</c> in the directive (delimiters included), or (-1, 0).</summary>
            private (int Start, int Length) IncludeSpan(IncludeDirective inc)
            {
                var text = _doc.Text;
                int lineEnd = inc.Offset;
                while (lineEnd < text.Length && text[lineEnd] != (byte)'\n') lineEnd++;
                byte open = inc.IsAngled ? (byte)'<' : (byte)'"', close = inc.IsAngled ? (byte)'>' : (byte)'"';
                // skip "#include" itself
                int i = inc.Offset + 1;
                while (i < lineEnd && text[i] != open) i++;
                if (i >= lineEnd) return (-1, 0);
                int j = i + 1;
                while (j < lineEnd && text[j] != close) j++;
                if (j >= lineEnd) return (-1, 0);
                return (i, j - i + 1);
            }

            private static string Quote(string path, bool angled) => angled ? "<" + path + ">" : "\"" + path + "\"";

            private static bool IsSystemHeaderName(string include)
            {
                string p = include.Replace('\\', '/');
                if (p.IndexOf('/') >= 0) return false;
                return SystemHeaders.Contains(p) || p.IndexOf('.') < 0; // <vector>-style standard headers have no extension
            }

            /// <summary>The spelling of the single indexed header with this file name, when it resolves from this file.</summary>
            private string UniqueHeaderSpelling(IncludeModel model, string fileName)
            {
                var named = model.FilesNamed(fileName).Where(IsHeader).Select(NavContext.NormalizePath).Distinct().ToList();
                if (named.Count != 1) return null;
                string header = model.FilesNamed(fileName).First(p => NavContext.NormalizePath(p) == named[0]);
                string sp = model.Spelling(header, _doc.Path);
                if (string.IsNullOrEmpty(sp)) return null;
                var r = model.Resolve(sp, false, _doc.Path);
                if (r.Status == IncludeStatus.Found && NavContext.NormalizePath(r.Path) == named[0]) return sp;
                return null;
            }

            private string SameFileSpelling(IncludeModel model, IReadOnlyList<string> candidates, string written)
            {
                var distinct = candidates.Select(NavContext.NormalizePath).Distinct().ToList();
                if (distinct.Count != 1) return null;
                string sp = model.Spelling(candidates[0], _doc.Path);
                if (string.IsNullOrEmpty(sp) || string.Equals(sp, written.Replace('\\', '/'), StringComparison.OrdinalIgnoreCase)) return null;
                var r = model.Resolve(sp, false, _doc.Path);
                return r.Status == IncludeStatus.Found && NavContext.NormalizePath(r.Path) == distinct[0] ? sp : null;
            }

            private void AddReplaceFix(CodeDiagnostic d, int byteStart, int byteLen, string newText)
            {
                var (line, col) = _doc.PositionOf(byteStart);
                (d.Fixes ??= new List<CodeDiagnosticFix>()).Add(new CodeDiagnosticFix
                {
                    Kind = "replace",
                    Args = { newText },
                    Edits = { new FileTextEdit { FilePath = _doc.Path, Line = line, Column = col, Length = d.Length, OldText = System.Text.Encoding.UTF8.GetString(_doc.Text, byteStart, Math.Min(byteLen, _doc.Text.Length - byteStart)), NewText = newText } },
                });
            }

            // ============================================================ UE0021 syntax

            public void Syntax(int start, int end, List<CodeDiagnostic> result)
            {
                var doc = _doc;
                bool branches = HasUnknownBranches();
                foreach (var sd in doc.Summary.Diagnostics)
                {
                    if (sd.Offset < start || sd.Offset >= end) continue;
                    string kind = SyntaxKind(sd.Message, out bool brace);
                    if (kind == null) continue;
                    _stats.Checked++;
                    if (brace && branches) { _stats.Skip("syntax:conditional-branches"); continue; }
                    if (InInactive(doc, sd.Offset)) continue;
                    if (_caret >= 0 && LineOf(_caret) == LineOf(sd.Offset) && kind == "unclosed-brace") { _stats.Skip("syntax:typing"); continue; }
                    int k = doc.TokenIndexFrom(sd.Offset);
                    int len = k >= 0 && k < doc.Tokens.Length && doc.Tokens[k].Start == sd.Offset ? doc.Tokens[k].Length : 1;
                    if (doc.Tokens.Length > k && k >= 0 && doc.Tokens[k].Kind == TokenKind.Directive) len = Math.Min(len, DirectiveNameLength(sd.Offset));
                    Report(result, Make(sd.Offset, len, CodeDiagnosticSeverity.Error, CodeDiagnosticCodes.SyntaxError, sd.Message, kind, sd.Message));
                }
                if (!branches) TypeBodySemicolons(start, end, result);
                FunctionBodies(start, end, result, branches);
            }

            private int LineOf(int offset) => _doc.PositionOf(Math.Min(Math.Max(0, offset), _doc.Text.Length)).Line;

            private int DirectiveNameLength(int offset)
            {
                var text = _doc.Text;
                int i = offset + 1;
                while (i < text.Length && (text[i] == (byte)' ' || text[i] == (byte)'\t')) i++;
                while (i < text.Length && text[i] >= (byte)'a' && text[i] <= (byte)'z') i++;
                return Math.Max(1, i - offset);
            }

            /// <summary>Parser messages → rule kinds; null for recovery notes that are not certain errors.</summary>
            private static string SyntaxKind(string message, out bool brace)
            {
                brace = true;
                switch (message)
                {
                    case "unbalanced '}'": return "unbalanced-brace";
                    case "unterminated block":
                    case "unterminated function body":
                    case "unterminated enum":
                    case "unexpected end of file inside a block": return "unclosed-brace";
                }
                brace = false;
                if (message.EndsWith(" without #if", StringComparison.Ordinal)) return message.StartsWith("#endif", StringComparison.Ordinal) ? "endif-without-if" : "else-without-if";
                return null;
            }

            private bool? _branches;

            /// <summary>An #else / #elif outside the inactive ranges: both branches are parsed, braces may legitimately not balance.</summary>
            private bool HasUnknownBranches()
            {
                if (_branches.HasValue) return _branches.Value;
                var doc = _doc;
                bool any = false;
                foreach (var t in doc.Tokens)
                {
                    if (t.Kind != TokenKind.Directive) continue;
                    var dk = t.Directive;
                    if (dk != DirectiveKind.Else && dk != DirectiveKind.Elif && dk != DirectiveKind.Elifdef && dk != DirectiveKind.Elifndef) continue;
                    if (InInactive(doc, t.Start)) continue;
                    any = true;
                    break;
                }
                _branches = any;
                return any;
            }

            /// <summary><c>struct A { … }</c> followed by another declaration instead of ';' (or a declarator).</summary>
            private void TypeBodySemicolons(int start, int end, List<CodeDiagnostic> result)
            {
                var doc = _doc;
                for (int i = 0; i < doc.Decls.Count; i++)
                {
                    var d = doc.Decls[i];
                    if (d.IsImplicit || d.Body.IsNone || d.IsConditional) continue;
                    if (!TypeText.IsRecordKind(d.Kind) && d.Kind != SymbolKind.Enum) continue;
                    int close = d.Body.End - 1;
                    if (close < start || close >= end) continue;
                    int k = doc.TokenIndexFrom(close);
                    if (k < 0 || k >= doc.Tokens.Length || !doc.Tokens[k].Is(Punct.RBrace)) continue;
                    if ((doc.Tokens[k].Flags & TokenFlags.Conditional) != 0) continue;
                    // a type defined inside a function / expression (lambda, template argument) is not this rule
                    int fn = doc.EnclosingFunction(close);
                    if (fn >= 0 && fn != i) continue;
                    _stats.Checked++;
                    int n = k + 1;
                    while (n < doc.Tokens.Length && doc.Tokens[n].Kind == TokenKind.DocComment) n++;
                    if (_caret >= doc.Tokens[k].Start && (n >= doc.Tokens.Length || _caret <= doc.Tokens[n].Start)) { _stats.Skip("syntax:typing"); continue; }
                    bool missing;
                    if (n >= doc.Tokens.Length || doc.Tokens[n].Kind == TokenKind.EndOfFile) missing = true;
                    else
                    {
                        var t = doc.Tokens[n];
                        missing = t.Is(Keyword.Class) || t.Is(Keyword.Struct) || t.Is(Keyword.Enum) || t.Is(Keyword.Namespace) || t.Is(Keyword.Template) ||
                                  t.Is(Keyword.Typedef) || t.Is(Keyword.Using) || t.Is(Keyword.Public) || t.Is(Keyword.Protected) || t.Is(Keyword.Private) ||
                                  t.Is(Punct.RBrace) && !InInactive(doc, t.Start) ||
                                  t.Kind == TokenKind.Directive && t.Directive == DirectiveKind.Include;
                    }
                    if (!missing) continue;
                    Report(result, Make(doc.Tokens[k].Start, 1, CodeDiagnosticSeverity.Error, CodeDiagnosticCodes.SyntaxError,
                        $"expected ';' after the definition of '{d.Name}'", "missing-semicolon-after-type", d.Name ?? string.Empty));
                }
            }

            /// <summary>In each function body of the range: ')' without '(' or '(' never closed, and a statement left without ';' before a block's '}'.</summary>
            private void FunctionBodies(int start, int end, List<CodeDiagnostic> result, bool branches)
            {
                var doc = _doc;
                for (int i = 0; i < doc.Decls.Count; i++)
                {
                    var d = doc.Decls[i];
                    if (d.IsImplicit || d.Body.IsNone || !TypeText.IsCallableKind(d.Kind) || d.IsConditional) continue;
                    if (d.Body.End <= start || d.Body.Start >= end) continue;
                    if (doc.EnclosingFunction(d.Body.Start) != i) continue; // a lambda-like nested body is scanned with its parent
                    int first = doc.TokenIndexFrom(d.Body.Start), last = doc.TokenIndexFrom(d.Body.End - 1);
                    if (first < 0 || last >= doc.Tokens.Length || last <= first) continue;
                    _s.Ct.ThrowIfCancellationRequested();
                    if (_caret >= d.Body.Start && _caret <= d.Body.End && !branches) Parens(first, last, start, end, result, true);
                    else if (!branches) Parens(first, last, start, end, result, false);
                    MissingSemicolons(first, last, start, end, result);
                }
            }

            private void Parens(int first, int last, int start, int end, List<CodeDiagnostic> result, bool typing)
            {
                var doc = _doc;
                var stack = new Stack<int>();
                for (int k = first + 1; k < last; k++)
                {
                    var t = doc.Tokens[k];
                    if (t.Kind != TokenKind.Punctuator) continue;
                    if ((t.Flags & TokenFlags.Conditional) != 0 && (t.Is(Punct.LParen) || t.Is(Punct.RParen))) return;
                    if (t.Is(Punct.LParen)) stack.Push(k);
                    else if (t.Is(Punct.RParen))
                    {
                        if (stack.Count > 0) { stack.Pop(); continue; }
                        if (t.Start < start || t.Start >= end) return;
                        if (typing && Near(t.Start)) { _stats.Skip("syntax:typing"); return; }
                        Report(result, Make(t.Start, 1, CodeDiagnosticSeverity.Error, CodeDiagnosticCodes.SyntaxError, "unbalanced ')'", "unbalanced-paren", ")"));
                        return;
                    }
                    else if (t.Is(Punct.LBrace) || t.Is(Punct.RBrace) || t.Is(Punct.Semicolon))
                    {
                        // a '(' left open at a statement boundary: lambdas in arguments and for(;;) keep '(' open legitimately, so
                        // only the end of the body decides
                    }
                }
                if (stack.Count == 0) return;
                int open = 0;
                foreach (var o in stack) open = o; // the outermost unclosed one
                var ot = doc.Tokens[open];
                if (ot.Start < start || ot.Start >= end) return;
                if (typing) { _stats.Skip("syntax:typing"); return; }
                Report(result, Make(ot.Start, 1, CodeDiagnosticSeverity.Error, CodeDiagnosticCodes.SyntaxError, "'(' is never closed", "unbalanced-paren", "("));
            }

            private bool Near(int offset) => _caret >= 0 && Math.Abs(LineOf(_caret) - LineOf(offset)) <= 1;

            /// <summary><c>x = 1 }</c>: an expression statement runs into the '}' of a block.</summary>
            private void MissingSemicolons(int first, int last, int start, int end, List<CodeDiagnostic> result)
            {
                var doc = _doc;
                var blocks = new Stack<bool>(); // true = statement block, false = braced initializer / other
                blocks.Push(true);
                for (int k = first + 1; k <= last; k++)
                {
                    var t = doc.Tokens[k];
                    if (t.Is(Punct.LBrace)) { blocks.Push(IsBlockOpen(k)); continue; }
                    if (!t.Is(Punct.RBrace)) continue;
                    bool block = blocks.Count > 0 && blocks.Pop();
                    if (!block || t.Start < start || t.Start >= end) continue;
                    int p = Prev(k);
                    if (p <= first) continue;
                    var pt = doc.Tokens[p];
                    if ((pt.Flags & TokenFlags.Conditional) != 0 || (t.Flags & TokenFlags.Conditional) != 0) continue;
                    if (InInactive(doc, pt.Start)) continue;
                    bool exprEnd = pt.Kind == TokenKind.Identifier || pt.Kind == TokenKind.Number || pt.Kind == TokenKind.String || pt.Kind == TokenKind.Char ||
                                   pt.Is(Punct.RBracket) || pt.Is(Keyword.True) || pt.Is(Keyword.False) || pt.Is(Keyword.Nullptr) || pt.Is(Keyword.This) ||
                                   pt.Is(Punct.PlusPlus) || pt.Is(Punct.MinusMinus) || pt.Is(Punct.RParen);
                    if (!exprEnd) continue;
                    _stats.Checked++;
                    if (pt.Kind == TokenKind.Identifier)
                    {
                        // a label ("End:"), or a macro that is a whole statement on its own
                        string name = doc.TokenText(p);
                        if (doc.Is(Prev(p), Punct.Colon) || IsMacro(p, name)) { _stats.Skip("syntax:macro"); continue; }
                    }
                    if (pt.Is(Punct.RParen))
                    {
                        int open = doc.MatchBackward(p, Punct.LParen, Punct.RParen);
                        if (open < 0) continue;
                        int callee = Prev(open);
                        // if (…) } / while (…) } are other errors; a macro call may expand to a full statement
                        if (callee >= 0 && doc.Tokens[callee].Kind == TokenKind.Keyword) continue;
                        if (doc.IsIdentifier(callee) && IsMacro(callee, doc.TokenText(callee))) { _stats.Skip("syntax:macro"); continue; }
                        if (doc.Is(callee, Punct.RBracket) || doc.Is(callee, Punct.RParen)) continue; // [](…) / (*f)(…): unusual, skip
                    }
                    // what starts the statement: a declaration-like start after '{', ';' or '}'... is fine, a case label is not a statement
                    if (Near(pt.Start)) { _stats.Skip("syntax:typing"); continue; }
                    Report(result, Make(pt.Start + pt.Length - 1, 1, CodeDiagnosticSeverity.Error, CodeDiagnosticCodes.SyntaxError,
                        "expected ';' before '}'", "missing-semicolon", "}"));
                }
            }

            /// <summary>Whether the '{' at <paramref name="k"/> opens a statement block (not a braced initializer).</summary>
            private bool IsBlockOpen(int k)
            {
                var doc = _doc;
                int p = Prev(k);
                if (p < 0) return true;
                var t = doc.Tokens[p];
                if (t.Is(Punct.LBrace) || t.Is(Punct.RBrace) || t.Is(Punct.Semicolon) || t.Is(Keyword.Else) || t.Is(Keyword.Do) || t.Is(Keyword.Try))
                    return true;
                if (t.Is(Punct.Colon)) return true; // case X: { … }
                if (t.Is(Punct.RParen))
                {
                    int open = doc.MatchBackward(p, Punct.LParen, Punct.RParen);
                    int before = Prev(open);
                    if (before < 0) return false;
                    var b = doc.Tokens[before];
                    // if / for / while / switch / catch (…) {, and a lambda's ](…) {
                    return b.Is(Keyword.If) || b.Is(Keyword.For) || b.Is(Keyword.While) || b.Is(Keyword.Switch) || b.Is(Keyword.Catch) || b.Is(Punct.RBracket);
                }
                if (t.Is(Punct.RBracket)) return true;              // [&] { … }
                if (t.Is(Keyword.Mutable) || t.Is(Keyword.Noexcept)) return true;
                return false;
            }
        }
    }
}
