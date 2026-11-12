namespace UEToVS.UI.Logic
{
    /// <summary>
    /// Spacing of a settings page (VS 2026 / Fluent settings layout), in DIPs before the spacing scale:
    /// page title, then groups; a group header has more space above than below; cards in a group are 4 apart and
    /// groups are 24 apart. Kept WPF-free so the rhythm is unit-tested and shared by every page.
    /// </summary>
    public static class SettingsPageLayout
    {
        public const double PagePaddingX = 32;
        public const double PagePaddingTop = 20;
        public const double PagePaddingBottom = 28;
        /// <summary>Space under the page title before the first group header.</summary>
        public const double TitleGapBelow = 12;
        public const double CardGap = 4;
        public const double GroupGap = 24;
        public const double GroupHeaderGapBelow = 8;
        /// <summary>Most a card stretches; wider windows keep the cards readable and left-aligned.</summary>
        public const double MaxContentWidth = 960;
        /// <summary>Footer: separator line + this vertical padding; buttons share one minimum width.</summary>
        public const double FooterPaddingY = 12;
        public const double FooterButtonMinWidth = 88;
        public const double FooterButtonGap = 8;

        /// <summary>Top margin of a group header: none for the first group on the page, <see cref="GroupGap"/> otherwise
        /// (the previous card's own 4px bottom margin is subtracted so the visible gap is exactly 24).</summary>
        public static double GroupHeaderTop(bool firstOnPage) => firstOnPage ? 0 : GroupGap - CardGap;

        /// <summary>Top margin of a category title in search results (several categories on one page).</summary>
        public static double SearchCategoryTop(bool firstOnPage) => firstOnPage ? 0 : GroupGap + 8 - CardGap;
    }
}
