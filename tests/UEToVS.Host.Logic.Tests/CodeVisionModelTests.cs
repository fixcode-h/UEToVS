using System;
using System.Collections.Generic;
using System.Linq;
using UEToVS.Contracts;
using UEToVS.Core.Localization;
using UEToVS.Host.Logic.Editing;
using UEToVS.Host.Logic.Navigation;
using UEToVS.Host.Logic.Search;
using Xunit;

namespace UEToVS.Host.Logic.Tests
{
    /// <summary>v0.7.6: clicking a Code Vision count lists the derived types / Blueprints behind it (Rider).</summary>
    public class CodeVisionModelTests : IDisposable
    {
        private readonly UiLanguage _saved = Loc.Language;

        public void Dispose() => Loc.Language = _saved;

        private static CodeVisionBlueprint Bp(string name, string parent, int depth = 0, bool level = false) => new CodeVisionBlueprint
        {
            Asset = new AssetItem { Name = name, FilePath = @"D:\G\Content\" + name + (level ? ".umap" : ".uasset"), ObjectPath = "/Game/" + name + "." + name, ClassName = level ? "World" : "Blueprint" },
            ParentClass = parent, Depth = depth, IsLevel = level,
        };

        [Fact]
        public void PartsAreTheNonZeroCountsInOrder()
        {
            Loc.Language = UiLanguage.Chinese;
            var h = new InlayHint { Kind = InlayHintKind.Usages, SymbolName = "AHero", DerivedTypes = 2, Blueprints = 1 };
            var parts = CodeVisionModel.Parts(h);
            Assert.Equal(new[] { CodeVisionSegment.DerivedTypes, CodeVisionSegment.Blueprints }, parts.Select(p => p.Segment));
            Assert.Equal("2 个派生类 · 1 个蓝图", InlayHintStore.UsagesText(h));
            Assert.Equal(CodeVisionSegment.Blueprints, Assert.Single(CodeVisionModel.Parts(new InlayHint { Blueprints = 3 })).Segment);
            Assert.Empty(CodeVisionModel.Parts(new InlayHint()));
            Assert.Empty(CodeVisionModel.Parts(null));
            Loc.Language = UiLanguage.English;
            Assert.Equal("2 derived types · 1 Blueprint", InlayHintStore.UsagesText(h));
        }

        [Fact]
        public void BlueprintRowsActLikeSearchEverywhereAssets()
        {
            Loc.Language = UiLanguage.Chinese;
            var resp = new CodeVisionItemsResponse
            {
                BlueprintIndexReady = true,
                Blueprints = { Bp("BP_Hero", "MyCharacter"), Bp("BP_HeroElite", "BP_Hero", 1), Bp("BP_HeroBoss", "BP_HeroElite", 2) },
            };
            var rows = CodeVisionModel.Rows(CodeVisionSegment.Blueprints, resp);
            Assert.Equal(3, rows.Count);
            Assert.All(rows, r => Assert.Equal(SearchRowKind.Asset, r.Kind));
            Assert.All(rows, r => Assert.NotNull(r.Asset));
            Assert.Equal("BP_Hero", rows[0].Title);
            Assert.Equal("└ BP_HeroElite", rows[1].Title);
            Assert.Equal("   └ BP_HeroBoss", rows[2].Title);
            Assert.Equal("/Game/BP_Hero.BP_Hero", rows[0].Detail);
            Assert.Equal("← MyCharacter", rows[0].Right);
            Assert.Equal("蓝图", rows[0].Badges.Single().Key);
            // the filter box matches the name, not the tree indent
            Assert.StartsWith("BP_HeroElite ", CodeVisionModel.FilterText(rows[1]));
        }

        [Fact]
        public void MemberUsagesListLevelsToo()
        {
            Loc.Language = UiLanguage.Chinese;
            var resp = new CodeVisionItemsResponse { BlueprintIndexReady = true, Blueprints = { Bp("BP_Hero", null), Bp("L_Main", null, level: true) } };
            var rows = CodeVisionModel.Rows(CodeVisionSegment.Blueprints, resp);
            Assert.Null(rows[0].Right);
            Assert.Equal("关卡", rows[1].Badges.Single().Key);
            Assert.Equal("使用 AHero::Fire 的蓝图和关卡（2）", CodeVisionModel.Title(CodeVisionSegment.Blueprints, "AHero::Fire", 2));
            Assert.Equal("继承 AHero 的蓝图（1）", CodeVisionModel.Title(CodeVisionSegment.Blueprints, "AHero", 1));
            Assert.Equal("AHero 的派生类（2）", CodeVisionModel.Title(CodeVisionSegment.DerivedTypes, "AHero", 2));
            Loc.Language = UiLanguage.English;
            Assert.Equal("Blueprints derived from AHero (1)", CodeVisionModel.Title(CodeVisionSegment.Blueprints, "AHero", 1));
        }

        [Fact]
        public void DerivedTypeRowsJumpToTheDeclaration()
        {
            var s = new SymbolInfo
            {
                Name = "AHeroChild", Kind = SymbolKind.Class, Ue = UeFlags.UClass, Origin = ItemOrigin.Project,
                Location = new SourceLocation { FilePath = @"D:\G\Source\HeroChild.h", Line = 11, Column = 6 },
            };
            var row = Assert.Single(CodeVisionModel.Rows(CodeVisionSegment.DerivedTypes, new CodeVisionItemsResponse { DerivedTypes = { s } }));
            Assert.Equal(SearchRowKind.Type, row.Kind);
            Assert.Equal("AHeroChild", row.Title);
            Assert.Equal(@"D:\G\Source\HeroChild.h", row.FilePath);
            Assert.Equal(11, row.Line);
        }

        [Fact]
        public void EmptyAndCutListsSaySo()
        {
            Loc.Language = UiLanguage.Chinese;
            var loading = CodeVisionModel.Rows(CodeVisionSegment.Blueprints, new CodeVisionItemsResponse { BlueprintIndexReady = false });
            Assert.Equal(SearchRowKind.Hint, Assert.Single(loading).Kind);
            Assert.Contains("还在加载", loading[0].Title);
            var none = CodeVisionModel.Rows(CodeVisionSegment.DerivedTypes, new CodeVisionItemsResponse { BlueprintIndexReady = false });
            Assert.Contains("没有找到", Assert.Single(none).Title);
            var cut = CodeVisionModel.Rows(CodeVisionSegment.Blueprints, new CodeVisionItemsResponse { BlueprintIndexReady = true, Truncated = true, Blueprints = { Bp("A", "X"), Bp("B", "X") } });
            Assert.Equal(3, cut.Count);
            Assert.Equal(SearchRowKind.Hint, cut[2].Kind);
            Assert.False(cut[2].IsSelectable);
            Assert.Empty(CodeVisionModel.Rows(CodeVisionSegment.Blueprints, null));
        }

        [Theory]
        [InlineData("AHero", false)]
        [InlineData("AHero::Fire", true)]
        [InlineData(null, false)]
        public void MemberHintsHaveAContainer(string name, bool member) => Assert.Equal(member, CodeVisionModel.IsMember(name));
    }
}
