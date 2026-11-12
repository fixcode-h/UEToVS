using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using UEToVS.Core.Storage;
using UEToVS.Engine.Tests;
using UEToVS.Mcp.Data;
using UEToVS.Mcp.Tools;
using Xunit;

namespace UEToVS.Mcp.Tests
{
    /// <summary>The synthetic tree plus a header UHT would reject (no GENERATED_BODY) and a USTRUCT to rename; index built, engine stopped.</summary>
    public sealed class BuildToolsFixture : IDisposable
    {
        public BuildToolsFixture()
        {
            T = Tree.Create();
            T.Write("Game/Source/MyGame/Public/Broken.h",
                "#pragma once\n#include \"Broken.generated.h\"\n\nUCLASS()\nclass MYGAME_API UBroken : public UObject\n{\npublic:\n\tint32 X;\n};\n");
            T.Write("Game/Source/MyGame/Public/RenameMe.h",
                "#pragma once\n#include \"RenameMe.generated.h\"\n\nUSTRUCT(BlueprintType)\nstruct MYGAME_API FRenameMe\n{\n\tGENERATED_BODY()\n\tint32 Value;\n};\n");
            T.Write("Game/Source/MyGame/Private/RenameUser.cpp",
                "\uFEFF#include \"RenameMe.h\"\r\n\r\nstatic FRenameMe MakeOne()\r\n{\r\n\tFRenameMe R;\r\n\treturn R;\r\n}\r\n");
            var host = Tree.StartEngine(T);
            host.StartIndexing();
            host.Index.WaitForReadyAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(60)).GetAwaiter().GetResult();
            host.StopAsync(TimeSpan.FromSeconds(10)).GetAwaiter().GetResult();
            Index = new CodeIndex(T.ProjectRoot, DataSourceMode.Auto);
            Invoker = new ToolInvoker(new ToolCatalog(Index));
        }

        public FakeUnrealTree T { get; }
        public CodeIndex Index { get; }
        public ToolInvoker Invoker { get; }

        public async Task<JsonElement> Call(string tool, object args = null, bool expectError = false)
        {
            var r = await Invoker.InvokeAsync(tool, args == null ? new Dictionary<string, JsonElement>() : Tree.Args(args), CancellationToken.None);
            Assert.True(r.IsError == expectError, $"{tool}: isError={r.IsError} text={r.Text}");
            using var doc = JsonDocument.Parse(r.Text);
            return doc.RootElement.Clone();
        }

        public void Dispose()
        {
            Index.Dispose();
            T.Dispose();
        }
    }

    public sealed class BuildToolTests : IClassFixture<BuildToolsFixture>
    {
        private readonly BuildToolsFixture _f;

        public BuildToolTests(BuildToolsFixture f) => _f = f;

        private string BuildFile => Path.Combine(StoragePaths.ForSolution(_f.T.ProjectRoot).Root, "build", "last-build.json");

        [Fact]
        public async Task GetBuildErrors_ReadsTheRecordWrittenByVs()
        {
            if (File.Exists(BuildFile)) File.Delete(BuildFile);
            var none = await _f.Call("get_build_errors");
            Assert.Equal(0, none.Int("count"));
            Assert.Contains("No build recorded", none.Str("note"));

            Directory.CreateDirectory(Path.GetDirectoryName(BuildFile));
            // the format written by src/UEToVS.Host/Logic/Build/BuildRecord.cs
            File.WriteAllText(BuildFile, @"{
  ""version"": 1, ""source"": ""vs-build"", ""startedUtc"": ""2026-11-12T10:00:00.0000000Z"", ""finishedUtc"": ""2026-11-12T10:00:12.5000000Z"",
  ""durationMs"": 12500, ""result"": ""failed"", ""errors"": 2, ""warnings"": 1, ""notes"": 1, ""truncated"": false, ""linesParsed"": 40,
  ""unparsedSamples"": [],
  ""items"": [
    { ""severity"": ""warning"", ""file"": ""C:\\P\\Source\\MyGame\\Private\\MyHero.cpp"", ""line"": 3, ""code"": ""C4996"", ""message"": ""old"", ""tool"": ""msvc"" },
    { ""severity"": ""error"", ""file"": ""C:\\P\\Source\\MyGame\\Private\\MyHero.cpp"", ""line"": 5, ""column"": 2, ""code"": ""C2065"", ""message"": ""'Fire2': undeclared identifier"", ""tool"": ""msvc"" },
    { ""severity"": ""note"", ""file"": ""C:\\P\\Source\\MyGame\\Public\\MyHero.h"", ""line"": 1, ""message"": ""see declaration"" },
    { ""severity"": ""error"", ""code"": ""LNK2019"", ""message"": ""unresolved external symbol"", ""tool"": ""linker"" }
  ]
}");
            var all = await _f.Call("get_build_errors");
            Assert.Equal("failed", all.GetProperty("build").Str("result"));
            Assert.Equal(12500, all.GetProperty("build").GetProperty("durationMs").GetInt64());
            var items = all.Items().ToList();
            Assert.Equal(3, items.Count); // notes excluded
            Assert.Equal("error", items[0].Str("severity"));   // errors first
            Assert.Equal(5, items[0].Int("line"));
            Assert.Equal(2, items[0].Int("col"));
            Assert.Equal("C2065", items[0].Str("code"));

            var file = await _f.Call("get_build_errors", new { file = "myhero.cpp", severity = "error" });
            Assert.Single(file.Items());
            Assert.Equal("C2065", file.Items().First().Str("code"));

            var notes = await _f.Call("get_build_errors", new { include_notes = true, limit = 2 });
            Assert.Equal(4, notes.Int("matching"));
            Assert.True(notes.Bool("hasMore"));
            Assert.Equal(2, notes.Int("nextOffset"));

            await _f.Call("get_build_errors", new { severity = "fatal" }, expectError: true);

            File.WriteAllText(BuildFile, "{ broken");
            await _f.Call("get_build_errors", expectError: true);
            File.Delete(BuildFile);
        }

        [Fact]
        public async Task GetDiagnostics_RunsUeRulesOfflineAndSkipsEngineFiles()
        {
            var r = await _f.Call("get_diagnostics", new
            {
                paths = new[] { "Source/MyGame/Public/Broken.h", "Source/MyGame/Public/MyHero.h", _f.T.P("UE/Engine/Source/Runtime/Engine/Classes/GameFramework/Actor.h"), "Missing.h", "Notes.txt" },
            });
            Assert.Equal("index", r.Str("source"));
            var results = r.GetProperty("results").EnumerateArray().ToList();
            var broken = results.Single(x => x.Str("file").EndsWith("Broken.h"));
            var item = broken.Items().Single(i => i.Str("code") == "UE0003");
            Assert.Equal("error", item.Str("severity"));
            Assert.True(item.Int("line") >= 1);
            Assert.False(string.IsNullOrEmpty(item.Str("message")));
            var hero = results.Single(x => x.Str("file").EndsWith("MyHero.h"));
            Assert.DoesNotContain(hero.Items(), i => i.Str("code") == "UE0003");
            var skipped = r.GetProperty("skipped").EnumerateArray().ToList();
            Assert.Contains(skipped, s => s.Str("file").EndsWith("Actor.h") && s.Str("reason") == "not-project-file");
            Assert.Contains(skipped, s => s.Str("file").EndsWith("Missing.h") && s.Str("reason") == "not-found");
            Assert.Contains(skipped, s => s.Str("file").EndsWith("Notes.txt") && s.Str("reason") == "not-cpp");
            Assert.True(r.Int("errors") >= 1);

            var errorsOnly = await _f.Call("get_diagnostics", new { path = "Source/MyGame/Public/Broken.h", min_severity = "error" });
            Assert.True(errorsOnly.Int("count") >= 1);
            await _f.Call("get_diagnostics", new { }, expectError: true);
        }

        [Fact]
        public async Task RenameSymbol_PreviewsThenAppliesWithCoreRedirect()
        {
            string header = _f.T.P("Game/Source/MyGame/Public/RenameMe.h"), user = _f.T.P("Game/Source/MyGame/Private/RenameUser.cpp");
            string ini = _f.T.P("Game/Config/DefaultEngine.ini");
            string headerBefore = File.ReadAllText(header), userBefore = File.ReadAllText(user);

            await _f.Call("rename_symbol", new { symbol = "FRenameMe", new_name = "1Bad" }, expectError: true);
            await _f.Call("rename_symbol", new { symbol = "FRenameMe", new_name = "RenamedThing" }, expectError: true); // USTRUCT keeps the F prefix
            await _f.Call("rename_symbol", new { symbol = "AActor", new_name = "AOther" }, expectError: true);     // engine symbol
            await _f.Call("rename_symbol", new { symbol = "FNoSuchThing", new_name = "FOther" }, expectError: true);

            // offline: name-based references only → listed, not included by default
            var preview = await _f.Call("rename_symbol", new { symbol = "FRenameMe", new_name = "FRenamedThing" });
            Assert.False(preview.Bool("applied"));
            Assert.Equal(0, preview.Int("edits"));
            Assert.True(preview.Int("nameOnlyNotIncluded") >= 3, preview.ToString());
            Assert.Contains("include_name_only", preview.Str("note"));
            Assert.Equal(headerBefore, File.ReadAllText(header));

            var withNames = await _f.Call("rename_symbol", new { symbol = "FRenameMe", new_name = "FRenamedThing", include_name_only = true });
            Assert.True(withNames.Int("edits") >= 3);
            Assert.Equal(2, withNames.Int("files"));
            Assert.Contains("StructRedirects", withNames.GetProperty("coreRedirect").Str("line"));
            Assert.Contains("Preview only", withNames.Str("note"));
            Assert.Equal(userBefore, File.ReadAllText(user));

            var applied = await _f.Call("rename_symbol", new { symbol = "FRenameMe", new_name = "FRenamedThing", include_name_only = true, apply = true });
            Assert.True(applied.Bool("applied"));
            Assert.Equal(2, applied.GetProperty("written").Int("files"));
            Assert.Equal(0, applied.GetProperty("written").Int("skipped"));
            Assert.Contains("get_diagnostics", applied.Str("note"));
            string h = File.ReadAllText(header), u = File.ReadAllText(user);
            Assert.Contains("struct MYGAME_API FRenamedThing", h);
            Assert.DoesNotContain("FRenameMe", h.Replace("RenameMe.generated.h", ""));
            Assert.Contains("static FRenamedThing MakeOne()\r\n", u);   // line endings kept
            Assert.Equal(0xEF, File.ReadAllBytes(user)[0]);              // BOM kept
            Assert.Contains("+StructRedirects=(OldName=\"/Script/MyGame.RenameMe\",NewName=\"/Script/MyGame.RenamedThing\")", File.ReadAllText(ini));
            Assert.Contains("[/Script/Engine.Engine]", File.ReadAllText(ini));
        }
    }

    /// <summary>get_diagnostics against a running engine.</summary>
    public sealed class LiveDiagnosticsTests : IClassFixture<LiveFixture>
    {
        private readonly LiveFixture _f;

        public LiveDiagnosticsTests(LiveFixture f) => _f = f;

        [Fact]
        public async Task GetDiagnostics_UsesTheEngine()
        {
            _f.T.Write("Game/Source/MyGame/Public/LiveBroken.h",
                "#pragma once\n#include \"LiveBroken.generated.h\"\n\nUSTRUCT()\nstruct MYGAME_API FLiveBroken\n{\n\tint32 X;\n};\n");
            var r = await _f.Call("get_diagnostics", new { path = "Source/MyGame/Public/LiveBroken.h" });
            Assert.Equal("engine", r.Str("source"));
            var file = r.GetProperty("results").EnumerateArray().Single();
            Assert.Contains(file.Items(), i => i.Str("code") == "UE0003");
        }
    }
}
