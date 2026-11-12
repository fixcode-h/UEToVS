using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using UEToVS.Core.Storage;
using UEToVS.Engine.Indexing;
using UEToVS.Engine.Tests;
using UEToVS.Mcp.Data;
using UEToVS.Mcp.Tools;
using Xunit;

namespace UEToVS.Mcp.Tests
{
    /// <summary>The synthetic tree plus a replicated, configurable actor, ini files, a CVar, tags, delegate bindings and a Lua override.</summary>
    public sealed class UeToolsFixture : IDisposable
    {
        private readonly LuaIndexService _lua;

        public UeToolsFixture()
        {
            T = Tree.Create();
            T.Write("Game/Source/MyGame/Public/MyNetActor.h",
                "#pragma once\n#include \"GameFramework/Actor.h\"\n" +
                "UCLASS(config=Game, BlueprintType)\n" +
                "class MYGAME_API AMyNetActor : public AActor\n{\n\tGENERATED_BODY()\npublic:\n" +
                "\tUPROPERTY(EditAnywhere, BlueprintReadWrite, Category=\"Stats\", meta=(ClampMin=\"0\", AllowPrivateAccess=\"true\"))\n\tfloat Health;\n" +
                "\tUPROPERTY(ReplicatedUsing=OnRep_Ammo)\n\tint32 Ammo;\n" +
                "\tUPROPERTY(Replicated, BlueprintReadOnly)\n\tint32 Score;\n" +
                "\tUPROPERTY(Config)\n\tint32 MaxPlayers;\n" +
                "\tUFUNCTION(Server, Reliable, WithValidation)\n\tvoid ServerFire(int32 Shots);\n" +
                "\tUFUNCTION(NetMulticast, Unreliable)\n\tvoid MulticastBoom();\n" +
                "\tUFUNCTION(BlueprintImplementableEvent)\n\tvoid ReceiveHit();\n" +
                "\tUFUNCTION()\n\tvoid OnRep_Ammo();\n" +
                "\tUFUNCTION()\n\tvoid HandleClicked();\n" +
                "\tvoid BindAll();\n};\n");
            T.Write("Game/Source/MyGame/Private/MyNetActor.cpp",
                "#include \"MyNetActor.h\"\n" +
                "static TAutoConsoleVariable<int32> CVarMyGameDebug(TEXT(\"mygame.Debug\"), 0, TEXT(\"Turns on MyGame debug drawing\"), ECVF_Default);\n" +
                "UE_DEFINE_GAMEPLAY_TAG(TAG_Ability_Fire, \"Ability.Fire\");\n" +
                "void AMyNetActor::BindAll()\n{\n" +
                "\tButton->OnClicked.AddDynamic(this, &AMyNetActor::HandleClicked);\n" +
                "\tOnSomething.AddUObject(this,\n\t\t&AMyNetActor::HandleClicked);\n" +
                "\tTimerDelegate.BindUFunction(this, FName(\"HandleClicked\"));\n" +
                "\tHandleClicked();\n}\n");
            T.Write("Game/Config/DefaultGame.ini", "[/Script/MyGame.MyNetActor]\nMaxPlayers=16\n\n[/Script/EngineSettings.GameMapsSettings]\nGameDefaultMap=/Game/Maps/L_Main\n");
            T.Write("Game/Config/DefaultGameplayTags.ini", "[/Script/GameplayTags.GameplayTagsSettings]\n+GameplayTagList=(Tag=\"Ability.Dash\",DevComment=\"Dash forward\")\n");
            T.Write("Game/Content/Script/Actors/NetActor.lua", "local M = UnLua.Class()\nfunction M:ReceiveHit()\nend\nreturn M\n");

            var host = Tree.StartEngine(T);
            host.StartIndexing();
            host.Index.WaitForReadyAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(60)).GetAwaiter().GetResult();
            host.StopAsync(TimeSpan.FromSeconds(10)).GetAwaiter().GetResult();

            _lua = new LuaIndexService(StoragePaths.ForSolution(T.ProjectRoot).Lua, T.ProjectRoot, watch: false, CancellationToken.None);
            _lua.Configure(new[] { Path.Combine(T.ProjectRoot, "Content", "Script") }, "test");
            _lua.ScanTask.WaitAsync(TimeSpan.FromSeconds(30)).GetAwaiter().GetResult();

            Index = new CodeIndex(T.ProjectRoot, DataSourceMode.Auto);
            Invoker = new ToolInvoker(new ToolCatalog(Index));
        }

        public FakeUnrealTree T { get; }
        public CodeIndex Index { get; }
        public ToolInvoker Invoker { get; }

        public async Task<JsonElement> Call(string tool, object args, bool expectError = false)
        {
            var r = await Invoker.InvokeAsync(tool, Tree.Args(args), CancellationToken.None);
            Assert.True(r.IsError == expectError, $"{tool}: isError={r.IsError} text={r.Text}");
            using var doc = JsonDocument.Parse(r.Text);
            return doc.RootElement.Clone();
        }

        public void Dispose()
        {
            _lua.Dispose();
            Index.Dispose();
            T.Dispose();
        }
    }

    public sealed class UeToolTests : IClassFixture<UeToolsFixture>
    {
        private readonly UeToolsFixture _f;

        public UeToolTests(UeToolsFixture f) => _f = f;

        private static JsonElement Item(JsonElement page, string name) =>
            page.GetProperty("items").EnumerateArray().First(i => i.GetProperty("name").GetString() == name);

        [Fact]
        public async Task ReflectionInfo_SplitsSpecifiers_Replication_Rpcs_AndLinksLua()
        {
            var r = await _f.Call("get_reflection_info", new { type_name = "AMyNetActor" });
            Assert.Equal("AMyNetActor", r.GetProperty("type").GetProperty("name").GetString());
            Assert.Equal("Game", r.GetProperty("type").GetProperty("values").GetProperty("config").GetString());
            var totals = r.GetProperty("totals");
            Assert.Equal(4, totals.GetProperty("properties").GetInt32());
            Assert.Equal(2, totals.GetProperty("replicated").GetInt32());
            Assert.Equal(2, totals.GetProperty("rpcs").GetInt32());

            var health = Item(r, "Health");
            Assert.Contains("BlueprintReadWrite", health.GetProperty("specifiers").EnumerateArray().Select(x => x.GetString()));
            Assert.Equal("Stats", health.GetProperty("values").GetProperty("Category").GetString());
            Assert.Equal("0", health.GetProperty("meta").GetProperty("ClampMin").GetString());
            Assert.Equal("ReplicatedUsing=OnRep_Ammo", Item(r, "Ammo").GetProperty("replication").GetString());
            var fire = Item(r, "ServerFire").GetProperty("rpc");
            Assert.Equal("Server", fire.GetProperty("type").GetString());
            Assert.Equal("Reliable", fire.GetProperty("reliability").GetString());
            Assert.True(fire.GetProperty("withValidation").GetBoolean());
            Assert.True(Item(r, "MaxPlayers").GetProperty("config").GetBoolean());
            var lua = Item(r, "ReceiveHit").GetProperty("lua")[0];
            Assert.EndsWith("NetActor.lua", lua.GetProperty("file").GetString());

            var rw = await _f.Call("get_reflection_info", new { type_name = "AMyNetActor", specifiers = new[] { "blueprintreadwrite" } });
            Assert.Equal(new[] { "Health" }, rw.GetProperty("items").EnumerateArray().Select(i => i.GetProperty("name").GetString()));
            var rep = await _f.Call("get_reflection_info", new { type_name = "AMyNetActor", replicated_only = true });
            Assert.Equal(new[] { "Ammo", "MulticastBoom", "Score", "ServerFire" },
                rep.GetProperty("items").EnumerateArray().Select(i => i.GetProperty("name").GetString()).OrderBy(x => x, StringComparer.Ordinal));
            var meta = await _f.Call("get_reflection_info", new { type_name = "AMyNetActor", specifiers = new[] { "AllowPrivateAccess" }, kind = "property" });
            Assert.Single(meta.GetProperty("items").EnumerateArray());
        }

        [Fact]
        public async Task ModuleInfo_ByFileAndName_AndHeaderDependency()
        {
            var byFile = await _f.Call("get_module_info", new { path = _f.T.P("Game/Source/MyGame/Private/MyNetActor.cpp") });
            var m = byFile.GetProperty("module");
            Assert.Equal("MyGame", m.GetProperty("name").GetString());
            Assert.Equal("Runtime", m.GetProperty("type").GetString());
            Assert.Contains("Niagara", m.GetProperty("privateDependencies").EnumerateArray().Concat(m.GetProperty("publicDependencies").EnumerateArray()).Select(x => x.GetString()));

            var niagara = await _f.Call("get_module_info", new { module = "Niagara" });
            Assert.Equal("Runtime", niagara.GetProperty("module").GetProperty("type").GetString());
            Assert.Equal("uplugin", niagara.GetProperty("module").GetProperty("typeFrom").GetString());
            Assert.Equal("Niagara", niagara.GetProperty("module").GetProperty("plugin").GetString());
            Assert.Equal("engine_plugin", niagara.GetProperty("module").GetProperty("origin").GetString());
            var tools = await _f.Call("get_module_info", new { module = "MyToolsEditor" });
            Assert.Equal("Editor", tools.GetProperty("module").GetProperty("type").GetString());
            Assert.Contains("MyGame", tools.GetProperty("module").GetProperty("publicDependencies").EnumerateArray().Select(x => x.GetString()));

            var hdr = await _f.Call("get_module_info", new { path = _f.T.P("Game/Source/MyGame/Public/MyNetActor.h"), header = "GameFramework/Actor.h" });
            var h = hdr.GetProperty("header");
            Assert.Equal("Engine", h.GetProperty("module").GetString());
            Assert.True(h.GetProperty("needsDependency").GetBoolean());
            Assert.Equal("PublicDependencyModuleNames", h.GetProperty("addTo").GetString());

            var ok = await _f.Call("get_module_info", new { path = _f.T.P("Game/Source/MyGame/Private/MyNetActor.cpp"), header = "NiagaraSystem.h" });
            Assert.False(ok.GetProperty("header").GetProperty("needsDependency").GetBoolean());

            var missing = await _f.Call("get_module_info", new { module = "NoSuchModule" });
            Assert.False(missing.TryGetProperty("module", out _));
            await _f.Call("get_module_info", new { }, expectError: true);
        }

        [Fact]
        public async Task SuggestInclude_GivesUeStylePath_AndDependency()
        {
            var r = await _f.Call("suggest_include", new { type = "ACharacter", from = _f.T.P("Game/Source/MyGame/Private/MyNetActor.cpp") });
            Assert.Equal("#include \"GameFramework/Character.h\"", r.GetProperty("include").GetString());
            Assert.Equal("Engine", r.GetProperty("module").GetString());
            var dep = r.GetProperty("dependency");
            Assert.True(dep.GetProperty("needed").GetBoolean());
            Assert.Equal("PrivateDependencyModuleNames", dep.GetProperty("addTo").GetString());

            var same = await _f.Call("suggest_include", new { type = "AMyNetActor", from = _f.T.P("Game/Source/MyGame/Private/MyNetActor.cpp") });
            Assert.Equal("#include \"MyNetActor.h\"", same.GetProperty("include").GetString());
            Assert.False(same.GetProperty("dependency").GetProperty("needed").GetBoolean());

            var none = await _f.Call("suggest_include", new { type = "FNoSuchType" });
            Assert.False(none.TryGetProperty("include", out _));
        }

        [Fact]
        public async Task SearchConfig_FindsIniKeys_ConfigProperties_CVars_AndTags()
        {
            var ini = await _f.Call("search_config", new { query = "GameDefaultMap" });
            var i0 = ini.GetProperty("items")[0];
            Assert.Equal("ini", i0.GetProperty("type").GetString());
            Assert.Equal("/Script/EngineSettings.GameMapsSettings", i0.GetProperty("section").GetString());
            Assert.Equal(5, i0.GetProperty("line").GetInt32());

            var prop = await _f.Call("search_config", new { query = "MaxPlayers" });
            var items = prop.GetProperty("items").EnumerateArray().ToList();
            var key = items.First(x => x.GetProperty("type").GetString() == "ini");
            Assert.Equal("AMyNetActor", key.GetProperty("property").GetProperty("class").GetString());
            var cp = items.First(x => x.GetProperty("type").GetString() == "config_property");
            Assert.Equal("DefaultGame.ini", cp.GetProperty("iniFile").GetString());
            Assert.Equal("/Script/MyGame.MyNetActor", cp.GetProperty("section").GetString());
            Assert.Equal("16", cp.GetProperty("setIn")[0].GetProperty("value").GetString());

            var cvar = await _f.Call("search_config", new { query = "mygame.debug", kinds = new[] { "cvar" } });
            var c = cvar.GetProperty("items")[0];
            Assert.Equal("mygame.Debug", c.GetProperty("name").GetString());
            Assert.Equal("CVarMyGameDebug", c.GetProperty("variable").GetString());
            Assert.Equal("Turns on MyGame debug drawing", c.GetProperty("help").GetString());
            Assert.Equal(2, c.GetProperty("line").GetInt32());

            var tags = await _f.Call("search_config", new { query = "Ability", kinds = new[] { "tag" } });
            var names = tags.GetProperty("items").EnumerateArray().Select(t => t.GetProperty("tag").GetString()).ToList();
            Assert.Contains("Ability.Dash", names);
            Assert.Contains("Ability.Fire", names);

            await _f.Call("search_config", new { query = "x", kinds = new[] { "nope" } }, expectError: true);
        }

        [Fact]
        public async Task DelegateBindings_FindsDynamicNativeAndByNameBindings_NotPlainCalls()
        {
            var r = await _f.Call("find_delegate_bindings", new { function = "AMyNetActor::HandleClicked" });
            var items = r.GetProperty("items").EnumerateArray().ToList();
            var methods = items.Select(i => i.GetProperty("method").GetString()).OrderBy(x => x, StringComparer.Ordinal).ToList();
            Assert.Equal(new[] { "AddDynamic", "AddUObject", "BindUFunction" }, methods);
            var dyn = items.First(i => i.GetProperty("method").GetString() == "AddDynamic");
            Assert.Equal("dynamic", dyn.GetProperty("kind").GetString());
            Assert.Equal("Button->OnClicked", dyn.GetProperty("delegate").GetString());
            Assert.Equal("AMyNetActor", dyn.GetProperty("targetClass").GetString());
            Assert.Equal("ufunction_name", items.First(i => i.GetProperty("method").GetString() == "BindUFunction").GetProperty("kind").GetString());

            var other = await _f.Call("find_delegate_bindings", new { function = "AOther::HandleClicked" });
            Assert.Equal(new[] { "BindUFunction" }, other.GetProperty("items").EnumerateArray().Select(i => i.GetProperty("method").GetString())); // by-name: class unknown
        }

        [Fact]
        public async Task FindReferences_ListsLuaOverridesOfAUFunction()
        {
            var r = await _f.Call("find_references", new { symbol = "AMyNetActor::ReceiveHit" });
            var lua = r.GetProperty("lua");
            Assert.Equal("lua-index", lua.GetProperty("source").GetString());
            Assert.EndsWith("NetActor.lua", lua.GetProperty("definitions")[0].GetProperty("file").GetString());
            var none = await _f.Call("find_references", new { symbol = "AMyNetActor::BindAll" });
            Assert.False(none.TryGetProperty("lua", out _));
        }

        [Fact]
        public void Specifiers_Parse()
        {
            var s = UeSpecifiers.Parse("EditAnywhere, Category=\"A, B\", meta=(DisplayName=\"X (y)\", EditCondition=\"bOn\", HideInDetailPanel), ReplicatedUsing = OnRep_X");
            Assert.Equal(new[] { "EditAnywhere" }, s.Flags);
            Assert.Equal("A, B", s.Value("category"));
            Assert.Equal("X (y)", s.Meta["DisplayName"]);
            Assert.True(s.Meta.ContainsKey("HideInDetailPanel"));
            Assert.True(s.IsReplicated);
            Assert.Null(s.Rpc);
            Assert.Equal("Client", UeSpecifiers.Parse("Client, Unreliable").Rpc);
        }

        [Fact]
        public void Bindings_Match()
        {
            Assert.Null(DelegateBindings.Match("\tHandleClicked();", "HandleClicked"));
            var b = DelegateBindings.Match("PlayerInputComponent->BindAction(\"Jump\", IE_Pressed, this, &ACharacter::Jump);", "Jump");
            Assert.Equal("input", b.Kind);
            var t = DelegateBindings.Match("GetWorldTimerManager().SetTimer(Handle, this, &AMyActor::Tick2, 1.f, true);", "Tick2");
            Assert.Equal("timer", t.Kind);
            var g = DelegateBindings.Match("D.BindUFunction(this, GET_FUNCTION_NAME_CHECKED(UMyWidget, OnDone));", "OnDone");
            Assert.Equal("UMyWidget", g.TargetClass);
            Assert.Equal("ufunction_name", g.Kind);
        }
    }
}
