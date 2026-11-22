using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using UEToVS.Contracts;
using UEToVS.Host.Logic.Commands;
using UEToVS.Host.Logic.Editing;
using UEToVS.Host.Logic.IntelliSense;
using UEToVS.Host.Logic.Refactoring;
using UEToVS.Host.Logic.Settings;
using Xunit;

namespace UEToVS.Host.Logic.Tests
{
    /// <summary>v0.9.12 写代码辅助：输入即纠正 . / -&gt;、UE0009 / UE0010 修复、说明符编辑器。</summary>
    public sealed class CodingAssistTests
    {
        private static CodeDiagnostic D(string code, int line, int col, int len, params string[] args) =>
            new CodeDiagnostic { Code = code, Line = line, Column = col, Length = len, Args = args.ToList() };

        private static string Apply(string text, IEnumerable<TextEdit> edits)
        {
            string r = TextEditApplier.Apply(text, edits, out int skipped);
            Assert.Equal(0, skipped);
            return r;
        }

        private static (int Line, int Column) Pos(string text, string needle, int nth = 0)
        {
            int at = -1;
            for (int i = 0; i <= nth; i++) at = text.IndexOf(needle, at + 1, StringComparison.Ordinal);
            Assert.True(at >= 0, needle);
            var starts = TextEditApplier.LineStarts(text);
            return UeRuleFixPlanner.PositionOf(starts, at);
        }

        // ============================================================ . ↔ ->

        [Fact]
        public void AccessorFix_OnTypeOnlyWhenConfidentAndJustTyped()
        {
            var dot = new AccessorFix { Line = 3, Column = 10, Length = 1, Replacement = "->", Confident = true };
            Assert.True(AccessorFixes.ShouldApplyOnType(dot, '.', 3, 11, true));
            Assert.False(AccessorFixes.ShouldApplyOnType(dot, '.', 3, 11, false)); // 设置关
            Assert.False(AccessorFixes.ShouldApplyOnType(dot, '\0', 3, 11, true)); // Ctrl+Space 打开的：等提交
            Assert.False(AccessorFixes.ShouldApplyOnType(dot, '.', 3, 12, true)); // 访问符不在光标前
            Assert.False(AccessorFixes.ShouldApplyOnType(dot, '.', 4, 11, true));
            Assert.False(AccessorFixes.ShouldApplyOnType(new AccessorFix { Line = 3, Column = 10, Length = 1, Replacement = "->" }, '.', 3, 11, true)); // 没把握
            var arrow = new AccessorFix { Line = 0, Column = 4, Length = 2, Replacement = ".", Confident = true };
            Assert.True(AccessorFixes.ShouldApplyOnType(arrow, '>', 0, 6, true));
            Assert.False(AccessorFixes.ShouldApplyOnType(arrow, '.', 0, 6, true));
            Assert.False(AccessorFixes.ShouldApplyOnType(null, '.', 0, 0, true));
        }

        [Fact]
        public void AccessorFix_SettingRoundTripsAndDefaultsOn()
        {
            Assert.True(new HostSettings().CompletionAccessorFixOnType);
            var schema = HostSettingsSchema.Build(8);
            Assert.Single(schema, d => d.Key == CodingAssistSettings.AccessorFixOnTypeKey);
            var s = HostSettingsSchema.Codec.FromValues(new Dictionary<string, string> { [CodingAssistSettings.AccessorFixOnTypeKey] = "false" }, schema, new HostSettings());
            Assert.False(s.CompletionAccessorFixOnType);
            Assert.Equal("false", HostSettingsSchema.Codec.ToValues(s)[CodingAssistSettings.AccessorFixOnTypeKey]);
            Assert.Contains(HostSettingsPages.Cards, c => c.Keys.Contains(CodingAssistSettings.AccessorFixOnTypeKey));
        }

        // ============================================================ 反射宏定位

        [Fact]
        public void ReflectionMacro_FoundOnNameAndInsideParensOnly()
        {
            const string h = "\t// UPROPERTY(Fake)\n\tUPROPERTY(EditAnywhere, meta=(ClampMin=\"0\"))\n\tint32 Value;\n";
            int name = h.IndexOf("UPROPERTY(Edit", StringComparison.Ordinal);
            var call = ReflectionMacroText.FindAt(h, name + 2);
            Assert.NotNull(call);
            Assert.Equal("UPROPERTY", call.Name);
            Assert.Equal("EditAnywhere, meta=(ClampMin=\"0\")", call.Arguments);
            Assert.NotNull(ReflectionMacroText.FindAt(h, h.IndexOf("ClampMin", StringComparison.Ordinal)));
            Assert.NotNull(ReflectionMacroText.FindAt(h, call.End));
            Assert.Null(ReflectionMacroText.FindAt(h, h.IndexOf("Value", StringComparison.Ordinal)));
            Assert.Null(ReflectionMacroText.FindAt(h, h.IndexOf("Fake", StringComparison.Ordinal))); // 注释里的不算
            Assert.Null(ReflectionMacroText.FindAt("UPROPERTYX(A)", 3));
        }

        [Fact]
        public void ReflectionMacro_BeforeDeclarationAndRemoval()
        {
            const string h = "\tUPROPERTY()\n\tint32 A;\n\tTSharedPtr<FFoo> B;\n\tUPROPERTY(Transient) FBar* C;\n";
            Assert.Null(ReflectionMacroText.FindBefore(h, h.IndexOf("B;", StringComparison.Ordinal), "UPROPERTY")); // 中间有 ';'
            var a = ReflectionMacroText.FindBefore(h, h.IndexOf("A;", StringComparison.Ordinal), "UPROPERTY");
            Assert.NotNull(a);
            var (s, l) = ReflectionMacroText.RemovalSpan(h, a);
            Assert.Equal("\tint32 A;\n", h.Remove(s, l).Substring(0, 10)); // 独占一行：整行删掉
            var c = ReflectionMacroText.FindBefore(h, h.IndexOf("C;", StringComparison.Ordinal), "UPROPERTY");
            var (s2, l2) = ReflectionMacroText.RemovalSpan(h, c);
            Assert.EndsWith("\tFBar* C;\n", h.Remove(s2, l2));
        }

        // ============================================================ UE0009 / UE0010

        private static UeRuleFix Fix(CodeDiagnostic d, string variant, string target = null)
        {
            var f = UeRuleFix.From(d);
            f.Variant = variant;
            f.Target = target;
            return f;
        }

        [Fact]
        public void Ue0009_ByValueAndRemoveUProperty()
        {
            const string h = "\tUPROPERTY(EditAnywhere)\n\tFWeaponData* Data;\n\tUPROPERTY()\n\tTSharedPtr<FFoo> Shared;\n";
            var (l, c) = Pos(h, "Data;");
            var d = D(CodeDiagnosticCodes.UnsupportedPropertyType, l, c, 4, "Data", "FWeaponData*");
            var items = new UhtTypeFixProvider().GetActions(new RefactorContext { Diagnostics = new List<CodeDiagnostic> { d } }).ToList();
            Assert.Equal(new[] { UhtTypeFixVariants.ByValue, UhtTypeFixVariants.RemoveUProperty }, items.Select(i => ((UeRuleFix)i.Argument).Variant));
            Assert.All(items, i => Assert.Equal(UeRuleFixIds.Fix, i.ActionId));

            var edits = UhtTypeFixPlanner.Plan("A.h", h, Fix(d, UhtTypeFixVariants.ByValue), out string problem);
            Assert.Null(problem);
            Assert.Contains("\tFWeaponData Data;\n", Apply(h, edits));
            edits = UhtTypeFixPlanner.Plan("A.h", h, Fix(d, UhtTypeFixVariants.RemoveUProperty), out problem);
            Assert.StartsWith("\tFWeaponData* Data;\n\tUPROPERTY()", Apply(h, edits));

            var (l2, c2) = Pos(h, "Shared;");
            var shared = D(CodeDiagnosticCodes.UnsupportedPropertyType, l2, c2, 6, "Shared", "TSharedPtr<FFoo>");
            var only = new UhtTypeFixProvider().GetActions(new RefactorContext { Diagnostics = new List<CodeDiagnostic> { shared } }).ToList();
            Assert.Equal(UhtTypeFixVariants.RemoveUProperty, ((UeRuleFix)Assert.Single(only).Argument).Variant);
            edits = UhtTypeFixPlanner.Plan("A.h", h, Fix(shared, UhtTypeFixVariants.RemoveUProperty), out problem);
            Assert.EndsWith("\tFWeaponData* Data;\n\tTSharedPtr<FFoo> Shared;\n", Apply(h, edits));
        }

        [Fact]
        public void Ue0009_ByValueSpacing()
        {
            foreach (var (src, want) in new[] { ("FFoo *P;", "FFoo P;"), ("FFoo*P;", "FFoo P;"), ("FFoo * P;", "FFoo P;"), ("const FFoo* P;", "const FFoo P;") })
            {
                string h = "UPROPERTY() " + src + "\n";
                var (l, c) = Pos(h, "P;");
                var edits = UhtTypeFixPlanner.Plan("A.h", h, Fix(D(CodeDiagnosticCodes.UnsupportedPropertyType, l, c, 1, "P", "FFoo*"), UhtTypeFixVariants.ByValue), out string problem);
                Assert.Null(problem);
                Assert.Equal("UPROPERTY() " + want + "\n", Apply(h, edits));
            }
        }

        [Fact]
        public void Ue0010_PointerParameterToReference()
        {
            const string h = "\tUFUNCTION(BlueprintCallable)\n\tvoid Use(int32 A, FHitResult* Hit, const FVector* Where);\n";
            var (l, c) = Pos(h, "Hit,");
            var d = D(CodeDiagnosticCodes.UnsupportedParameterType, l, c, 3, "Hit", "FHitResult*", "Use", "pointer");
            var items = new UhtTypeFixProvider().GetActions(new RefactorContext { Diagnostics = new List<CodeDiagnostic> { d } }).ToList();
            Assert.Equal(new[] { UhtTypeFixVariants.ConstRef, UhtTypeFixVariants.Ref }, items.Select(i => ((UeRuleFix)i.Argument).Variant));
            Assert.Contains("const FHitResult&", items[0].Title);

            Assert.Contains("int32 A, const FHitResult& Hit,", Apply(h, UhtTypeFixPlanner.Plan("A.h", h, Fix(d, UhtTypeFixVariants.ConstRef), out _)));
            Assert.Contains("int32 A, FHitResult& Hit,", Apply(h, UhtTypeFixPlanner.Plan("A.h", h, Fix(d, UhtTypeFixVariants.Ref), out _)));
            // 已经有 const 的不再加
            var (l2, c2) = Pos(h, "Where)");
            var w = D(CodeDiagnosticCodes.UnsupportedParameterType, l2, c2, 5, "Where", "FVector*", "Use", "pointer");
            Assert.Contains("const FVector& Where)", Apply(h, UhtTypeFixPlanner.Plan("A.h", h, Fix(w, UhtTypeFixVariants.ConstRef), out _)));
        }

        [Fact]
        public void Ue0010_BlueprintNumbersAndRemoveUFunction()
        {
            const string h = "\tUFUNCTION(BlueprintCallable)\n\tvoid Set(uint16 Small, const uint32& Big);\n\tUFUNCTION()\n\tvoid Bind(TFunction<void()> Fn);\n";
            var (l, c) = Pos(h, "Small");
            var small = D(CodeDiagnosticCodes.UnsupportedParameterType, l, c, 5, "Small", "uint16", "Set", "blueprint");
            var item = Assert.Single(new UhtTypeFixProvider().GetActions(new RefactorContext { Diagnostics = new List<CodeDiagnostic> { small } }));
            var fix = (UeRuleFix)item.Argument;
            Assert.Equal("int32", fix.Target);
            Assert.Contains("void Set(int32 Small,", Apply(h, UhtTypeFixPlanner.Plan("A.h", h, fix, out _)));
            var (l2, c2) = Pos(h, "Big)");
            var big = D(CodeDiagnosticCodes.UnsupportedParameterType, l2, c2, 3, "Big", "uint32&", "Set", "blueprint");
            Assert.Contains("const int64& Big", Apply(h, UhtTypeFixPlanner.Plan("A.h", h, Fix(big, UhtTypeFixVariants.BlueprintNumber, "int64"), out _)));
            Assert.Equal("int64", UhtTypeFixPlanner.BlueprintNumberFor("uint64"));
            Assert.Null(UhtTypeFixPlanner.BlueprintNumberFor("float"));

            var (l3, c3) = Pos(h, "Fn)");
            var fn = D(CodeDiagnosticCodes.UnsupportedParameterType, l3, c3, 2, "Fn", "TFunction<void()>", "Bind", "unsupported");
            var rm = Assert.Single(new UhtTypeFixProvider().GetActions(new RefactorContext { Diagnostics = new List<CodeDiagnostic> { fn } }));
            Assert.EndsWith("\tvoid Set(uint16 Small, const uint32& Big);\n\tvoid Bind(TFunction<void()> Fn);\n",
                Apply(h, UhtTypeFixPlanner.Plan("A.h", h, (UeRuleFix)rm.Argument, out _)));
        }

        [Fact]
        public void UhtTypeFix_StaleTextIsRefused()
        {
            const string h = "UPROPERTY() FFoo* P;\n";
            var d = D(CodeDiagnosticCodes.UnsupportedPropertyType, 0, 3, 1, "P", "FFoo*");
            Assert.Null(UhtTypeFixPlanner.Plan("A.h", h, Fix(d, UhtTypeFixVariants.ByValue), out string problem));
            Assert.Equal("stale", problem);
            Assert.Empty(new UhtTypeFixProvider().GetActions(new RefactorContext { IsProjectFile = false, Diagnostics = new List<CodeDiagnostic> { d } }));
        }

        // ============================================================ 说明符编辑器

        private static SpecifierCatalog Catalog() =>
            SpecifierCatalog.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "UeMacros.json")));

        [Fact]
        public void SpecifierCatalog_HasChineseDocsAndConflicts()
        {
            var c = Catalog();
            foreach (var macro in ReflectionMacroText.EditableMacros)
            {
                Assert.NotEmpty(c.Specifiers(macro));
                Assert.All(c.Specifiers(macro).Concat(c.MetaKeys(macro)), d => Assert.False(string.IsNullOrEmpty(d.DocZh), d.ToString()));
                Assert.DoesNotContain(c.Specifiers(macro), d => d.Name == "meta");
            }
            var edit = c.Find("UPROPERTY", "editanywhere", false);
            Assert.Contains("VisibleAnywhere", edit.Conflicts);
            Assert.Contains("BlueprintReadWrite", c.Find("UPROPERTY", "BlueprintReadOnly", false).Conflicts);
            Assert.True(c.Find("UPROPERTY", "ClampMin", true).HasValue);
            Assert.Equal("Editable in the Details panel of archetypes and instances.", edit.Description(true));
            Assert.NotEqual(edit.Description(true), edit.Description(false));
        }

        [Fact]
        public void SpecifierSet_ParseFormatRoundTrip()
        {
            const string args = "EditAnywhere, BlueprintReadOnly, Category = \"Combat|Weapons\", meta = (ClampMin = \"0\", AllowPrivateAccess), Custom";
            var s = SpecifierSet.Parse(args);
            Assert.Equal(new[] { "EditAnywhere", "BlueprintReadOnly", "Category", "Custom" }, s.Items.Select(e => e.Key));
            Assert.Equal(new[] { "ClampMin", "AllowPrivateAccess" }, s.Meta.Select(e => e.Key));
            Assert.Equal(args, s.Format());
            Assert.Equal("\"Combat|Weapons\"", s.Get("category", false).Value);

            var tight = SpecifierSet.Parse("Replicated,Category=\"A, B\"");
            Assert.Equal("Replicated, Category=\"A, B\"", tight.Format()); // 等号风格跟原文
            tight.Remove("Replicated", false);
            tight.Set("ReplicatedUsing", "OnRep_X", false);
            tight.Set("ClampMin", "\"1\"", true);
            Assert.Equal("Category=\"A, B\", ReplicatedUsing=OnRep_X, meta=(ClampMin=\"1\")", tight.Format());
            Assert.Equal(string.Empty, SpecifierSet.Parse("  ").Format());
        }

        [Fact]
        public void SpecifierSet_ValueNormalization()
        {
            Assert.Equal("\"Combat\"", SpecifierSet.NormalizeValue("=\"Category\"", "Combat"));
            Assert.Equal("\"Combat\"", SpecifierSet.NormalizeValue("=\"Category\"", "\"Combat\""));
            Assert.Equal("\"a \\\"b\\\"\"", SpecifierSet.NormalizeValue("=\"Text\"", "a \"b\""));
            Assert.Equal("(A, B)", SpecifierSet.NormalizeValue("=(Category1, ...)", "A, B"));
            Assert.Equal("OnRep_X", SpecifierSet.NormalizeValue("=OnRep_Function", " OnRep_X "));
            Assert.Equal(string.Empty, SpecifierSet.NormalizeValue("=\"X\"", "  "));
            Assert.Equal("Combat", SpecifierSet.DisplayValue("\"Combat\""));
            Assert.Equal("a \"b\"", SpecifierSet.DisplayValue("\"a \\\"b\\\"\""));
        }

        [Fact]
        public void SpecifierSet_ChecksConflictsValuesAndRpc()
        {
            var c = Catalog();
            var p = SpecifierSet.Parse("EditAnywhere, VisibleAnywhere, BlueprintReadOnly, BlueprintReadWrite, ReplicatedUsing=").Check(c, "UPROPERTY");
            Assert.Contains(p, x => x.Kind == "conflict" && new[] { x.A, x.B }.OrderBy(n => n).SequenceEqual(new[] { "EditAnywhere", "VisibleAnywhere" }));
            Assert.Contains(p, x => x.Kind == "conflict" && x.A.StartsWith("Blueprint", StringComparison.Ordinal));
            Assert.Equal(2, p.Count(x => x.Kind == "conflict")); // 每对只报一次
            Assert.Contains(p, x => x.Kind == "missing-value" && x.A == "ReplicatedUsing");
            Assert.All(p, x => Assert.True(x.Blocking));

            var rpc = SpecifierSet.Parse("Server").Check(c, "UFUNCTION");
            var r = Assert.Single(rpc);
            Assert.Equal("rpc-reliability", r.Kind);
            Assert.False(r.Blocking);
            Assert.Empty(SpecifierSet.Parse("Server, Reliable, WithValidation").Check(c, "UFUNCTION"));
            Assert.Empty(SpecifierSet.Parse("EditAnywhere, meta=(AllowPrivateAccess)").Check(c, "UPROPERTY")); // =true 类的值可省
        }

        [Fact]
        public void SpecifierEditor_EditReplacesTheArgumentsOnly()
        {
            const string h = "\tUPROPERTY(EditAnywhere, Category=\"A\")\n\tint32 V;\n";
            var call = ReflectionMacroText.FindAt(h, h.IndexOf("Edit", StringComparison.Ordinal));
            var s = SpecifierSet.Parse(call.Arguments);
            Assert.Null(SpecifierEditorPlanner.Edit("A.h", h, call, s)); // 没改
            s.Remove("EditAnywhere", false);
            s.Set("VisibleAnywhere", null, false);
            s.Set("ClampMin", "\"0\"", true);
            var edit = SpecifierEditorPlanner.Edit("A.h", h, call, s);
            Assert.Equal("\tUPROPERTY(Category=\"A\", VisibleAnywhere, meta=(ClampMin=\"0\"))\n\tint32 V;\n", Apply(h, new[] { edit }));
        }

        [Fact]
        public void SpecifierEditor_ProviderOnMacroOnly()
        {
            const string h = "UFUNCTION(BlueprintCallable)\nvoid Fire();\n";
            RefactorContext Ctx(int caret, bool project = true) => new RefactorContext { FilePath = "A.h", TextSource = () => h, CaretOffset = caret, IsProjectFile = project };
            var item = Assert.Single(new SpecifierEditorProvider().GetActions(Ctx(3)));
            Assert.Equal(SpecifierEditorIds.Edit, item.ActionId);
            Assert.Equal("UFUNCTION", ((ReflectionMacroCall)item.Argument).Name);
            Assert.Empty(new SpecifierEditorProvider().GetActions(Ctx(h.IndexOf("Fire", StringComparison.Ordinal))));
            Assert.Empty(new SpecifierEditorProvider().GetActions(Ctx(3, false)));
        }

        [Fact]
        public void SpecifierEditor_CommandRegistered()
        {
            var cmd = CommandCatalog.ByName("UEToVS.EditSpecifiers");
            Assert.NotNull(cmd);
            Assert.Equal(SpecifierEditorIds.Edit, cmd.ActionId);
            Assert.Equal(CommandScope.TextEditor, cmd.Scope);
            Assert.Equal(CommandCatalog.EditSpecifiers, cmd.Id);
        }
    }
}
