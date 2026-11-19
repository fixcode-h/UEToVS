using System;
using System.Collections.Generic;
using System.Linq;
using UEToVS.Host.Logic.Settings;
using UEToVS.Host.Logic.Editing;
using Xunit;

namespace UEToVS.Host.Logic.Tests
{
    public class EngineReadOnlyPolicyTests
    {
        private const string Root = @"D:\UE_5.4";

        [Theory]
        [InlineData(@"D:\UE_5.4\Engine\Source\Runtime\Engine\Classes\GameFramework\Actor.h", true)]
        [InlineData(@"d:/ue_5.4/engine/plugins/Foo/Source/Foo.cpp", true)]
        [InlineData(@"D:\UE_5.4\Engine\Shaders\Private\Common.ush", true)]
        [InlineData(@"D:\UE_5.4\MyGame\Source\MyGame\MyActor.cpp", false)]
        [InlineData(@"D:\UE_5.4\EngineExtras\Foo.h", false)]
        [InlineData(@"D:\UE_5.4\Engine", false)]
        [InlineData(@"E:\Projects\MyGame\Source\MyGame.h", false)]
        [InlineData(null, false)]
        public void IsEngineFile_UnderEngineDir(string path, bool expected)
        {
            Assert.Equal(expected, EngineReadOnlyPolicy.IsEngineFile(path, Root));
        }

        [Theory]
        [InlineData(@"D:\UE_5.4")]
        [InlineData(@"D:\UE_5.4\")]
        [InlineData(@"D:/UE_5.4/Engine")]
        [InlineData(@"D:\UE_5.4\Engine\")]
        public void EngineRoot_AcceptsRootOrEngineDir(string root)
        {
            Assert.Equal(@"D:\UE_5.4\Engine\", EngineReadOnlyPolicy.EngineDirPrefix(root));
            Assert.True(EngineReadOnlyPolicy.IsEngineFile(@"D:\UE_5.4\Engine\Source\A.h", root));
        }

        [Fact]
        public void NoEngineRoot_NeverLocks()
        {
            Assert.Null(EngineReadOnlyPolicy.EngineDirPrefix(null));
            Assert.Null(EngineReadOnlyPolicy.EngineDirPrefix("  "));
            Assert.False(EngineReadOnlyPolicy.ShouldLock(true, true, false, @"D:\UE_5.4\Engine\Source\A.h", null, null));
        }

        [Fact]
        public void ShouldLock_RespectsSwitchSolutionKindAndSessionUnlocks()
        {
            const string file = @"D:\UE_5.4\Engine\Source\A.h";
            var unlocked = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            Assert.True(EngineReadOnlyPolicy.ShouldLock(true, true, false, file, Root, unlocked));
            Assert.False(EngineReadOnlyPolicy.ShouldLock(false, true, false, file, Root, unlocked));
            Assert.False(EngineReadOnlyPolicy.ShouldLock(true, false, false, file, Root, unlocked));
            Assert.False(EngineReadOnlyPolicy.ShouldLock(true, true, true, file, Root, unlocked));
            unlocked.Add(EngineReadOnlyPolicy.Key(@"d:/ue_5.4/engine/source/a.h"));
            Assert.False(EngineReadOnlyPolicy.ShouldLock(true, true, false, file, Root, unlocked));
        }

        [Fact]
        public void RelativeToEngine_ForLogs()
        {
            Assert.Equal(@"Engine\Source\A.h", EngineReadOnlyPolicy.RelativeToEngine(@"D:\UE_5.4\Engine\Source\A.h", Root));
            Assert.Equal(@"E:\X.h", EngineReadOnlyPolicy.RelativeToEngine("E:/X.h", Root));
        }
    
        [Fact]
        public void Setting_DefaultOn_OnEditingPage_RoundTrip()
        {
            var schema = HostSettingsSchema.Build(8);
            var d = schema.Single(x => x.Key == "editor.engineReadOnly");
            Assert.Equal("true", d.Default);
            Assert.Equal(HostSettingsSchema.Editing, d.Category);
            Assert.True(new HostSettings().EngineReadOnly);
            var s = HostSettingsSchema.Codec.FromValues(new Dictionary<string, string> { ["editor.engineReadOnly"] = "false" }, schema, new HostSettings());
            Assert.False(s.EngineReadOnly);
            Assert.Equal("false", HostSettingsSchema.Codec.ToValues(s)["editor.engineReadOnly"]);
        }
    }
}
