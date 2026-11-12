using System;
using System.Collections.Generic;
using UEToVS.Core.Localization;
using UEToVS.Host.Logic.Settings;
using UEToVS.UI.Logic;

namespace UEToVS.Host.Logic.Translation
{
    /// <summary>
    /// Settings window rows of 代码感知 > 注释翻译 and their HostSettings codec. Kept out of HostSettingsSchema (like the
    /// font rows) so the translation feature stays in one place; the schema calls <see cref="AddDescriptors"/>,
    /// <see cref="ToValues"/> and <see cref="TrySet"/>.
    /// </summary>
    public static class TranslationSettingsSchema
    {
        public const string EnabledKey = "translate.enabled";
        public const string DisplayKey = "translate.display";
        public const string ProviderKey = "translate.provider";
        public const string TargetKey = "translate.target";
        public const string GoogleUrlKey = "translate.googleUrl";
        public const string ClearCacheKey = "translate.clearCache";
        public const string ClearCacheAction = "uetovs.translate.clearCache";
        public const string AutoTarget = "auto";

        private const string Group = "注释翻译", GroupEn = "Comment translation";
        private const string Keywords = "translate translation google bing microsoft mymemory 翻译 谷歌 微软 双语 译文 注释";

        public static void AddDescriptors(List<SettingDescriptor> list, string category)
        {
            list.Add(Row(category, EnabledKey, SettingKind.Bool, "翻译 Quick Info 里的注释", "Translate comments in Quick Info",
                "悬停提示里的英文注释在后台翻译，先显示原文，译文到了再补上，不阻塞悬停；翻译过的内容缓存在本机（%LocalAppData%\\UEToVS\\Translation），下次直接显示。注释原文会发给下面选的翻译服务。",
                "English comments in Quick Info are translated in the background: the original shows at once and the translation is added when it arrives. Results are cached locally (%LocalAppData%\\UEToVS\\Translation). The comment text is sent to the service selected below.",
                "true"));
            var display = Row(category, DisplayKey, SettingKind.Choice, "显示方式", "Show",
                "双语对照：每段原文下面跟着译文；只显示译文：译文到了以后替换原文（翻译失败时仍显示原文）。右键“翻译选中内容”也按这个显示。",
                "Bilingual: each paragraph followed by its translation; Translation only: the translation replaces the original once it arrives (the original stays if it fails). Also used by \"Translate Selection\".",
                nameof(TranslationDisplay.Bilingual));
            display.Options = new[]
            {
                new SettingOption(nameof(TranslationDisplay.Bilingual), "双语对照", "Bilingual"),
                new SettingOption(nameof(TranslationDisplay.TranslationOnly), "只显示译文", "Translation only"),
            };
            list.Add(display);
            var provider = Row(category, ProviderKey, SettingKind.Choice, "翻译服务", "Service",
                "都是免费接口，不需要密钥。谷歌翻译质量最好，但需要能访问 translate.googleapis.com（可在下面换成镜像地址，或使用系统代理）；微软翻译用的是 Edge 网页翻译的接口，国内通常可以直接访问；MyMemory 匿名使用每天约 5000 词。",
                "Free services, no key needed. Google gives the best results but needs access to translate.googleapis.com (set a mirror below, or use the system proxy); Microsoft uses the endpoint of Edge's page translation; MyMemory allows about 5000 words a day anonymously.",
                nameof(TranslationProviderKind.Google));
            provider.Options = new[]
            {
                new SettingOption(nameof(TranslationProviderKind.Google), "谷歌翻译", "Google"),
                new SettingOption(nameof(TranslationProviderKind.Microsoft), "微软翻译", "Microsoft"),
                new SettingOption(nameof(TranslationProviderKind.MyMemory), "MyMemory", "MyMemory"),
            };
            list.Add(provider);
            var target = Row(category, TargetKey, SettingKind.Choice, "翻译成", "Translate into",
                "自动：中文界面翻译成简体中文，英文界面只翻译中文注释。", "Auto: Simplified Chinese for a Chinese UI; an English UI only translates Chinese comments.",
                AutoTarget);
            target.Options = new[]
            {
                new SettingOption(AutoTarget, "自动（跟随界面语言）", "Auto (UI language)"),
                new SettingOption("zh-CN", "简体中文", "Simplified Chinese"),
                new SettingOption("zh-TW", "繁體中文", "Traditional Chinese"),
                new SettingOption("en", "English", "English"),
                new SettingOption("ja", "日本語", "Japanese"),
                new SettingOption("ko", "한국어", "Korean"),
            };
            list.Add(target);
            list.Add(Row(category, GoogleUrlKey, SettingKind.Text, "谷歌翻译服务地址", "Google endpoint",
                "留空使用 " + GoogleTranslateProvider.DefaultBaseUrl + "；可以填一个兼容的镜像地址。",
                "Empty = " + GoogleTranslateProvider.DefaultBaseUrl + "; or a compatible mirror.", ""));
            var clear = Row(category, ClearCacheKey, SettingKind.Action, "清空翻译缓存", "Clear translation cache",
                "删除本机缓存的译文（换了翻译服务或觉得译文不对时用）。", "Deletes the locally cached translations.", null);
            clear.ActionId = ClearCacheAction;
            clear.ActionLabelZh = "清空";
            clear.ActionLabelEn = "Clear";
            list.Add(clear);
        }

        private static SettingDescriptor Row(string category, string key, SettingKind kind, string zh, string en, string descZh, string descEn, string def) =>
            new SettingDescriptor
            {
                Category = category, Key = key, Kind = kind, TitleZh = zh, TitleEn = en, DescriptionZh = descZh, DescriptionEn = descEn,
                Default = def, GroupZh = Group, GroupEn = GroupEn, Keywords = Keywords,
            };

        public static void ToValues(HostSettings s, IDictionary<string, string> d)
        {
            d[EnabledKey] = s.CommentTranslation ? "true" : "false";
            d[DisplayKey] = s.TranslationDisplay.ToString();
            d[ProviderKey] = s.TranslationProvider.ToString();
            d[TargetKey] = string.IsNullOrWhiteSpace(s.TranslationTarget) ? AutoTarget : s.TranslationTarget;
            d[GoogleUrlKey] = s.TranslationGoogleUrl ?? "";
        }

        public static bool TrySet(HostSettings s, string key, string v)
        {
            switch (key)
            {
                case EnabledKey: s.CommentTranslation = string.Equals(v, "true", StringComparison.OrdinalIgnoreCase); return true;
                case DisplayKey: s.TranslationDisplay = Enum.TryParse(v, true, out TranslationDisplay d) && Enum.IsDefined(typeof(TranslationDisplay), d) ? d : TranslationDisplay.Bilingual; return true;
                case ProviderKey: s.TranslationProvider = Enum.TryParse(v, true, out TranslationProviderKind p) && Enum.IsDefined(typeof(TranslationProviderKind), p) ? p : TranslationProviderKind.Google; return true;
                case TargetKey: s.TranslationTarget = string.IsNullOrWhiteSpace(v) ? AutoTarget : v.Trim(); return true;
                case GoogleUrlKey: s.TranslationGoogleUrl = (v ?? "").Trim(); return true;
                default: return false;
            }
        }

        /// <summary>HostSettings → effective options (target resolved against the UI language).</summary>
        public static TranslationOptions OptionsOf(HostSettings s) => new TranslationOptions
        {
            Enabled = s.CommentTranslation,
            Display = s.TranslationDisplay,
            Provider = s.TranslationProvider,
            Target = TranslationText.ResolveTarget(s.TranslationTarget, Loc.IsEnglish),
        };
    }
}
