using System;
using System.Collections.Generic;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Shell.Interop;
using Microsoft.Win32;
using UEToVS.Core.Diagnostics;
using UEToVS.Host.Logic.Fonts;

namespace UEToVS.Host.Fonts
{
    /// <summary>
    /// 类别的 GUID：自带类别用写死的；<see cref="VsFontCategory.RegisteredName"/> 的类别（VS 的 Unreal Engine 日志窗口）GUID
    /// 不公开、各版本也可能变，到 VS 配置注册表的 FontAndColors\&lt;类别名&gt; 下按名字找它的 Category 值。一个 VS 会话查一次。
    /// 找不到（没装 VS 的 UE 工具）返回 null，这一行就不写，并把 FontAndColors 下的全部类别名记进日志，方便对名字。
    /// </summary>
    internal static class VsFontCategoryGuids
    {
        private static readonly Logger L = Log.For("Fonts");
        private static readonly Dictionary<string, Guid?> Cache = new Dictionary<string, Guid?>(StringComparer.OrdinalIgnoreCase);

        public static Guid? Of(VsFontCategory c)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            if (c.RegisteredName == null) return c.Guid;
            if (Cache.TryGetValue(c.Id, out var known)) return known;
            Guid? found = null;
            var names = new List<string>();
            try
            {
                using (var root = VSRegistry.RegistryRoot(__VsLocalRegistryType.RegType_Configuration))
                using (var fc = root?.OpenSubKey("FontAndColors"))
                {
                    if (fc != null)
                        foreach (var name in fc.GetSubKeyNames())
                        {
                            names.Add(name);
                            if (found != null || !c.RegisteredName(name.Replace(" ", "").ToLowerInvariant())) continue;
                            using (var k = fc.OpenSubKey(name))
                                if (Guid.TryParse(k?.GetValue("Category") as string, out var g)) found = g;
                        }
                }
            }
            catch (Exception ex) when (ex is System.Security.SecurityException || ex is UnauthorizedAccessException || ex is System.IO.IOException || ex is InvalidOperationException)
            {
                L.Warn("font.vs.category.lookup", "读 VS 配置注册表的 FontAndColors 失败", ex, ("category", c.Id));
            }
            Cache[c.Id] = found;
            if (found != null) L.Info("font.vs.category.found", null, ("category", c.Id), ("guid", found.Value.ToString("B")));
            else L.Info("font.vs.category.missing", "VS 里没有这个字体类别（没装对应组件），这一行不写", ("category", c.Id), ("registered", names));
            return found;
        }
    }
}
