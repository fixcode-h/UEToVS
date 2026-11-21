namespace UEToVS.Host.Logic.Settings
{
    /// <summary>
    /// Which completion items UEToVS contributes (M3, §7.5/§7.6). VS's own C++ completion keeps running until the takeover,
    /// so in that phase we only add what VS cannot do, to avoid listing every symbol twice.
    /// </summary>
    public enum CompletionScope
    {
        /// <summary>Everything once VS's C++ IntelliSense is taken over; UE enhancements only before that (default).</summary>
        Auto = 0,

        /// <summary>Only reflection specifiers, #include paths, overrides and UE-reflected members (VS lists the rest).</summary>
        UeOnly = 1,

        /// <summary>Always the full list (both lists show up while VS's IntelliSense is still on).</summary>
        All = 2,
    }

    /// <summary>How the letter case typed in the completion filter counts (v0.9.9, Rider "match case").</summary>
    public enum CompletionCaseMode
    {
        /// <summary>Everything is listed; items whose case matches what was typed come first (first letter weighs most).</summary>
        Prefer = 0,

        /// <summary>Only items whose first letter has the typed case (Rider "first letter only").</summary>
        FirstLetter = 1,

        /// <summary>Only items whose every matched letter has the typed case.</summary>
        All = 2,

        /// <summary>Case plays no part in the order.</summary>
        Ignore = 3,
    }
}
