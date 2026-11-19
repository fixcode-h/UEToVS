using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading;
using UEToVS.Engine.Unreal;

namespace UEToVS.Engine.Indexing
{
    /// <summary>What <see cref="BlueprintIndexScan.Scan"/> found for one class (and member).</summary>
    public sealed class BlueprintScanResult
    {
        /// <summary>False when the file is missing, from another format or unreadable (being replaced, corrupt).</summary>
        public bool Ok { get; set; }

        /// <summary>Without a member: Blueprints deriving from the class (tree, as <see cref="BlueprintIndex.Derived"/>).</summary>
        public List<DerivedBlueprint> Derived { get; } = new List<DerivedBlueprint>();

        /// <summary>Blueprints / maps using the member (or, without a member, mentioning the class), sorted by path; name tables dropped.</summary>
        public List<BlueprintEntry> Users { get; } = new List<BlueprintEntry>();

        /// <summary>More users exist than the cap passed to <see cref="BlueprintIndexScan.Scan"/>.</summary>
        public bool UsersCapped { get; set; }
    }

    /// <summary>
    /// Answers find_blueprint_usages by streaming blueprints.jsonl.gz instead of loading it. A big UE project's index
    /// (hundreds of thousands of assets, every Blueprint's whole name table) does not fit the MCP server's 512 MiB heap
    /// cap when loaded as <see cref="BlueprintIndex"/>, which is what threw OutOfMemoryException. Here each line is only
    /// byte-searched for the JSON-quoted name; just the few matching lines are parsed, so memory stays at one line plus
    /// the matches, whatever the project size.
    /// </summary>
    public static class BlueprintIndexScan
    {
        private const int InitialBuffer = 1 << 20;

        public static BlueprintScanResult Scan(string file, string className, string member, int maxUsers, CancellationToken ct)
        {
            var result = new BlueprintScanResult();
            if (string.IsNullOrEmpty(className) || !File.Exists(file)) return result;
            bool withMember = !string.IsNullOrEmpty(member);
            // 文件里的字符串由 System.Text.Json 写出（非 ASCII 会转义），用同一个编码器生成要找的字节，保证能对上
            byte[] userPattern = Quoted(withMember ? member : className);
            byte[] classPattern = Quoted(className);
            byte[] classSuffix = Suffix(className); // "/Script/G.MyCharacter" 结尾：父类 / 原生父类字段
            var family = new List<BlueprintEntry>();
            var familyUsers = new List<BlueprintEntry>(); // 同族里名字表也提到这个类的：不在派生树里时才算「用到」
            try
            {
                using var fs = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                using var gz = new GZipStream(fs, CompressionMode.Decompress);
                bool first = true;
                foreach (var line in Lines(gz, ct))
                {
                    if (first)
                    {
                        first = false;
                        var head = line.Span;
                        if (head.StartsWith(new byte[] { 0xEF, 0xBB, 0xBF })) head = head.Slice(3);
                        if (!HeaderOk(head)) return result;
                        continue;
                    }
                    var span = line.Span;
                    bool maybeUser = span.IndexOf(userPattern) >= 0;
                    bool maybeFamily = !withMember && (span.IndexOf(classSuffix) >= 0 || span.IndexOf(classPattern) >= 0);
                    if (!maybeUser && !maybeFamily) continue;
                    var e = ParseLine(span);
                    if (e == null || string.IsNullOrEmpty(e.FilePath) || !e.IsSearchable) continue;
                    bool isUser = maybeUser && IsUser(e, className, member);
                    if (maybeFamily && IsFamily(e, className))
                    {
                        family.Add(e);
                        if (isUser) familyUsers.Add(e);
                    }
                    else if (isUser)
                    {
                        if (result.Users.Count < maxUsers) result.Users.Add(e);
                        else result.UsersCapped = true;
                    }
                    e.Names = Array.Empty<string>(); // 名字表只用来判断，留着会把内存吃回去
                }
                if (first) return result; // 空文件
            }
            catch (Exception ex) when (ex is IOException || ex is InvalidDataException || ex is UnauthorizedAccessException)
            {
                return result;
            }
            if (!withMember)
            {
                var tree = new BlueprintIndex();
                tree.Replace(family);
                result.Derived.AddRange(tree.Derived(new[] { className }));
                var inTree = new HashSet<string>(BlueprintIndexService.Flatten(result.Derived).Select(x => x.FilePath), StringComparer.OrdinalIgnoreCase);
                foreach (var e in familyUsers)
                    if (!inTree.Contains(e.FilePath) && result.Users.Count < maxUsers) result.Users.Add(e);
            }
            result.Users.Sort((a, b) => string.Compare(a.FilePath, b.FilePath, StringComparison.OrdinalIgnoreCase));
            result.Ok = true;
            return result;
        }

        /// <summary>Candidate for the derived tree: its parent or nearest native parent is the class.</summary>
        private static bool IsFamily(BlueprintEntry e, string className) =>
            e.ParentClass != null &&
            (string.Equals(UassetReader.ClassName(e.NativeParentClass), className, StringComparison.OrdinalIgnoreCase) ||
             (UassetReader.IsNative(e.ParentClass) && string.Equals(UassetReader.ClassName(e.ParentClass), className, StringComparison.OrdinalIgnoreCase)));

        /// <summary>Same rule as <see cref="BlueprintIndex.Usages"/>: the member name together with the class (or the class as native parent).</summary>
        private static bool IsUser(BlueprintEntry e, string className, string member)
        {
            if (string.IsNullOrEmpty(member)) return Has(e, className);
            return Has(e, member) &&
                   (Has(e, className) || string.Equals(UassetReader.ClassName(e.NativeParentClass), className, StringComparison.OrdinalIgnoreCase));
        }

        /// <summary>Linear, not binary search: only matching lines get here, and it does not rely on the file being sorted.</summary>
        private static bool Has(BlueprintEntry e, string name) => Array.IndexOf(e.Names, name) >= 0;

        private static byte[] Quoted(string s) => Encoding.UTF8.GetBytes(JsonSerializer.Serialize(s));

        private static byte[] Suffix(string s)
        {
            string q = JsonSerializer.Serialize(s);
            return Encoding.UTF8.GetBytes("." + q.Substring(1));
        }

        private static bool HeaderOk(ReadOnlySpan<byte> line)
        {
            try
            {
                var r = new Utf8JsonReader(line);
                if (!r.Read() || r.TokenType != JsonTokenType.StartObject) return false;
                while (r.Read() && r.TokenType == JsonTokenType.PropertyName)
                {
                    bool isFormat = r.ValueTextEquals("format");
                    r.Read();
                    if (isFormat) return r.TokenType == JsonTokenType.Number && r.TryGetInt32(out int f) && f == BlueprintIndex.Format;
                    r.Skip();
                }
            }
            catch (JsonException)
            {
            }
            return false;
        }

        /// <summary>One saved entry (format of <see cref="BlueprintIndex.Save"/>); null when the line is not valid JSON.</summary>
        internal static BlueprintEntry ParseLine(ReadOnlySpan<byte> line)
        {
            try
            {
                var r = new Utf8JsonReader(line);
                if (!r.Read() || r.TokenType != JsonTokenType.StartObject) return null;
                var e = new BlueprintEntry();
                while (r.Read() && r.TokenType == JsonTokenType.PropertyName)
                {
                    string key = r.GetString();
                    r.Read();
                    switch (key)
                    {
                        case "p": e.FilePath = Str(ref r); break;
                        case "o": e.ObjectPath = Str(ref r); break;
                        case "pc": e.ParentClass = Str(ref r); break;
                        case "npc": e.NativeParentClass = Str(ref r); break;
                        case "gc": e.GeneratedClass = Str(ref r); break;
                        case "t": e.LastWriteUtcTicks = r.TokenType == JsonTokenType.Number ? r.GetInt64() : 0; break;
                        case "s": e.Size = r.TokenType == JsonTokenType.Number ? r.GetInt64() : 0; break;
                        case "n":
                            if (r.TokenType != JsonTokenType.StartArray)
                            {
                                r.Skip();
                                break;
                            }
                            var names = new List<string>();
                            while (r.Read() && r.TokenType == JsonTokenType.String) names.Add(r.GetString());
                            e.Names = names.ToArray(); // saved sorted
                            break;
                        default: r.Skip(); break;
                    }
                }
                return e;
            }
            catch (Exception ex) when (ex is JsonException || ex is InvalidOperationException || ex is FormatException)
            {
                return null;
            }
        }

        private static string Str(ref Utf8JsonReader r) => r.TokenType == JsonTokenType.String ? r.GetString() : null;

        /// <summary>The decompressed lines (without "\r\n"); a returned line is valid until the next one is read.</summary>
        private static IEnumerable<ReadOnlyMemory<byte>> Lines(Stream s, CancellationToken ct)
        {
            byte[] buf = new byte[InitialBuffer];
            int start = 0, end = 0;
            bool eof = false;
            while (true)
            {
                int nl = buf.AsSpan(start, end - start).IndexOf((byte)'\n');
                if (nl >= 0)
                {
                    yield return Trim(buf, start, nl);
                    start += nl + 1;
                    continue;
                }
                if (eof)
                {
                    if (end > start) yield return Trim(buf, start, end - start);
                    yield break;
                }
                ct.ThrowIfCancellationRequested();
                if (start > 0)
                {
                    Buffer.BlockCopy(buf, start, buf, 0, end - start);
                    end -= start;
                    start = 0;
                }
                if (end == buf.Length) Array.Resize(ref buf, buf.Length * 2); // 一行比缓冲区长（名字特别多的关卡）
                int n = s.Read(buf, end, buf.Length - end);
                if (n == 0) eof = true;
                else end += n;
            }
        }

        private static ReadOnlyMemory<byte> Trim(byte[] buf, int start, int length)
        {
            if (length > 0 && buf[start + length - 1] == (byte)'\r') length--;
            return new ReadOnlyMemory<byte>(buf, start, length);
        }
    }
}
