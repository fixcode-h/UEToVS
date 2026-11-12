using System;
using System.Linq;
using System.Threading.Tasks;
using UEToVS.Host.Logic.Actions;
using Xunit;

namespace UEToVS.Host.Logic.Tests
{
    public class ActionRegistryTests
    {
        private static ActionRegistry Build(Action<string> executed = null)
        {
            var r = new ActionRegistry();
            r.Register(new DelegateAction("uetovs.diagnostics.openLogFolder", "Open Log Folder", ActionCategory.Diagnostics,
                c => { executed?.Invoke("log"); return Task.CompletedTask; }, null, null, "日志", "logs"));
            r.Register(new DelegateAction("uetovs.engine.restart", "Restart Engine", ActionCategory.Unreal,
                c => { executed?.Invoke("restart"); return Task.CompletedTask; }, c => c.IsUnrealSolution, c => c.EngineAvailable ? 0 : 10, "引擎"));
            r.Register(new DelegateAction("uetovs.intellisense.restore", "Restore Visual Studio C++ IntelliSense", ActionCategory.Settings,
                c => Task.CompletedTask, null, c => 5, "恢复"));
            r.Register(new DelegateAction("uetovs.broken", "Broken Thing", ActionCategory.Fix,
                c => throw new InvalidOperationException("boom"), c => throw new Exception("isAvailable boom")));
            r.Register(new DelegateAction("uetovs.fails", "Fails On Execute", ActionCategory.Fix,
                c => Task.FromException(new InvalidOperationException("boom"))));
            return r;
        }

        [Fact]
        public void DuplicateIdsAreRejected()
        {
            var r = Build();
            Assert.Throws<InvalidOperationException>(() => r.Register(new DelegateAction("UETOVS.ENGINE.RESTART", "x", ActionCategory.Unreal, c => Task.CompletedTask)));
            Assert.NotNull(r.Get("UEToVS.Engine.Restart"));
            Assert.True(r.Unregister("uetovs.fails"));
            Assert.Null(r.Get("uetovs.fails"));
        }

        [Fact]
        public void EmptyQueryListsAvailableActionsByPriority()
        {
            var r = Build();
            var ue = r.Query("", new ActionContext { IsUnrealSolution = true });
            Assert.Equal("uetovs.engine.restart", ue[0].Action.Id); // priority 10
            Assert.Equal("uetovs.intellisense.restore", ue[1].Action.Id); // priority 5
            Assert.DoesNotContain(ue, m => m.Action.Id == "uetovs.broken"); // throwing IsAvailable → unavailable

            var plain = r.Query("", new ActionContext { IsUnrealSolution = false });
            Assert.DoesNotContain(plain, m => m.Action.Id == "uetovs.engine.restart");
        }

        [Fact]
        public void FuzzyMatchesTitleIdAndKeywords()
        {
            var r = Build();
            var ctx = new ActionContext { IsUnrealSolution = true };
            Assert.Equal("uetovs.engine.restart", r.Query("RE", ctx)[0].Action.Id); // camel humps on the title
            Assert.Equal("uetovs.diagnostics.openLogFolder", r.Query("日志", ctx).Single().Action.Id); // keyword
            Assert.Equal("uetovs.intellisense.restore", r.Query("恢复", ctx).Single().Action.Id);
            Assert.Contains(r.Query("intellisense", ctx), m => m.Action.Id == "uetovs.intellisense.restore");
            Assert.Empty(r.Query("zzzz", ctx));
            Assert.Single(r.Query("", ctx, max: 1));
            Assert.All(r.Query("", ctx, category: ActionCategory.Diagnostics), m => Assert.Equal(ActionCategory.Diagnostics, m.Action.Category));
        }

        [Fact]
        public async Task ExecuteLogsAndNeverThrows()
        {
            string ran = null;
            var r = Build(x => ran = x);
            Assert.True(await r.ExecuteAsync("uetovs.diagnostics.openLogFolder", new ActionContext()));
            Assert.Equal("log", ran);
            Assert.False(await r.ExecuteAsync("uetovs.engine.restart", new ActionContext { IsUnrealSolution = false })); // unavailable
            Assert.False(await r.ExecuteAsync("nope", null));
            Assert.False(await r.ExecuteAsync("uetovs.fails", new ActionContext()));
            Assert.False(await r.ExecuteAsync("uetovs.broken", new ActionContext()));
        }
    }
}
