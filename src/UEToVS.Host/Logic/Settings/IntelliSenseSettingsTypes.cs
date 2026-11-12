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
}
