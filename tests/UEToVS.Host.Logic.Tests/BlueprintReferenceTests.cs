using System;
using System.Linq;
using UEToVS.Contracts;
using UEToVS.Core.Localization;
using UEToVS.Host.Logic.Localization;
using UEToVS.Host.Logic.Navigation;
using Xunit;

namespace UEToVS.Host.Logic.Tests
{
    /// <summary>v0.7.3: Blueprint hits from the offline Blueprint index in the Find References view.</summary>
    public class BlueprintReferenceTests : IDisposable
    {
        private readonly UiLanguage _saved = Loc.Language;

        public void Dispose() => Loc.Language = _saved;

        private static ReferenceHit Code(string file, int line) => new ReferenceHit
        {
            Location = new SourceLocation { FilePath = file, Line = line, Column = 4 }, Length = 4, Kind = ReferenceKind.Call, LineText = "    Fire();",
            LineTextColumn = 4, Confidence = ResolutionConfidence.Exact, Origin = ItemOrigin.Project,
        };

        private static ReferenceHit Blueprint(string file, ReferenceKind kind, ResolutionConfidence c) => new ReferenceHit
        {
            Location = new SourceLocation { FilePath = file, Line = 0, Column = 0 }, Kind = kind | ReferenceKind.Blueprint,
            LineText = "Blueprint uses Fire", Context = "/Game/BP_Hero.BP_Hero", Confidence = c, Origin = ItemOrigin.Project,
        };

        [Fact]
        public void BlueprintGroupsComeAfterTheCode()
        {
            var view = ReferenceResults.Build(new[]
            {
                Blueprint(@"D:\G\Content\BP_Hero.uasset", ReferenceKind.Call, ResolutionConfidence.Scoped),
                Code(@"D:\G\Source\Z.cpp", 3),
                Blueprint(@"D:\G\Content\L_Main.umap", ReferenceKind.Read, ResolutionConfidence.Scoped),
                Code(@"D:\G\Source\A.cpp", 9),
            }, new ReferenceFilter());
            Assert.Equal(new[] { "A.cpp", "Z.cpp", "BP_Hero.uasset", "L_Main.umap" }, view.Groups.Select(g => g.FileName));
            Assert.Equal(4, view.Shown);
        }

        [Fact]
        public void BlueprintRowsSayWhatTheyAre()
        {
            Loc.Language = UiLanguage.Chinese;
            var use = Blueprint(@"D:\G\Content\BP_Hero.uasset", ReferenceKind.Call, ResolutionConfidence.Scoped);
            Assert.Equal("蓝图 BP_Hero 中使用", ReferenceResults.DisplayLine(use, out _, out int len));
            Assert.Equal(0, len);
            Assert.Equal("蓝图", ReferenceResults.KindText(use.Kind));
            Assert.Equal("关卡 L_Main 中使用", ReferenceResults.DisplayLine(Blueprint(@"D:\G\Content\L_Main.umap", ReferenceKind.Read, ResolutionConfidence.Scoped), out _, out _));
            Assert.Equal("蓝图 B_Child 继承自它", ReferenceResults.DisplayLine(Blueprint(@"D:\G\Content\B_Child.uasset", ReferenceKind.TypeUse, ResolutionConfidence.Exact), out _, out _));
            Loc.Language = UiLanguage.English;
            Assert.Equal("Used in Blueprint BP_Hero", ReferenceResults.DisplayLine(use, out _, out _));
            Assert.Equal("BP_Hero (Blueprint)", NavStrings.BlueprintNode("BP_Hero"));

            Assert.True(ReferenceResults.IsBlueprint(use));
            Assert.False(ReferenceResults.IsBlueprint(Code("a.cpp", 1)));
            Assert.True(ReferenceResults.IsAssetPath("x.UMAP"));
            Assert.False(ReferenceResults.IsAssetPath("x.cpp"));
            // name-only Blueprint hits are hidden with the other uncertain matches
            var nameOnly = Blueprint(@"D:\G\Content\BP_X.uasset", ReferenceKind.TypeUse, ResolutionConfidence.NameOnly);
            Assert.False(ReferenceResults.Passes(nameOnly, new ReferenceFilter { ShowUncertain = false }));
        }
    }
}
