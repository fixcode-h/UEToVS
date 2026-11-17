using System;

namespace UEToVS.Host.Logic.Debugging
{
    /// <summary>
    /// 决定 Agent 调试时要不要把前台还给原来的窗口。
    /// <para>VS 调试器一进中断模式（断点、单步停下、pause、异常）就会把自己拉到前台，最小化着也会还原：这是调试器的固定行为，
    /// VS 没有选项关掉，被调试的 UE 在前台时 Windows 也允许它这么做（前台进程正被调试）。Agent 单步一次 VS 就跳出来一次，
    /// 用户正在看面板或者别的窗口时很打扰，所以 Agent 控制的调试会话里，VS 刚被调试器拉起时把前台还回去、最小化着的再最小化回去。</para>
    /// <para>纯逻辑，时间由调用方传入（单测用）；窗口句柄、钩子在 Debugging/ForegroundGuard。线程安全：钩子回调和管道请求都会调。</para>
    /// </summary>
    public sealed class FocusPolicy
    {
        /// <summary>进中断模式后这么久之内 VS 拿到前台，算调试器拉起的（VS 先激活还是先发中断事件不固定，两边都等）。</summary>
        public static readonly TimeSpan Window = TimeSpan.FromSeconds(3);

        /// <summary>中断事件到达时 VS 已经在前台：激活发生在这之内算调试器刚拉起的，更早说明用户本来就在 VS 里。</summary>
        public static readonly TimeSpan JustNow = TimeSpan.FromMilliseconds(1500);

        /// <summary>一次中断最多还几次前台：VS 停下后可能先激活主窗口再激活源码文档，多还一两次；再多就是用户自己点的，不和用户抢。</summary>
        public const int MaxRestoresPerBreak = 3;

        private readonly object _gate = new object();
        private bool _agentSession;
        private bool _keepThroughDesign;
        private DateTime _vsActivatedAt = DateTime.MinValue;
        private DateTime _unminimizedAt = DateTime.MinValue;
        private DateTime? _armedAt;
        private int _restores;

        /// <summary>Agent 这次调试会话里控制过（启动、附加、下断点、继续、单步、等待……）。</summary>
        public bool AgentSession
        {
            get { lock (_gate) return _agentSession; }
        }

        public void OnAgentControl()
        {
            lock (_gate) _agentSession = true;
        }

        /// <summary>restart 会先结束会话（进设计模式）再启动：这次设计模式不算会话结束。</summary>
        public void OnRestartRequested()
        {
            lock (_gate)
            {
                _agentSession = true;
                _keepThroughDesign = true;
            }
        }

        public void OnRun()
        {
            lock (_gate) _keepThroughDesign = false;
        }

        /// <summary>调试结束：之后用户自己按 F5 调试时 VS 照常跳到前台。</summary>
        public void OnDesign()
        {
            lock (_gate)
            {
                _armedAt = null;
                if (_keepThroughDesign) return;
                _agentSession = false;
            }
        }

        /// <summary>前台换了窗口。返回 true 表示 VS 刚被调试器拉到前台，该把前台还回去。</summary>
        public bool OnForeground(bool isVs, DateTime now)
        {
            lock (_gate)
            {
                if (!isVs) return false;
                _vsActivatedAt = now;
                return TakeRestore(now);
            }
        }

        /// <summary>VS 主窗口从最小化还原了。</summary>
        public void OnUnminimized(DateTime now)
        {
            lock (_gate) _unminimizedAt = now;
        }

        /// <summary>
        /// 进中断模式。<paramref name="enabled"/> 是设置开关。返回 true 表示 VS 已经被拉到前台、马上还回去；
        /// false 时如果 VS 还没激活，就在 <see cref="Window"/> 内等 <see cref="OnForeground"/>。
        /// </summary>
        public bool OnBreak(bool enabled, bool vsIsForeground, DateTime now)
        {
            lock (_gate)
            {
                _armedAt = null;
                _restores = 0;
                if (!enabled || !_agentSession) return false;
                if (vsIsForeground && now - _vsActivatedAt > JustNow) return false; // 用户本来就在 VS 里：不动
                _armedAt = now;
                return vsIsForeground && TakeRestore(now);
            }
        }

        /// <summary>还前台时要不要把 VS 再最小化：VS 是在这次中断前后被还原的。</summary>
        public bool ShouldMinimize(DateTime now)
        {
            lock (_gate)
            {
                if (_armedAt == null) return false;
                var at = _armedAt.Value;
                return _unminimizedAt >= at - JustNow && _unminimizedAt <= at + Window && _unminimizedAt <= now;
            }
        }

        private bool TakeRestore(DateTime now)
        {
            if (_armedAt == null || now - _armedAt.Value > Window || _restores >= MaxRestoresPerBreak) return false;
            _restores++;
            return true;
        }
    }
}
