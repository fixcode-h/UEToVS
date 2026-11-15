using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using Xunit;

namespace UEToVS.Core.Tests
{
    /// <summary>
    /// Source rule: an empty <c>catch</c> / <c>catch (Exception)</c> must say why it ignores the error. Allowed forms:
    /// a one-line best-effort cleanup (<c>try { x.Dispose(); } catch { }</c>), or a comment on the catch line.
    /// Narrow catches (IOException etc.) are not checked: the type already says what is expected.
    /// </summary>
    public class SilentCatchTests
    {
        private static readonly Regex BroadEmptyCatch = new Regex(
            @"catch\s*(\(\s*(System\.)?Exception\s*\w*\s*\))?\s*\{\s*\}", RegexOptions.Compiled);

        [Fact]
        public void BroadEmptyCatchesExplainThemselves()
        {
            string src = Path.Combine(RepoRoot(), "src");
            var offenders = new List<string>();
            foreach (var file in Directory.EnumerateFiles(src, "*.cs", SearchOption.AllDirectories))
            {
                if (file.Contains(Path.DirectorySeparatorChar + "obj" + Path.DirectorySeparatorChar) ||
                    file.Contains(Path.DirectorySeparatorChar + "bin" + Path.DirectorySeparatorChar)) continue;
                string text = File.ReadAllText(file);
                foreach (Match m in BroadEmptyCatch.Matches(text))
                {
                    int lineStart = text.LastIndexOf('\n', m.Index) + 1;
                    int lineEnd = text.IndexOf('\n', m.Index + m.Length);
                    if (lineEnd < 0) lineEnd = text.Length;
                    string line = text.Substring(lineStart, lineEnd - lineStart);
                    bool oneLineCleanup = line.Contains("try {") || line.Contains("try{");
                    bool commented = line.IndexOf("//", m.Index - lineStart + m.Length - 1, StringComparison.Ordinal) >= 0 ||
                                     m.Value.Contains("//");
                    if (oneLineCleanup || commented) continue;
                    int lineNo = text.Take(m.Index).Count(c => c == '\n') + 1;
                    offenders.Add(Path.GetRelativePath(src, file) + ":" + lineNo);
                }
            }
            Assert.True(offenders.Count == 0,
                "Empty catch without a reason (add a trailing // comment, log it, or narrow the exception type):\n" + string.Join("\n", offenders));
        }

        private static string RepoRoot()
        {
            var dir = new DirectoryInfo(AppContext.BaseDirectory);
            while (dir != null && !File.Exists(Path.Combine(dir.FullName, "UEToVS.sln"))) dir = dir.Parent;
            Assert.NotNull(dir);
            return dir.FullName;
        }
    }
}
