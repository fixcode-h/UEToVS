using System;
using System.Collections.Generic;

namespace UEToVS.Host.Logic.Vcs
{
    /// <summary>提交面板里文件的改动种类（决定图标旁的标记）。</summary>
    public enum FileChangeKind
    {
        Modified,
        Added,
        Deleted,
        Moved,
    }

    /// <summary>提交面板目录树的一个节点：目录（<see cref="File"/> 为 null）或文件。</summary>
    public sealed class ChangeTreeNode
    {
        /// <summary>显示名：文件名，或（只有一个子目录的目录压成一行时）"a/b/c"。</summary>
        public string Name { get; set; }

        /// <summary>目录的完整路径（不带结尾 /），文件的完整路径。</summary>
        public string FullPath { get; set; }

        public CommitFile File { get; set; }

        public bool IsDirectory => File == null;

        /// <summary>目录下（含子目录）的文件数；文件为 1。</summary>
        public int FileCount { get; set; }

        public List<ChangeTreeNode> Children { get; } = new List<ChangeTreeNode>();
    }

    /// <summary>
    /// 提交面板（v0.10.2，照 Rider“在更改列表中提交的修订”）：把一次提交改的文件排成目录树。只有一个子目录、没有文件的目录
    /// 和子目录压成一行（"//Depot/Main/Source"），目录在前、文件在后，各自按名字排（不分大小写）。P4 的 depot 路径开头的 "//"
    /// 留在第一段上。纯逻辑。
    /// </summary>
    public static class ChangeTree
    {
        public static List<ChangeTreeNode> Build(IEnumerable<CommitFile> files)
        {
            var root = new ChangeTreeNode { Name = string.Empty, FullPath = string.Empty };
            if (files != null)
                foreach (var f in files)
                {
                    if (f == null || string.IsNullOrEmpty(f.Path)) continue;
                    Add(root, f);
                }
            Finish(root); // 根下的目录链也压成一行（"//Silver/Ver_1.0.0/Dev/..."）
            return root.Children;
        }

        private static void Add(ChangeTreeNode root, CommitFile f)
        {
            string path = f.Path.Replace('\\', '/');
            string prefix = string.Empty;
            if (path.StartsWith("//", StringComparison.Ordinal))
            {
                prefix = "//";
                path = path.Substring(2);
            }
            var parts = path.Split(new[] { '/' }, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length == 0) return;
            var dir = root;
            string full = prefix;
            for (int i = 0; i < parts.Length - 1; i++)
            {
                string name = i == 0 ? prefix + parts[i] : parts[i];
                full = i == 0 ? name : full + "/" + parts[i];
                ChangeTreeNode next = null;
                foreach (var c in dir.Children)
                    if (c.IsDirectory && string.Equals(c.FullPath, full, StringComparison.OrdinalIgnoreCase)) { next = c; break; }
                if (next == null)
                {
                    next = new ChangeTreeNode { Name = name, FullPath = full };
                    dir.Children.Add(next);
                }
                dir = next;
            }
            string fileName = parts.Length == 1 ? prefix + parts[0] : parts[parts.Length - 1];
            dir.Children.Add(new ChangeTreeNode { Name = fileName, FullPath = f.Path, File = f, FileCount = 1 });
        }

        /// <summary>压缩单子目录链、数文件、排序（自底向上）。</summary>
        private static void Finish(ChangeTreeNode node)
        {
            for (int i = 0; i < node.Children.Count; i++)
            {
                var c = node.Children[i];
                if (!c.IsDirectory) continue;
                while (c.Children.Count == 1 && c.Children[0].IsDirectory)
                {
                    var only = c.Children[0];
                    var merged = new ChangeTreeNode { Name = c.Name + "/" + only.Name, FullPath = only.FullPath };
                    merged.Children.AddRange(only.Children);
                    c = merged;
                }
                node.Children[i] = c;
                Finish(c);
            }
            int count = 0;
            foreach (var c in node.Children) count += c.FileCount;
            if (node.IsDirectory) node.FileCount = count;
            node.Children.Sort(Compare);
        }

        private static int Compare(ChangeTreeNode a, ChangeTreeNode b)
        {
            if (a.IsDirectory != b.IsDirectory) return a.IsDirectory ? -1 : 1;
            return string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>Git name-status（M / A / D / R / C / T）和 P4 action（edit / add / delete / move/add / branch / integrate …）。</summary>
        public static FileChangeKind KindOf(string status)
        {
            string s = (status ?? string.Empty).Trim().ToLowerInvariant();
            if (s.Length == 0) return FileChangeKind.Modified;
            if (s.StartsWith("move/add", StringComparison.Ordinal) || IsGitCode(s, 'r')) return FileChangeKind.Moved;
            if (IsGitCode(s, 'a') || IsGitCode(s, 'c') || s.EndsWith("add", StringComparison.Ordinal) || s == "branch" || s == "import") return FileChangeKind.Added;
            if (IsGitCode(s, 'd') || s.EndsWith("delete", StringComparison.Ordinal) || s == "purge" || s == "archive") return FileChangeKind.Deleted;
            return FileChangeKind.Modified;
        }

        /// <summary>Git 的单字母状态，改名 / 复制带相似度（"R100"）。</summary>
        private static bool IsGitCode(string s, char code)
        {
            if (s[0] != code) return false;
            for (int i = 1; i < s.Length; i++) if (!char.IsDigit(s[i])) return false;
            return true;
        }
    }
}
