using System;
using System.Globalization;

namespace UEToVS.Core.Localization
{
    public enum UiLanguage
    {
        Chinese = 0,
        English = 1,
    }

    /// <summary>
    /// UI language for every user-visible string. Rule (user decision): follow Visual Studio's own UI language —
    /// English when VS is English, Chinese for any other language. The host sets it once at startup from the VS UI
    /// locale (IUIHostLocale / DTE.LocaleID) via <see cref="InitializeFromCulture"/>; the engine receives it in the
    /// handshake if it ever produces user-facing text.
    ///
    /// Usage: keep user-facing strings of a module together in one <c>Strings</c> class:
    /// <code>internal static class Strings { public static string OpenLog => Loc.T("打开日志", "Open Log"); }</code>
    /// Log messages (evt/msg in log files) stay English and are not localized.
    /// </summary>
    public static class Loc
    {
        private static volatile UiLanguage _language = FromCulture(CultureInfo.CurrentUICulture);

        public static UiLanguage Language
        {
            get => _language;
            set
            {
                if (_language == value) return;
                _language = value;
                LanguageChanged?.Invoke();
            }
        }

        /// <summary>Raised when <see cref="Language"/> changes (UI rebuilds its texts).</summary>
        public static event Action LanguageChanged;

        public static bool IsEnglish => _language == UiLanguage.English;

        /// <summary>Chinese text, English text → the one for the current language.</summary>
        public static string T(string zh, string en) => _language == UiLanguage.English ? en : zh;

        /// <summary>Formatted variant: <c>Loc.F("已索引 {0} 个文件", "Indexed {0} files", n)</c>.</summary>
        public static string F(string zh, string en, params object[] args) =>
            string.Format(CultureInfo.CurrentCulture, T(zh, en), args);

        public static UiLanguage FromCulture(CultureInfo culture)
        {
            if (culture == null) return UiLanguage.Chinese;
            return culture.TwoLetterISOLanguageName.Equals("en", StringComparison.OrdinalIgnoreCase)
                ? UiLanguage.English
                : UiLanguage.Chinese;
        }

        /// <summary>From a Windows LCID (e.g. 1033 = en-US, 2052 = zh-CN, 1041 = ja-JP → Chinese).</summary>
        public static UiLanguage FromLcid(int lcid)
        {
            try { return FromCulture(new CultureInfo(lcid)); }
            catch { return UiLanguage.Chinese; }
        }

        public static void InitializeFromCulture(CultureInfo vsUiCulture) => Language = FromCulture(vsUiCulture);

        public static void InitializeFromLcid(int lcid) => Language = FromLcid(lcid);
    }
}
