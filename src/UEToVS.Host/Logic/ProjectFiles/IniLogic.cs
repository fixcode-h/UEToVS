using System;
using System.Collections.Generic;

namespace UEToVS.Host.Logic.ProjectFiles
{
    /// <summary>ini 里的一个键（<c>+Key=…</c> 的 Key），列从 0 起。</summary>
    public sealed class IniKey
    {
        public string Name { get; set; }
        public int Line { get; set; }
        public int Start { get; set; }
        public int Length { get; set; }

        /// <summary>'+' '-' '.' '!' 或 '\0'。</summary>
        public char Op { get; set; }
    }

    public sealed class IniSection
    {
        /// <summary>方括号里的文本，如 "/Script/Engine.Engine"。</summary>
        public string Name { get; set; }
        public int Line { get; set; }
        public int Start { get; set; }
        public int Length { get; set; }

        /// <summary>[/Script/Module.Class] 时的模块和类（不带 U / A 前缀），否则 null。</summary>
        public string Module { get; set; }
        public string Class { get; set; }

        public List<IniKey> Keys { get; } = new List<IniKey>();
    }

    /// <summary>值的类型提示（补全说明、悬停）。</summary>
    public sealed class IniValueHint
    {
        /// <summary>C++ 类型，如 "bool"、"TArray&lt;FString&gt;"；不知道时为 null。</summary>
        public string Type { get; set; }
        public string HintZh { get; set; }
        public string HintEn { get; set; }

        /// <summary>固定的取值（bool → True / False），没有时为空。</summary>
        public IReadOnlyList<string> Values { get; set; } = Array.Empty<string>();

        public string Hint(bool english) => english ? HintEn : HintZh;
    }

    /// <summary>
    /// UE ini 的纯逻辑：分节和键、节名补全的位置、config 类的判断、按 C++ 类型给出值的写法提示、未知键的判定。
    /// 解析是逐行的（ini 一般不大），只在打开 / 编辑 ini、补全时按需跑。
    /// </summary>
    public static class IniLogic
    {
        /// <summary>不是 /Script/ 的常见节（补全时放在前面）。</summary>
        public static readonly IReadOnlyList<string> CommonSections = new[]
        {
            "/Script/Engine.Engine", "/Script/EngineSettings.GameMapsSettings", "/Script/EngineSettings.GeneralProjectSettings",
            "/Script/Engine.RendererSettings", "/Script/Engine.InputSettings", "/Script/Engine.CollisionProfile", "/Script/Engine.PhysicsSettings",
            "/Script/Engine.GarbageCollectionSettings", "/Script/Engine.StreamingSettings", "/Script/Engine.NetworkSettings",
            "/Script/Engine.GameNetworkManager", "/Script/Engine.AudioSettings", "/Script/UnrealEd.ProjectPackagingSettings",
            "/Script/UnrealEd.CookerSettings", "/Script/WindowsTargetPlatform.WindowsTargetSettings",
            "/Script/AndroidRuntimeSettings.AndroidRuntimeSettings", "/Script/IOSRuntimeSettings.IOSRuntimeSettings",
            "/Script/GameplayTags.GameplayTagsSettings", "/Script/EnhancedInput.EnhancedInputDeveloperSettings",
            "Core.Log", "Core.System", "SystemSettings", "ConsoleVariables", "CoreRedirects", "OnlineSubsystem", "URL", "DevOptions.Shaders",
        };

        public static IReadOnlyList<IniSection> Parse(IReadOnlyList<string> lines)
        {
            var list = new List<IniSection>();
            IniSection cur = null;
            for (int n = 0; n < lines.Count; n++)
            {
                string line = lines[n] ?? string.Empty;
                int i = 0;
                while (i < line.Length && char.IsWhiteSpace(line[i])) i++;
                if (i >= line.Length || line[i] == ';' || line[i] == '#') continue;
                if (line[i] == '[')
                {
                    int close = line.IndexOf(']', i + 1);
                    if (close < 0) { cur = null; continue; }
                    string name = line.Substring(i + 1, close - i - 1).Trim();
                    cur = new IniSection { Name = name, Line = n, Start = line.IndexOf(name, i + 1, StringComparison.Ordinal), Length = name.Length };
                    var script = ProjectFileLogic.ScriptSection("[" + name + "]");
                    if (script != null)
                    {
                        cur.Module = script.Value.Module;
                        cur.Class = script.Value.Class;
                    }
                    list.Add(cur);
                    continue;
                }
                if (cur == null) continue;
                int eq = line.IndexOf('=', i);
                if (eq <= i) continue;
                char op = "+-.!".IndexOf(line[i]) >= 0 ? line[i] : '\0';
                int ks = op != '\0' ? i + 1 : i;
                int ke = eq;
                while (ke > ks && char.IsWhiteSpace(line[ke - 1])) ke--;
                // Key[0]=… / Key(0)=…：数组下标不算键名
                int br = line.IndexOfAny(new[] { '[', '(' }, ks, ke - ks);
                if (br > ks) ke = br;
                if (ke <= ks) continue;
                cur.Keys.Add(new IniKey { Name = line.Substring(ks, ke - ks), Line = n, Start = ks, Length = ke - ks, Op = op });
            }
            return list;
        }

        /// <summary>
        /// 光标在节头 <c>[…</c> 里（还没到 ']'）时返回 '[' 后的列，并给出已输入的文本；否则 -1。
        /// </summary>
        public static int SectionStart(string line, int column, out string typed)
        {
            typed = string.Empty;
            if (line == null || column < 0 || column > line.Length) return -1;
            int i = 0;
            while (i < line.Length && char.IsWhiteSpace(line[i])) i++;
            if (i >= line.Length || line[i] != '[' || column <= i) return -1;
            int close = line.IndexOf(']', i);
            if (close >= 0 && column > close) return -1;
            typed = line.Substring(i + 1, column - i - 1);
            return i + 1;
        }

        /// <summary>UCLASS 说明符里有 config= / Config 时为 true（config 类的节才会出现在 ini 里）。</summary>
        public static bool IsConfigClass(string specifiers)
        {
            if (string.IsNullOrEmpty(specifiers)) return false;
            foreach (var raw in specifiers.Split(','))
            {
                string s = raw.Trim();
                int eq = s.IndexOf('=');
                if (eq >= 0) s = s.Substring(0, eq).Trim();
                if (s.Equals("config", StringComparison.OrdinalIgnoreCase) || s.Equals("defaultconfig", StringComparison.OrdinalIgnoreCase) ||
                    s.Equals("globaluserconfig", StringComparison.OrdinalIgnoreCase) || s.Equals("projectuserconfig", StringComparison.OrdinalIgnoreCase))
                    return true;
            }
            return false;
        }

        /// <summary>C++ 类名去掉 UE 前缀（UMyClass → MyClass、AMyActor → AMyActor 的 MyActor），就是 ini 节名里的写法。</summary>
        public static string ScriptName(string cppName)
        {
            if (string.IsNullOrEmpty(cppName) || cppName.Length < 2) return cppName;
            char p = cppName[0];
            if ((p == 'U' || p == 'A') && char.IsUpper(cppName[1])) return cppName.Substring(1);
            return cppName;
        }

        /// <summary>
        /// 从成员的一行签名（如 "UPROPERTY(Config) TArray&lt;FString&gt; Paths;" 或 "bool bEnabled"）取出类型。
        /// </summary>
        public static string TypeFromSignature(string signature, string name)
        {
            if (string.IsNullOrWhiteSpace(signature) || string.IsNullOrEmpty(name)) return null;
            string s = signature.Trim();
            int rp = s.StartsWith("UPROPERTY", StringComparison.Ordinal) ? CloseParen(s, s.IndexOf('(')) : -1;
            if (rp > 0) s = s.Substring(rp + 1).Trim();
            int at = LastWord(s, name);
            if (at <= 0) return null;
            string type = s.Substring(0, at).Trim();
            foreach (var m in new[] { "mutable ", "static ", "const " })
                if (type.StartsWith(m, StringComparison.Ordinal)) type = type.Substring(m.Length).Trim();
            return type.Length == 0 ? null : type;
        }

        private static int CloseParen(string s, int open)
        {
            if (open < 0) return -1;
            int d = 0;
            for (int i = open; i < s.Length; i++)
            {
                if (s[i] == '(') d++;
                else if (s[i] == ')' && --d == 0) return i;
            }
            return -1;
        }

        private static int LastWord(string s, string word)
        {
            int at = s.LastIndexOf(word, StringComparison.Ordinal);
            while (at > 0)
            {
                bool before = !(char.IsLetterOrDigit(s[at - 1]) || s[at - 1] == '_');
                int end = at + word.Length;
                bool after = end >= s.Length || !(char.IsLetterOrDigit(s[end]) || s[end] == '_');
                if (before && after) return at;
                at = s.LastIndexOf(word, at - 1, StringComparison.Ordinal);
            }
            return -1;
        }

        /// <summary>按 C++ 类型给出 ini 里值的写法。</summary>
        public static IniValueHint HintFor(string cppType)
        {
            var h = new IniValueHint { Type = cppType };
            string t = (cppType ?? string.Empty).Replace(" ", string.Empty);
            if (t.Length == 0)
            {
                h.HintZh = "值的类型未知。";
                h.HintEn = "Unknown value type.";
                return h;
            }
            if (t == "bool" || t.StartsWith("uint8", StringComparison.Ordinal) && t.Contains(":1"))
            {
                h.HintZh = "布尔：True 或 False。";
                h.HintEn = "Boolean: True or False.";
                h.Values = new[] { "True", "False" };
                return h;
            }
            if (t.StartsWith("TArray<", StringComparison.Ordinal) || t.StartsWith("TSet<", StringComparison.Ordinal))
            {
                h.HintZh = "数组：每行一个元素，用 +Key=值 追加、-Key=值 删除、!Key=ClearArray 清空。";
                h.HintEn = "Array: one element per line; +Key=Value appends, -Key=Value removes, !Key=ClearArray empties it.";
                return h;
            }
            if (t.StartsWith("TMap<", StringComparison.Ordinal))
            {
                h.HintZh = "映射：Key=((\"键\", 值),(\"键2\", 值2)) 或每行 +Key=(\"键\", 值)。";
                h.HintEn = "Map: Key=((\"Key\", Value),(\"Key2\", Value2)) or one +Key=(\"Key\", Value) per line.";
                return h;
            }
            switch (t)
            {
                case "int32": case "int64": case "int": case "uint32": case "uint64": case "int16": case "uint16": case "int8": case "uint8":
                    h.HintZh = "整数。";
                    h.HintEn = "Integer.";
                    return h;
                case "float": case "double":
                    h.HintZh = "数字，如 1.5。";
                    h.HintEn = "Number, e.g. 1.5.";
                    return h;
                case "FString": case "FName": case "FText":
                    h.HintZh = "文本；含逗号、括号时加引号。";
                    h.HintEn = "Text; quote it when it contains commas or parentheses.";
                    return h;
                case "FSoftObjectPath": case "FSoftClassPath":
                    h.HintZh = "资源路径，如 /Game/Maps/Main.Main。";
                    h.HintEn = "Asset path, e.g. /Game/Maps/Main.Main.";
                    return h;
                case "FVector": case "FVector3f": case "FVector3d":
                    h.HintZh = "向量：(X=1.0,Y=0.0,Z=0.0)。";
                    h.HintEn = "Vector: (X=1.0,Y=0.0,Z=0.0).";
                    return h;
                case "FLinearColor": case "FColor":
                    h.HintZh = "颜色：(R=1.0,G=1.0,B=1.0,A=1.0)。";
                    h.HintEn = "Colour: (R=1.0,G=1.0,B=1.0,A=1.0).";
                    return h;
            }
            if (t.StartsWith("TSubclassOf<", StringComparison.Ordinal) || t.StartsWith("TSoftClassPtr<", StringComparison.Ordinal))
            {
                h.HintZh = "类路径，如 /Script/MyGame.MyClass 或 /Game/BP_X.BP_X_C。";
                h.HintEn = "Class path, e.g. /Script/MyGame.MyClass or /Game/BP_X.BP_X_C.";
                return h;
            }
            if (t.StartsWith("TSoftObjectPtr<", StringComparison.Ordinal))
            {
                h.HintZh = "资源路径，如 /Game/Data/X.X。";
                h.HintEn = "Asset path, e.g. /Game/Data/X.X.";
                return h;
            }
            if (t.StartsWith("E", StringComparison.Ordinal) || t.StartsWith("TEnumAsByte<", StringComparison.Ordinal))
            {
                h.HintZh = "枚举：写枚举值的名字。";
                h.HintEn = "Enum: write the value's name.";
                return h;
            }
            if (t.StartsWith("F", StringComparison.Ordinal))
            {
                h.HintZh = "结构体：(成员=值,成员=值)。";
                h.HintEn = "Struct: (Member=Value,Member=Value).";
                return h;
            }
            h.HintZh = "类型 " + cppType + "。";
            h.HintEn = "Type " + cppType + ".";
            return h;
        }

        /// <summary>
        /// 节里不是 config 属性的键（大小写不敏感）。<paramref name="configMembers"/> 为空时什么都不报（类可能用 GConfig 手动读）。
        /// </summary>
        public static List<IniKey> UnknownKeys(IniSection section, ICollection<string> configMembers)
        {
            var list = new List<IniKey>();
            if (section == null || configMembers == null || configMembers.Count == 0) return list;
            var set = new HashSet<string>(configMembers, StringComparer.OrdinalIgnoreCase);
            foreach (var k in section.Keys)
                if (!set.Contains(k.Name)) list.Add(k);
            return list;
        }

        /// <summary>光标在 '=' 之后时：键名和值的起始列，否则 -1。</summary>
        public static int ValueStart(string line, int column, out string key)
        {
            key = null;
            if (line == null || column < 0 || column > line.Length) return -1;
            int eq = line.IndexOf('=');
            if (eq < 0 || column <= eq) return -1;
            int s = ProjectFileLogic.IniKeyStart(line, eq, out string k);
            if (s < 0 || k.Length == 0) return -1;
            key = k;
            int v = eq + 1;
            while (v < column && line[v] == ' ') v++;
            return v;
        }
    }
}
