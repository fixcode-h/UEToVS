using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace UEToVS.Host.Logic.Mcp
{
    /// <summary>
    /// 在原文上做最小改动的 JSON（含注释的 JSONC）编辑：只插入、替换或删除指定成员那一段文字，文件其余部分
    /// （注释、缩进、换行符、键的顺序、其他服务器的写法）一个字节都不动。
    /// 为什么不直接 JObject.ToString()：整份重排会丢掉用户的注释、改掉缩进，在版本库里看起来就是整份文件被覆盖了（v0.10.4 用户反馈）。
    /// 只认对象成员，不认识的写法（扫描失败）返回 false，由调用方退回整份重排。
    /// </summary>
    internal static class JsoncTextEditor
    {
        /// <summary>对象成员在原文里的位置。</summary>
        internal sealed class Member
        {
            public string Name;
            public int KeyStart;
            public int ValueStart;
            public int ValueEnd; // 值最后一个字符之后
            public ObjectSpan Value; // 值是对象时
        }

        /// <summary>对象在原文里的位置：Open 是 '{'，Close 是 '}'。</summary>
        internal sealed class ObjectSpan
        {
            public int Open;
            public int Close;
            public readonly List<Member> Members = new List<Member>();

            /// <summary>同名键取最后一个（和 Newtonsoft 的 DuplicatePropertyNameHandling.Replace 一致）。</summary>
            public Member Find(string name) => Members.LastOrDefault(m => m.Name == name);
        }

        /// <summary>
        /// 把 <paramref name="text"/> 里 <paramref name="before"/> 到 <paramref name="after"/> 的变化落到原文上，只动 <paramref name="keys"/>
        /// 这些根键下的 <paramref name="entryName"/> 成员（以及因此新建或变空的根键）。结果解析回来必须等于 <paramref name="after"/>，否则返回 false。
        /// </summary>
        public static bool TryApply(string text, JObject before, JObject after, IEnumerable<string> keys, string entryName, out string result)
        {
            result = null;
            try
            {
                string t = text;
                foreach (var key in keys.Distinct())
                {
                    var oldServers = before[key] as JObject;
                    var newServers = after[key] as JObject;
                    var oldEntry = oldServers?[entryName];
                    var newEntry = newServers?[entryName];
                    if (oldServers != null && newServers == null)
                    {
                        // 根键整个去掉（里面只剩我们的条目）
                        if (!Edit(ref t, root => RemoveMember(t, root, root.Find(key)))) return false;
                    }
                    else if (oldServers == null && newServers != null)
                    {
                        if (before.Property(key) != null) // "key": null 之类：替换值
                        {
                            if (!Edit(ref t, root => ReplaceValue(t, root.Find(key), newServers))) return false;
                        }
                        else if (!Edit(ref t, root => InsertMember(t, root, key, newServers))) return false;
                    }
                    else if (oldServers != null)
                    {
                        if (oldEntry == null && newEntry != null)
                        {
                            if (!Edit(ref t, root => InsertMember(t, root.Find(key)?.Value, entryName, newEntry))) return false;
                        }
                        else if (oldEntry != null && newEntry == null)
                        {
                            if (!Edit(ref t, root => RemoveMember(t, root.Find(key)?.Value, root.Find(key)?.Value?.Find(entryName)))) return false;
                        }
                        else if (oldEntry != null && !JToken.DeepEquals(oldEntry, newEntry))
                        {
                            if (!Edit(ref t, root => ReplaceValue(t, root.Find(key)?.Value?.Find(entryName), newEntry))) return false;
                        }
                    }
                }

                // 兜底：编辑后的文字必须正好是想要的内容，扫描器有任何没想到的写法都不冒险
                var check = JToken.Parse(t, new JsonLoadSettings { CommentHandling = CommentHandling.Ignore, DuplicatePropertyNameHandling = DuplicatePropertyNameHandling.Replace });
                if (!JToken.DeepEquals(check, after)) return false;
                result = t;
                return true;
            }
            catch (JsonException)
            {
                return false;
            }
        }

        /// <summary>每一步都重新扫描当前文字（配置文件很小），这样多步编辑之间的位置不会错。</summary>
        private static bool Edit(ref string text, Func<ObjectSpan, string> edit)
        {
            var root = Scan(text);
            if (root == null) return false;
            string next = edit(root);
            if (next == null) return false;
            text = next;
            return true;
        }

        // ------------------------------------------------------------------ 编辑

        private static string ReplaceValue(string text, Member member, JToken value)
        {
            if (member == null) return null;
            string indent = LineIndent(text, member.KeyStart);
            return text.Substring(0, member.ValueStart) + Serialize(value, text, indent) + text.Substring(member.ValueEnd);
        }

        private static string InsertMember(string text, ObjectSpan obj, string name, JToken value)
        {
            if (obj == null) return null;
            string eol = DetectEol(text);
            string openIndent = LineIndent(text, obj.Open);
            var first = obj.Members.FirstOrDefault();
            string indent = first != null && IsFirstOnLine(text, first.KeyStart) ? LineIndent(text, first.KeyStart) : openIndent + DetectIndentUnit(text);
            string member = JsonConvert.ToString(name) + ": " + Serialize(value, text, indent);

            var last = obj.Members.LastOrDefault();
            int closeLineStart = LineStart(text, obj.Close);
            bool closeOnOwnLine = closeLineStart > obj.Open && IsBlank(text, closeLineStart, obj.Close);
            var sb = new StringBuilder(text.Length + member.Length + 16);
            if (closeOnOwnLine)
            {
                // 正常的多行对象：新成员作为最后一行插在 '}' 那行之前，上一个成员补逗号（JSONC 已有尾逗号就不补）
                int commaAt = last != null && !HasCommaAfter(text, last.ValueEnd, obj.Close) ? last.ValueEnd : -1;
                if (commaAt >= 0) sb.Append(text, 0, commaAt).Append(',').Append(text, commaAt, closeLineStart - commaAt);
                else sb.Append(text, 0, closeLineStart);
                sb.Append(indent).Append(member).Append(eol);
                sb.Append(text, closeLineStart, text.Length - closeLineStart);
            }
            else
            {
                // '{}' 或写在一行里的对象：在 '}' 前换行插入，'}' 回到对象开头那行的缩进
                int comma = last != null ? FindCommaAfter(text, last.ValueEnd, obj.Close) : -1;
                int at = last == null ? obj.Open + 1 : comma >= 0 ? comma + 1 : last.ValueEnd;
                sb.Append(text, 0, at);
                if (last != null && comma < 0) sb.Append(',');
                sb.Append(eol).Append(indent).Append(member).Append(eol).Append(openIndent);
                int rest = SkipBlank(text, at, obj.Close) == obj.Close ? obj.Close : at; // 去掉 '}' 前的空白
                sb.Append(text, rest, text.Length - rest);
            }
            return sb.ToString();
        }

        private static string RemoveMember(string text, ObjectSpan obj, Member member)
        {
            if (obj == null || member == null) return null;
            int index = obj.Members.IndexOf(member);
            int comma = FindCommaAfter(text, member.ValueEnd, obj.Close);
            int start, end;
            if (comma >= 0)
            {
                // 后面有逗号：删掉“键 … 逗号”，键独占一行时连同整行
                start = member.KeyStart;
                end = comma + 1;
                if (IsFirstOnLine(text, start) && RestOfLineBlank(text, end, out int nextLine))
                {
                    start = LineStart(text, start);
                    end = nextLine;
                }
            }
            else if (index > 0)
            {
                // 最后一个成员：去掉上一个成员后面的逗号；成员独占几行时删掉这几行，上一行行尾的注释不动
                int prevComma = FindCommaAfter(text, obj.Members[index - 1].ValueEnd, member.KeyStart);
                if (prevComma < 0) return null;
                if (IsFirstOnLine(text, member.KeyStart) && RestOfLineBlank(text, member.ValueEnd, out int next))
                    return text.Substring(0, prevComma) + text.Substring(prevComma + 1, LineStart(text, member.KeyStart) - prevComma - 1) + text.Substring(next);
                start = prevComma;
                end = member.ValueEnd;
            }
            else
            {
                // 唯一的成员
                start = member.KeyStart;
                end = member.ValueEnd;
                if (IsFirstOnLine(text, start) && RestOfLineBlank(text, end, out int nextLine))
                {
                    start = LineStart(text, start);
                    end = nextLine;
                }
            }
            return text.Substring(0, start) + text.Substring(end);
        }

        // ------------------------------------------------------------------ 格式

        /// <summary>值按文件自己的缩进单位和换行符写成多行，第二行起加上成员所在行的缩进。</summary>
        private static string Serialize(JToken value, string text, string indent)
        {
            string unit = DetectIndentUnit(text);
            var sw = new StringWriter();
            using (var w = new JsonTextWriter(sw) { Formatting = Formatting.Indented, IndentChar = unit[0], Indentation = unit.Length })
                value.WriteTo(w);
            string eol = DetectEol(text);
            var lines = sw.ToString().Replace("\r\n", "\n").Split('\n');
            return string.Join(eol, lines.Select((l, i) => i == 0 ? l : indent + l));
        }

        private static string DetectEol(string text) => text.Contains("\r\n") ? "\r\n" : "\n";

        /// <summary>文件里第一行有缩进的行的缩进（制表符或空格），没有时两个空格。</summary>
        internal static string DetectIndentUnit(string text)
        {
            foreach (var raw in text.Replace("\r\n", "\n").Split('\n'))
            {
                if (raw.Length == 0 || raw.Trim().Length == 0) continue;
                if (raw[0] == '\t') return "\t";
                int n = 0;
                while (n < raw.Length && raw[n] == ' ') n++;
                if (n > 0) return new string(' ', Math.Min(n, 8));
            }
            return "  ";
        }

        private static int LineStart(string text, int pos)
        {
            int i = pos;
            while (i > 0 && text[i - 1] != '\n') i--;
            return i;
        }

        private static string LineIndent(string text, int pos)
        {
            int s = LineStart(text, pos), i = s;
            while (i < text.Length && (text[i] == ' ' || text[i] == '\t')) i++;
            return text.Substring(s, i - s);
        }

        private static bool IsBlank(string text, int from, int to)
        {
            for (int i = from; i < to; i++) if (!char.IsWhiteSpace(text[i])) return false;
            return true;
        }

        private static bool IsFirstOnLine(string text, int pos) => IsBlank(text, LineStart(text, pos), pos);

        private static int SkipBlank(string text, int from, int to)
        {
            while (from < to && char.IsWhiteSpace(text[from])) from++;
            return from;
        }

        /// <summary>从 <paramref name="from"/> 到行尾只有空白时为 true，<paramref name="nextLine"/> 是下一行开头。</summary>
        private static bool RestOfLineBlank(string text, int from, out int nextLine)
        {
            int i = from;
            while (i < text.Length && text[i] != '\n')
            {
                if (!char.IsWhiteSpace(text[i])) { nextLine = -1; return false; }
                i++;
            }
            nextLine = i < text.Length ? i + 1 : i;
            return true;
        }

        private static bool HasCommaAfter(string text, int from, int limit) => FindCommaAfter(text, from, limit) >= 0;

        /// <summary>从 <paramref name="from"/> 起跳过空白和注释后的第一个字符是逗号时返回它的位置，否则 -1。</summary>
        private static int FindCommaAfter(string text, int from, int limit)
        {
            int i = SkipTrivia(text, from);
            return i < limit && i < text.Length && text[i] == ',' ? i : -1;
        }

        // ------------------------------------------------------------------ 扫描

        /// <summary>扫描根对象及其各层对象成员的位置；不是对象或写法认不出时返回 null。</summary>
        internal static ObjectSpan Scan(string text)
        {
            try
            {
                int i = SkipTrivia(text, 0);
                if (i >= text.Length || text[i] != '{') return null;
                var root = ScanObject(text, ref i);
                return SkipTrivia(text, i) == text.Length ? root : null;
            }
            catch (FormatException)
            {
                return null;
            }
        }

        private static ObjectSpan ScanObject(string text, ref int i)
        {
            var obj = new ObjectSpan { Open = i };
            i++;
            while (true)
            {
                i = SkipTrivia(text, i);
                if (i >= text.Length) throw new FormatException();
                if (text[i] == '}') { obj.Close = i; i++; return obj; }
                if (text[i] == ',') { i++; continue; }
                var m = new Member { KeyStart = i };
                m.Name = ReadKey(text, ref i);
                i = SkipTrivia(text, i);
                if (i >= text.Length || text[i] != ':') throw new FormatException();
                i = SkipTrivia(text, i + 1);
                m.ValueStart = i;
                m.Value = ScanValue(text, ref i);
                m.ValueEnd = i;
                obj.Members.Add(m);
            }
        }

        private static ObjectSpan ScanValue(string text, ref int i)
        {
            if (i >= text.Length) throw new FormatException();
            char c = text[i];
            if (c == '{') return ScanObject(text, ref i);
            if (c == '[')
            {
                i++;
                while (true)
                {
                    i = SkipTrivia(text, i);
                    if (i >= text.Length) throw new FormatException();
                    if (text[i] == ']') { i++; return null; }
                    if (text[i] == ',') { i++; continue; }
                    ScanValue(text, ref i);
                }
            }
            if (c == '"' || c == '\'') { ReadString(text, ref i); return null; }
            // 数字、true/false/null 等字面量：读到分隔符为止
            int s = i;
            while (i < text.Length && !IsDelimiter(text[i])) i++;
            if (i == s) throw new FormatException();
            return null;
        }

        private static bool IsDelimiter(char c) => char.IsWhiteSpace(c) || c == ',' || c == '}' || c == ']' || c == ':' || c == '/';

        private static string ReadKey(string text, ref int i)
        {
            if (text[i] == '"' || text[i] == '\'') return ReadString(text, ref i);
            int s = i; // Newtonsoft 也接受不带引号的键
            while (i < text.Length && !IsDelimiter(text[i])) i++;
            if (i == s) throw new FormatException();
            return text.Substring(s, i - s);
        }

        private static string ReadString(string text, ref int i)
        {
            int s = i;
            char q = text[i++];
            while (i < text.Length && text[i] != q)
            {
                if (text[i] == '\\') i++;
                i++;
            }
            if (i >= text.Length) throw new FormatException();
            i++;
            using (var r = new JsonTextReader(new StringReader(text.Substring(s, i - s))))
                return r.Read() && r.Value is string v ? v : throw new FormatException();
        }

        /// <summary>跳过空白、// 行注释和 /* */ 块注释。</summary>
        private static int SkipTrivia(string text, int i)
        {
            while (i < text.Length)
            {
                char c = text[i];
                if (char.IsWhiteSpace(c) || c == '﻿') { i++; continue; }
                if (c == '/' && i + 1 < text.Length && text[i + 1] == '/')
                {
                    while (i < text.Length && text[i] != '\n') i++;
                    continue;
                }
                if (c == '/' && i + 1 < text.Length && text[i + 1] == '*')
                {
                    int e = text.IndexOf("*/", i + 2, StringComparison.Ordinal);
                    if (e < 0) throw new FormatException();
                    i = e + 2;
                    continue;
                }
                break;
            }
            return i;
        }
    }
}
