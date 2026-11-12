using UEToVS.Core.Input;
using Xunit;

namespace UEToVS.Core.Tests
{
    public class InputTests
    {
        [Fact]
        public void DoubleTapTriggers()
        {
            var d = new DoubleShiftDetector();
            d.OnShiftDown(ShiftSide.Left, 0, false);
            Assert.False(d.OnShiftUp(ShiftSide.Left, 60));
            d.OnShiftDown(ShiftSide.Left, 150, false);
            Assert.True(d.OnShiftUp(ShiftSide.Left, 210));
        }

        [Fact]
        public void TooSlowDoesNotTrigger()
        {
            var d = new DoubleShiftDetector { IntervalMs = 300 };
            d.OnShiftDown(ShiftSide.Left, 0, false);
            d.OnShiftUp(ShiftSide.Left, 50);
            d.OnShiftDown(ShiftSide.Left, 400, false);
            Assert.False(d.OnShiftUp(ShiftSide.Left, 450));
            // ...but that second tap counts as a new first tap
            d.OnShiftDown(ShiftSide.Left, 600, false);
            Assert.True(d.OnShiftUp(ShiftSide.Left, 650));
        }

        [Fact]
        public void TypingUppercaseDoesNotTrigger()
        {
            var d = new DoubleShiftDetector();
            d.OnShiftDown(ShiftSide.Left, 0, false);
            d.OnOtherInput(); // Shift+A
            d.OnShiftUp(ShiftSide.Left, 80);
            d.OnShiftDown(ShiftSide.Left, 150, false);
            Assert.False(d.OnShiftUp(ShiftSide.Left, 200));
        }

        [Fact]
        public void KeyBetweenTapsBreaks()
        {
            var d = new DoubleShiftDetector();
            d.OnShiftDown(ShiftSide.Left, 0, false);
            d.OnShiftUp(ShiftSide.Left, 50);
            d.OnOtherInput();
            d.OnShiftDown(ShiftSide.Left, 120, false);
            Assert.False(d.OnShiftUp(ShiftSide.Left, 170));
        }

        [Fact]
        public void LongHoldIsNotATap()
        {
            var d = new DoubleShiftDetector();
            d.OnShiftDown(ShiftSide.Left, 0, false);
            d.OnShiftUp(ShiftSide.Left, 50);
            d.OnShiftDown(ShiftSide.Left, 100, false);
            Assert.False(d.OnShiftUp(ShiftSide.Left, 900));
        }

        [Fact]
        public void LeftOnlyIgnoresRight()
        {
            var d = new DoubleShiftDetector { LeftShiftOnly = true };
            d.OnShiftDown(ShiftSide.Right, 0, false);
            d.OnShiftUp(ShiftSide.Right, 50);
            d.OnShiftDown(ShiftSide.Right, 100, false);
            Assert.False(d.OnShiftUp(ShiftSide.Right, 150));
        }

        [Fact]
        public void ImeComposingSuppresses()
        {
            var d = new DoubleShiftDetector { IsImeComposing = () => true };
            d.OnShiftDown(ShiftSide.Left, 0, false);
            d.OnShiftUp(ShiftSide.Left, 50);
            d.OnShiftDown(ShiftSide.Left, 100, false);
            Assert.False(d.OnShiftUp(ShiftSide.Left, 150));
        }

        [Fact]
        public void AutoRepeatIgnored()
        {
            var d = new DoubleShiftDetector();
            d.OnShiftDown(ShiftSide.Left, 0, false);
            d.OnShiftDown(ShiftSide.Left, 30, true);
            d.OnShiftUp(ShiftSide.Left, 60);
            d.OnShiftDown(ShiftSide.Left, 120, false);
            Assert.True(d.OnShiftUp(ShiftSide.Left, 170));
        }
    }
}
