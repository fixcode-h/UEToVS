using System;
using UEToVS.Core.Diagnostics;
using Xunit;

namespace UEToVS.Core.Tests
{
    public class UnobservedExceptionTests
    {
        private sealed class StackException : Exception
        {
            private readonly string _stack;
            public StackException(string stack, Exception inner = null) : base("x", inner) { _stack = stack; }
            public override string StackTrace => _stack;
        }

        [Fact]
        public void OwnFramesAreOursForeignFramesAreNot()
        {
            // the VS 18.6 package: JTF WithTimeout, VS telemetry, Nerdbank pipes — none of it UEToVS
            var vs = new AggregateException(new StackException("   在 Microsoft.VisualStudio.Threading.TplExtensions.<WithTimeout>d__5.MoveNext()"));
            Assert.False(Log.IsOwnException(vs));

            var ours = new AggregateException(new StackException("   at Nerdbank.Streams.X()",
                new StackException("   at UEToVS.Host.Engine.EngineClient.InvokeAsync()")));
            Assert.True(Log.IsOwnException(ours));

            // no stack at all: treated as ours so nothing real is hidden
            Assert.True(Log.IsOwnException(new AggregateException(new TimeoutException())));
        }
    }
}
