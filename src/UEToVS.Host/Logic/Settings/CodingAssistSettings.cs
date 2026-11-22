using System;
using System.Collections.Generic;
using UEToVS.UI.Logic;

namespace UEToVS.Host.Logic.Settings
{
    /// <summary>
    /// v0.9.12 写代码辅助的设置行（输入即纠正 . / -&gt;）和它们的 HostSettings 编解码，单独成文件，
    /// 和 <see cref="EditorFeatureSettings"/> 一样由 HostSettingsSchema 调 <see cref="AddDescriptors"/> / <see cref="ToValues"/> /
    /// <see cref="TrySet"/>，免得几个功能同时改 HostSettingsSchema 的同一段。
    /// </summary>
    public static class CodingAssistSettings
    {
        public const string AccessorFixOnTypeKey = "completion.accessorFixOnType";

        private static SettingDescriptor D(string category, string key, SettingKind kind, string zh, string en, string descZh, string descEn, string def,
                                           string groupZh, string groupEn, string keywords) =>
            new SettingDescriptor
            {
                Category = category, Key = key, Kind = kind, TitleZh = zh, TitleEn = en, DescriptionZh = descZh, DescriptionEn = descEn,
                Default = def, GroupZh = groupZh, GroupEn = groupEn, Keywords = keywords,
            };

        public static void AddDescriptors(List<SettingDescriptor> list, string completionCategory)
        {
            list.Add(D(completionCategory, AccessorFixOnTypeKey, SettingKind.Bool, "输入时立即修正 . 和 ->", "Fix . / -> as you type",
                "对指针（T*、TObjectPtr）打 . 时马上换成 ->，对普通对象打 -> 时换回 .，然后照常弹出成员列表；只在能确定变量类型时才改，Ctrl+Z 恢复原来的字符。TSharedPtr、TWeakObjectPtr 等智能指针的 . 和 -> 都合法，不改。需要先打开“提交时自动修正 . 和 ->”。",
                "Typing . on a pointer (T*, TObjectPtr) turns it into -> right away, and -> on a plain object back into ., then the member list opens as usual. Only when the variable's type is certain; Ctrl+Z restores the typed character. Smart pointers such as TSharedPtr and TWeakObjectPtr accept both and are left alone. Needs \"Fix . / -> on commit\".",
                "true", "代码补全", "Completion", "accessor arrow dot pointer 指针 箭头 点 纠正 va"));
        }

        public static void ToValues(HostSettings s, IDictionary<string, string> d)
        {
            d[AccessorFixOnTypeKey] = B(s.CompletionAccessorFixOnType);
        }

        /// <summary>Sets one value; false when <paramref name="key"/> is not one of these rows.</summary>
        public static bool TrySet(HostSettings s, string key, string v)
        {
            switch (key)
            {
                case AccessorFixOnTypeKey: s.CompletionAccessorFixOnType = PB(v); return true;
                default: return false;
            }
        }

        /// <summary>Flat values for the session header / diagnostics package.</summary>
        public static void Describe(HostSettings s, IDictionary<string, object> d)
        {
            d["accessorFixOnType"] = s.CompletionAccessorFixOnType;
        }

        private static string B(bool v) => v ? "true" : "false";
        private static bool PB(string v) => string.Equals(v, "true", StringComparison.OrdinalIgnoreCase);
    }
}
