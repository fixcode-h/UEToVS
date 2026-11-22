using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using UEToVS.Host.Logic.Shaders;
using Xunit;

namespace UEToVS.Host.Logic.Tests
{
    public class HlslParserTests
    {
        private const string Sample = @"// Copyright
#include ""/Engine/Private/Common.ush""
#include ""MyHelpers.ush""
#define MY_RADIUS 4
#define MULTI(a) \
    (a * 2)

struct FMyVertex
{
    float3 Position : POSITION;
    float2 UV, UV2;
    float Area() { return 1; }
};

cbuffer FMyParams
{
    float4 Tint;
};

Texture2D MyTexture;
SamplerState MySampler;
static const float MyScale = 2.0f;
typedef float4 FMyColor;

float3 Shade(FMyVertex V, float Amount)
{
    float3 Local = V.Position; // not a global
    return Local * Amount;
}

float4 ProtoOnly(float A);

[numthreads(8, 8, 1)]
void MainCS(uint3 Id : SV_DispatchThreadID)
{
}

float4 MainPS(float2 UV : TEXCOORD0) : SV_Target0
{
    /* struct NotThis { }; */
    string s = ""float NotAFunction() {"";
    return MyTexture.Sample(MySampler, UV);
}
";

        private static HlslDocument Doc => HlslParser.Parse(Sample);

        private static HlslSymbol Sym(string name) => Doc.Symbols.Single(s => s.Name == name);

        [Fact]
        public void FindsTopLevelDeclarations()
        {
            Assert.Equal(HlslSymbolKind.Struct, Sym("FMyVertex").Kind);
            Assert.Equal(HlslSymbolKind.CBuffer, Sym("FMyParams").Kind);
            Assert.Equal(HlslSymbolKind.Variable, Sym("MyTexture").Kind);
            Assert.Equal(HlslSymbolKind.Variable, Sym("MySampler").Kind);
            Assert.Equal(HlslSymbolKind.Variable, Sym("MyScale").Kind);
            Assert.Equal(HlslSymbolKind.Typedef, Sym("FMyColor").Kind);
            Assert.Equal(HlslSymbolKind.Function, Sym("Shade").Kind);
            Assert.Equal(HlslSymbolKind.Function, Sym("MainCS").Kind);
            Assert.Equal(HlslSymbolKind.Function, Sym("MainPS").Kind);
            Assert.Equal(HlslSymbolKind.Macro, Sym("MY_RADIUS").Kind);
            Assert.Equal(HlslSymbolKind.Macro, Sym("MULTI").Kind);
        }

        [Fact]
        public void StructMembersCarryTheirContainer()
        {
            Assert.Equal("FMyVertex", Sym("Position").Container);
            Assert.Equal("FMyVertex", Sym("UV2").Container);
            Assert.Equal(HlslSymbolKind.Function, Sym("Area").Kind);
            Assert.Equal("FMyVertex", Sym("Area").Container);
            Assert.Null(Sym("Tint").Container); // cbuffer members are global names in HLSL
            Assert.Equal(HlslSymbolKind.Variable, Sym("Tint").Kind);
        }

        [Fact]
        public void IgnoresLocalsCommentsStringsAndPrototypes()
        {
            var names = Doc.Symbols.Select(s => s.Name).ToList();
            Assert.DoesNotContain("Local", names);
            Assert.DoesNotContain("NotThis", names);
            Assert.DoesNotContain("NotAFunction", names);
            Assert.DoesNotContain("ProtoOnly", names);
            Assert.DoesNotContain("s", names);
        }

        [Fact]
        public void RecordsPositionsSignatureAndIncludes()
        {
            var shade = Sym("Shade");
            var lines = Sample.Split('\n');
            Assert.StartsWith("float3 Shade(", lines[shade.Line].Trim());
            Assert.Equal(lines[shade.Line].IndexOf("Shade", StringComparison.Ordinal), shade.Column);
            Assert.StartsWith("float3 Shade(FMyVertex V", shade.Signature);
            Assert.Equal(new[] { "/Engine/Private/Common.ush", "MyHelpers.ush" }, Doc.Includes.Select(i => i.Path));
            Assert.Equal(1, Doc.Includes[0].Line);
            // symbols are in file order
            Assert.Equal(Doc.Symbols.OrderBy(s => s.Line).ThenBy(s => s.Column).Select(s => s.Name), Doc.Symbols.Select(s => s.Name));
        }

        [Fact]
        public void EmptyAndHugeInputsAreSafe()
        {
            Assert.Empty(HlslParser.Parse(null).Symbols);
            Assert.Empty(HlslParser.Parse("float").Symbols);
            Assert.Empty(HlslParser.Parse("struct {").Symbols);
            Assert.NotNull(HlslParser.Parse("float4 F(float a"));
        }

        [Fact]
        public void WordAtAndMemberAccess()
        {
            Assert.Equal("Shade", HlslParser.WordAt("x = Shade(V);", 6, out int s));
            Assert.Equal(4, s);
            Assert.Equal("Shade", HlslParser.WordAt("x = Shade(V);", 9, out _)); // caret right after the word
            Assert.Null(HlslParser.WordAt("x = 12;", 5, out _));
            Assert.True(HlslParser.IsMemberAccess("V.Position", 2));
            Assert.False(HlslParser.IsMemberAccess("Position", 0));
        }
    }

    public class ShaderDefinitionTests
    {
        private sealed class MemFiles : IShaderFileSource
        {
            public readonly Dictionary<string, string> Files = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            public int Reads;
            public string Stamp(string path) => Files.TryGetValue(path, out var t) ? t.Length.ToString() : null;
            public string Read(string path)
            {
                Reads++;
                return Files.TryGetValue(path, out var t) ? t : null;
            }
        }

        private static readonly string Root = Path.GetFullPath("shaders");
        private static string P(string name) => Path.Combine(Root, name);

        private static MemFiles Files()
        {
            var fs = new MemFiles();
            fs.Files[P("Main.usf")] = "#include \"A.ush\"\n#include \"B.ush\"\nfloat Local() { return Helper(); }\n";
            fs.Files[P("A.ush")] = "#include \"C.ush\"\n#include \"A.ush\"\nfloat Helper() { return 1; }\n";
            fs.Files[P("B.ush")] = "float Helper() { return 2; }\n#define FROM_B 1\n";
            fs.Files[P("C.ush")] = "struct FDeep { float X; };\n";
            return fs;
        }

        private static string Resolve(string inc, string from) => Path.Combine(Path.GetDirectoryName(from), inc);

        [Fact]
        public void FollowsTheIncludeChainBreadthFirst()
        {
            var fs = Files();
            var cache = new ShaderDocumentCache(8);
            var hits = ShaderDefinitionFinder.Find("Helper", P("Main.usf"), null, cache, fs, Resolve, CancellationToken.None);
            Assert.Equal(new[] { P("A.ush"), P("B.ush") }, hits.Select(h => h.File));
            Assert.All(hits, h => Assert.Equal(1, h.Depth));
            var deep = ShaderDefinitionFinder.Find("FDeep", P("Main.usf"), null, cache, fs, Resolve, CancellationToken.None).Single();
            Assert.Equal(P("C.ush"), deep.File);
            Assert.Equal(2, deep.Depth);
            Assert.Empty(ShaderDefinitionFinder.Find("Nope", P("Main.usf"), null, cache, fs, Resolve, CancellationToken.None));
        }

        [Fact]
        public void CurrentFileWinsAndStopsTheWalk()
        {
            var fs = Files();
            var cache = new ShaderDocumentCache(8);
            var hit = ShaderDefinitionFinder.Find("Local", P("Main.usf"), null, cache, fs, Resolve, CancellationToken.None).Single();
            Assert.Equal(0, hit.Depth);
            Assert.Equal(1, cache.Count); // the includes were not parsed
        }

        [Fact]
        public void AllListsEachNameOnceNearestFirst()
        {
            var fs = Files();
            var all = ShaderDefinitionFinder.All(P("Main.usf"), null, new ShaderDocumentCache(8), fs, Resolve, CancellationToken.None);
            Assert.Equal(1, all.Count(d => d.Symbol.Name == "Helper"));
            Assert.Equal(P("A.ush"), all.Single(d => d.Symbol.Name == "Helper").File);
            Assert.Contains(all, d => d.Symbol.Name == "FROM_B");
            Assert.DoesNotContain(all, d => d.Symbol.Name == "X"); // struct members are not top-level names
        }

        [Fact]
        public void CacheReusesParsesUntilTheFileChangesAndEvictsTheOldest()
        {
            var fs = Files();
            var cache = new ShaderDocumentCache(2);
            cache.Get(P("A.ush"), fs);
            cache.Get(P("A.ush"), fs);
            Assert.Equal(1, cache.Parses);
            fs.Files[P("A.ush")] += "float More() { return 0; }\n";
            Assert.Contains(cache.Get(P("A.ush"), fs).Symbols, s => s.Name == "More");
            Assert.Equal(2, cache.Parses);
            cache.Get(P("B.ush"), fs);
            cache.Get(P("C.ush"), fs);
            Assert.Equal(2, cache.Count);
            Assert.Null(cache.Get(P("Missing.ush"), fs));
        }
    }

    public class ShaderIncludeCompletionTests
    {
        [Fact]
        public void AnalyzesTheTypedPath()
        {
            var c = ShaderIncludePaths.Analyze("#include \"/Engine/Private/Co\"", 28);
            Assert.NotNull(c);
            Assert.Equal("/Engine/Private/Co", c.Typed);
            Assert.Equal("/Engine/Private/", c.Directory);
            Assert.Equal(26, c.ReplaceStart);
            Assert.Equal(2, c.ReplaceLength);
            var empty = ShaderIncludePaths.Analyze("#include \"", 10);
            Assert.Equal(string.Empty, empty.Directory);
            Assert.Null(ShaderIncludePaths.Analyze("float x = \"a\";", 12));
            Assert.Null(ShaderIncludePaths.Analyze("#include \"A.ush\" // x", 20)); // after the closing quote
        }

        [Fact]
        public void MapsVirtualDirectories()
        {
            string engine = Path.GetFullPath("UE/Engine");
            string project = Path.GetFullPath("Game");
            var plugins = new Dictionary<string, string> { ["Niagara"] = Path.GetFullPath("UE/Engine/Plugins/FX/Niagara/Shaders") };
            Assert.Equal(Path.Combine(engine, "Shaders", "Private"), ShaderIncludePaths.MapDirectory("/Engine/Private/", null, engine, project, plugins));
            Assert.Equal(Path.Combine(engine, "Shaders"), ShaderIncludePaths.MapDirectory("/Engine/", null, engine, project, plugins));
            Assert.Equal(Path.Combine(plugins["Niagara"], "Private"), ShaderIncludePaths.MapDirectory("/Plugin/niagara/Private/", null, engine, project, plugins));
            Assert.Equal(Path.Combine(project, "Shaders"), ShaderIncludePaths.MapDirectory("/Project/", null, engine, project, plugins));
            Assert.Null(ShaderIncludePaths.MapDirectory("/Plugin/", null, engine, project, plugins));
            Assert.Null(ShaderIncludePaths.MapDirectory("/Plugin/Unknown/", null, engine, project, plugins));
            string cur = Path.Combine(project, "Shaders", "My.usf");
            Assert.Equal(Path.Combine(project, "Shaders", "Sub"), ShaderIncludePaths.MapDirectory("Sub/", cur, engine, project, plugins));
            Assert.True(ShaderIncludePaths.IsShaderInclude("Common.ush"));
            Assert.False(ShaderIncludePaths.IsShaderInclude("Common.cpp"));
            Assert.Equal(plugins["Niagara"], ShaderIncludePaths.AsVirtualRoots(plugins)["/Plugin/Niagara"]);
        }

        private sealed class MemDirs : IDirectoryLister
        {
            public readonly HashSet<string> Dirs = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            public readonly List<string> FilesList = new List<string>();
            public readonly List<string> Listed = new List<string>();

            public IEnumerable<string> Directories(string dir)
            {
                Listed.Add(dir);
                return Dirs.Where(d => string.Equals(Path.GetDirectoryName(d), dir, StringComparison.OrdinalIgnoreCase)).ToList();
            }

            public IEnumerable<string> Files(string dir, string pattern) =>
                FilesList.Where(f => string.Equals(Path.GetDirectoryName(f), dir, StringComparison.OrdinalIgnoreCase) && f.EndsWith(".uplugin")).ToList();
        }

        [Fact]
        public void PluginDiscoveryStopsAtPluginsAndSkipsBigFolders()
        {
            string root = Path.GetFullPath("P");
            var fs = new MemDirs();
            foreach (var d in new[] { "FX", "FX/Niagara", "FX/Niagara/Source", "FX/Niagara/Source/Deep", "FX/Niagara/Sub", "Content", "Content/X", "Game" })
                fs.Dirs.Add(Path.Combine(root, d.Replace('/', Path.DirectorySeparatorChar)));
            fs.FilesList.Add(Path.Combine(root, "FX", "Niagara", "Niagara.uplugin"));
            fs.FilesList.Add(Path.Combine(root, "Content", "X", "Hidden.uplugin"));
            fs.FilesList.Add(Path.Combine(root, "Game", "Game.uplugin"));
            var found = PluginDiscovery.Find(new[] { root }, fs, CancellationToken.None);
            Assert.Equal(new[] { "Game", "Niagara" }, found.Keys.OrderBy(k => k));
            Assert.DoesNotContain(fs.Listed, d => d.Contains("Source") || d.Contains("Content"));
        }
    }

    public class HlslBuiltinTests
    {
        [Fact]
        public void BuiltinsAreUniqueAndDescribedInBothLanguages()
        {
            var all = HlslBuiltins.All.ToList();
            Assert.Equal(all.Count, all.Select(b => b.Name).Distinct().Count());
            Assert.True(all.Count > 120);
            Assert.All(all, b =>
            {
                Assert.False(string.IsNullOrWhiteSpace(b.DescriptionZh), b.Name);
                Assert.False(string.IsNullOrWhiteSpace(b.DescriptionEn), b.Name);
                Assert.False(string.IsNullOrWhiteSpace(b.Signature), b.Name);
            });
            Assert.Equal(HlslBuiltinKind.Intrinsic, HlslBuiltins.Get("saturate").Kind);
            Assert.Equal(HlslBuiltinKind.UeMacro, HlslBuiltins.Get("FEATURE_LEVEL").Kind);
            Assert.Null(HlslBuiltins.Get("NotABuiltin"));
            Assert.All(HlslBuiltins.For(true), b => Assert.Equal(HlslBuiltinKind.Method, b.Kind));
            Assert.Contains(HlslBuiltins.For(true), b => b.Name == "SampleLevel");
            Assert.DoesNotContain(HlslBuiltins.For(false), b => b.Name == "SampleLevel");
            foreach (HlslBuiltinKind k in Enum.GetValues(typeof(HlslBuiltinKind)))
            {
                Assert.False(string.IsNullOrEmpty(HlslBuiltins.KindText(k, true)));
                Assert.False(string.IsNullOrEmpty(HlslBuiltins.KindText(k, false)));
            }
        }
    }
}
