using System;
using System.ComponentModel;
using System.Reflection;

namespace UEToVS.Host.Logic.Localization
{
    /// <summary>Resolves a <see cref="Strings"/> property by name (attributes need constant arguments).</summary>
    public static class StringKeys
    {
        /// <summary>Value of the static string property <paramref name="key"/> on <see cref="Strings"/>, or the key itself.</summary>
        public static string Resolve(string key)
        {
            if (string.IsNullOrEmpty(key)) return key;
            var p = typeof(Strings).GetProperty(key, BindingFlags.Public | BindingFlags.Static);
            return p != null && p.PropertyType == typeof(string) ? (string)p.GetValue(null) ?? key : key;
        }

        public static bool Exists(string key) =>
            typeof(Strings).GetProperty(key ?? string.Empty, BindingFlags.Public | BindingFlags.Static)?.PropertyType == typeof(string);
    }

    /// <summary>Localized property-grid category (options pages). Argument: a <see cref="Strings"/> property name.</summary>
    [AttributeUsage(AttributeTargets.Property)]
    public sealed class UiCategoryAttribute : CategoryAttribute
    {
        public UiCategoryAttribute(string key) : base(key) { }

        protected override string GetLocalizedString(string value) => StringKeys.Resolve(value);
    }

    /// <summary>Localized property-grid display name. Argument: a <see cref="Strings"/> property name.</summary>
    [AttributeUsage(AttributeTargets.Property)]
    public sealed class UiDisplayNameAttribute : DisplayNameAttribute
    {
        public UiDisplayNameAttribute(string key) : base(key) => Key = key;

        public string Key { get; }

        public override string DisplayName => StringKeys.Resolve(Key);
    }

    /// <summary>Localized property-grid description. Argument: a <see cref="Strings"/> property name.</summary>
    [AttributeUsage(AttributeTargets.Property)]
    public sealed class UiDescriptionAttribute : DescriptionAttribute
    {
        public UiDescriptionAttribute(string key) : base(key) => Key = key;

        public string Key { get; }

        public override string Description => StringKeys.Resolve(Key);
    }
}
