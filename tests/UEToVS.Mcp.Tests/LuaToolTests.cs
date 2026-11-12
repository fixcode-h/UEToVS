using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using UEToVS.Contracts;
using UEToVS.Core.Storage;
using UEToVS.Engine.Indexing;
using UEToVS.Mcp;
using UEToVS.Mcp.Data;
using UEToVS.Mcp.Tools;
using Xunit;

namespace UEToVS.Mcp.Tests
{
    /// <summary>
    /// Lua script index end to end: the engine's <see cref="LuaIndexService"/> writes lua-index.json, the MCP tools read it,
    /// incremental updates and rebuild/disable show up in the answers.
    /// </summary>
    public sealed class LuaToolTests : IDisposable
    {
        private readonly string _dir = Path.Combine(Path.GetTempPath(), "uetovs-lua-tests", Guid.NewGuid().ToString("N"));
        private readonly LuaIndexService _svc;
        private readonly CodeIndex _index;
        private readonly ToolInvoker _invoker;

        public LuaToolTests()
        {
            Write("UI/MainPanel.lua",
                "---@class UI.MainPanel\n" +
                "local MainPanel = UnLua.Class()\n" +
                "\n" +
                "function MainPanel:Construct()\n" +
                "end\n" +
                "\n" +
                "function MainPanel:OnButtonClicked(button)\n" +
                "end\n" +
                "\n" +
                "return MainPanel\n");
            Write("Game/Weapon.lua", "local Weapon = {}\nWeapon.Damage = 10\nfunction Weapon.Fire(target) end\nlocal function helper() end\nreturn Weapon\n");
            _svc = new LuaIndexService(StoragePaths.ForSolution(_dir).Lua, _dir, watch: false, CancellationToken.None);
            _index = new CodeIndex(_dir, DataSourceMode.IndexOnly);
            _invoker = new ToolInvoker(new ToolCatalog(_index));
        }

        private string Write(string rel, string text)
        {
            string p = Path.Combine(_dir, "Content", "Script", rel);
            Directory.CreateDirectory(Path.GetDirectoryName(p));
            File.WriteAllText(p, text);
            return Path.GetFullPath(p);
        }

        private async Task Configure(params string[] paths)
        {
            _svc.Configure(paths, "test");
            await _svc.ScanTask.WaitAsync(TimeSpan.FromSeconds(30));
            _index.Lua.Invalidate();
        }

        private async Task<JsonElement> Call(string tool, object args)
        {
            var r = await _invoker.InvokeAsync(tool, Tree.Args(args), CancellationToken.None);
            Assert.False(r.IsError, r.Text);
            using var doc = JsonDocument.Parse(r.Text);
            return doc.RootElement.Clone();
        }

        public void Dispose()
        {
            _svc.Dispose();
            _index.Dispose();
            try { Directory.Delete(_dir, recursive: true); } catch { }
        }

        [Fact]
        public async Task FileWatcher_PicksUpNewScripts()
        {
            using var watched = new LuaIndexService(Path.Combine(_dir, "watched-index"), _dir, watch: true, CancellationToken.None);
            watched.Configure(new[] { "Content/Script" }, "test");
            await watched.ScanTask.WaitAsync(TimeSpan.FromSeconds(30));
            Write("Game/Late.lua", "function LateArrival() end\n");
            var deadline = DateTime.UtcNow.AddSeconds(15);
            while (DateTime.UtcNow < deadline && !watched.Data.Files.Any(f => f.Symbols.Any(x => x.Name == "LateArrival")))
                await Task.Delay(100);
            Assert.Contains(watched.Data.Files, f => f.Symbols.Any(x => x.Name == "LateArrival"));
        }

        [Fact]
        public async Task NoLuaPath_ToolsExplainHowToEnable()
        {
            var r = await Call("search_lua", new { query = "Fire" });
            Assert.Equal(0, r.Int("count"));
            Assert.False(r.Bool("indexReady"));
            Assert.Contains("Lua 脚本路径", r.Str("note"));
        }

        [Fact]
        public async Task Search_Files_Outline()
        {
            await Configure("Content/Script");
            Assert.Equal(LuaIndexState.Ready, _svc.State);
            Assert.True(File.Exists(_svc.IndexFile));

            var camel = await Call("search_lua", new { query = "OBC" });
            var top = camel.Items().First();
            Assert.Equal("OnButtonClicked", top.Str("name"));
            Assert.Equal("method", top.Str("kind"));
            Assert.Equal("MainPanel", top.Str("container"));
            Assert.Equal(7, top.Int("line"));
            Assert.Equal("UI.MainPanel", top.Str("module"));
            Assert.Equal("function MainPanel:OnButtonClicked(button)", top.Str("sig"));
            Assert.Equal("lua-index", camel.Str("source"));

            var qualified = await Call("search_lua", new { query = "Weapon.Fire", match = "exact" });
            Assert.Equal("Fire", qualified.Items().Single().Str("name"));
            var classes = await Call("search_lua", new { query = "MainPanel", kinds = new[] { "class" } });
            Assert.Equal(2, classes.Int("count")); // ---@class + UnLua.Class()
            var noLocals = await Call("search_lua", new { query = "helper", include_locals = false });
            Assert.Equal(0, noLocals.Int("count"));

            var files = await Call("search_lua_files", new { query = "UI.MainPanel" });
            Assert.EndsWith("MainPanel.lua", files.Items().First().Str("path"));
            var fuzzyFile = await Call("search_lua_files", new { query = "Weap" });
            Assert.Equal("Game.Weapon", fuzzyFile.Items().Single().Str("module"));

            var outline = await Call("get_lua_outline", new { path = "Game.Weapon" });
            Assert.Equal(new[] { "Weapon", "Damage", "Fire", "helper" }, outline.Items().Select(i => i.Str("name")).ToArray());
            var byPath = await Call("get_lua_outline", new { path = "Content/Script/UI/MainPanel.lua" });
            Assert.Equal("UI.MainPanel", byPath.Str("module"));

            var status = await Call("index_status", new { });
            var lua = status.GetProperty("lua");
            Assert.Equal(2, lua.Int("files"));
        }

        [Fact]
        public async Task SavedScripts_UpdateIncrementally_RebuildAndDisable()
        {
            await Configure("Content/Script");
            string weapon = Path.Combine(_dir, "Content", "Script", "Game", "Weapon.lua");
            File.AppendAllText(weapon, "function Weapon.Reload() end\n");
            string ammo = Write("Game/Ammo.lua", "function SpawnAmmo() end\n");
            _svc.NotifyChanges(new List<FileChange>
            {
                new FileChange { FilePath = weapon, Kind = FileChangeKind.Changed },
                new FileChange { FilePath = ammo, Kind = FileChangeKind.Created },
                new FileChange { FilePath = Path.Combine(_dir, "Source", "X.cpp"), Kind = FileChangeKind.Changed }, // not Lua: ignored
            });
            await _svc.FlushAsync();
            _index.Lua.Invalidate();
            Assert.Equal("Reload", (await Call("search_lua", new { query = "Reload", match = "exact" })).Items().Single().Str("name"));
            Assert.Equal(1, (await Call("search_lua", new { query = "SpawnAmmo", match = "exact" })).Int("count"));

            File.Delete(ammo);
            _svc.NotifyChanges(new List<FileChange> { new FileChange { FilePath = ammo, Kind = FileChangeKind.Deleted } });
            await _svc.FlushAsync();
            _index.Lua.Invalidate();
            Assert.Equal(0, (await Call("search_lua", new { query = "SpawnAmmo", match = "exact" })).Int("count"));

            _svc.Rebuild();
            await _svc.ScanTask.WaitAsync(TimeSpan.FromSeconds(30));
            Assert.Equal(2, _svc.LastScan.Parsed);
            Assert.Equal(0, _svc.LastScan.Reused);

            // Same folders again: no re-scan; then an incremental re-scan after a restart re-reads nothing.
            _svc.Configure(new[] { "Content/Script" }, "test");
            using (var restarted = new LuaIndexService(StoragePaths.ForSolution(_dir).Lua, _dir, watch: false, CancellationToken.None))
            {
                restarted.Configure(new[] { "Content/Script" }, "test");
                await restarted.ScanTask.WaitAsync(TimeSpan.FromSeconds(30));
                Assert.Equal(0, restarted.LastScan.Parsed);
                Assert.Equal(2, restarted.LastScan.Reused);
            }

            await Configure();
            Assert.Equal(LuaIndexState.Disabled, _svc.State);
            Assert.False(File.Exists(_svc.IndexFile));
            Assert.Contains("Lua 脚本路径", (await Call("search_lua", new { query = "Fire" })).Str("note"));
        }
    }
}
