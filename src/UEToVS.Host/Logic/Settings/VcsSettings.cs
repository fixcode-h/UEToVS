using System;
using System.Collections.Generic;
using System.Globalization;
using UEToVS.Host.Logic.Vcs;
using UEToVS.UI.Logic;

namespace UEToVS.Host.Logic.Settings
{
    /// <summary>
    /// 编辑器内的版本信息（Git / Perforce，只读）的设置行（编辑页“版本信息”卡片，HostSettingsPages）和 HostSettings 编解码；
    /// 和 <see cref="EditorFeatureSettings"/> 一样单独放，HostSettingsSchema 只调 <see cref="AddDescriptors"/>、<see cref="ToValues"/>、<see cref="TrySet"/>。
    /// </summary>
    public static class VcsSettings
    {
        public const string AnnotateKey = "vcs.annotate";
        public const string InlineBlameKey = "vcs.inlineBlame";
        public const string InlineBlameScopeKey = "vcs.inlineBlameScope";
        public const string InlineBlameEngineKey = "vcs.inlineBlameEngine";
        public const string ChangeBarsKey = "vcs.changeBars";
        public const string HistoryMaxKey = "vcs.historyMaxCommits";
        public const string IssueLinksKey = "vcs.issueLinks";
        public const string ProviderKey = "vcs.provider";

        public const int MinHistory = 20, MaxHistory = 10000, DefaultHistory = 500;

        public static readonly string[] Keys = { AnnotateKey, InlineBlameKey, InlineBlameScopeKey, InlineBlameEngineKey, ChangeBarsKey, HistoryMaxKey, IssueLinksKey, ProviderKey };

        private static SettingDescriptor D(string category, string key, SettingKind kind, string zh, string en, string descZh, string descEn, string def, string keywords) =>
            new SettingDescriptor
            {
                Category = category, Key = key, Kind = kind, TitleZh = zh, TitleEn = en, DescriptionZh = descZh, DescriptionEn = descEn,
                Default = def, GroupZh = "版本信息", GroupEn = "Version control", Keywords = keywords,
            };

        public static void AddDescriptors(List<SettingDescriptor> list, string category)
        {
            list.Add(D(category, AnnotateKey, SettingKind.Bool, "打开文件时显示注解", "Show annotations when a file opens",
                "默认关：在编辑器右键菜单点“注解”（或 Ctrl+Shift+Alt+U, O）在当前编辑器里显示 / 隐藏。打开后行号左边一列，每行显示最后改它的日期和作者，越新底色越蓝；点一行打开提交面板，列出这次提交改的所有文件。显示注解时追溯一次，编辑过的行显示“未提交”。",
                "Off by default: right-click > Annotate in the editor (or Ctrl+Shift+Alt+U, O) shows / hides them in the current editor. A column left of the line numbers shows, for every line, the date and author of the last change, bluer for newer commits. Click a line to open the commit panel listing every file of that commit. The file is blamed once when the annotations appear; edited lines show \"Not committed\".",
                "false", "git blame annotate 注解 追溯 作者 日期 rider idea p4 perforce"));
            list.Add(D(category, InlineBlameKey, SettingKind.Bool, "行尾显示追溯信息", "Inline blame at the end of the line",
                "光标停下 300 毫秒后，在行尾用灰字显示“作者 · 多久前 · 提交说明”，没提交的行显示“未提交”；悬停看完整说明、改动的文件，可复制哈希、看这次提交的 diff、追溯上一版。Ctrl+Shift+Alt+U, J 临时开关。超过 5000 行的文件要等光标第一次停下才计算。",
                "After the caret rests for 300 ms, the end of the line shows \"author · how long ago · commit summary\" in grey (\"Not committed yet\" for local changes). Hover for the full message and changed files, copy the hash, see the commit's diff or blame the previous revision. Ctrl+Shift+Alt+U, J toggles. Files over 5000 lines are blamed only once the caret first rests.",
                "false", "git blame annotate inline 追溯 作者 提交 gitlens p4 perforce"));
            var scope = D(category, InlineBlameScopeKey, SettingKind.Choice, "行尾追溯显示在", "Show inline blame on",
                "仅当前行：只在光标所在行显示；所有行：屏幕上每一行都显示（同一提交连续的几行只在第一行显示）。",
                "Current line: only the caret line; All lines: every visible line (consecutive lines of the same commit show it once).",
                nameof(InlineBlameScope.CurrentLine), "git blame inline all lines 所有行 当前行");
            scope.Options = new[]
            {
                new SettingOption(nameof(InlineBlameScope.CurrentLine), "仅当前行", "Current line"),
                new SettingOption(nameof(InlineBlameScope.AllLines), "所有行", "All lines"),
            };
            list.Add(scope);
            list.Add(D(category, InlineBlameEngineKey, SettingKind.Bool, "引擎文件也自动追溯", "Blame engine files automatically too",
                "引擎目录（Engine\\）下的文件提交历史很长，追溯一次可能要几秒、占满一个核，默认打开时不显示注解和行尾追溯；Ctrl+Shift+Alt+U, O 仍可在当前编辑器里手动打开注解。",
                "Engine files (under Engine\\) have long histories and one blame can take seconds and a whole core, so annotations and inline blame are off for them by default; Ctrl+Shift+Alt+U, O still shows the annotations in the current editor.",
                "false", "engine blame 引擎 追溯"));
            list.Add(D(category, ChangeBarsKey, SettingKind.Bool, "行号旁显示改动色条", "Change bars next to line numbers",
                "和 Git 的 HEAD（Perforce 的 #have 版本）比较：新增绿色、修改蓝色、删除红色三角。点色条看这一块的旧内容，可回滚这一块（可撤销）或复制旧内容；Ctrl+Shift+Alt+U, ↓ / ↑ 跳到下一处 / 上一处改动。基准版本每个文件只取一次，编辑时在本地比较。",
                "Compared with Git HEAD (Perforce #have): green added, blue modified, red triangle deleted. Click a bar to see the old text, roll the block back (undoable) or copy the old text; Ctrl+Shift+Alt+U, Down / Up jumps to the next / previous change. The base version is read once per file and compared locally while you type.",
                "true", "git diff gutter change bar 改动 色条 回滚 hunk p4"));
            var max = D(category, HistoryMaxKey, SettingKind.Int, "选中部分历史最多查看的提交数", "Max commits for selection history",
                "“查看选中部分的历史”“查看此函数的历史”最多往回看多少次提交。大仓库上 git log -L 要遍历历史，越大越慢。",
                "How far back \"Show History for Selection\" and \"Show History for Function\" look. git log -L walks the history, so larger is slower on big repositories.",
                DefaultHistory.ToString(CultureInfo.InvariantCulture), "history git log -L 历史 提交 函数");
            max.Min = MinHistory;
            max.Max = MaxHistory;
            list.Add(max);
            list.Add(D(category, IssueLinksKey, SettingKind.Text, "提交说明里的链接", "Links in commit messages",
                "“正则 => URL”，多条用 ; 分开，URL 里 $0 是整个匹配、$1 是第一个分组。例如 #(\\d+) => https://github.com/组织/仓库/issues/$1; [A-Z]+-\\d+ => https://jira.example.com/browse/$0。悬停卡片里匹配的文字可点击。",
                "\"regex => URL\", separated by ;. In the URL $0 is the whole match and $1 the first group, e.g. #(\\d+) => https://github.com/org/repo/issues/$1; [A-Z]+-\\d+ => https://jira.example.com/browse/$0. Matches become clickable in the hover card.",
                "", "issue link jira github 链接 正则 regex"));
            var provider = D(category, ProviderKey, SettingKind.Choice, "版本管理", "Version control",
                "自动：按文件所在目录最近一层判断是 Git 还是 Perforce（Git 仓库套在 P4 工作区里时取近的那层）。Perforce 用本机 p4 命令行和它自己的登录票据，UEToVS 不保存密码；不会定时访问服务器。",
                "Auto: the nearest of Git and Perforce above the file wins. Perforce uses the local p4 command line with its own login ticket (UEToVS never stores a password) and never polls the server.",
                nameof(VcsProviderChoice.Auto), "git perforce p4 vcs 版本管理 源代码管理");
            provider.Options = new[]
            {
                new SettingOption(nameof(VcsProviderChoice.Auto), "自动", "Auto"),
                new SettingOption(nameof(VcsProviderChoice.Git), "仅 Git", "Git only"),
                new SettingOption(nameof(VcsProviderChoice.Perforce), "仅 Perforce", "Perforce only"),
                new SettingOption(nameof(VcsProviderChoice.Off), "关闭", "Off"),
            };
            list.Add(provider);
        }

        public static void ToValues(HostSettings s, IDictionary<string, string> d)
        {
            d[AnnotateKey] = B(s.VcsAnnotate);
            d[InlineBlameKey] = B(s.VcsInlineBlame);
            d[InlineBlameScopeKey] = s.VcsInlineBlameScope.ToString();
            d[InlineBlameEngineKey] = B(s.VcsInlineBlameEngine);
            d[ChangeBarsKey] = B(s.VcsChangeBars);
            d[HistoryMaxKey] = s.VcsHistoryMaxCommits.ToString(CultureInfo.InvariantCulture);
            d[IssueLinksKey] = s.VcsIssueLinks ?? string.Empty;
            d[ProviderKey] = s.VcsProvider.ToString();
        }

        public static bool TrySet(HostSettings s, string key, string v)
        {
            switch (key)
            {
                case AnnotateKey: s.VcsAnnotate = PB(v); return true;
                case InlineBlameKey: s.VcsInlineBlame = PB(v); return true;
                case InlineBlameScopeKey:
                    s.VcsInlineBlameScope = Enum.TryParse(v, true, out InlineBlameScope sc) && Enum.IsDefined(typeof(InlineBlameScope), sc) ? sc : InlineBlameScope.CurrentLine;
                    return true;
                case InlineBlameEngineKey: s.VcsInlineBlameEngine = PB(v); return true;
                case ChangeBarsKey: s.VcsChangeBars = PB(v); return true;
                case HistoryMaxKey:
                    s.VcsHistoryMaxCommits = int.TryParse(v, NumberStyles.Integer, CultureInfo.InvariantCulture, out int n) ? Math.Max(MinHistory, Math.Min(MaxHistory, n)) : DefaultHistory;
                    return true;
                case IssueLinksKey: s.VcsIssueLinks = v ?? string.Empty; return true;
                case ProviderKey:
                    s.VcsProvider = Enum.TryParse(v, true, out VcsProviderChoice p) && Enum.IsDefined(typeof(VcsProviderChoice), p) ? p : VcsProviderChoice.Auto;
                    return true;
                default: return false;
            }
        }

        public static void Describe(HostSettings s, IDictionary<string, object> d)
        {
            d["vcsAnnotate"] = s.VcsAnnotate;
            d["vcsInlineBlame"] = s.VcsInlineBlame;
            d["vcsInlineBlameScope"] = s.VcsInlineBlameScope.ToString();
            d["vcsInlineBlameEngine"] = s.VcsInlineBlameEngine;
            d["vcsChangeBars"] = s.VcsChangeBars;
            d["vcsHistoryMaxCommits"] = s.VcsHistoryMaxCommits;
            d["vcsIssueLinks"] = (s.VcsIssueLinks ?? string.Empty).Length > 0;
            d["vcsProvider"] = s.VcsProvider.ToString();
        }

        private static string B(bool v) => v ? "true" : "false";
        private static bool PB(string v) => string.Equals(v, "true", StringComparison.OrdinalIgnoreCase);
    }
}
