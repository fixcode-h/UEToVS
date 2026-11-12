using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using UEToVS.Contracts;
using UEToVS.Engine.Indexing;
using UEToVS.Engine.Navigation;
using Xunit;
using Xunit.Abstractions;

namespace UEToVS.Engine.Tests
{
    /// <summary>Find references, type hierarchy, highlights, members and caret context over the navigation corpus.</summary>
    public sealed class NavigationFeatureTests : IClassFixture<NavigationFixture>
    {
        private readonly NavigationFixture _f;
        private readonly ITestOutputHelper _out;

        public NavigationFeatureTests(NavigationFixture f, ITestOutputHelper output)
        {
            _f = f;
            _out = output;
        }

        private NavigationService Nav => _f.Navigation;

        private static string Where(ReferenceHit h) => Path.GetFileName(h.Location.FilePath) + ":" + (h.Location.Line + 1);

        private string Line(string file, string text, int nth = 0) => Path.GetFileName(file) + ":" + (_f.Find(file, text, nth).Line + 1);

        // ================================================================== references

        [Fact]
        public void References_SameNamedMembersOfDifferentClassesAreSeparated()
        {
            // UHealthComponent::Health
            var resp = Nav.FindReferences(new ReferencesRequest { Caret = _f.Caret("Game/HealthComponent.h", "Health = 100.f") }, CancellationToken.None);
            Assert.Equal("Health", resp.Symbol.Name);
            Assert.Equal("UHealthComponent", resp.Symbol.Container);
            var where = resp.Hits.Select(Where).ToList();
            _out.WriteLine(string.Join("\n", resp.Hits.Select(h => Where(h) + " " + h.Kind + " " + h.Confidence + " | " + h.LineText)));
            Assert.Contains(Line("Game/HealthComponent.h", "Health = 100.f"), where);
            Assert.Contains(Line("Game/HealthComponent.h", "Health <= 0.f"), where);
            Assert.Contains(Line("Game/HealthComponent.cpp", "Health = MaxHealth"), where);
            Assert.Contains(Line("Game/MyCharacter.cpp", "Health*/Health = 100.f"), where);
            Assert.Contains(Line("Game/Weapon.cpp", "Health += 10.f"), where);
            // AMyCharacter::Health is a different member
            Assert.DoesNotContain(Line("Game/MyCharacter.h", "Health = 3;"), where);
            Assert.DoesNotContain(Line("Game/MyCharacter.cpp", "this->"), where);
            Assert.DoesNotContain(Line("Game/Weapon.cpp", "Health -= 1"), where);
            Assert.Equal(ReferenceKind.Declaration, resp.Hits.Single(h => Where(h) == Line("Game/HealthComponent.h", "Health = 100.f")).Kind);
            Assert.True(resp.Hits.Single(h => Where(h) == Line("Game/HealthComponent.cpp", "Health = MaxHealth")).Kind.HasFlag(ReferenceKind.Write));

            var mine = Nav.FindReferences(new ReferencesRequest { Caret = _f.Caret("Game/MyCharacter.h", "Health = 3;") }, CancellationToken.None);
            Assert.Equal("AMyCharacter", mine.Symbol.Container);
            var mineWhere = mine.Hits.Select(Where).ToList();
            Assert.Contains(Line("Game/MyCharacter.cpp", "this->"), mineWhere);
            Assert.Contains(Line("Game/Weapon.cpp", "Health -= 1"), mineWhere);
            Assert.DoesNotContain(Line("Game/Weapon.cpp", "Health += 10.f"), mineWhere);
            Assert.DoesNotContain(Line("Game/HealthComponent.cpp", "Health = MaxHealth"), mineWhere);
        }

        [Fact]
        public void References_LineTextColumnsAndContext()
        {
            var resp = Nav.FindReferences(new ReferencesRequest { Caret = _f.Caret("Game/HealthComponent.cpp", "ApplyDamage*/ApplyDamage") }, CancellationToken.None);
            Assert.NotEmpty(resp.Hits);
            foreach (var h in resp.Hits)
            {
                Assert.Equal("ApplyDamage", h.LineText.Substring(h.LineTextColumn, h.Length));
                Assert.False(char.IsWhiteSpace(h.LineText[0]));
                Assert.Equal(ItemOrigin.Project, h.Origin);
                Assert.Equal("MyGame", h.Module);
            }
            var call = resp.Hits.Single(h => Where(h) == Line("Game/HealthComponent.cpp", "@caret:UHealthComponent.ApplyDamage*/ApplyDamage"));
            Assert.Equal("UHealthComponent::Heal", call.Context);
            Assert.True(call.Kind.HasFlag(ReferenceKind.Call));
            Assert.Contains(resp.Hits, h => h.Kind == ReferenceKind.Definition);
            Assert.Contains(resp.Hits, h => h.Kind == ReferenceKind.Declaration);
            Assert.Contains(resp.Hits, h => Path.GetFileName(h.Location.FilePath) == "MyCharacter.cpp");

            var noDecl = Nav.FindReferences(new ReferencesRequest { Caret = _f.Caret("Game/HealthComponent.cpp", "ApplyDamage*/ApplyDamage"), IncludeDeclarations = false }, CancellationToken.None);
            Assert.DoesNotContain(noDecl.Hits, h => (h.Kind & (ReferenceKind.Declaration | ReferenceKind.Definition)) != 0);
            Assert.Equal(resp.Hits.Count - 2, noDecl.Hits.Count);
        }

        [Fact]
        public void References_OfLocalStayInItsFunction()
        {
            var resp = Nav.FindReferences(new ReferencesRequest { Caret = _f.Caret("Game/HealthComponent.cpp", "local.Old*/Old = Health") }, CancellationToken.None);
            Assert.Equal(2, resp.Hits.Count);
            Assert.Equal(ReferenceKind.Declaration, resp.Hits[0].Kind);
            Assert.Equal(1, resp.FilesScanned);
        }

        [Fact]
        public void References_TypeUsesInHeaders_NonProjectFilter_MaxResults()
        {
            var weapon = Nav.FindReferences(new ReferencesRequest { Symbol = Nav.Navigate(new NavigationRequest { Caret = _f.Caret("Game/Weapon.h", "AWeapon*/AWeapon : ") }, CancellationToken.None).Resolved }, CancellationToken.None);
            var where = weapon.Hits.Select(Where).ToList();
            // declaration context (indexed for project files): field type, parameter type, base clause
            Assert.Contains(Line("Game/MyCharacter.h", "TObjectPtr<AWeapon>"), where);
            Assert.Contains(Line("Game/MyCharacter.h", "Equip(AWeapon* Weapon)"), where);
            Assert.Contains(Line("Game/Weapon.h", "public /*@caret:AWeapon*/AWeapon"), where);
            Assert.Contains(Line("Game/Weapon.cpp", "/*@caret:AWeapon*/AWeapon::"), where);

            var actorProject = Nav.FindReferences(new ReferencesRequest { Caret = _f.Caret("Game/Weapon.h", "AActor*/AActor") }, CancellationToken.None);
            Assert.DoesNotContain(actorProject.Hits, h => h.Origin != ItemOrigin.Project);
            var actorAll = Nav.FindReferences(new ReferencesRequest { Caret = _f.Caret("Game/Weapon.h", "AActor*/AActor"), IncludeNonProject = true }, CancellationToken.None);
            Assert.Contains(actorAll.Hits, h => h.Origin == ItemOrigin.Engine);
            Assert.True(actorAll.Hits.Count > actorProject.Hits.Count);

            var capped = Nav.FindReferences(new ReferencesRequest { Caret = _f.Caret("Game/Weapon.h", "AActor*/AActor"), IncludeNonProject = true, MaxResults = 2 }, CancellationToken.None);
            Assert.Equal(2, capped.Hits.Count);
            Assert.True(capped.Truncated);
        }

        [Fact]
        public void References_UseTheUnsavedBuffer_AndCanBeCancelled()
        {
            string path = _f.PathOf("Game/HealthComponent.cpp");
            string text = File.ReadAllText(path) + "\nvoid ExtraUse(UHealthComponent* C)\n{\n\tC->Heal(1.f);\n}\n";
            int line = text.Split('\n').Length - 3;
            var caret = _f.Caret("Game/HealthComponent.cpp", "Heal.decl:toggle*//*@def:UHealthComponent.Heal*/Heal");
            caret.Text = text;
            caret.TextVersion = 42;
            var resp = Nav.FindReferences(new ReferencesRequest { Caret = caret }, CancellationToken.None);
            Assert.Contains(resp.Hits, h => h.Location.FilePath == path && h.Location.Line == line && h.Kind.HasFlag(ReferenceKind.Call));

            using var cts = new CancellationTokenSource();
            cts.Cancel();
            Assert.ThrowsAny<OperationCanceledException>(() => Nav.FindReferences(new ReferencesRequest { Caret = _f.Caret("Game/Weapon.h", "AActor*/AActor") }, cts.Token));
        }

        [Fact]
        public void References_CommonSymbolUnder300ms()
        {
            var req = new ReferencesRequest { Caret = _f.Caret("Game/HealthComponent.h", "Health = 100.f") };
            Nav.FindReferences(req, CancellationToken.None); // warm caches
            var sw = Stopwatch.StartNew();
            var resp = Nav.FindReferences(req, CancellationToken.None);
            sw.Stop();
            _out.WriteLine($"references: {resp.Hits.Count} hits, {resp.FilesScanned} files, {sw.Elapsed.TotalMilliseconds:F1} ms");
            Assert.True(sw.Elapsed.TotalMilliseconds < 300, sw.Elapsed.TotalMilliseconds + " ms");
        }

        // ================================================================== type hierarchy

        [Fact]
        public void Hierarchy_BasesAndDerived()
        {
            var h = Nav.GetTypeHierarchy(new TypeHierarchyRequest { TypeName = "AWeapon" }, CancellationToken.None);
            Assert.Equal("AWeapon", h.Root.Name);
            Assert.Equal(new[] { "AActor", "IInteractable" }, h.Bases.Select(b => b.DisplayName));
            var actor = h.Bases[0];
            Assert.Equal(ItemOrigin.Engine, actor.Symbol.Origin);
            Assert.Equal("UObject", Assert.Single(actor.Children).DisplayName);
            Assert.Equal(new[] { "ARifle", "AShotgun" }, h.Derived.Select(d => d.Symbol.Name));
            Assert.True(h.NodeCount >= 5);

            var fromActor = Nav.GetTypeHierarchy(new TypeHierarchyRequest { TypeName = "AActor", Direction = HierarchyDirection.Derived }, CancellationToken.None);
            Assert.Empty(fromActor.Bases);
            var pawn = fromActor.Derived.Single(d => d.Symbol.Name == "APawn");
            var character = Assert.Single(pawn.Children);
            Assert.Equal("ACharacter", character.Symbol.Name);
            Assert.Equal("AMyCharacter", Assert.Single(character.Children).Symbol.Name);
            Assert.Contains(fromActor.Derived, d => d.Symbol.Name == "AWeapon" && d.Children.Count == 2);
        }

        [Fact]
        public void Hierarchy_UnresolvedBases_Limits_AndCaret()
        {
            var ui = Nav.GetTypeHierarchy(new TypeHierarchyRequest { TypeName = "UInteractable", Direction = HierarchyDirection.Bases }, CancellationToken.None);
            var unknown = Assert.Single(ui.Bases);
            Assert.Null(unknown.Symbol);
            Assert.Equal("UInterface", unknown.DisplayName);

            var shallow = Nav.GetTypeHierarchy(new TypeHierarchyRequest { TypeName = "AShotgun", MaxDepth = 1 }, CancellationToken.None);
            var weapon = Assert.Single(shallow.Bases);
            Assert.Empty(weapon.Children);
            Assert.True(weapon.Truncated);

            var few = Nav.GetTypeHierarchy(new TypeHierarchyRequest { TypeName = "AActor", MaxNodes = 2 }, CancellationToken.None);
            Assert.True(few.NodeCount <= 2);

            // caret on a member → its class
            var byCaret = Nav.GetTypeHierarchy(new TypeHierarchyRequest { Caret = _f.Caret("Game/Weapon.cpp", "Pellets*/Pellets") }, CancellationToken.None);
            Assert.Equal("AShotgun", byCaret.Root.Name);
            Assert.Equal("AWeapon", Assert.Single(byCaret.Bases).Symbol.Name);
        }

        [Fact]
        public void Hierarchy_SurvivesCycles()
        {
            using var t = new MiniProject(("Cycle.h", "class FA : public FB {};\nclass FB : public FA {};\nclass FC : public FB {};\n"));
            var h = t.Nav.GetTypeHierarchy(new TypeHierarchyRequest { TypeName = "FA" }, CancellationToken.None);
            Assert.Equal("FB", Assert.Single(h.Bases).DisplayName);
            Assert.True(h.NodeCount < 20);
            Assert.Contains(h.Derived, d => d.Symbol.Name == "FB");
        }

        // ================================================================== highlights

        [Fact]
        public void Highlights_CurrentDocumentOnly_WithoutIndex()
        {
            var nav = new NavigationService(new ThrowingBackend(), null);
            var caret = _f.Caret("Game/HealthComponent.cpp", "Health = FMath");
            var resp = nav.GetHighlights(caret, CancellationToken.None);
            Assert.Equal("Health", resp.Word);
            string text = File.ReadAllText(_f.PathOf("Game/HealthComponent.cpp"));
            string code = System.Text.RegularExpressions.Regex.Replace(text, @"/\*.*?\*/|//[^\n]*|""[^""\n]*""", " ");
            int expected = System.Text.RegularExpressions.Regex.Matches(code, @"\bHealth\b").Count;
            Assert.Equal(expected, resp.Spans.Count);
            Assert.Contains(resp.Spans, s => s.Kind.HasFlag(ReferenceKind.Write));

            var local = nav.GetHighlights(_f.Caret("Game/HealthComponent.cpp", "local.Old*/Old)"), CancellationToken.None);
            Assert.Equal(2, local.Spans.Count);
            Assert.Equal(ReferenceKind.Declaration, local.Spans.OrderBy(s => s.Line).First().Kind);

            // the same snapshot is answered from the cache
            var again = nav.GetHighlights(caret, CancellationToken.None);
            Assert.Same(resp.Spans, again.Spans);
        }

        [Fact]
        public void Highlights_P95Under20ms_OnALargeBuffer()
        {
            var sb = new StringBuilder("#include \"Big.h\"\n");
            for (int i = 0; i < 400; i++)
                sb.Append($"void ABig::Func{i}(float Value)\n{{\n\tconst float Local{i} = Value * Health;\n\tHealth = Local{i} + Other->Health;\n\tif (Local{i} > MaxHealth) {{ Apply(Local{i}); }}\n}}\n\n");
            string text = sb.ToString();
            var nav = new NavigationService(new ThrowingBackend(), null);
            var lines = text.Split('\n');
            var candidates = Enumerable.Range(0, lines.Length).Where(l => lines[l].Contains("Local")).ToList();
            var times = new List<double>();
            for (int i = 0; i < 200; i++)
            {
                int line = candidates[(i * 7) % candidates.Count];
                int col = lines[line].IndexOf(i % 2 == 0 ? "Health" : "Local", StringComparison.Ordinal);
                if (col < 0) col = lines[line].IndexOf("Local", StringComparison.Ordinal);
                var sw = Stopwatch.StartNew();
                nav.GetHighlights(new CaretRequest { FilePath = "/virtual/Big.cpp", Text = text, TextVersion = 7, Line = line, Column = col + 1 }, CancellationToken.None);
                times.Add(sw.Elapsed.TotalMilliseconds);
            }
            times.Sort();
            double p95 = times[(int)(times.Count * 0.95)];
            _out.WriteLine($"highlights p50={times[times.Count / 2]:F2} p95={p95:F2} max={times.Last():F2} ms (lines={lines.Length})");
            Assert.True(p95 < 20, "p95 " + p95);
        }

        // ================================================================== members / caret context

        [Fact]
        public void Members_IncludeInheritedAndKeepMostDerivedOverride()
        {
            var resp = Nav.GetMembers(new MembersRequest { TypeName = "AShotgun" }, CancellationToken.None);
            Assert.Equal("AShotgun", resp.Type.Name);
            var fire = Assert.Single(resp.Members, m => m.Name == "Fire");
            Assert.Equal("AShotgun", fire.Container);
            Assert.Contains(resp.Members, m => m.Name == "Pellets" && m.Container == "AShotgun");
            Assert.Contains(resp.Members, m => m.Name == "Ammo" && m.Container == "AWeapon");
            Assert.Equal("AWeapon", Assert.Single(resp.Members, m => m.Name == "Interact").Container);
            Assert.Contains(resp.Members, m => m.Name == "CanInteract" && m.Container == "IInteractable");
            Assert.Contains(resp.Members, m => m.Name == "GetActorLocation" && m.Container == "AActor");
            Assert.Contains(resp.Members, m => m.Name == "GetName" && m.Container == "UObject");
            // GENERATED_BODY members (StaticClass, Super...) are included once, from the most derived class
            Assert.Equal("AShotgun", Assert.Single(resp.Members, m => m.Name == "StaticClass").Container);

            var own = Nav.GetMembers(new MembersRequest { TypeName = "AShotgun", IncludeInherited = false }, CancellationToken.None);
            Assert.All(own.Members, m => Assert.Equal("AShotgun", m.Container));
            Assert.Contains(own.Members, m => m.Name == "Pellets");

            var capped = Nav.GetMembers(new MembersRequest { TypeName = "AShotgun", MaxResults = 3 }, CancellationToken.None);
            Assert.Equal(3, capped.Members.Count);
            Assert.True(capped.Truncated);

            var ns = Nav.GetMembers(new MembersRequest { TypeName = "FSpread", Container = "Game::Weapons" }, CancellationToken.None);
            Assert.Equal(new[] { "Angle", "Compute" }, ns.Members.Select(m => m.Name));
            Assert.Empty(Nav.GetMembers(new MembersRequest { TypeName = "NoSuchType" }, CancellationToken.None).Members);
        }

        [Fact]
        public void CaretContext_ScopesOuterToInner_AndFileSymbolsInOrder()
        {
            var ctx = Nav.GetCaretContext(_f.Caret("Game/WeaponTypes.cpp", "Angle*/Angle * Shots"), CancellationToken.None);
            Assert.Equal(new[] { "Game", "Weapons", "FSpread", "Compute" }, ctx.Scopes.Select(s => s.Name));
            Assert.Equal(SymbolKind.Namespace, ctx.Scopes[0].Kind);
            Assert.Equal(SymbolKind.Struct, ctx.Scopes[2].Kind);
            Assert.Equal("WeaponTypes.h", Path.GetFileName(ctx.Scopes[2].Location.FilePath));
            Assert.Equal(SymbolKind.Method, ctx.Scopes[3].Kind);

            var lines = ctx.FileSymbols.Select(s => s.Location.Line).ToList();
            Assert.Equal(lines.OrderBy(x => x), lines);
            Assert.Contains(ctx.FileSymbols, s => s.Name == "UseGlobalClamp");

            var inClass = Nav.GetCaretContext(_f.Caret("Game/HealthComponent.h", "Health <= 0.f"), CancellationToken.None);
            Assert.Equal(new[] { "UHealthComponent", "IsDead" }, inClass.Scopes.Select(s => s.Name));
            Assert.DoesNotContain(inClass.FileSymbols, s => s.Name == "StaticClass"); // implicit members are not file symbols

            var outside = Nav.GetCaretContext(new CaretRequest { FilePath = _f.PathOf("Game/HealthComponent.h"), Line = 0, Column = 0 }, CancellationToken.None);
            Assert.Empty(outside.Scopes);
        }

        // ================================================================== navigation details

        [Fact]
        public void Navigate_UsesUnsavedBuffer_AndCachesByVersion()
        {
            string path = _f.PathOf("Game/Weapon.cpp");
            string text = File.ReadAllText(path) + "\nstatic int BufferOnlyHelper() { return 1; }\nvoid AWeapon::Reload()\n{\n\tAmmo = BufferOnlyHelper();\n}\n";
            var lines = text.Split('\n');
            int line = Array.FindLastIndex(lines, l => l.Contains("Ammo = BufferOnlyHelper"));
            int col = lines[line].IndexOf("BufferOnlyHelper", StringComparison.Ordinal) + 2;
            var req = new NavigationRequest { Caret = new CaretRequest { FilePath = path, Text = text, TextVersion = 1001, Line = line, Column = col } };
            long missesBefore = Nav.Documents.Misses;
            var resp = Nav.Navigate(req, CancellationToken.None);
            var target = Assert.Single(resp.Targets);
            Assert.Equal(Array.FindIndex(lines, l => l.Contains("static int BufferOnlyHelper")), target.Symbol.Location.Line);
            Assert.Equal(ResolutionConfidence.Exact, resp.Confidence);
            Assert.Equal("global", resp.Reason);
            Assert.True(resp.ElapsedMs >= 0);

            Nav.Navigate(req, CancellationToken.None);
            Assert.Equal(missesBefore + 1, Nav.Documents.Misses); // second request reused the parse of version 1001
        }

        [Fact]
        public void Navigate_ReportsReasonsAndConfidence()
        {
            var macro = Nav.Navigate(new NavigationRequest { Caret = _f.Caret("Game/MyCharacter.h", "GENERATED_BODY*/GENERATED_BODY") }, CancellationToken.None);
            Assert.Equal("ue-macro", macro.Reason);
            // UE reflection macros go to their #define (ObjectMacros.h); no UHT output in the corpus, so no .generated.h target
            var def = Assert.Single(macro.Targets);
            Assert.Equal("ObjectMacros.h", Path.GetFileName(def.Symbol.Location.FilePath));
            Assert.Equal(SymbolKind.Macro, def.Symbol.Kind);

            var decl = Nav.Navigate(new NavigationRequest { Kind = NavigationKind.Declaration, Caret = _f.Caret("Game/Weapon.h", "AWeapon.Data*/Data") }, CancellationToken.None);
            Assert.Equal("declaration", decl.Reason);
            Assert.Equal(ResolutionConfidence.Exact, decl.Confidence);

            var super = Nav.Navigate(new NavigationRequest { Caret = _f.Caret("Game/MyCharacter.cpp", "AActor.BeginPlay*/BeginPlay") }, CancellationToken.None);
            Assert.Equal("super", super.Reason);

            var unknownMember = Nav.Navigate(new NavigationRequest { Caret = _f.Caret("Game/MyCharacter.cpp", "Direction.IsNearlyZero", 0, 11) }, CancellationToken.None);
            Assert.Equal("IsNearlyZero", unknownMember.Word);
            Assert.Equal(ResolutionConfidence.None, unknownMember.Confidence);

            var whitespace = Nav.Navigate(new NavigationRequest { Caret = new CaretRequest { FilePath = _f.PathOf("Game/Weapon.cpp"), Line = 1, Column = 30 } }, CancellationToken.None);
            Assert.Equal(string.Empty, whitespace.Word);
            Assert.Empty(whitespace.Targets);

            var include = Nav.Navigate(new NavigationRequest { Caret = _f.Caret("Game/Weapon.cpp", "#include \"MyCharacter.h\"", 0, 12) }, CancellationToken.None);
            Assert.Equal("include", include.Reason);
            Assert.Equal(SymbolKind.Unknown, include.Resolved.Kind);
            Assert.Equal("MyCharacter.h", include.Resolved.Name);
        }

        // ================================================================== helpers

        [Fact]
        public void TypeText_Helpers()
        {
            Assert.Equal("UFoo", TypeText.CoreType("const TObjectPtr<UFoo>&"));
            Assert.Equal("AActor", TypeText.CoreType("class AActor*"));
            Assert.Equal("TArray", TypeText.CoreType("TArray<int32>"));
            Assert.Equal("UFoo", TypeText.CoreType("TWeakObjectPtr<const UFoo>"));
            Assert.Null(TypeText.CoreType("auto"));
            Assert.Null(TypeText.CoreType("const float"));
            Assert.Equal("AWeapon*", TypeText.ElementType("const TArray<AWeapon*>&"));
            Assert.Equal("TObjectPtr<UHealth>", TypeText.TypeFromSignature("TObjectPtr<UHealth> Health", "Health"));
            Assert.Equal("constFVector&|int32", TypeText.ParameterKey("void A::F(const FVector& Dir, int32 N = 3)"));
            Assert.Equal("", TypeText.ParameterKey("void F(void)"));
            Assert.Equal(2, TypeText.ParameterCount("void F(TMap<int, float> M, float)"));
            Assert.True(TypeText.HasSpecifier("Server, Reliable, WithValidation", "WithValidation"));
            Assert.False(TypeText.HasSpecifier("ServerOnly", "Server"));
            Assert.True(TypeText.ContainerMatches("Game::Weapons::FSpread", "FSpread"));
            Assert.False(TypeText.ContainerMatches("Game::XFSpread", "FSpread"));
            var (text, col) = NavigationService.DisplayLine("\t\tFoo(Bar);", 6);
            Assert.Equal("Foo(Bar);", text);
            Assert.Equal(4, col);
        }

        [Fact]
        public void SyntaxFileParser_IndexesBasesAndDeclarationContextUses_ForProjectFilesOnly()
        {
            const string text = "UCLASS()\nclass MYGAME_API AFoo : public AActor, public TSharedFromThis<AFoo>\n{\n\tGENERATED_BODY()\n" +
                                "\tUPROPERTY(EditAnywhere)\n\tTObjectPtr<UBar> Bar;\n\tvoid Use(const FVector& V);\n\tvoid Inline() { Bar->Go(); }\n};\n";
            var parser = new SyntaxFileParser();
            Index.FileSymbols Parse(ItemOrigin origin) => (Index.FileSymbols)parser.Parse(new FileContext { Path = "/p/Foo.h", Origin = origin, Kind = Unreal.SourceFileKind.Header },
                Encoding.UTF8.GetBytes(text), CancellationToken.None).Payload;

            var project = Parse(ItemOrigin.Project);
            Assert.Equal(new[] { "AActor", "TSharedFromThis<AFoo>" }, project.Decls.Single(d => d.Name == "AFoo").Bases);
            var names = project.Occurrences.Select(o => o.Name).ToList();
            Assert.Contains("UBar", names);
            Assert.Contains("FVector", names);
            Assert.Contains("AActor", names);
            Assert.Single(project.Occurrences, o => o.Name == "Go"); // body occurrences are not duplicated
            Assert.DoesNotContain("EditAnywhere", names);             // reflection macro arguments are skipped
            Assert.DoesNotContain("MYGAME_API", names);
            Assert.DoesNotContain(project.Occurrences, o => o.Name == "AFoo" && o.Offset == text.IndexOf("AFoo :", StringComparison.Ordinal)); // the declared name

            var engine = Parse(ItemOrigin.Engine);
            Assert.DoesNotContain("UBar", engine.Occurrences.Select(o => o.Name));
            Assert.Equal(project.Decls.Single(d => d.Name == "AFoo").Bases, engine.Decls.Single(d => d.Name == "AFoo").Bases);
        }

        [Fact]
        public void DocumentCache_IsBounded()
        {
            var cache = new DocumentCache(maxEntries: 4);
            for (int i = 0; i < 10; i++) cache.Get("/v/F" + i + ".h", "int X" + i + ";", 1);
            Assert.Equal(4, cache.Count);
            var a = cache.Get("/v/F9.h", "int X9;", 1);
            Assert.Same(a, cache.Get("/v/F9.h", "int X9;", 1));
            Assert.NotSame(a, cache.Get("/v/F9.h", "int X9; int Y;", 2));
        }

        // ================================================================== fakes

        /// <summary>A small project on disk indexed with the real parser.</summary>
        private sealed class MiniProject : IDisposable
        {
            private readonly string _root = Path.Combine(Path.GetTempPath(), "uetovs-nav-mini", Guid.NewGuid().ToString("N"));
            private readonly SegmentedIndexBackend _backend = new SegmentedIndexBackend();

            public MiniProject(params (string Name, string Text)[] files)
            {
                Directory.CreateDirectory(_root);
                _backend.TryLoad(Path.Combine(_root, ".index"), "mini");
                var table = new FileTable();
                var parser = new SyntaxFileParser();
                foreach (var (name, text) in files)
                {
                    string p = Path.Combine(_root, name);
                    File.WriteAllText(p, text);
                    var listed = new Unreal.ListedFile { Path = p, Name = name, Module = "Mini", Origin = ItemOrigin.Project, Kind = Unreal.SourceFileKind.Header };
                    table.Upsert(listed);
                    _backend.UpdateParsed(p, parser.Parse(new FileContext { Path = p, Module = "Mini", Origin = ItemOrigin.Project, Kind = listed.Kind },
                        Encoding.UTF8.GetBytes(text), CancellationToken.None));
                }
                Nav = new NavigationService(_backend, table);
            }

            public NavigationService Nav { get; }

            public void Dispose()
            {
                _backend.Dispose();
                try { Directory.Delete(_root, true); } catch (IOException) { }
            }
        }

        /// <summary>Highlights must not touch the index.</summary>
        private sealed class ThrowingBackend : ISymbolIndexBackend
        {
            private static Exception No() => new InvalidOperationException("index access");
            public string Name => "throwing";
            public string FormatVersion => "0";
            public SymbolSearchResult Search(SymbolQuery query, CancellationToken ct) => throw No();
            public IReadOnlyList<SymbolInfo> GetFileSymbols(string filePath) => throw No();
            public IReadOnlyList<SymbolInfo> FindSymbols(string name, string container) => throw No();
            public void UpdateFile(string filePath, IReadOnlyList<SymbolInfo> symbols) => throw No();
            public void RemoveFile(string filePath) => throw No();
            public bool ContainsFile(string filePath) => throw No();
            public IndexStats GetStats() => throw No();
            public IReadOnlyList<MemoryPart> GetMemoryParts() => throw No();
            public bool TryLoad(string indexDir, string key) => throw No();
            public void Save(string indexDir, string key) => throw No();
            public void Dispose() { }
        }
    }
}
