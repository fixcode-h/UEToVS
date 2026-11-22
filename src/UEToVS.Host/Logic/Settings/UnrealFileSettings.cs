using System;
using System.Collections.Generic;
using UEToVS.UI.Logic;

namespace UEToVS.Host.Logic.Settings
{
    /// <summary>
    /// v0.10：着色器（.usf / .ush）和 UE 配置文件（.ini / .uproject / .uplugin）编辑器功能的设置行和编解码，单独放一处，
    /// HostSettingsSchema 只调 <see cref="AddDescriptors"/>、<see cref="ToValues"/>、<see cref="TrySet"/>。
    /// </summary>
    public static class UnrealFileSettings
    {
        public const string ShaderKey = "shaders.intelliSense";
        public const string ValidationKey = "projectFiles.validation";

        public static void AddDescriptors(List<SettingDescriptor> list, string category)
        {
            const string g = "着色器与配置文件", gEn = "Shaders and config files";
            list.Add(new SettingDescriptor
            {
                Category = category, Key = ShaderKey, Kind = SettingKind.Bool, Default = "true", GroupZh = g, GroupEn = gEn,
                TitleZh = "着色器（.usf / .ush）补全、悬停和转到定义", TitleEn = "Shader (.usf / .ush) completion, Quick Info and Go to Definition",
                DescriptionZh = "HLSL 内建函数和 UE 常用着色器函数 / 宏的补全与说明；#include \"/Engine/…\"、\"/Plugin/<名字>/…\" 虚拟路径补全；文件内和 #include 链上函数、结构体、宏的转到定义（F12 / Ctrl+点击）；文件结构（Ctrl+Shift+Alt+M）。只在打开着色器文件时按需解析，最近用过的文件留在内存里，不进 C++ 索引。",
                DescriptionEn = "Completion and descriptions for HLSL intrinsics and common UE shader functions / macros; completion of #include \"/Engine/…\" and \"/Plugin/<Name>/…\" virtual paths; Go to Definition (F12 / Ctrl+click) for functions, structs and macros in the file and along its #include chain; File Structure (Ctrl+Shift+Alt+M). Parsed on demand while shader files are open, recently used files are kept in memory; not part of the C++ index.",
                Keywords = "hlsl usf ush shader 着色器 include 补全 completion 定义 definition",
            });
            list.Add(new SettingDescriptor
            {
                Category = category, Key = ValidationKey, Kind = SettingKind.Bool, Default = "true", GroupZh = g, GroupEn = gEn,
                TitleZh = "检查 ini / .uproject / .uplugin", TitleEn = "Check ini / .uproject / .uplugin",
                DescriptionZh = "ini：项目里的 config 类（[/Script/模块.类]）下写了类里没有的 config 属性时用提示级波浪线标出；项目模块里找不到的类也会标出。引擎的类常用 GConfig 手动读，不检查。.uproject / .uplugin：Modules 里的模块名找不到对应 Build.cs 时标警告。节名、键名、取值的补全不受这一项影响。",
                DescriptionEn = "ini: keys that are not config properties of a project config class ([/Script/Module.Class]) get a suggestion squiggle, as do classes missing from a project module. Engine classes are not checked (they often read GConfig by hand). .uproject / .uplugin: module names in Modules without a matching Build.cs get a warning. Section, key and value completion is not affected.",
                Keywords = "ini config uproject uplugin 配置 检查 校验 validation squiggle",
            });
        }

        public static void ToValues(HostSettings s, IDictionary<string, string> d)
        {
            d[ShaderKey] = B(s.ShaderIntelliSense);
            d[ValidationKey] = B(s.ConfigFileValidation);
        }

        public static bool TrySet(HostSettings s, string key, string v)
        {
            switch (key)
            {
                case ShaderKey: s.ShaderIntelliSense = PB(v); return true;
                case ValidationKey: s.ConfigFileValidation = PB(v); return true;
                default: return false;
            }
        }

        public static void Describe(HostSettings s, IDictionary<string, object> d)
        {
            d["shaderIntelliSense"] = s.ShaderIntelliSense;
            d["configFileValidation"] = s.ConfigFileValidation;
        }

        private static string B(bool v) => v ? "true" : "false";
        private static bool PB(string v) => string.Equals(v, "true", StringComparison.OrdinalIgnoreCase);
    }
}
