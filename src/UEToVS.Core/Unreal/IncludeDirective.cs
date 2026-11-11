using System.Text.RegularExpressions;

namespace UEToVS.Core.Unreal
{
    public static class IncludeDirective
    {
        private static readonly Regex Include = new Regex(@"^\s*#\s*include\s*[""<](?<path>[^"">]+)["">]", RegexOptions.Compiled);

        /// <summary>Returns the include path of a line like '#include "GameplayTagContainer.h"', or null.</summary>
        public static string Parse(string line)
        {
            var m = Include.Match(line ?? string.Empty);
            return m.Success ? m.Groups["path"].Value.Replace('\\', '/') : null;
        }

        /// <summary>
        /// Does <paramref name="filePath"/> (absolute) satisfy the include <paramref name="includePath"/>?
        /// "GameFramework/Actor.h" matches ".../Classes/GameFramework/Actor.h".
        /// </summary>
        public static bool Matches(string filePath, string includePath)
        {
            string f = filePath.Replace('\\', '/');
            string inc = includePath.TrimStart('/');
            return f.EndsWith("/" + inc, System.StringComparison.OrdinalIgnoreCase)
                || string.Equals(f, inc, System.StringComparison.OrdinalIgnoreCase);
        }
    }
}
