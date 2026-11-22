using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using UEToVS.Host.Logic.Unreal;
using Xunit;

namespace UEToVS.Host.Logic.Tests
{
    public class ModuleGraphTests
    {
        private static ModuleNode N(string name, string[] pub, string[] priv = null, bool project = true) => new ModuleNode
        {
            Name = name, IsProject = project, Directory = Path.GetFullPath(Path.Combine("Src", name)), BuildCsPath = Path.GetFullPath(Path.Combine("Src", name, name + ".Build.cs")),
            Public = new HashSet<string>(pub, StringComparer.OrdinalIgnoreCase),
            Private = new HashSet<string>(priv ?? new string[0], StringComparer.OrdinalIgnoreCase),
        };

        [Fact]
        public void FindsCyclesAmongKnownModules()
        {
            var g = ModuleGraph.Build(new[]
            {
                N("Game", new[] { "Core", "UI" }), N("UI", new[] { "Core" }, new[] { "Net" }), N("Net", new[] { "Game" }),
                N("Tools", new[] { "Core" }), N("Core", new string[0], project: false),
            });
            Assert.Single(g.Cycles);
            Assert.Equal(new[] { "Game", "Net", "UI" }, g.Cycles[0]);
            Assert.True(g.InCycle("UI"));
            Assert.False(g.InCycle("Tools"));
            Assert.False(g.InCycle("Core"));
            Assert.True(g.Edges.Single(e => e.From == "UI" && e.To == "Net").InCycle);
            Assert.False(g.Edges.Single(e => e.From == "UI" && e.To == "Net").IsPublic);
            Assert.False(g.Edges.Single(e => e.From == "Game" && e.To == "Core").InCycle);
            Assert.Equal(new[] { "Net" }, g.Dependents("Game"));
            Assert.Single(ModuleDependencyAudit.CycleFindings(g));
        }

        [Fact]
        public void DuplicatesAndSelfReferencesAreIgnored()
        {
            var a = N("A", new[] { "B", "A" }, new[] { "B" });
            var g = ModuleGraph.Build(new[] { a, N("B", new string[0]), N("A", new[] { "X" }) });
            Assert.Single(g.Edges);
            Assert.True(g.Edges[0].IsPublic);
            Assert.Empty(g.Cycles);
        }

        [Fact]
        public void LayoutPutsDependentsLeftOfTheirDependencies()
        {
            var g = ModuleGraph.Build(new[] { N("Game", new[] { "UI", "Core" }), N("UI", new[] { "Core" }), N("Core", new string[0]) });
            var l = g.Layout(new[] { "Game", "UI", "Core" });
            Assert.Equal(0, l["Game"].Layer);
            Assert.Equal(1, l["UI"].Layer);
            Assert.Equal(2, l["Core"].Layer);
            // cycles do not loop forever and still get a place
            var c = ModuleGraph.Build(new[] { N("A", new[] { "B" }), N("B", new[] { "A" }) });
            Assert.Equal(2, c.Layout(new[] { "A", "B" }).Count);
        }

        [Fact]
        public void AuditFindsPrivateDependenciesInPublicHeadersAndUnusedOnes()
        {
            var game = N("Game", new[] { "Core", "Engine" }, new[] { "UMG", "Slate", "Json" });
            var umg = N("UMG", new string[0], project: false);
            var engine = N("Engine", new string[0], project: false);
            var slate = N("Slate", new string[0], project: false);
            var json = N("Json", new string[0], project: false);
            var nodes = new[] { game, umg, engine, slate, json }.ToDictionary(n => n.Name, StringComparer.OrdinalIgnoreCase);
            var exists = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            {
                Path.Combine(umg.Directory, "Public", "Components", "Widget.h"),
                Path.Combine(engine.Directory, "Classes", "GameFramework", "Actor.h"),
                Path.Combine(slate.Directory, "Public", "Widgets", "SBox.h"),
                Path.Combine(game.Directory, "Public", "MyActor.h"),
            };
            string header = Path.Combine(game.Directory, "Public", "MyActor.h");
            string cpp = Path.Combine(game.Directory, "Private", "MyActor.cpp");
            var files = new List<ModuleSourceFile>
            {
                new ModuleSourceFile { Path = header, Includes = { new IncludeRef("GameFramework/Actor.h", 2), new IncludeRef("Components/Widget.h", 3), new IncludeRef("MyActor.generated.h", 4) } },
                new ModuleSourceFile { Path = cpp, Includes = { new IncludeRef("MyActor.h", 0), new IncludeRef("Widgets/SBox.h", 1), new IncludeRef("Unknown/Thing.h", 2) } },
            };
            var findings = ModuleDependencyAudit.Run(game, files, n => nodes.TryGetValue(n, out var m) ? m : null, exists.Contains, CancellationToken.None);
            var priv = findings.Single(f => f.Kind == ModuleFindingKind.PublicHeaderUsesPrivateDependency);
            Assert.Equal("UMG", priv.Dependency);
            Assert.Equal(header, priv.File);
            Assert.Equal(3, priv.Line);
            var unused = findings.Where(f => f.Kind == ModuleFindingKind.UnusedDependency).Select(f => f.Dependency).ToList();
            Assert.Equal(new[] { "Json" }, unused); // Core is always used, Engine / UMG / Slate are included
            Assert.Contains("PrivateDependencyModuleNames", priv.Message(true));
            Assert.Contains("可能不准", findings.Single(f => f.Kind == ModuleFindingKind.UnusedDependency).Message(false));
        }

        [Fact]
        public void NothingResolvedMeansNoUnusedReports()
        {
            var game = N("Game", new[] { "Engine" });
            var engine = N("Engine", new string[0], project: false);
            var files = new List<ModuleSourceFile> { new ModuleSourceFile { Path = Path.Combine(game.Directory, "Private", "A.cpp"), Includes = { new IncludeRef("X.h", 0) } } };
            Assert.Empty(ModuleDependencyAudit.Run(game, files, n => n == "Engine" ? engine : null, _ => false, CancellationToken.None));
        }

        [Fact]
        public void NodeFromBuildCs()
        {
            const string text = "public class MyGame : ModuleRules { public MyGame(ReadOnlyTargetRules T) : base(T) {\n" +
                                "PublicDependencyModuleNames.AddRange(new string[] { \"Core\", \"Engine\" });\n" +
                                "PrivateDependencyModuleNames.Add(\"Slate\");\n" +
                                "// PrivateDependencyModuleNames.Add(\"Commented\");\n} }";
            var n = ModuleNode.FromBuildCs(Path.GetFullPath(Path.Combine("S", "MyGame", "MyGame.Build.cs")), text, true);
            Assert.Equal("MyGame", n.Name);
            Assert.Equal(new[] { "Core", "Engine", "Slate" }, n.Dependencies);
            Assert.True(ModuleDependencyAudit.IsPublicHeader(n, Path.Combine(n.Directory, "Public", "A.h")));
            Assert.False(ModuleDependencyAudit.IsPublicHeader(n, Path.Combine(n.Directory, "Private", "A.h")));
            Assert.False(ModuleDependencyAudit.IsPublicHeader(n, Path.Combine(n.Directory, "Public", "A.cpp")));
        }
    }
}
