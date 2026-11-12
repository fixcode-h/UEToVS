using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading;
using UEToVS.Contracts;
using Xunit;
using Xunit.Abstractions;

namespace UEToVS.Engine.Tests
{
    /// <summary>
    /// Corpus regression for M3 IntelliSense: every <c>@complete</c> / <c>@sig</c> / <c>@quick</c> marker has an expectation
    /// below (items that must appear in the top N, items that must not appear, context, resolved type, accessor fix...),
    /// every <c>@cls</c> marker must get exactly its semantic kind.
    /// </summary>
    public sealed class IntelliSenseCorpusTests : IClassFixture<IntelliSenseFixture>
    {
        private readonly IntelliSenseFixture _f;
        private readonly ITestOutputHelper _out;

        public IntelliSenseCorpusTests(IntelliSenseFixture f, ITestOutputHelper output)
        {
            _f = f;
            _out = output;
        }

        private sealed class Case
        {
            public string Id;
            public CompletionContextKind Context;
            public string[] Top = Array.Empty<string>();
            public int N = 10;
            public string[] Absent = Array.Empty<string>();
            public string Resolved;
            public string Fix;
        }

        private static Case C(string id, CompletionContextKind ctx, string[] top, int n = 10, string[] absent = null, string resolved = null, string fix = null) =>
            new Case { Id = id, Context = ctx, Top = top, N = n, Absent = absent ?? Array.Empty<string>(), Resolved = resolved, Fix = fix };

        private static readonly Case[] Cases =
        {
            // general: scope order locals > members > globals, prefix + camel-hump matching, keywords, snippets, UE prefix
            C("general.member", CompletionContextKind.General, new[] { "Charges" }, 1),
            C("general.local", CompletionContextKind.General, new[] { "Scaled" }, 1),
            C("general.param", CompletionContextKind.General, new[] { "Power" }, 1),
            C("general.global", CompletionContextKind.General, new[] { "Clamp01" }, 3),
            C("general.keyword", CompletionContextKind.General, new[] { "return" }, 1),
            C("general.snippet", CompletionContextKind.General, new[] { "UPROPERTY" }, 1),
            C("general.unreal", CompletionContextKind.General, new[] { "AGadget", "AGadgetChild" }, 5),
            // member access, chains, UE containers and smart pointers
            C("member.arrow", CompletionContextKind.Member, new[] { "ApplyDamage", "Heal", "MaxHealth", "IsDead", "OnHealthChanged" }, 8,
                new[] { "Charges", "UHealthComponent", "Super" }, "UHealthComponent"),
            C("member.dotfix", CompletionContextKind.Member, new[] { "ApplyDamage", "Heal" }, 10, null, "UHealthComponent", "->"),
            C("member.shared", CompletionContextKind.Member, new[] { "Damage", "DamageAt" }, 2, new[] { "IsValid" }, "FWeaponData"),
            C("member.sharedapi", CompletionContextKind.Member, new[] { "IsValid", "Get", "Reset" }, 10, new[] { "Damage" }, "TSharedPtr<FWeaponData>"),
            C("member.weak", CompletionContextKind.Member, new[] { "GetActorLocation" }, 1, null, "AActor"),
            C("member.index", CompletionContextKind.Member, new[] { "Charges", "Use", "Linked" }, 30, null, "AGadget"),
            C("member.tarray", CompletionContextKind.Member, new[] { "Num", "Add", "IsValidIndex", "Last" }, 60, new[] { "Charges" }, "TArray<AGadget*>"),
            C("member.chain.last", CompletionContextKind.Member, new[] { "Charges" }, 1, null, "AGadget"),
            C("member.map", CompletionContextKind.Member, new[] { "Charges", "Cooldown" }, 40, null, "AGadget"),
            C("member.getowner", CompletionContextKind.Member, new[] { "GetActorLocation" }, 1, null, "AActor"),
            C("member.chain", CompletionContextKind.Member, new[] { "Destroy" }, 1, null, "AActor"),
            C("member.auto", CompletionContextKind.Member, new[] { "Heal", "Health" }, 2, null, "UHealthComponent"),
            C("member.arrowfix", CompletionContextKind.Member, new[] { "X", "Size" }, 10, null, "FVector", "."),
            C("member.value", CompletionContextKind.Member, new[] { "Size" }, 1, null, "FVector"),
            C("member.delegate", CompletionContextKind.Member, new[] { "AddUObject", "AddLambda", "Broadcast" }, 20, new[] { "AddDynamic" }),
            C("member.dyndelegate", CompletionContextKind.Member, new[] { "AddDynamic", "Broadcast", "RemoveDynamic" }, 20, new[] { "AddUObject" }),
            C("member.this", CompletionContextKind.Member, new[] { "Cooldown" }, 1, null, "AGadget"),
            // scopes
            C("scope.enum", CompletionContextKind.Scope, new[] { "Single", "Burst", "Auto" }, 3, null, "EWeaponMode"),
            C("scope.super", CompletionContextKind.Scope, new[] { "BeginPlay", "Tick", "GetActorLocation" }, 40, new[] { "Charges" }, "AActor"),
            C("scope.class", CompletionContextKind.Scope, new[] { "CountAll", "Use", "StaticClass" }, 60, null, "AGadget"),
            C("scope.namespace", CompletionContextKind.Scope, new[] { "FSpread", "DefaultSpread", "Clamp01" }, 3, null, "Game::Weapons"),
            // reflection specifiers (UE5 tables, conflicts and already used ones removed)
            C("spec.uclass", CompletionContextKind.Specifier, new[] { "Abstract", "BlueprintType", "Config" }, 60, new[] { "Blueprintable", "NotBlueprintable", "EditAnywhere" }, "UCLASS"),
            C("spec.uproperty", CompletionContextKind.Specifier, new[] { "BlueprintReadOnly", "Category", "Replicated", "meta" }, 60,
                new[] { "VisibleAnywhere", "EditAnywhere", "EditDefaultsOnly", "BlueprintCallable" + "X" }, "UPROPERTY"),
            C("spec.meta", CompletionContextKind.Specifier, new[] { "ClampMin", "ClampMax", "EditCondition", "DisplayName" }, 80, new[] { "EditAnywhere" }, "UPROPERTY.meta"),
            C("spec.conflict", CompletionContextKind.Specifier, new[] { "BlueprintAssignable", "BlueprintGetter" }, 10, new[] { "BlueprintReadWrite", "BlueprintReadOnly", "EditAnywhere" }, "UPROPERTY"),
            C("spec.ufunction", CompletionContextKind.Specifier, new[] { "Reliable", "Unreliable", "WithValidation" }, 40, new[] { "Server", "Client", "NetMulticast" }, "UFUNCTION"),
            // include paths relative to the module's Public folder
            C("include.gad", CompletionContextKind.Include, new[] { "Gadgets/Gadget.h", "GadgetMacros.h" }, 2),
            C("include.health", CompletionContextKind.Include, new[] { "HealthComponent.h" }, 1),
            // virtual in a class body: overridable base methods with full signatures
            C("override", CompletionContextKind.Override, new[] { "Recharge", "BeginPlay", "OnActivated_Implementation", "PostInitProperties" }, 30,
                new[] { "Tick", "Sealed", "Use", "Charges", "OnActivated" }, "AGadgetChild"),
        };

        private CompletionResponse Complete(string id, char trigger = '\0') =>
            _f.Service.GetCompletions(new CompletionRequest { Caret = _f.Caret(id), TriggerChar = trigger }, CancellationToken.None);

        [Fact]
        public void Completion_Corpus()
        {
            var failures = new StringBuilder();
            int ok = 0;
            foreach (var c in Cases)
            {
                var resp = Complete(c.Id);
                var labels = resp.Items.Select(i => i.Label).ToList();
                var top = labels.Take(c.N).ToList();
                var problems = new List<string>();
                if (resp.Context != c.Context) problems.Add($"context {resp.Context} != {c.Context}");
                foreach (var e in c.Top) if (!top.Contains(e)) problems.Add($"'{e}' not in top {c.N}");
                foreach (var a in c.Absent) if (labels.Contains(a)) problems.Add($"'{a}' must not appear");
                if (c.Resolved != null && resp.ResolvedType != c.Resolved) problems.Add($"resolved '{resp.ResolvedType}' != '{c.Resolved}'");
                if (c.Fix != (resp.AccessorFix?.Replacement)) problems.Add($"fix '{resp.AccessorFix?.Replacement}' != '{c.Fix}'");
                if (problems.Count == 0) ok++;
                else failures.AppendLine($"FAIL {c.Id}: {string.Join("; ", problems)}\n     got [{resp.Context}/{resp.ResolvedType}] {string.Join(", ", labels.Take(Math.Max(c.N, 12)))}");
            }
            _out.WriteLine($"completion corpus: {ok}/{Cases.Length}");
            _out.WriteLine(failures.ToString());
            Assert.True(failures.Length == 0, failures.ToString());
        }

        [Fact]
        public void Completion_SuppressedInCommentsAndStrings()
        {
            Assert.Empty(Complete("none.comment").Items);
            Assert.Empty(Complete("none.string").Items);
        }

        [Fact]
        public void Completion_ReplaceRangeAndScoringOrder()
        {
            var resp = Complete("general.member");
            var caret = _f.Caret("general.member");
            Assert.Equal(caret.Column - 3, resp.ReplaceColumn);
            Assert.Equal(3, resp.ReplaceLength);
            Assert.True(resp.IndexReady);
            var item = resp.Items.First(i => i.Label == "Charges");
            Assert.Equal(SymbolKind.Field, item.Kind);
            Assert.Equal("AGadget", item.Container);
            Assert.Equal("int32", item.Detail);
            Assert.True(item.Ue.HasFlag(UeFlags.UProperty));

            // inherited members are flagged and rank below the class's own
            var arrow = Complete("member.arrow");
            var heal = arrow.Items.First(i => i.Label == "Heal");
            var activate = arrow.Items.First(i => i.Label == "Activate");
            Assert.False(heal.IsInherited);
            Assert.True(activate.IsInherited);
            Assert.True(heal.Score > activate.Score);
            Assert.Equal("UActorComponent", activate.Container);
        }

        [Fact]
        public void Completion_AccessorFixPosition()
        {
            var resp = Complete("member.dotfix");
            var caret = _f.Caret("member.dotfix");
            Assert.NotNull(resp.AccessorFix);
            Assert.Equal(caret.Line, resp.AccessorFix.Line);
            Assert.Equal(caret.Column - 1, resp.AccessorFix.Column);
            Assert.Equal(1, resp.AccessorFix.Length);
            var back = Complete("member.arrowfix");
            Assert.Equal(2, back.AccessorFix.Length);
            Assert.Null(Complete("member.arrow").AccessorFix);
            Assert.Null(Complete("member.sharedapi").AccessorFix); // TSharedPtr has its own API on '.'
        }

        [Fact]
        public void Completion_SpecifierItemsCarryDocs()
        {
            var resp = Complete("spec.uproperty");
            var cat = resp.Items.First(i => i.Label == "Category");
            Assert.True(cat.IsKeyword);
            Assert.Equal("Category = ", cat.InsertText);
            Assert.Contains("Category", cat.Detail);
            Assert.Equal("meta = ()", resp.Items.First(i => i.Label == "meta").InsertText);
            var replicated = resp.Items.First(i => i.Label == "Replicated");
            Assert.Contains("ReplicatedUsing", replicated.Detail);
        }

        [Fact]
        public void Completion_OverrideInsertsFullSignature()
        {
            var resp = Complete("override");
            var caret = _f.Caret("override");
            var recharge = resp.Items.First(i => i.Label == "Recharge");
            Assert.Equal("virtual void Recharge(int32 Amount, bool bFull) const override;", recharge.InsertText);
            Assert.Equal("AGadget", recharge.Container);
            var native = resp.Items.First(i => i.Label == "OnActivated_Implementation");
            Assert.Equal("virtual void OnActivated_Implementation(int32 Level) override;", native.InsertText);
            // the replace range starts at 'virtual'
            Assert.Equal(caret.Column - "virtual ".Length, resp.ReplaceColumn);
        }

        [Fact]
        public void Completion_IncludeItems()
        {
            var resp = Complete("include.gad");
            var item = resp.Items.First(i => i.Label == "Gadgets/Gadget.h");
            Assert.Equal("Gadgets/Gadget.h", item.InsertText);
            Assert.Equal("MyGame", item.Container);
            Assert.DoesNotContain(resp.Items, i => i.Label.EndsWith(".cpp", StringComparison.Ordinal));
            Assert.Equal(_f.Caret("include.gad").Column - 3, resp.ReplaceColumn);
        }

        // ================================================================== signature help

        [Theory]
        [InlineData("use.second", 1, 2, "bool Use(float Power, bool bForce = false)")]
        [InlineData("use.third", 2, 3, "bool Use(AActor* Target, float Power, int32 Times = 1)")]
        [InlineData("use.nested", 1, 2, null)]
        [InlineData("use.string", 1, 2, null)]
        [InlineData("use.lambda", 1, 2, null)]
        [InlineData("use.template", 1, 2, null)]
        [InlineData("use.first", 0, 2, null)]
        [InlineData("member", 0, 1, "void ApplyDamage(float Amount)")]
        [InlineData("delegate", 1, 2, "void Broadcast(float NewHealth, float Delta) const")]
        [InlineData("delegate.execute", 1, 2, "void Execute(int32, bool) const")]
        [InlineData("delegate.dynexecute", 0, 1, "bool ExecuteIfBound(float Strength) const")]
        [InlineData("delegate.native", 0, 1, "void Broadcast(int32) const")]
        [InlineData("ctor", 1, 3, "FVector(float InX, float InY, float InZ)")]
        [InlineData("defaults", 1, 2, "void Recharge(int32 Amount, bool bFull = false) const")]
        public void SignatureHelp_Corpus(string id, int activeParameter, int paramCount, string label)
        {
            var resp = _f.Service.GetSignatureHelp(_f.Caret(id), CancellationToken.None);
            _out.WriteLine(string.Join("\n", resp.Signatures.Select(s => s.Label)));
            Assert.NotEmpty(resp.Signatures);
            Assert.Equal(activeParameter, resp.ActiveParameter);
            var active = resp.Signatures[resp.ActiveSignature];
            Assert.Equal(paramCount, active.Parameters.Count);
            if (label != null) Assert.Equal(label, active.Label);
            // parameter ranges point into the label
            foreach (var p in active.Parameters)
            {
                Assert.InRange(p.LabelStart, 0, active.Label.Length - 1);
                Assert.True(p.LabelStart + p.LabelLength <= active.Label.Length);
            }
        }

        [Fact]
        public void SignatureHelp_OverloadsDocsAndParameterRanges()
        {
            var resp = _f.Service.GetSignatureHelp(_f.Caret("use.second"), CancellationToken.None);
            Assert.Equal(2, resp.Signatures.Count);
            var sig = resp.Signatures[resp.ActiveSignature];
            Assert.Equal("bool bForce = false", sig.Label.Substring(sig.Parameters[1].LabelStart, sig.Parameters[1].LabelLength));
            Assert.Equal("bForce", sig.Parameters[1].Name);
            Assert.StartsWith("Uses the gadget once.", sig.Documentation);
            Assert.Equal("how hard to use it", sig.Parameters[0].Documentation);
            var caret = _f.Caret("use.second");
            Assert.Equal(caret.Line, resp.OpenParenLine);
            Assert.Equal(4, resp.OpenParenColumn); // "\tUse("
        }

        // ================================================================== quick info

        [Fact]
        public void QuickInfo_FunctionDocsReflectionOverloads()
        {
            var q = _f.Service.GetQuickInfo(_f.Caret("use"), CancellationToken.None);
            _out.WriteLine(q.Declaration + "\n" + q.Documentation + "\n" + q.Reflection);
            Assert.Equal("bool AGadget::Use(float Power, bool bForce = false)", q.Declaration);
            Assert.StartsWith("Uses the gadget once.", q.Documentation);
            Assert.DoesNotContain("/**", q.Documentation);
            Assert.DoesNotContain("*/", q.Documentation);
            Assert.StartsWith("UFUNCTION(BlueprintCallable, Category = \"Gadget\")", q.Reflection);
            Assert.Contains("Blueprint callable", q.Reflection);
            Assert.Equal(1, q.OverloadCount);
            Assert.Equal("Use", q.Symbol.Name);
            Assert.True(q.Confidence >= ResolutionConfidence.Scoped);
            Assert.Equal(3, q.Length);

            var t = _f.Service.GetQuickInfo(_f.Caret("use.target"), CancellationToken.None);
            Assert.Equal("bool AGadget::Use(AActor* Target, float Power, int32 Times = 1)", t.Declaration);
            Assert.Equal("Uses the gadget on a target.", t.Documentation);
        }

        [Fact]
        public void QuickInfo_PropertyLineCommentAndMeta()
        {
            var q = _f.Service.GetQuickInfo(_f.Caret("charges"), CancellationToken.None);
            _out.WriteLine(q.Declaration + "\n" + q.Documentation + "\n" + q.Reflection);
            Assert.Equal("int32 AGadget::Charges", q.Declaration);
            Assert.Equal("Number of charges left.", q.Documentation);
            Assert.Contains("UPROPERTY(EditAnywhere, BlueprintReadWrite", q.Reflection);
            Assert.Contains("meta: ClampMin = \"0\", UIMax = \"10\"", q.Reflection);
            Assert.Contains("Editable", q.Reflection);
            Assert.Contains("Category: Gadget", q.Reflection);
        }

        [Fact]
        public void QuickInfo_ClassDocs()
        {
            var q = _f.Service.GetQuickInfo(_f.Caret("class"), CancellationToken.None);
            Assert.Equal("class AGadget : public AActor", q.Declaration);
            Assert.Equal("A gadget the player can carry.\nSupports charges.", q.Documentation);
            Assert.StartsWith("UCLASS(Blueprintable", q.Reflection);
        }

        [Fact]
        public void QuickInfo_MacroExpansion()
        {
            var q = _f.Service.GetQuickInfo(_f.Caret("macro.mul"), CancellationToken.None);
            _out.WriteLine(q.Declaration + "\n" + q.MacroExpansion);
            Assert.Equal("#define GADGET_MUL(X, Y) ((X) * (Y) * GADGET_SCALE)", q.Declaration);
            Assert.Equal("((2) * (3) * GADGET_SCALE)\n\n((2) * (3) * 2)", q.MacroExpansion);

            var va = _f.Service.GetQuickInfo(_f.Caret("macro.va"), CancellationToken.None);
            _out.WriteLine(va.MacroExpansion);
            Assert.StartsWith("UE_LOG(LogTemp, Log, TEXT(\"x\"),1)", va.MacroExpansion.Replace(", 1", ",1"));
            Assert.Equal("Logs a gadget message.", va.Documentation);

            // self-reference is not expanded again (preprocessor rule), no infinite loop
            var self = _f.Service.GetQuickInfo(_f.Caret("macro.self"), CancellationToken.None);
            Assert.Equal("GADGET_SELF + 1", self.MacroExpansion);

            var ue = _f.Service.GetQuickInfo(_f.Caret("uemacro"), CancellationToken.None);
            Assert.Contains("UE_LOG(Category", ue.Documentation);

            var unknown = _f.Service.GetQuickInfo(_f.Caret("unknown"), CancellationToken.None);
            Assert.Equal(ResolutionConfidence.None, unknown.Confidence);
            Assert.Null(unknown.Declaration);
        }

        [Fact]
        public void QuickInfo_SpecifierAndLocal()
        {
            // hovering a specifier inside UPROPERTY(...)
            string header = _f.PathOf("IS/Game/Public/Gadgets/Gadget.h");
            var lines = System.IO.File.ReadAllLines(header);
            int line = Array.FindIndex(lines, l => l.Contains("VisibleAnywhere, /*@complete:spec.uproperty"));
            int col = lines[line].IndexOf("VisibleAnywhere", StringComparison.Ordinal) + 3;
            var q = _f.Service.GetQuickInfo(new CaretRequest { FilePath = header, Line = line, Column = col }, CancellationToken.None);
            Assert.Equal("UPROPERTY specifier VisibleAnywhere", q.Declaration);
            Assert.Contains("Visible (read-only)", q.Documentation);
            Assert.Contains("EditAnywhere", q.Documentation);
        }

        // ================================================================== classification

        [Fact]
        public void Classify_Corpus()
        {
            Assert.True(_f.Classes.Count >= 30, "markers: " + _f.Classes.Count);
            var failures = new StringBuilder();
            int ok = 0;
            foreach (var group in _f.Classes.GroupBy(c => c.File))
            {
                var resp = _f.Service.Classify(new ClassifyRequest { Document = new CaretRequest { FilePath = group.Key }, StartLine = 0, EndLine = 100000 }, CancellationToken.None);
                foreach (var m in group)
                {
                    var span = resp.Spans.FirstOrDefault(s => s.Line == m.Line && s.Column == m.Column);
                    bool good = span != null && span.Kind == m.Kind && span.Length == m.Word.Length &&
                                span.Modifiers.HasFlag(SemanticModifiers.Declaration) == m.Declaration;
                    if (good) ok++;
                    else failures.AppendLine($"FAIL {m}: got {(span == null ? "nothing" : span.Kind + " " + span.Modifiers + " len " + span.Length)}");
                }
            }
            _out.WriteLine($"classification corpus: {ok}/{_f.Classes.Count}");
            _out.WriteLine(failures.ToString());
            Assert.True(failures.Length == 0, failures.ToString());
        }

        [Fact]
        public void Classify_RangeOnlyAndCached()
        {
            string cpp = _f.PathOf("IS/Game/Private/Gadget.cpp");
            var doc = new CaretRequest { FilePath = cpp, Text = System.IO.File.ReadAllText(cpp), TextVersion = 77 };
            var a = _f.Service.Classify(new ClassifyRequest { Document = doc, StartLine = 80, EndLine = 85 }, CancellationToken.None);
            Assert.Equal(77, a.TextVersion);
            Assert.NotEmpty(a.Spans);
            Assert.All(a.Spans, s => Assert.InRange(s.Line, 80, 85));
            long misses = _f.Service.Classifier.LineMisses;
            var b = _f.Service.Classify(new ClassifyRequest { Document = doc, StartLine = 80, EndLine = 85 }, CancellationToken.None);
            Assert.Equal(misses, _f.Service.Classifier.LineMisses); // all lines from the cache
            Assert.Equal(a.Spans.Count, b.Spans.Count);
            // an edit: new version, lines are recomputed but name decisions come from the memo
            long memoHits = _f.Service.Classifier.MemoHits;
            var edited = new CaretRequest { FilePath = cpp, Text = doc.Text.Replace("int32 N = ", "int32 N2 = "), TextVersion = 78 };
            var c = _f.Service.Classify(new ClassifyRequest { Document = edited, StartLine = 80, EndLine = 85 }, CancellationToken.None);
            Assert.True(_f.Service.Classifier.MemoHits > memoHits);
            Assert.Equal(a.Spans.Count, c.Spans.Count);
        }
    }
}
