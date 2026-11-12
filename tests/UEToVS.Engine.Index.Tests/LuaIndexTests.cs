using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using UEToVS.Engine.Index.Lua;
using Xunit;

namespace UEToVS.Engine.Index.Tests
{
    public sealed class LuaParserTests
    {
        private static LuaSymbol One(List<LuaSymbol> list, string qualified) =>
            Assert.Single(list, s => s.QualifiedName == qualified);

        [Fact]
        public void Functions_MethodsAndLocals()
        {
            var s = LuaParser.Parse(
                "local M = {}\n" +                               // 0
                "function M.Init(a, b)\n" +                      // 1
                "  local function helper(x) return x end\n" +    // 2
                "  self.cache = {}\n" +                          // 3  (inside a function: not a field)
                "end\n" +                                        // 4
                "function M:Fire(target, ...)\n" +               // 5
                "end\n" +                                        // 6
                "M.OnClick = function(self, btn) end\n" +        // 7
                "function GlobalFn() end\n" +                    // 8
                "local function LocalFn() end\n" +               // 9
                "return M\n");
            Assert.Equal(LuaSymbolKind.Table, One(s, "M").Kind);
            Assert.True(One(s, "M").IsLocal);
            var init = One(s, "M.Init");
            Assert.Equal(LuaSymbolKind.Function, init.Kind);
            Assert.Equal(1, init.Line);
            Assert.Equal(11, init.Column);
            Assert.Equal("function M.Init(a, b)", init.Signature);
            Assert.True(One(s, "helper").IsLocal);
            var fire = One(s, "M:Fire");
            Assert.Equal(LuaSymbolKind.Method, fire.Kind);
            Assert.Equal("function M:Fire(target, ...)", fire.Signature);
            Assert.Equal(LuaSymbolKind.Method, One(s, "M:OnClick").Kind); // self first
            Assert.Equal(LuaSymbolKind.Function, One(s, "GlobalFn").Kind);
            Assert.True(One(s, "LocalFn").IsLocal);
            Assert.DoesNotContain(s, x => x.Name == "cache");
            Assert.Equal(s.OrderBy(x => x.Line).ThenBy(x => x.Column).ToList(), s);
        }

        [Fact]
        public void Classes_TablesFieldsAndVariables()
        {
            var s = LuaParser.Parse(
                "---@class UI.MainPanel : UI.BasePanel\n" +                         // 0
                "local MainPanel = UnLua.Class(\"UI.BasePanel\")\n" +               // 1
                "local Weapon = class(\"Weapon\", Base)\n" +                        // 2
                "local Config = require(\"Common.Config\")\n" +                     // 3
                "local Tuning = {\n" +                                               // 4
                "  Speed = 10,\n" +                                                  // 5
                "  OnHit = function(self, other) end,\n" +                           // 6
                "  Nested = { Depth = 1 },\n" +                                      // 7
                "  [\"quoted\"] = 2,\n" +                                            // 8
                "  call(x = 1),\n" +                                                 // 9 (not a field)
                "}\n" +                                                              // 10
                "MainPanel.MaxItems = 20\n" +                                        // 11
                "GLOBAL_FLAG = true\n" +                                             // 12
                "for i = 1, 10 do local inner = i end\n" +                           // 13
                "if x == 1 then y = 2 end\n");                                       // 14
            var cls = One(s, "UI.MainPanel");
            Assert.Equal(LuaSymbolKind.Class, cls.Kind);
            Assert.Equal("UI", cls.Container);
            Assert.Equal(0, cls.Line);
            Assert.Equal(LuaSymbolKind.Class, One(s, "MainPanel").Kind);
            Assert.Equal(LuaSymbolKind.Class, One(s, "Weapon").Kind);
            var cfg = One(s, "Config");
            Assert.Equal(LuaSymbolKind.Variable, cfg.Kind);
            Assert.Equal("local Config = require(\"Common.Config\")", cfg.Signature);
            Assert.Equal(LuaSymbolKind.Table, One(s, "Tuning").Kind);
            Assert.Equal(LuaSymbolKind.Field, One(s, "Tuning.Speed").Kind);
            Assert.Equal(LuaSymbolKind.Method, One(s, "Tuning:OnHit").Kind);
            Assert.Equal(LuaSymbolKind.Table, One(s, "Tuning.Nested").Kind);
            Assert.Equal(LuaSymbolKind.Field, One(s, "Tuning.Nested.Depth").Kind);
            Assert.Equal(LuaSymbolKind.Field, One(s, "MainPanel.MaxItems").Kind);
            Assert.Equal(LuaSymbolKind.Variable, One(s, "GLOBAL_FLAG").Kind);
            Assert.Equal(LuaSymbolKind.Variable, One(s, "y").Kind);
            Assert.DoesNotContain(s, x => x.Name == "i" || x.Name == "inner" || x.Name == "x" || x.Name == "quoted");
        }

        [Fact]
        public void CommentsAndStrings_AreNotCode()
        {
            var s = LuaParser.Parse(
                "-- function Commented() end\n" +
                "--[[ function BlockCommented() end ]]\n" +
                "--[==[ function Level2() end ]] still comment ]==]\n" +
                "local text = [[function InString() end]]\n" +
                "local q = \"function InQuotes() end\"\n" +
                "function Real() end\n");
            Assert.Equal(new[] { "text", "q", "Real" }, s.Select(x => x.Name).ToArray());
            Assert.Equal(5, s.Single(x => x.Name == "Real").Line);
        }

        [Fact]
        public void BrokenInput_DoesNotThrow()
        {
            foreach (var text in new[] { "function", "function a.", "local", "M = {", "}}}} end end", "--[[ unterminated", "\"unterminated", "local x <const", "﻿function Bom() end" })
                LuaParser.Parse(text);
            Assert.Equal("Bom", LuaParser.Parse("﻿function Bom() end").Single().Name);
        }
    }

    public sealed class LuaIndexerTests : IDisposable
    {
        private readonly TempDir _dir = new TempDir();

        private string Root => Path.Combine(_dir.Path, "Content", "Script");

        private string Write(string rel, string text)
        {
            string p = Path.Combine(Root, rel);
            Directory.CreateDirectory(Path.GetDirectoryName(p));
            File.WriteAllText(p, text);
            return Path.GetFullPath(p);
        }

        public void Dispose() => _dir.Dispose();

        [Fact]
        public void ResolveRoots_RelativeToProjectThenSolution_Deduplicated()
        {
            Directory.CreateDirectory(Root);
            var roots = LuaIndexer.ResolveRoots(new[] { "Content/Script; Content/Script/UI", Root, " " }, solutionDir: "/nowhere", projectRoot: _dir.Path);
            Assert.Equal(new[] { Path.GetFullPath(Root) }, roots);
            var abs = LuaIndexer.ResolveRoots(new[] { "Missing" }, solutionDir: _dir.Path, projectRoot: null);
            Assert.Equal(Path.Combine(_dir.Path, "Missing"), abs.Single());
        }

        [Fact]
        public void Scan_ThenIncremental_ReusesUnchangedFiles()
        {
            string a = Write("UI/MainPanel.lua", "local M = {}\nfunction M:Show() end\nreturn M\n");
            string b = Write("Game/Weapon.lua", "function Fire() end\n");
            Write(".git/ignored.lua", "function Hidden() end\n");
            Write("Notes.txt", "function NotLua() end\n");
            var roots = LuaIndexer.ResolveRoots(new[] { Root }, null, null);

            var s1 = new LuaScanStats();
            var d1 = LuaIndexer.Scan(roots, null, s1);
            Assert.Equal(2, s1.Files);
            Assert.Equal(2, s1.Parsed);
            Assert.Equal("UI.MainPanel", d1.Files.Single(f => f.Path == a).Module);
            Assert.DoesNotContain(d1.Files, f => f.Path.Contains(".git"));

            // Second scan with the saved index: nothing re-read.
            string file = Path.Combine(_dir.Path, "lua-index.json");
            LuaIndexStore.Save(file, d1);
            var loaded = LuaIndexStore.Load(file);
            Assert.Equal(d1.SymbolCount, loaded.SymbolCount);
            var s2 = new LuaScanStats();
            var d2 = LuaIndexer.Scan(roots, loaded, s2);
            Assert.Equal(0, s2.Parsed);
            Assert.Equal(2, s2.Reused);

            // Touched but same content: re-hashed, not re-parsed.
            File.SetLastWriteTimeUtc(b, DateTime.UtcNow.AddMinutes(1));
            var s3 = new LuaScanStats();
            var d3 = LuaIndexer.Apply(d2, new[] { b }, s3);
            Assert.Equal(1, s3.Unchanged);
            Assert.Equal(0, s3.Parsed);

            // Changed, created, deleted.
            File.WriteAllText(b, "function Fire() end\nfunction Reload() end\n");
            File.SetLastWriteTimeUtc(b, DateTime.UtcNow.AddMinutes(2));
            string c = Write("Game/Ammo.lua", "Ammo = {}\n");
            File.Delete(a);
            var s4 = new LuaScanStats();
            var d4 = LuaIndexer.Apply(d3, new[] { b, c, a, Path.Combine(_dir.Path, "outside.lua") }, s4);
            Assert.Equal(2, s4.Parsed);
            Assert.Equal(1, s4.Removed);
            Assert.Contains(d4.Files.Single(f => f.Path == b).Symbols, x => x.Name == "Reload");
            Assert.Contains(d4.Files, f => f.Path == c);
            Assert.DoesNotContain(d4.Files, f => f.Path == a);

            // Folder deleted: its files drop out.
            Directory.Delete(Path.Combine(Root, "Game"), recursive: true);
            var s5 = new LuaScanStats();
            var d5 = LuaIndexer.Apply(d4, new[] { Path.Combine(Root, "Game") }, s5);
            Assert.Empty(d5.Files);
            Assert.Equal(2, s5.Removed);
        }
    }
}
