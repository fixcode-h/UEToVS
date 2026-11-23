using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using UEToVS.Host.Logic.Editing;
using UEToVS.Host.Logic.Settings;
using UEToVS.Host.Logic.Vcs;
using Xunit;

namespace UEToVS.Host.Logic.Tests
{
    /// <summary>编辑器内版本信息（Git / Perforce）的纯逻辑：解析、diff、改动块、时间、链接、缓存、设置。</summary>
    public class VcsLogicTests
    {
        private const string ShaA = "1111111111111111111111111111111111111111";
        private const string ShaB = "2222222222222222222222222222222222222222";

        // ---------------------------------------------------------------- git blame --porcelain

        [Fact]
        public void GitBlame_ParsesPorcelainGroupsAndUncommittedLines()
        {
            string porcelain =
                ShaA + " 1 1 2\n" +
                "author Zhang San\nauthor-mail <zs@example.com>\nauthor-time 1700000000\nauthor-tz +0800\n" +
                "committer Zhang San\ncommitter-mail <zs@example.com>\ncommitter-time 1700000000\ncommitter-tz +0800\n" +
                "summary 修复 Tick 里空指针\nprevious " + ShaB + " Source/Old.cpp\nfilename Source/A.cpp\n\tline one\n" +
                ShaA + " 2 2\n\tline two\n" +
                ShaB + " 5 3 1\nauthor Li Si\nauthor-mail <ls@example.com>\nauthor-time 1600000000\nauthor-tz -0500\nsummary Initial\nboundary\nfilename Source/A.cpp\n\tline three\n" +
                GitBlameParser.UncommittedId + " 4 4 1\nauthor Not Committed Yet\nauthor-mail <not.committed.yet>\nauthor-time 1700000100\nauthor-tz +0000\nsummary Version of A.cpp from A.cpp\nfilename Source/A.cpp\n\tlocal\n";
            var b = GitBlameParser.Parse(porcelain);
            Assert.Equal(4, b.Lines.Length);
            Assert.Equal(ShaA, b.Lines[0].CommitId);
            Assert.Equal(ShaA, b.Lines[1].CommitId);
            Assert.Equal(2, b.Lines[1].OriginalLine);
            Assert.Equal(ShaB, b.Lines[2].CommitId);
            Assert.Equal(5, b.Lines[2].OriginalLine);
            var a = b.CommitAt(0);
            Assert.Equal("Zhang San", a.Author);
            Assert.Equal("zs@example.com", a.AuthorMail);
            Assert.Equal("修复 Tick 里空指针", a.Summary);
            Assert.Equal(TimeSpan.FromHours(8), a.Time.Offset);
            Assert.Equal(1700000000, a.Time.ToUnixTimeSeconds());
            Assert.Equal((ShaB, "Source/Old.cpp"), b.Previous[ShaA]);
            Assert.False(b.Previous.ContainsKey(ShaB)); // boundary：没有上一版
            Assert.True(b.CommitAt(3).IsUncommitted);
            Assert.Null(b.CommitAt(9));
            Assert.Empty(GitBlameParser.Parse("").Lines);
        }

        [Fact]
        public void GitBlame_TimezoneParsing()
        {
            Assert.True(GitBlameParser.TryParseOffset("-0530", out var off));
            Assert.Equal(-new TimeSpan(5, 30, 0), off);
            Assert.False(GitBlameParser.TryParseOffset("0800", out _));
            Assert.False(GitBlameParser.TryParseOffset("+2500", out _));
        }

        // ---------------------------------------------------------------- git show / log

        [Fact]
        public void GitLog_ParsesRecordsNameStatusAndPatchPaths()
        {
            string show = "\u001e" + ShaA + "\u001f" + ShaB + "\u001fZhang San\u001fzs@example.com\u001f1700000000\u001f修复崩溃\n\n详细说明 #12\n\u001f\n";
            var rec = GitLogParser.Parse(show).Single();
            Assert.Equal(ShaA, rec.Commit.Id);
            Assert.Equal(new[] { ShaB }, rec.Commit.Parents);
            Assert.Equal("修复崩溃", rec.Commit.Summary);
            Assert.Equal("修复崩溃\n\n详细说明 #12", rec.Commit.Message);
            Assert.Equal(1700000000, rec.Commit.Time.ToUnixTimeSeconds());

            var files = GitLogParser.ParseNameStatus("M\tSource/A.cpp\nR087\tSource/Old.h\tSource/New.h\nA\t\"Source/with space.h\"\n\n");
            Assert.Equal(3, files.Count);
            Assert.Equal("M", files[0].Status);
            Assert.Equal("R", files[1].Status);
            Assert.Equal("Source/Old.h", files[1].OldPath);
            Assert.Equal("Source/New.h", files[1].Path);
            Assert.Equal("Source/with space.h", files[2].Path);

            string patch = "\ndiff --git a/Source/Old.cpp b/Source/New.cpp\n--- a/Source/Old.cpp\n+++ b/Source/New.cpp\n@@ -1,2 +1,2 @@\n";
            Assert.Equal(("Source/Old.cpp", "Source/New.cpp"), GitLogParser.PatchPaths(patch));
            Assert.Equal(((string)null, "Source/A.cpp"), GitLogParser.PatchPaths("--- /dev/null\n+++ b/Source/A.cpp\n"));

            var two = GitLogParser.Parse(show + show.Replace(ShaA, ShaB));
            Assert.Equal(2, two.Count);
        }

        [Fact]
        public void CatFileHeaders()
        {
            Assert.True(CatFileHeader.TryParse(ShaA + " blob 120\n", out var sha, out var type, out long size, out bool missing));
            Assert.Equal(ShaA, sha);
            Assert.Equal("blob", type);
            Assert.Equal(120, size);
            Assert.False(missing);
            Assert.True(CatFileHeader.TryParse("HEAD:Source/X.cpp missing", out _, out _, out _, out missing));
            Assert.True(missing);
            Assert.False(CatFileHeader.TryParse("garbage", out _, out _, out _, out _));
        }

        [Fact]
        public void GitArgs_AlwaysAvoidOptionalLocksAndQuotePaths()
        {
            foreach (var a in new[] { GitArgs.CatFileBatch, GitArgs.Blame(null, "Source/My File.cpp"), GitArgs.Show(ShaA), GitArgs.ShowFiles(ShaA),
                                      GitArgs.LineLog(10, 20, "Source/A.cpp", 500), GitArgs.RevParseHead, GitStatusParser.Arguments })
                Assert.StartsWith("--no-optional-locks", a);
            Assert.Contains("blame --porcelain HEAD -- \"Source/My File.cpp\"", GitArgs.Blame(null, "Source/My File.cpp"));
            Assert.Contains("-n 500", GitArgs.LineLog(10, 20, "Source/A.cpp", 500));
            Assert.Contains("-L10,20:Source/A.cpp", GitArgs.LineLog(10, 20, "Source/A.cpp", 500));
            Assert.Contains("--untracked-files=normal", GitStatusParser.Arguments);
            Assert.Equal("plain", CommandLine.Quote("plain"));
            Assert.Equal("\"a b\"", CommandLine.Quote("a b"));
            Assert.Equal("\"a \\\"q\\\"\"", CommandLine.Quote("a \"q\""));
            Assert.Equal("\"C:\\dir with space\\\\\"", CommandLine.Quote("C:\\dir with space\\"));
        }

        [Fact]
        public void GitStatus_UntrackedFoldersCountForFilesInside()
        {
            string root = Path.GetFullPath("repo");
            var dirs = new List<string>();
            var set = GitStatusParser.Parse(" M Source/A.cpp\0?? Source/NewModule/\0?? Source/New.h\0", root, dirs);
            string P(string rel) => Path.GetFullPath(Path.Combine(root, rel.Replace('/', Path.DirectorySeparatorChar)));
            Assert.Contains(P("Source/A.cpp"), set);
            Assert.Contains(P("Source/New.h"), set);
            Assert.Single(dirs);
            Assert.True(GitStatusParser.IsChanged(P("Source/NewModule/Foo.cpp"), set, dirs));
            Assert.True(GitStatusParser.IsChanged(P("Source/NewModule/Deep/Bar.h"), set, dirs));
            Assert.False(GitStatusParser.IsChanged(P("Source/NewModuleX.cpp"), set, dirs));
            Assert.False(GitStatusParser.IsChanged(P("Source/B.cpp"), set, dirs));
            Assert.False(GitStatusParser.IsChanged(null, set, dirs));
        }

        // ---------------------------------------------------------------- p4

        [Fact]
        public void ZTag_RecordsAndMultilineValues()
        {
            string text = "... change 123\n... user jdoe\n... time 1700000000\n... desc 修复崩溃\n\n第二段\n\n... depotFile0 //depot/A.cpp\n... action0 edit\n... rev0 4\n\n" +
                          "... change 124\n... user amy\n... time 1700000100\n... desc one line\n\n";
            var records = ZTagParser.Parse(text);
            Assert.Equal(2, records.Count);
            Assert.Equal("修复崩溃\n\n第二段", ZTagParser.Get(records[0], "desc").TrimEnd('\n'));
            var d = P4Parsers.ParseDescribe(text);
            Assert.Equal(2, d.Count);
            Assert.Equal("123", d[0].Commit.Id);
            Assert.Equal("jdoe", d[0].Commit.Author);
            Assert.Equal("修复崩溃", d[0].Commit.Summary);
            Assert.Equal("//depot/A.cpp", d[0].Files.Single().Path);
            Assert.Equal(4, d[0].Files[0].Revision);
            Assert.Equal("edit", d[0].Files[0].Status);
            Assert.Equal(1700000100, d[1].Commit.Time.ToUnixTimeSeconds());
        }

        [Fact]
        public void P4Filelog_RevisionsAndPrevious()
        {
            string text = "... depotFile //depot/Game/A.cpp\n" +
                          "... rev0 3\n... change0 300\n... action0 edit\n... type0 text\n... time0 1700000300\n... user0 amy\n... client0 ws\n... desc0 第三次\n\n" +
                          "... rev1 2\n... change1 200\n... action1 integrate\n... time1 1700000200\n... user1 bob\n... desc1 second\n" +
                          "... rev2 1\n... change2 100\n... action2 add\n... time2 1700000100\n... user2 bob\n... desc2 first\n";
            var log = P4Parsers.ParseFilelog(text);
            Assert.Equal(3, log.Count);
            Assert.Equal("300", log[0].Commit.Id);
            Assert.Equal("#3", log[0].Revision);
            Assert.Equal("#2", log[0].PreviousRevision);
            Assert.Equal("第三次", log[0].Commit.Summary);
            Assert.Null(log[2].PreviousRevision); // add：没有上一版
            Assert.Equal("//depot/Game/A.cpp", log[1].Path);
        }

        [Fact]
        public void P4Annotate_LinesAuthorsDates()
        {
            string text = "300: amy 2026/11/20 int a = 1;\n100: bob 2024/01/31 \n300: amy 2026/11/20     return a;\n";
            var b = P4Parsers.ParseAnnotate(text);
            Assert.Equal(3, b.Lines.Length);
            Assert.Equal("300", b.Lines[0].CommitId);
            Assert.Equal("100", b.Lines[1].CommitId);
            Assert.Equal(2, b.Commits.Count);
            var c = b.Commits["100"];
            Assert.Equal("bob", c.Author);
            Assert.True(c.DateOnly);
            Assert.Equal(new DateTime(2024, 1, 31), c.Time.Date);
        }

        [Fact]
        public void P4_InfoFstatAndErrors()
        {
            Assert.Equal("D:\\P4\\Game", P4Parsers.ClientRoot("... userName amy\n... clientName ws\n... clientRoot D:\\P4\\Game\n"));
            Assert.Null(P4Parsers.ClientRoot("... clientRoot *unknown*\n"));
            Assert.Equal(7, P4Parsers.HaveRevision("... depotFile //d/A.cpp\n... headRev 9\n... haveRev 7\n"));
            Assert.Equal(0, P4Parsers.HaveRevision("... depotFile //d/A.cpp\n... headRev 9\n"));
            Assert.True(P4Parsers.IsLoginError("Your session has expired, please login again."));
            Assert.True(P4Parsers.IsLoginError("Perforce password (P4PASSWD) invalid or unset."));
            Assert.False(P4Parsers.IsLoginError("//depot/A.cpp - no such file(s)."));
            Assert.True(P4Parsers.IsNotInDepot("D:\\x.cpp - file(s) not in client view."));
            Assert.Equal("print -q \"D:\\My Game\\A.cpp#have\"", P4Args.Print("D:\\My Game\\A.cpp", null));
            Assert.Equal("annotate -u -c -q D:\\A.cpp#3", P4Args.Annotate("D:\\A.cpp", "#3"));
            Assert.Equal("-ztag describe -s 1 2", P4Args.Describe(new[] { "1", "2" }));
            Assert.DoesNotContain("-P", P4Args.Fstat("x") + P4Args.Filelog("x", 100) + P4Args.Info); // 不传密码
        }

        // ---------------------------------------------------------------- diff / 改动块

        private static string[] L(params string[] lines) => lines;

        [Fact]
        public void Diff_AddModifyDeleteHunks()
        {
            var old = L("a", "b", "c", "d", "e");
            var cur = L("a", "B", "c", "x", "y", "d");
            var h = LineDiff.Compute(old, cur);
            Assert.Equal(3, h.Count);
            Assert.Equal(ChangeKind.Modified, h[0].Kind);
            Assert.Equal((1, 1, 1, 1), (h[0].OldStart, h[0].OldCount, h[0].NewStart, h[0].NewCount));
            Assert.Equal(ChangeKind.Added, h[1].Kind);
            Assert.Equal((3, 0, 3, 2), (h[1].OldStart, h[1].OldCount, h[1].NewStart, h[1].NewCount));
            Assert.Equal(ChangeKind.Deleted, h[2].Kind);
            Assert.Equal((4, 1, 6, 0), (h[2].OldStart, h[2].OldCount, h[2].NewStart, h[2].NewCount));
            Assert.Empty(LineDiff.Compute(old, old));
        }

        [Fact]
        public void Diff_IgnoresCrLfAndRoundTrips()
        {
            var a = LineDiff.SplitLines("one\ntwo\nthree\n");
            var b = LineDiff.SplitLines("one\r\ntwo\r\nthree\r\n");
            Assert.Equal(4, a.Length); // 和编辑器一样：结尾换行后还有一个空行
            Assert.Empty(LineDiff.Compute(a, b));
            Assert.Single(LineDiff.SplitLines(""));
        }

        [Fact]
        public void Diff_RandomEditsReconstructTheNewText()
        {
            var rnd = new Random(7);
            for (int round = 0; round < 200; round++)
            {
                var old = Enumerable.Range(0, rnd.Next(0, 40)).Select(_ => "l" + rnd.Next(0, 8)).ToArray();
                var cur = old.ToList();
                for (int k = rnd.Next(0, 6); k > 0; k--)
                {
                    int op = rnd.Next(3), at = rnd.Next(0, cur.Count + 1);
                    if (op == 0) cur.Insert(at, "n" + rnd.Next(0, 5));
                    else if (op == 1 && at < cur.Count) cur.RemoveAt(at);
                    else if (at < cur.Count) cur[at] = "m" + rnd.Next(0, 5);
                }
                var hunks = LineDiff.Compute(old, cur);
                // 按块把旧文本改成新文本，应该完全一致
                var rebuilt = new List<string>();
                int ai = 0;
                foreach (var h in hunks)
                {
                    Assert.True(h.OldStart >= ai);
                    while (ai < h.OldStart) rebuilt.Add(old[ai++]);
                    for (int i = 0; i < h.NewCount; i++) rebuilt.Add(cur[h.NewStart + i]);
                    ai += h.OldCount;
                }
                while (ai < old.Length) rebuilt.Add(old[ai++]);
                Assert.Equal(cur, rebuilt);
                // 行号映射：没改的行映射回同样内容的基准行
                var map = HunkMath.BaseLineMap(hunks, cur.Count);
                for (int i = 0; i < cur.Count; i++)
                {
                    Assert.Equal(map[i], HunkMath.ToBaseLine(hunks, i));
                    if (map[i] >= 0) Assert.Equal(cur[i], old[map[i]]);
                }
            }
        }

        [Fact]
        public void Diff_LargeDistanceFallsBackToOneBlock()
        {
            var old = Enumerable.Range(0, 3000).Select(i => "o" + i).ToArray();
            var cur = Enumerable.Range(0, 3000).Select(i => "n" + i).ToArray();
            var h = LineDiff.Compute(old, cur);
            Assert.Single(h);
            Assert.Equal(ChangeKind.Modified, h[0].Kind);
        }

        [Fact]
        public void Hunks_NavigationAndRanges()
        {
            var hunks = LineDiff.Compute(L("a", "b", "c", "d", "e", "f"), L("a", "X", "c", "d", "f", "g"));
            // 修改第 1 行、删除 e（标在第 4 行）、新增 g（第 5 行）
            Assert.Equal(3, hunks.Count);
            Assert.Same(hunks[0], HunkMath.At(hunks, 1));
            Assert.Same(hunks[1], HunkMath.At(hunks, 4));
            Assert.Null(HunkMath.At(hunks, 0));
            Assert.Same(hunks[1], HunkMath.Next(hunks, 1, out bool w));
            Assert.False(w);
            Assert.Same(hunks[0], HunkMath.Next(hunks, 5, out w));
            Assert.True(w);
            Assert.Same(hunks[0], HunkMath.Previous(hunks, 4, out w));
            Assert.Same(hunks[2], HunkMath.Previous(hunks, 0, out w));
            Assert.True(w);
            Assert.Equal(-1, HunkMath.ToBaseLine(hunks, 1));
            Assert.Equal(5, HunkMath.ToBaseLine(hunks, 4)); // f
            Assert.Equal((3, 6), HunkMath.BaseRange(hunks, 2, 4)); // c..f → 基准第 3..6 行（从 1 开始）
            Assert.Null(HunkMath.BaseRange(hunks, 5, 5)); // 只有新加的行
            Assert.Equal((1, 3), HunkMath.BaseRange(hunks, 2, 0)); // 反着选也行
        }

        // ---------------------------------------------------------------- 文字

        [Fact]
        public void RelativeTimeAndInlineText()
        {
            var now = new DateTimeOffset(2026, 11, 22, 12, 0, 0, TimeSpan.Zero);
            Assert.Equal("刚刚", RelativeTime.Format(now.AddSeconds(-20), now, false));
            Assert.Equal("5 分钟前", RelativeTime.Format(now.AddMinutes(-5), now, false));
            Assert.Equal("1 hour ago", RelativeTime.Format(now.AddMinutes(-70), now, true));
            Assert.Equal("3 天前", RelativeTime.Format(now.AddDays(-3), now, false));
            Assert.Equal("2 weeks ago", RelativeTime.Format(now.AddDays(-15), now, true));
            Assert.Equal("2 个月前", RelativeTime.Format(now.AddDays(-65), now, false));
            Assert.Equal("3 years ago", RelativeTime.Format(now.AddDays(-1100), now, true));
            Assert.Equal("刚刚", RelativeTime.Format(now.AddMinutes(5), now, false)); // 时钟差
            Assert.Equal("今天", RelativeTime.Format(now, now, false, dateOnly: true));
            Assert.Equal("yesterday", RelativeTime.Format(now.AddDays(-1), now, true, dateOnly: true));
            Assert.Equal(string.Empty, RelativeTime.Format(default, now, false));

            var c = new VcsCommit { Id = ShaA, Author = "张三", Time = now.AddDays(-3), Summary = "修复 Tick 里空指针" };
            Assert.Equal("张三 · 3 天前 · 修复 Tick 里空指针", BlameText.Inline(c, now, false));
            Assert.Equal("未提交", BlameText.Inline(new VcsCommit { IsUncommitted = true }, now, false));
            Assert.Equal("Not committed yet", BlameText.Inline(new VcsCommit { IsUncommitted = true }, now, true));
            c.Summary = new string('x', 200);
            Assert.True(BlameText.Inline(c, now, false).Length < 120);
            Assert.EndsWith("…", BlameText.Inline(c, now, false));
            string g = BlameText.Gutter(c, false);
            Assert.StartsWith(c.Time.ToString("yyyy/M/d", System.Globalization.CultureInfo.InvariantCulture), g);
            Assert.EndsWith("张三", g);
            Assert.Equal(BlameText.DateWidth + 1, g.IndexOf('张'));
            Assert.Equal("未提交", BlameText.Gutter(new VcsCommit { IsUncommitted = true }, false));
        }

        [Fact]
        public void BlameColors_NewerIsBrighterAndStablePerCommit()
        {
            var now = DateTimeOffset.Now;
            var commits = new[]
            {
                new VcsCommit { Id = "a", Time = now.AddDays(-30) },
                new VcsCommit { Id = "b", Time = now.AddDays(-1) },
                new VcsCommit { Id = "c", Time = now.AddDays(-10) },
                new VcsCommit { Id = "u", IsUncommitted = true },
            };
            var r = BlameColors.AgeRanks(commits);
            Assert.Equal(0.0, r["a"]);
            Assert.Equal(1.0, r["b"]);
            Assert.Equal(0.5, r["c"]);
            Assert.False(r.ContainsKey("u"));
            Assert.Equal(BlameColors.Hue(ShaA), BlameColors.Hue(ShaA));
            var oldDark = BlameColors.Background(0, true);
            var newDark = BlameColors.Background(1, true);
            Assert.Equal((byte)255, newDark.A);
            Assert.True(newDark.R + newDark.G + newDark.B > oldDark.R + oldDark.G + oldDark.B);
            Assert.True(newDark.B > newDark.R); // 蓝
            var oldLight = BlameColors.Background(0, false);
            var newLight = BlameColors.Background(1, false);
            Assert.True(newLight.R + newLight.G + newLight.B < oldLight.R + oldLight.G + oldLight.B); // 浅色主题越新越深
            Assert.Equal((byte)255, BlameColors.FromHsl(0, 1, 0.5).R);
        }

        [Fact]
        public void IssueLinks_ParseAndMatch()
        {
            var errors = new List<string>();
            var rules = IssueLinks.Parse(@"#(\d+) => https://github.com/org/repo/issues/$1; [A-Z]+-\d+ => https://jira.example.com/browse/$0; bad[ => https://x; nourl => ftp://x", errors);
            Assert.Equal(2, rules.Count);
            Assert.Equal(2, errors.Count);
            var m = IssueLinks.Find("修复 #12 和 UE-345，见 #7", rules);
            Assert.Equal(3, m.Count);
            Assert.Equal("https://github.com/org/repo/issues/12", m[0].Url);
            Assert.Equal("https://jira.example.com/browse/UE-345", m[1].Url);
            Assert.Equal(3, m[0].Start);
            Assert.Equal(3, m[0].Length);
            Assert.Empty(IssueLinks.Parse(""));
            // 正则里自己有 ; 的情况
            var semi = IssueLinks.Parse(@"a;b => https://x/$0");
            Assert.Single(semi);
            Assert.Equal("a;b", semi[0].Pattern.ToString());
        }

        [Fact]
        public void LruCache_KeepsMostRecent()
        {
            var c = new LruCache<string, int>(2, StringComparer.OrdinalIgnoreCase);
            c.Set("a", 1);
            c.Set("b", 2);
            Assert.True(c.TryGet("A", out _)); // a 变成最近用过
            c.Set("c", 3);
            Assert.False(c.TryGet("b", out _));
            Assert.True(c.TryGet("a", out var a));
            Assert.Equal(1, a);
            c.RemoveWhere(k => k == "a");
            Assert.Equal(1, c.Count);
        }

        [Fact]
        public void Throttle_AtMostOncePerInterval()
        {
            var t = new RefreshThrottle(TimeSpan.FromSeconds(30));
            var now = new DateTime(2026, 11, 22, 10, 0, 0, DateTimeKind.Utc);
            Assert.True(t.TryEnter(now));
            Assert.False(t.TryEnter(now.AddSeconds(10)));
            Assert.True(t.TryEnter(now.AddSeconds(31)));
            t.Mark(now.AddSeconds(40));
            Assert.False(t.TryEnter(now.AddSeconds(50)));
        }

        [Fact]
        public void FunctionRange_MatchesBracesSkippingCommentsAndStrings()
        {
            var lines = L(
                "void AFoo::Tick(float Dt)",
                "{",
                "    const char* s = \"}\"; // }",
                "    /* { */ if (x) { y(); }",
                "    char c = '}';",
                "}",
                "int After;");
            Assert.Equal((0, 5), FunctionRange.Find(lines, 0));
            Assert.Equal((0, 0), FunctionRange.Find(L("void Decl(int a);", "int x;"), 0));
            Assert.Null(FunctionRange.Find(L("void Open() {", "  x;"), 0));
            Assert.Null(FunctionRange.Find(lines, 99));
        }

        [Fact]
        public void TextDecoding_BomUtf8AndFallback()
        {
            Assert.Equal("中文", TextDecoding.Decode(new byte[] { 0xEF, 0xBB, 0xBF }.Concat(Encoding.UTF8.GetBytes("中文")).ToArray(), null));
            Assert.Equal("abc", TextDecoding.Decode(Encoding.UTF8.GetBytes("abc"), null));
            Assert.Equal("\u00e9", TextDecoding.Decode(new byte[] { 0xE9 }, Encoding.Latin1)); // 不是合法 UTF-8 → 退回 ANSI
            Assert.True(TextDecoding.LooksBinary(new byte[] { 1, 0, 2 }));
            Assert.False(TextDecoding.LooksBinary(Encoding.UTF8.GetBytes("text")));
        }

        [Fact]
        public void Detector_NearestRootAndEngineFiles()
        {
            string tmp = Path.Combine(Path.GetTempPath(), "uetovs-vcs-" + Guid.NewGuid().ToString("N"));
            try
            {
                string p4 = Path.Combine(tmp, "Depot");
                string git = Path.Combine(p4, "Game", "Plugins", "MyPlugin");
                Directory.CreateDirectory(Path.Combine(git, ".git"));
                Directory.CreateDirectory(Path.Combine(p4, "Game", "Source"));
                File.WriteAllText(Path.Combine(p4, ".p4config"), "P4PORT=ssl:p4:1666");
                Assert.Equal((VcsKind.Git, git), VcsDetector.Detect(Path.Combine(git, "Source"), VcsProviderChoice.Auto, ".p4config"));
                Assert.Equal((VcsKind.Perforce, p4), VcsDetector.Detect(Path.Combine(p4, "Game", "Source"), VcsProviderChoice.Auto, ".p4config"));
                Assert.Equal(VcsKind.None, VcsDetector.Detect(Path.Combine(p4, "Game", "Source"), VcsProviderChoice.Git, ".p4config").Kind);
                Assert.Equal(VcsKind.None, VcsDetector.Detect(git, VcsProviderChoice.Off).Kind);
            }
            finally
            {
                try { Directory.Delete(tmp, true); } catch (IOException) { }
            }
            Assert.True(VcsDetector.IsEngineFile(@"C:\UE\Engine\Source\Runtime\Engine\Private\Actor.cpp".Replace('\\', Path.DirectorySeparatorChar), null));
            Assert.False(VcsDetector.IsEngineFile(@"C:\Game\Source\Game\A.cpp".Replace('\\', Path.DirectorySeparatorChar), null));
        }

        // ---------------------------------------------------------------- 设置

        [Fact]
        public void Settings_DefaultsAndRoundTrip()
        {
            var s = new HostSettings();
            Assert.True(s.VcsAnnotate);     // v0.10.2：行号旁注解默认开
            Assert.False(s.VcsInlineBlame); // 行尾追溯默认关，由注解替代
            Assert.False(s.VcsInlineBlameEngine); // 引擎文件默认关
            Assert.True(s.VcsChangeBars);
            Assert.Equal(InlineBlameScope.CurrentLine, s.VcsInlineBlameScope);
            Assert.Equal(500, s.VcsHistoryMaxCommits);
            var v = HostSettingsSchema.Codec.ToValues(s);
            Assert.Equal("true", v[VcsSettings.AnnotateKey]);
            Assert.Equal("false", v[VcsSettings.InlineBlameKey]);
            Assert.True(VcsSettings.TrySet(s, VcsSettings.AnnotateKey, "false"));
            Assert.False(s.VcsAnnotate);
            Assert.Equal("CurrentLine", v[VcsSettings.InlineBlameScopeKey]);
            Assert.True(VcsSettings.TrySet(s, VcsSettings.InlineBlameScopeKey, "AllLines"));
            Assert.True(VcsSettings.TrySet(s, VcsSettings.HistoryMaxKey, "99999"));
            Assert.True(VcsSettings.TrySet(s, VcsSettings.ProviderKey, "Perforce"));
            Assert.True(VcsSettings.TrySet(s, VcsSettings.InlineBlameEngineKey, "true"));
            Assert.Equal(InlineBlameScope.AllLines, s.VcsInlineBlameScope);
            Assert.Equal(VcsSettings.MaxHistory, s.VcsHistoryMaxCommits);
            Assert.Equal(VcsProviderChoice.Perforce, s.VcsProvider);
            Assert.True(s.VcsInlineBlameEngine);
            Assert.False(VcsSettings.TrySet(s, "vcs.unknown", "x"));
            var schema = HostSettingsSchema.Build(8);
            foreach (var key in VcsSettings.Keys)
            {
                var d = schema.Single(x => x.Key == key);
                Assert.Equal(HostSettingsSchema.Editing, d.Category);
                Assert.Equal("版本信息", d.GroupZh);
            }
        }
    }
}
