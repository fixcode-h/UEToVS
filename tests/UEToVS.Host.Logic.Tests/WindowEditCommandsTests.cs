using System;
using UEToVS.Host.Logic.Shell;
using Xunit;

namespace UEToVS.Host.Logic.Tests
{
    public class WindowEditCommandsTests
    {
        [Theory]
        [InlineData(15u, WindowEditCommand.Copy)]
        [InlineData(16u, WindowEditCommand.Cut)]
        [InlineData(26u, WindowEditCommand.Paste)]
        [InlineData(43u, WindowEditCommand.Undo)]
        [InlineData(44u, WindowEditCommand.Undo)]
        [InlineData(29u, WindowEditCommand.Redo)]
        [InlineData(30u, WindowEditCommand.Redo)]
        [InlineData(31u, WindowEditCommand.SelectAll)]
        [InlineData(17u, WindowEditCommand.Delete)]
        [InlineData(220u, WindowEditCommand.None)]
        public void MapsVs97EditCommands(uint id, WindowEditCommand expected) =>
            Assert.Equal(expected, WindowEditCommands.Map(WindowEditCommands.VsStd97, id));

        [Fact]
        public void OtherGroupsAreNotMapped() =>
            Assert.Equal(WindowEditCommand.None, WindowEditCommands.Map(Guid.NewGuid(), 26));
    }
}
