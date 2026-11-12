using UEToVS.Core.Localization;
using UEToVS.Host.Logic.Translation;

namespace UEToVS.Host.Logic.Localization
{
    /// <summary>User-visible strings of the comment translation (Quick Info, "翻译选中内容"). Pure.</summary>
    public static class TranslationStrings
    {
        public static string CmdTranslateSelection => Loc.T("翻译选中内容", "Translate Selection");
        public static string CmdClearCache => Loc.T("清空翻译缓存", "Clear Translation Cache");

        public static readonly string[] KwTranslate = { "translate", "translation", "google", "翻译", "译文", "谷歌", "双语" };

        public static string Translating => Loc.T("翻译中…", "Translating…");
        public static string Failed(string error) => Loc.F("翻译失败：{0}", "Translation failed: {0}", error);
        public static string NothingToTranslate => Loc.T("选中的内容不需要翻译（没有英文句子，或已经是目标语言）。", "Nothing to translate in the selection (no sentences, or already in the target language).");
        public static string NoSelection => Loc.T("先选中要翻译的文字。", "Select the text to translate first.");
        public static string CacheCleared(int entries) => Loc.F("已清空翻译缓存（{0} 条）。", "Translation cache cleared ({0} entries).", entries);
        public static string Disabled => Loc.T("注释翻译已在设置中关闭。", "Comment translation is turned off in the settings.");

        public static string ProviderName(TranslationProviderKind kind)
        {
            switch (kind)
            {
                case TranslationProviderKind.Microsoft: return Loc.T("微软翻译", "Microsoft Translator");
                case TranslationProviderKind.MyMemory: return "MyMemory";
                default: return Loc.T("谷歌翻译", "Google Translate");
            }
        }

        /// <summary>Header of the "翻译选中内容" popup.</summary>
        public static string SelectionHeader(TranslationProviderKind kind) => Loc.F("翻译 · {0}", "Translation · {0}", ProviderName(kind));
    }
}
