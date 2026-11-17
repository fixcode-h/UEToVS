using System;
using UEToVS.Host.Logic.Debugging;
using Xunit;

namespace UEToVS.Host.Logic.Tests
{
    public class FocusPolicyTests
    {
        private static readonly DateTime T0 = new DateTime(2026, 11, 17, 12, 0, 0, DateTimeKind.Utc);

        private static DateTime Ms(int ms) => T0.AddMilliseconds(ms);

        [Fact]
        public void UserSession_VsComesForwardAsUsual()
        {
            var p = new FocusPolicy();
            Assert.False(p.OnBreak(true, false, Ms(0)));
            Assert.False(p.OnForeground(true, Ms(50)));
        }

        [Fact]
        public void AgentSession_VsActivatedAfterBreak_Restores()
        {
            var p = new FocusPolicy();
            p.OnAgentControl();
            Assert.False(p.OnBreak(true, false, Ms(0)));
            Assert.True(p.OnForeground(true, Ms(80)));
        }

        [Fact]
        public void AgentSession_VsActivatedJustBeforeBreakEvent_RestoresAtOnce()
        {
            var p = new FocusPolicy();
            p.OnAgentControl();
            p.OnForeground(false, Ms(0));
            p.OnForeground(true, Ms(1000));
            Assert.True(p.OnBreak(true, true, Ms(1100)));
        }

        [Fact]
        public void UserAlreadyInVs_NotTouched()
        {
            var p = new FocusPolicy();
            p.OnAgentControl();
            p.OnForeground(true, Ms(0));
            Assert.False(p.OnBreak(true, true, Ms(10_000)));
            Assert.False(p.OnForeground(true, Ms(10_100)));
        }

        [Fact]
        public void SettingOff_NeverRestores()
        {
            var p = new FocusPolicy();
            p.OnAgentControl();
            Assert.False(p.OnBreak(false, false, Ms(0)));
            Assert.False(p.OnForeground(true, Ms(50)));
        }

        [Fact]
        public void LateActivation_IsTheUser()
        {
            var p = new FocusPolicy();
            p.OnAgentControl();
            p.OnBreak(true, false, Ms(0));
            Assert.False(p.OnForeground(true, Ms(5000)));
        }

        [Fact]
        public void RestoresAreCappedPerBreak()
        {
            var p = new FocusPolicy();
            p.OnAgentControl();
            p.OnBreak(true, false, Ms(0));
            for (int i = 0; i < FocusPolicy.MaxRestoresPerBreak; i++)
                Assert.True(p.OnForeground(true, Ms(100 + i * 100)));
            Assert.False(p.OnForeground(true, Ms(900)));
            // 下一次停下重新计数
            p.OnBreak(true, false, Ms(2000));
            Assert.True(p.OnForeground(true, Ms(2100)));
        }

        [Fact]
        public void SessionEnd_ResetsButRestartKeeps()
        {
            var p = new FocusPolicy();
            p.OnRestartRequested();
            p.OnDesign(); // restart 先结束旧会话
            p.OnRun();
            Assert.True(p.AgentSession);
            p.OnDesign(); // 真结束
            Assert.False(p.AgentSession);
            Assert.False(p.OnBreak(true, false, Ms(0)));
        }

        [Fact]
        public void Minimize_OnlyWhenVsWasRestoredAroundTheBreak()
        {
            var p = new FocusPolicy();
            p.OnAgentControl();
            p.OnUnminimized(Ms(0));
            p.OnBreak(true, false, Ms(10_000));
            Assert.False(p.ShouldMinimize(Ms(10_200)));

            p.OnUnminimized(Ms(10_050));
            Assert.True(p.ShouldMinimize(Ms(10_200)));
        }

        [Fact]
        public void AgentControlOps_ExcludeReadOnly()
        {
            Assert.True(DebugProtocol.IsAgentControl(DebugProtocol.Step));
            Assert.True(DebugProtocol.IsAgentControl(DebugProtocol.Wait));
            Assert.False(DebugProtocol.IsAgentControl(DebugProtocol.Status));
            Assert.False(DebugProtocol.IsAgentControl(DebugProtocol.Locals));
            Assert.False(DebugProtocol.IsAgentControl(null));
        }
    }
}
