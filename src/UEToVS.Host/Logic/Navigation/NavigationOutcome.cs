using System;
using System.Collections.Generic;
using System.Linq;
using UEToVS.Contracts;
using UEToVS.Core.Unreal;
using UEToVS.Host.Logic.Localization;

namespace UEToVS.Host.Logic.Navigation
{
    public enum NavigationDecision
    {
        /// <summary>Nothing to do; <see cref="NavigationOutcome.Message"/> says why (null = silent, e.g. canceled).</summary>
        Message = 0,
        /// <summary>Exactly one target: jump.</summary>
        Jump = 1,
        /// <summary>Several targets: show the chooser.</summary>
        Choose = 2,
    }

    /// <summary>What go to definition / implementation does with an engine answer. Pure.</summary>
    public sealed class NavigationOutcome
    {
        public NavigationDecision Decision { get; private set; }

        /// <summary>Targets to jump to / choose from, deduplicated and ranked.</summary>
        public IReadOnlyList<NavigationTarget> Targets { get; private set; } = Array.Empty<NavigationTarget>();

        /// <summary>Status-bar text (localized) for <see cref="NavigationDecision.Message"/>; may carry a hint for the others.</summary>
        public string Message { get; private set; }

        public string Word { get; private set; }

        /// <summary>
        /// 同名文件的不同副本（例如插件里并存的两份 lua 源码 lua-5.4.3/src/lauxlib.c 和 lua-5.4.6/src/lauxlib.c）在列表里只显示
        /// "lauxlib.c:311" 时看起来一模一样：这里给这些目标一个区分用的目录名（第一处不同的目录），其他目标没有。
        /// </summary>
        public IReadOnlyDictionary<NavigationTarget, string> LocationHints { get; private set; } = new Dictionary<NavigationTarget, string>();

        public static NavigationOutcome Decide(EngineCallStatus status, NavigationResponse response, NavigationKind kind)
        {
            var o = new NavigationOutcome { Word = response?.Word };
            if (status != EngineCallStatus.Ok || response == null)
            {
                o.Message = EngineCallText.ForStatus(status);
                return o;
            }
            var targets = Rank(response.Targets);
            o.Targets = targets;
            o.LocationHints = DistinguishingDirectories(targets);
            if (targets.Count == 0)
            {
                o.Message = NoResultMessage(response, kind);
                return o;
            }
            // 手写文件只命中一处时直接跳过去，不为 X.generated.h 之类的生成文件弹列表（它们排在后面，列表里也不再需要选）；
            // 没有命中手写文件时才在生成文件里跳 / 选
            int handwritten = targets.Count(t => !GeneratedFiles.IsGenerated(t.Symbol.Location.FilePath));
            int pool = handwritten > 0 ? handwritten : targets.Count;
            o.Decision = pool == 1 ? NavigationDecision.Jump : NavigationDecision.Choose;
            if (!response.IndexReady) o.Message = NavStrings.IndexNotReady;
            return o;
        }

        private static string NormalizePath(string path) => path.Replace('/', '\\').Replace("\\.\\", "\\").TrimEnd('\\');

        /// <summary>
        /// 文件名相同、路径不同的目标 → 各自第一处不同的目录名（去掉共同前缀后的第一段），例如 "lua-5.4.3"；
        /// 没有同名冲突的目标不在结果里。
        /// </summary>
        public static Dictionary<NavigationTarget, string> DistinguishingDirectories(IEnumerable<NavigationTarget> targets)
        {
            var result = new Dictionary<NavigationTarget, string>();
            var withPath = (targets ?? Enumerable.Empty<NavigationTarget>())
                .Where(t => !string.IsNullOrEmpty(t?.Symbol?.Location?.FilePath))
                .Select(t => (T: t, Segs: NormalizePath(t.Symbol.Location.FilePath).Split(new[] { '\\' }, StringSplitOptions.RemoveEmptyEntries)))
                .ToList();
            foreach (var group in withPath.GroupBy(x => x.Segs[x.Segs.Length - 1], StringComparer.OrdinalIgnoreCase))
            {
                var items = group.ToList();
                if (items.Select(x => string.Join("\\", x.Segs)).Distinct(StringComparer.OrdinalIgnoreCase).Count() < 2) continue;
                // 所有路径共同的目录前缀长度（不含文件名）
                int common = 0;
                int minDirs = items.Min(x => x.Segs.Length - 1);
                while (common < minDirs && items.All(x => string.Equals(x.Segs[common], items[0].Segs[common], StringComparison.OrdinalIgnoreCase))) common++;
                foreach (var x in items)
                    if (common < x.Segs.Length - 1) result[x.T] = x.Segs[common];
            }
            return result;
        }

        /// <summary>Localized "why nothing" text: not on a word / not found (+ engine reason, + index not ready).</summary>
        public static string NoResultMessage(NavigationResponse r, NavigationKind kind)
        {
            if (r == null) return NavStrings.EngineFailed;
            if (string.IsNullOrEmpty(r.Word) || r.Reason == "no-word") return NavStrings.NotOnIdentifier;
            string msg = kind == NavigationKind.Implementation ? NavStrings.NoImplementation(r.Word) : NavStrings.NoDefinition(r.Word);
            string reason = NavStrings.ReasonText(r.Reason);
            if (!string.IsNullOrEmpty(reason)) msg += NavStrings.ReasonSuffix(reason);
            if (!r.IndexReady) msg += NavStrings.IndexNotReady;
            return msg;
        }

        /// <summary>
        /// Drops targets without a location and duplicates (same file + line), keeps the engine's order otherwise but puts
        /// hand-written files before UE-generated ones (X.generated.h, X.gen.cpp, Intermediate), then project items before
        /// engine items (stable).
        /// </summary>
        public static List<NavigationTarget> Rank(IEnumerable<NavigationTarget> targets)
        {
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var list = new List<(NavigationTarget T, int Index)>();
            int i = 0;
            foreach (var t in targets ?? Enumerable.Empty<NavigationTarget>())
            {
                var loc = t?.Symbol?.Location;
                if (loc == null || string.IsNullOrEmpty(loc.FilePath)) continue;
                if (!seen.Add(NormalizePath(loc.FilePath) + "|" + loc.Line)) continue;
                list.Add((t, i++));
            }
            return list.OrderBy(x => GeneratedFiles.Rank(x.T.Symbol.Location.FilePath))
                .ThenBy(x => SymbolPresentation.IsNonProject(x.T.Symbol.Origin) ? 1 : 0).ThenBy(x => x.Index).Select(x => x.T).ToList();
        }
    }
}
