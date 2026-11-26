using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using UEToVS.Contracts;
using UEToVS.Engine.Includes;
using UEToVS.Engine.Indexing;
using UEToVS.Engine.Unreal;
using Xunit;

namespace UEToVS.Engine.Tests
{
    /// <summary>
    /// A small engine + project tree with Build.cs files for the v0.11.0 include model:
    /// <code>
    /// UE/Engine/Source/Runtime/Core        Public/CoreMinimal.h, Public/Containers/Array.h, Private/CorePrivate.h
    /// UE/Engine/Source/Runtime/Engine      depends on Core; Classes/GameFramework/Actor.h, Public/EngineUtils.h, Private/EnginePrivate.h
    /// UE/Engine/Source/Runtime/Legacy      Public/Sub/LegacyThing.h (legacy public include paths left at the default)
    /// UE/Engine/Source/Runtime/Modern      bLegacyPublicIncludePaths = false; Public/Sub/ModernThing.h
    /// Game/Source/MyGame                   depends on Core, Engine, Legacy, Modern; MyGame.h, Public/Chars/Hero.h, Private/Hero.cpp,
    ///                                      PrivateIncludePaths.Add(Path.Combine(ModuleDirectory, "Extra")) → Extra/ExtraThing.h
    /// Game/Source/Other                    not a dependency: Public/OtherThing.h, Private/OtherPrivate.h
    /// Game/Source/Opaque                   PublicIncludePaths.Add(SomeHelper()) (cannot be evaluated)
    /// </code>
    /// </summary>
    public sealed class IncludeTree : IDisposable
    {
        public IncludeTree()
        {
            Root = Path.Combine(Path.GetTempPath(), "uetovs-includes", Guid.NewGuid().ToString("N"));
            Write("UE/Engine/Source/Runtime/Core/Core.Build.cs", Rules("Core"));
            Write("UE/Engine/Source/Runtime/Core/Public/CoreMinimal.h", "#pragma once\n#include \"Containers/Array.h\"\n");
            Write("UE/Engine/Source/Runtime/Core/Public/Containers/Array.h", "#pragma once\ntemplate<typename T> class TArray {};\n");
            Write("UE/Engine/Source/Runtime/Core/Private/CorePrivate.h", "#pragma once\n");
            Write("UE/Engine/Source/Runtime/Engine/Engine.Build.cs", Rules("Engine", "PublicDependencyModuleNames.AddRange(new string[] { \"Core\" });"));
            Write("UE/Engine/Source/Runtime/Engine/Classes/GameFramework/Actor.h", "#pragma once\n#include \"CoreMinimal.h\"\nclass AActor {};\n");
            Write("UE/Engine/Source/Runtime/Engine/Public/EngineUtils.h", "#pragma once\n");
            Write("UE/Engine/Source/Runtime/Engine/Private/EnginePrivate.h", "#pragma once\n");
            Write("UE/Engine/Source/Runtime/Legacy/Legacy.Build.cs", Rules("Legacy"));
            Write("UE/Engine/Source/Runtime/Legacy/Public/Sub/LegacyThing.h", "#pragma once\n");
            Write("UE/Engine/Source/Runtime/Modern/Modern.Build.cs", Rules("Modern", "bLegacyPublicIncludePaths = false;"));
            Write("UE/Engine/Source/Runtime/Modern/Public/Sub/ModernThing.h", "#pragma once\n");
            Write("Game/Source/MyGame/MyGame.Build.cs", Rules("MyGame",
                "PublicDependencyModuleNames.AddRange(new string[] { \"Core\", \"Engine\" });\n" +
                "PrivateDependencyModuleNames.Add(\"Legacy\");\nPrivateDependencyModuleNames.Add(\"Modern\");\n" +
                "PrivateIncludePaths.Add(Path.Combine(ModuleDirectory, \"Extra\"));\nbLegacyPublicIncludePaths = false;"));
            Write("Game/Source/MyGame/MyGame.h", "#pragma once\n");
            Write("Game/Source/MyGame/Public/Chars/Hero.h", "#pragma once\n#include \"GameFramework/Actor.h\"\nclass AHero : public AActor {};\n");
            Write("Game/Source/MyGame/Private/Hero.cpp", "#include \"Chars/Hero.h\"\n");
            Write("Game/Source/MyGame/Private/Sub/Local.h", "#pragma once\n");
            Write("Game/Source/MyGame/Extra/ExtraThing.h", "#pragma once\n");
            Write("Game/Source/Other/Other.Build.cs", Rules("Other"));
            Write("Game/Source/Other/Public/OtherThing.h", "#pragma once\n");
            Write("Game/Source/Other/Private/OtherPrivate.h", "#pragma once\n");
            Write("Game/Source/Opaque/Opaque.Build.cs", Rules("Opaque", "PublicDependencyModuleNames.Add(\"Core\");\nPublicIncludePaths.Add(SomeHelper());"));
            Write("Game/Source/Opaque/Private/Opaque.cpp", "#include \"CoreMinimal.h\"\n");

            Workspace = new Workspace { EngineRoot = Path.Combine(Root, "UE"), ProjectRoot = Path.Combine(Root, "Game") };
            var found = new List<FoundBuildCs>();
            foreach (var path in Directory.GetFiles(Root, "*.*", SearchOption.AllDirectories).OrderBy(p => p, StringComparer.Ordinal))
            {
                bool engine = path.StartsWith(Path.Combine(Root, "UE"), StringComparison.Ordinal);
                var origin = engine ? ItemOrigin.Engine : ItemOrigin.Project;
                if (path.EndsWith(".Build.cs", StringComparison.Ordinal))
                {
                    found.Add(new FoundBuildCs { Path = path, Module = Path.GetFileName(path).Replace(".Build.cs", string.Empty), Origin = origin });
                    continue;
                }
                Files.Upsert(new ListedFile
                {
                    Path = path,
                    Name = Path.GetFileName(path),
                    Origin = origin,
                    Kind = path.EndsWith(".h", StringComparison.Ordinal) ? SourceFileKind.Header : SourceFileKind.Source,
                });
            }
            Modules = ModuleCatalog.Build(Workspace, found);
            Model = new IncludeModel(Files, () => Modules, () => Workspace) { ResponseFileLines = m => Responses.TryGetValue(m.Name, out var l) ? l : null };
            Files.Includes = Model;
        }

        public string Root { get; }
        public Workspace Workspace { get; }
        public FileTable Files { get; } = new FileTable();
        public ModuleCatalog Modules { get; }
        public IncludeModel Model { get; }
        public Dictionary<string, IReadOnlyList<string>> Responses { get; } = new Dictionary<string, IReadOnlyList<string>>();

        public string P(string relative) => Path.Combine(Root, relative.Replace('/', Path.DirectorySeparatorChar));

        private void Write(string relative, string text)
        {
            string p = P(relative);
            Directory.CreateDirectory(Path.GetDirectoryName(p));
            File.WriteAllText(p, text);
        }

        private static string Rules(string name, string body = "") =>
            "using System.IO;\nusing UnrealBuildTool;\npublic class " + name + " : ModuleRules\n{\n\tpublic " + name + "(ReadOnlyTargetRules Target) : base(Target)\n\t{\n" + body + "\n\t}\n}\n";

        public void Dispose()
        {
            try { Directory.Delete(Root, true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }
    }

    public sealed class IncludeModelTests : IClassFixture<IncludeTree>
    {
        private readonly IncludeTree _t;

        public IncludeModelTests(IncludeTree t) => _t = t;

        private IncludeResolution R(string include, string from, bool angled = false) => _t.Model.Resolve(include, angled, _t.P(from));

        private static string N(string p) => p?.Replace('\\', '/');

        [Fact]
        public void Resolves_own_public_dependency_and_relative_includes()
        {
            var r = R("Chars/Hero.h", "Game/Source/MyGame/Private/Hero.cpp");
            Assert.Equal(IncludeStatus.Found, r.Status);
            Assert.EndsWith("MyGame/Public/Chars/Hero.h", N(r.Path));
            Assert.Equal("root", r.Reason);

            r = R("GameFramework/Actor.h", "Game/Source/MyGame/Public/Chars/Hero.h");
            Assert.Equal(IncludeStatus.Found, r.Status);
            Assert.EndsWith("Engine/Classes/GameFramework/Actor.h", N(r.Path));

            // transitive public dependency: MyGame → Engine → Core
            Assert.Equal(IncludeStatus.Found, R("Containers/Array.h", "Game/Source/MyGame/Private/Hero.cpp").Status);
            // module folder itself
            Assert.Equal(IncludeStatus.Found, R("MyGame.h", "Game/Source/MyGame/Private/Hero.cpp").Status);
            // next to the file
            r = R("Local.h", "Game/Source/MyGame/Private/Sub/X.cpp");
            Assert.Equal("relative", r.Reason);
            // Build.cs PrivateIncludePaths.Add(Path.Combine(ModuleDirectory, "Extra"))
            r = R("ExtraThing.h", "Game/Source/MyGame/Private/Hero.cpp");
            Assert.Equal(IncludeStatus.Found, r.Status);
            Assert.Equal("build-cs", r.Reason);
        }

        [Fact]
        public void Legacy_public_include_paths_follow_the_module_switch()
        {
            // Legacy leaves bLegacyPublicIncludePaths at the default: "LegacyThing.h" (inside Public/Sub) may resolve
            var r = R("LegacyThing.h", "Game/Source/MyGame/Private/Hero.cpp");
            Assert.Equal(IncludeStatus.Found, r.Status);
            Assert.Equal("legacy-public", r.Reason);
            // Modern turns it off: only "Sub/ModernThing.h" works
            Assert.Equal(IncludeStatus.NotOnIncludePath, R("ModernThing.h", "Game/Source/MyGame/Private/Hero.cpp").Status);
            Assert.Equal(IncludeStatus.Found, R("Sub/ModernThing.h", "Game/Source/MyGame/Private/Hero.cpp").Status);
        }

        [Fact]
        public void Not_found_and_not_on_include_path_are_told_apart()
        {
            var r = R("Chars/Villain.h", "Game/Source/MyGame/Private/Hero.cpp");
            Assert.Equal(IncludeStatus.NotFound, r.Status);
            Assert.True(r.Certain);

            // a header of a module that is not a dependency
            r = R("OtherThing.h", "Game/Source/MyGame/Private/Hero.cpp");
            Assert.Equal(IncludeStatus.NotOnIncludePath, r.Status);
            Assert.Equal("Other", r.CandidateModule);
            Assert.False(r.CandidatesPrivate);

            // the Private folder of another module (a dependency, even): never on the include path
            r = R("EnginePrivate.h", "Game/Source/MyGame/Private/Hero.cpp");
            Assert.Equal(IncludeStatus.NotOnIncludePath, r.Status);
            Assert.True(r.CandidatesPrivate);
            Assert.True(r.Certain);

            // angled includes are never certain (system headers are not indexed)
            Assert.False(R("vector", "Game/Source/MyGame/Private/Hero.cpp", angled: true).Certain);
            // generated headers are not this model's business
            Assert.Equal(IncludeStatus.Unknown, R("Hero.generated.h", "Game/Source/MyGame/Public/Chars/Hero.h").Status);
        }

        [Fact]
        public void Opaque_build_cs_include_paths_make_negative_answers_uncertain()
        {
            var r = R("Missing/Nothing.h", "Game/Source/Opaque/Private/Opaque.cpp");
            Assert.Equal(IncludeStatus.NotFound, r.Status);
            Assert.False(r.Certain);
            Assert.True(_t.Model.View(_t.Modules.Get("Opaque")).OpaquePaths);
        }

        [Fact]
        public void Response_file_roots_are_exact()
        {
            var lines = new[] { "/I \"" + _t.P("Game/Source/Other/Private") + "\"", "/D WITH_EDITOR=1", "/I\"" + _t.P("UE/Engine/Source/Runtime/Core/Public") + "\"" };
            Assert.Equal(2, IncludeModel.ParseResponseIncludes(lines, null).Count);
            Assert.Single(IncludeModel.ParseResponseIncludes(new[] { "/I \"Runtime/Core/Public\"" }, _t.P("UE/Engine/Source")));
            // a fresh model whose Opaque module has a response file listing Other/Private
            var model = new IncludeModel(_t.Files, () => _t.Modules, () => _t.Workspace)
            {
                ResponseFileLines = m => m.Name == "Opaque" ? lines : null,
            };
            var view = model.View(_t.Modules.Get("Opaque"));
            Assert.True(view.FromResponseFile);
            Assert.False(view.OpaquePaths);
            var r = model.Resolve("OtherPrivate.h", false, _t.P("Game/Source/Opaque/Private/Opaque.cpp"));
            Assert.Equal(IncludeStatus.Found, r.Status);
            Assert.Equal("rsp", r.Reason);
        }

        [Fact]
        public void Spelling_is_the_shortest_path_that_resolves_back()
        {
            string from = _t.P("Game/Source/MyGame/Private/Hero.cpp");
            Assert.Equal("GameFramework/Actor.h", _t.Model.Spelling(_t.P("UE/Engine/Source/Runtime/Engine/Classes/GameFramework/Actor.h"), from));
            Assert.Equal("Chars/Hero.h", _t.Model.Spelling(_t.P("Game/Source/MyGame/Public/Chars/Hero.h"), from));
            Assert.Equal("MyGame.h", _t.Model.Spelling(_t.P("Game/Source/MyGame/MyGame.h"), from));
            // not a dependency yet: spelled below that module's Public folder (right once the dependency is added)
            Assert.Equal("OtherThing.h", _t.Model.Spelling(_t.P("Game/Source/Other/Public/OtherThing.h"), from));
            // legacy-public roots are not used to spell new includes
            Assert.Equal("Sub/LegacyThing.h", _t.Model.Spelling(_t.P("UE/Engine/Source/Runtime/Legacy/Public/Sub/LegacyThing.h"), from));
            // next to the including file
            Assert.Equal("Local.h", _t.Model.Spelling(_t.P("Game/Source/MyGame/Private/Sub/Local.h"), _t.P("Game/Source/MyGame/Private/Sub/X.cpp")));
        }

        [Fact]
        public void Visibility_follows_build_cs_dependencies()
        {
            Assert.True(_t.Model.IsVisible("MyGame", "Core"));
            Assert.True(_t.Model.IsVisible("MyGame", "Engine"));
            Assert.False(_t.Model.IsVisible("MyGame", "Other"));
            Assert.True(_t.Model.IsVisible("Unknown", "Other")); // unknown module: no opinion
        }

        [Theory]
        [InlineData("Path.Combine(ModuleDirectory, \"A\", \"B\")", "Game/Source/MyGame/A/B")]
        [InlineData("ModuleDirectory + \"/Sub\"", "Game/Source/MyGame/Sub")]
        [InlineData("Path.GetFullPath(Path.Combine(ModuleDirectory, \"..\", \"Shared\"))", "Game/Source/Shared")]
        public void Build_cs_expressions_are_evaluated(string expression, string expected)
        {
            var dirs = _t.Model.Evaluate(expression, _t.Modules.Get("MyGame"));
            Assert.NotNull(dirs);
            Assert.EndsWith(expected, N(dirs[0]));
        }

        [Fact]
        public void Relative_literals_try_engine_source_and_module_folder()
        {
            var dirs = _t.Model.Evaluate("\"Runtime/Engine/Private\"", _t.Modules.Get("MyGame"));
            Assert.Equal(2, dirs.Count);
            Assert.EndsWith("UE/Engine/Source/Runtime/Engine/Private", N(dirs[0]));
            Assert.Null(_t.Model.Evaluate("SomeHelper()", _t.Modules.Get("MyGame")));
        }
    }
}
