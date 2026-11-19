using System;
using System.IO;

namespace UEToVS.Core.Storage
{
    /// <summary>
    /// Where UEToVS keeps per-solution data: &lt;solution dir&gt;/.vs/UEToVS/{Index,Logs,Cache,Debug}.
    /// Deleting the .vs folder removes everything. When there is no solution directory (e.g. command-line use without
    /// one) a fallback under the user's local app data is used.
    /// </summary>
    public sealed class StoragePaths
    {
        public const string FolderName = "UEToVS";

        private StoragePaths(string root)
        {
            Root = root;
            Index = Path.Combine(root, "Index");
            Logs = Path.Combine(root, "Logs");
            Cache = Path.Combine(root, "Cache");
            LegacyLua = Path.Combine(root, "Lua");
            Debug = Path.Combine(root, "Debug");
            LocalSettings = Path.Combine(root, "settings.local.json");
        }

        public string Root { get; }
        public string Index { get; }
        public string Logs { get; }
        public string Cache { get; }

        /// <summary>
        /// 旧版（v0.8.0 及以前）Lua 索引所在的目录（lua-index.json）。v0.9.0 起 Lua 归 IntelliJ IDEA 的 EmmyLua 插件，
        /// UEToVS 不再生成它；只留这个路径给引擎启动时删掉旧文件用（<see cref="DeleteLegacyLuaIndex"/>）。
        /// </summary>
        public string LegacyLua { get; }
        public string LocalSettings { get; }

        /// <summary>
        /// VS debug control (v0.8.0): endpoint.json written by the VS host while a UE solution is open, so the MCP server and
        /// the Agent panel can find the pipe that drives VS's debugger. Removed when the solution closes.
        /// </summary>
        public string Debug { get; }

        /// <summary>The debug control endpoint file (<see cref="Debug"/>\endpoint.json).</summary>
        public string DebugEndpoint => Path.Combine(Debug, "endpoint.json");

        /// <summary>
        /// v0.9.5：每个开着调试控制的 VS 在这里另写一份发现文件（&lt;devenv PID&gt;.json，内容和 endpoint.json 一样），
        /// 让 Agent 面板找到「开着别的项目的 VS」，在那个 VS 里打开当前项目（solution.open）。关解决方案时删掉。
        /// </summary>
        public static string DebugInstancesDir()
        {
            string baseDir = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            if (string.IsNullOrEmpty(baseDir)) baseDir = Path.GetTempPath();
            return Path.Combine(baseDir, FolderName, "Debug", "instances");
        }

        /// <summary>Paths for a solution directory (the folder holding the .sln).</summary>
        public static StoragePaths ForSolution(string solutionDir)
        {
            if (string.IsNullOrEmpty(solutionDir)) return Fallback();
            return new StoragePaths(Path.Combine(Path.GetFullPath(solutionDir), ".vs", FolderName));
        }

        /// <summary>Per-user fallback used when no solution directory is known.</summary>
        public static StoragePaths Fallback()
        {
            string baseDir = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            if (string.IsNullOrEmpty(baseDir)) baseDir = Path.GetTempPath();
            return new StoragePaths(Path.Combine(baseDir, FolderName, "NoSolution"));
        }

        /// <summary>
        /// 删掉旧版留下的 Lua 索引目录（只在 UEToVS 自己的存储目录下，不碰用户文件）。返回是否删了东西；删不掉（被占用等）
        /// 就留着下次再删，不抛异常。
        /// </summary>
        public bool DeleteLegacyLuaIndex()
        {
            try
            {
                if (!Directory.Exists(LegacyLua)) return false;
                Directory.Delete(LegacyLua, recursive: true);
                return true;
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
            {
                return false; // 被占用或没权限：留到下次启动再删
            }
        }

        /// <summary>Creates all directories; returns this for chaining. Never throws (logging must keep working).</summary>
        public StoragePaths EnsureCreated()
        {
            foreach (var d in new[] { Root, Index, Logs, Cache })
            {
                try { Directory.CreateDirectory(d); }
                catch { /* reported by the caller when it tries to write */ }
            }
            return this;
        }
    }
}
