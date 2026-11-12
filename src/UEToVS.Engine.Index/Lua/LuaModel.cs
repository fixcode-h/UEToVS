using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace UEToVS.Engine.Index.Lua
{
    /// <summary>What a Lua declaration is. Stored as its lower-case name in the index file.</summary>
    public enum LuaSymbolKind : byte
    {
        /// <summary><c>function f()</c>, <c>local function f()</c>, <c>M.f = function()</c>, <c>function M.f()</c>.</summary>
        Function = 0,

        /// <summary><c>function M:f()</c> (implicit self), or <c>f = function(self, ...)</c> in a table constructor.</summary>
        Method = 1,

        /// <summary>A class: <c>---@class Name</c>, <c>local X = class("X", Base)</c>, <c>UnLua.Class()</c>, <c>Class()</c>.</summary>
        Class = 2,

        /// <summary>A table used as a module / namespace: <c>local M = {}</c>, <c>M.Sub = {}</c>.</summary>
        Table = 3,

        /// <summary>A non-function field assigned on a table: <c>M.Speed = 10</c>, <c>{ Speed = 10 }</c>.</summary>
        Field = 4,

        /// <summary>A file-level variable: <c>local x = 1</c>, <c>Global = 1</c>.</summary>
        Variable = 5,
    }

    /// <summary>One declaration in a Lua file. Line and column are 0-based (UTF-16 columns), like the C++ index.</summary>
    public sealed class LuaSymbol
    {
        [JsonPropertyName("n")] public string Name { get; set; }

        /// <summary>Dotted owner (<c>M</c>, <c>a.b.c</c>); null at file level.</summary>
        [JsonPropertyName("c")] public string Container { get; set; }

        [JsonPropertyName("k")] public LuaSymbolKind Kind { get; set; }
        [JsonPropertyName("l")] public int Line { get; set; }
        [JsonPropertyName("o")] public int Column { get; set; }

        /// <summary>One-line signature, e.g. <c>function M:Fire(target, damage)</c>.</summary>
        [JsonPropertyName("s")] public string Signature { get; set; }

        /// <summary>True for <c>local</c> declarations (not visible outside the file).</summary>
        [JsonPropertyName("p"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)] public bool IsLocal { get; set; }

        /// <summary><c>Container.Name</c> (or <c>Container:Name</c> for methods), or just the name.</summary>
        [JsonIgnore]
        public string QualifiedName => string.IsNullOrEmpty(Container) ? Name : Container + (Kind == LuaSymbolKind.Method ? ":" : ".") + Name;
    }

    /// <summary>One indexed Lua file with the stamp used for incremental updates.</summary>
    public sealed class LuaFileEntry
    {
        /// <summary>Absolute, normalized path.</summary>
        [JsonPropertyName("path")] public string Path { get; set; }

        /// <summary>Root folder this file was found under (one of the configured Lua script paths).</summary>
        [JsonPropertyName("root")] public string Root { get; set; }

        /// <summary><c>require</c> name: path relative to the root, without .lua, separators replaced by dots (<c>UI.MainPanel</c>).</summary>
        [JsonPropertyName("module")] public string Module { get; set; }

        [JsonPropertyName("size")] public long Size { get; set; }
        [JsonPropertyName("mtime")] public long LastWriteUtcTicks { get; set; }

        /// <summary>xxHash64 of the content: a touched but unchanged file is not re-parsed.</summary>
        [JsonPropertyName("hash")] public ulong Hash { get; set; }

        [JsonPropertyName("symbols")] public List<LuaSymbol> Symbols { get; set; } = new List<LuaSymbol>();
    }

    /// <summary>The whole Lua index (what <see cref="LuaIndexStore"/> persists). Treat an instance as immutable once published.</summary>
    public sealed class LuaIndexData
    {
        /// <summary>Bump when the parser's output changes: older files are re-parsed on the next scan.</summary>
        public const int CurrentFormat = 1;

        [JsonPropertyName("format")] public int Format { get; set; } = CurrentFormat;
        [JsonPropertyName("writtenUtc")] public DateTime WrittenUtc { get; set; }

        /// <summary>The configured roots (absolute) the files were collected from.</summary>
        [JsonPropertyName("roots")] public List<string> Roots { get; set; } = new List<string>();

        [JsonPropertyName("files")] public List<LuaFileEntry> Files { get; set; } = new List<LuaFileEntry>();

        [JsonIgnore] public int SymbolCount => Files.Sum(f => f.Symbols?.Count ?? 0);
    }

    /// <summary>
    /// Reads and writes &lt;solution&gt;\.vs\UEToVS\Lua\lua-index.json. The engine writes it (atomically: temp file +
    /// replace); the MCP server only reads it, reloading when the file's timestamp changes.
    /// </summary>
    public static class LuaIndexStore
    {
        public const string FileName = "lua-index.json";

        private static readonly JsonSerializerOptions Json = new JsonSerializerOptions
        {
            WriteIndented = false,
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
            Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
        };

        public static string PathIn(string luaDirectory) => System.IO.Path.Combine(luaDirectory, FileName);

        /// <summary>Loads the index; null when the file is missing, unreadable or of another format.</summary>
        public static LuaIndexData Load(string path)
        {
            if (string.IsNullOrEmpty(path) || !File.Exists(path)) return null;
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            var data = JsonSerializer.Deserialize<LuaIndexData>(stream, Json);
            if (data == null || data.Format != LuaIndexData.CurrentFormat) return null;
            data.Roots ??= new List<string>();
            data.Files ??= new List<LuaFileEntry>();
            foreach (var f in data.Files) f.Symbols ??= new List<LuaSymbol>();
            return data;
        }

        public static void Save(string path, LuaIndexData data)
        {
            Directory.CreateDirectory(System.IO.Path.GetDirectoryName(path));
            string tmp = path + ".tmp";
            using (var stream = new FileStream(tmp, FileMode.Create, FileAccess.Write, FileShare.None))
                JsonSerializer.Serialize(stream, data, Json);
            File.Move(tmp, path, overwrite: true);
        }

        public static void Delete(string path)
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }
}
