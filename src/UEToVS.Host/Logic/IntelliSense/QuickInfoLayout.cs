using System;

namespace UEToVS.Host.Logic.IntelliSense
{
    /// <summary>
    /// 悬停提示（Quick Info）的尺寸规则。VS 的提示弹窗不限高，展开 UHT 生成代码、长宏展开或长注释时会超出屏幕，
    /// 下半截既看不到也滚不动；这里给出代码块和整个提示的最大高度，超出部分在框内滚动。
    /// </summary>
    public static class QuickInfoLayout
    {
        /// <summary>单个代码块（宏展开一步、UHT 生成代码）最多占工作区高度的比例：留出声明、注释等上下文。</summary>
        public const double CodeFraction = 0.45;

        /// <summary>整个提示最多占工作区高度的比例：弹窗在悬停行的上方或下方，留一点空间给它摆放。</summary>
        public const double PopupFraction = 0.75;

        public const double CodeMinHeight = 160;
        public const double CodeMaxHeightCap = 560;
        public const double PopupMinHeight = 240;

        /// <summary>代码块的最大高度（DIP）。工作区高度未知（≤0）时按 1080p 估算。</summary>
        public static double CodeMaxHeight(double workAreaHeight)
        {
            double h = Normalize(workAreaHeight) * CodeFraction;
            return Math.Max(CodeMinHeight, Math.Min(CodeMaxHeightCap, h));
        }

        /// <summary>整个提示的最大高度（DIP），不小于 <see cref="PopupMinHeight"/>，也不超过工作区。</summary>
        public static double PopupMaxHeight(double workAreaHeight)
        {
            double area = Normalize(workAreaHeight);
            return Math.Min(area, Math.Max(PopupMinHeight, area * PopupFraction));
        }

        /// <summary>
        /// 内层滚动框已经滚到头时，滚轮要交给外层（整个提示）继续滚，否则鼠标停在代码块上就再也滚不到下面的内容。
        /// <paramref name="delta"/> 为正表示向上滚。
        /// </summary>
        public static bool ShouldChainWheel(int delta, double offset, double scrollableHeight)
        {
            if (scrollableHeight <= 0.5) return true;
            if (delta > 0) return offset <= 0.5;
            if (delta < 0) return offset >= scrollableHeight - 0.5;
            return false;
        }

        private static double Normalize(double workAreaHeight) =>
            double.IsNaN(workAreaHeight) || double.IsInfinity(workAreaHeight) || workAreaHeight <= 0 ? 1040 : workAreaHeight;
    }
}
