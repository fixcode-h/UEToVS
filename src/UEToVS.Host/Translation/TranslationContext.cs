using System;
using UEToVS.Host.Logic.Translation;
using UEToVS.Host.Navigation;

namespace UEToVS.Host.Translation
{
    /// <summary>
    /// The one <see cref="TranslationService"/> of the VS session (created on first use; settings are read on every
    /// request, so changes apply without restarting). Shared by Quick Info and "翻译选中内容".
    /// </summary>
    internal static class TranslationContext
    {
        private static readonly Lazy<TranslationService> _service = new Lazy<TranslationService>(() =>
            new TranslationService(
                () => TranslationSettingsSchema.OptionsOf(NavigationContext.Settings),
                new TranslationCache(TranslationCache.DefaultFile()),
                TranslationProviders.Create(() => NavigationContext.Settings.TranslationGoogleUrl)));

        public static TranslationService Service => _service.Value;

        public static bool Enabled => NavigationContext.Settings.CommentTranslation;
    }
}
