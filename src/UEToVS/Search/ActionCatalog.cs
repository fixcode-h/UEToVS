using System;
using System.Collections.Generic;
using System.Linq;
using EnvDTE;
using Microsoft.VisualStudio.Shell;
using UEToVS.Core.Matching;
using UEToVS.UI;

namespace UEToVS.Search
{
    /// <summary>VS commands and UEToVS toggles for the Actions tab (Rider "Find Action").</summary>
    internal sealed class ActionCatalog
    {
        internal sealed class ActionEntry
        {
            public string Name;          // canonical, e.g. Edit.GoToDefinition
            public string Display;       // localized, e.g. 编辑.转到定义
            public string Bindings;      // "Ctrl+B, F12"
            public Func<bool> Getter;    // toggles
            public Action<bool> Setter;
            public Action Run;
        }

        private List<ActionEntry> _commands;
        private readonly List<ActionEntry> _toggles = new List<ActionEntry>();

        public void AddToggle(string title, Func<bool> get, Action<bool> set) =>
            _toggles.Add(new ActionEntry { Name = title, Display = title, Getter = get, Setter = set });

        public void AddAction(string title, Action run, string bindings = null) =>
            _toggles.Add(new ActionEntry { Name = title, Display = title, Run = run, Bindings = bindings });

        public bool IsLoaded => _commands != null;

        /// <summary>Enumerates DTE commands once (UI thread, ~1 s on a big VS).</summary>
        public void EnsureLoaded()
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            if (_commands != null) return;
            var list = new List<ActionEntry>(12000);
            try
            {
                foreach (Command c in VsHelpers.Dte.Commands)
                {
                    string name;
                    try { name = c.Name; } catch { continue; }
                    if (string.IsNullOrEmpty(name)) continue;
                    string local = null;
                    try { local = c.LocalizedName; } catch { }
                    string keys = null;
                    try
                    {
                        if (c.Bindings is object[] b && b.Length > 0)
                            keys = string.Join(", ", b.Cast<object>().Select(x => x?.ToString() ?? "").Select(StripScope).Distinct().Take(2));
                    }
                    catch { }
                    list.Add(new ActionEntry { Name = name, Display = string.IsNullOrEmpty(local) ? name : local, Bindings = keys });
                }
            }
            catch (Exception ex)
            {
                OutputLog.Write("Reading VS commands failed: " + ex.Message);
            }
            _commands = list;
        }

        public void Invalidate() => _commands = null;

        private static string StripScope(string binding)
        {
            int i = binding.IndexOf("::", StringComparison.Ordinal);
            return i < 0 ? binding : binding.Substring(i + 2);
        }

        public List<SearchItem> Search(string pattern, int max)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            EnsureLoaded();
            var matcher = new FuzzyMatcher(pattern ?? "");
            if (matcher.IsEmpty) return new List<SearchItem>();
            var scored = new List<(ActionEntry e, MatchResult m, bool onDisplay, int score)>();
            foreach (var e in _toggles.Concat(_commands))
            {
                // match the last segment ("GoToDefinition") and the full name; prefer the localized text
                var m = matcher.Match(e.Display);
                bool onDisplay = true;
                if (!m.IsMatch)
                {
                    string last = e.Display.Substring(e.Display.LastIndexOf('.') + 1);
                    var m2 = matcher.Match(last);
                    if (m2.IsMatch) m = m2.ShiftRanges(e.Display.Length - last.Length);
                }
                if (!m.IsMatch && e.Name != e.Display)
                {
                    m = matcher.Match(e.Name);
                    onDisplay = false;
                    if (!m.IsMatch)
                    {
                        string last = e.Name.Substring(e.Name.LastIndexOf('.') + 1);
                        var m3 = matcher.Match(last);
                        if (m3.IsMatch) m = m3;
                    }
                }
                if (!m.IsMatch) continue;
                int score = m.Score + (e.Getter != null || e.Run != null ? 500 : 0) + (e.Bindings != null ? 100 : 0);
                scored.Add((e, m, onDisplay, score));
            }

            var result = new List<SearchItem>();
            foreach (var (e, m, onDisplay, score) in scored.OrderByDescending(x => x.score).ThenBy(x => x.e.Display.Length).Take(max))
            {
                var entry = e;
                bool available = true;
                if (entry.Getter == null && entry.Run == null)
                {
                    try { available = VsHelpers.Dte.Commands.Item(entry.Name).IsAvailable; } catch { available = false; }
                }
                var item = new SearchItem
                {
                    Kind = ItemKind.Action,
                    Title = entry.Getter != null ? entry.Display + (entry.Getter() ? "：开" : "：关") : entry.Display,
                    Highlights = onDisplay ? m.Ranges.SelectMany(r => new[] { r.Start, r.Length }).ToArray() : Array.Empty<int>(),
                    Detail = entry.Name != entry.Display ? entry.Name : null,
                    Right = entry.Bindings,
                    Badge = entry.Getter != null ? "◐" : "▶",
                    BadgeColor = BadgeColor.Gray,
                    Dimmed = !available,
                    IsToggle = entry.Getter != null,
                    Score = score,
                };
                if (entry.Getter != null)
                {
                    item.ToggleState = entry.Getter();
                    item.Activate = mode =>
                    {
                        entry.Setter(!entry.Getter());
                        return mode == OpenMode.KeepOpen ? false : true;
                    };
                }
                else if (entry.Run != null)
                {
                    item.Activate = _ => { entry.Run(); return true; };
                }
                else
                {
                    item.Activate = _ =>
                    {
                        ThreadHelper.ThrowIfNotOnUIThread();
                        string name = entry.Name;
                        // run after the popup closed so the command sees the editor as active
                        ThreadHelper.JoinableTaskFactory.RunAsync(async () =>
                        {
                            await System.Threading.Tasks.Task.Yield();
                            await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
                            try { VsHelpers.Dte.ExecuteCommand(name); }
                            catch (Exception ex) { OutputLog.Status("UEToVS: " + name + " 不可用：" + ex.Message); }
                        }).FileAndForget("UEToVS/RunAction");
                        return true;
                    };
                }
                result.Add(item);
            }
            return result;
        }
    }
}
