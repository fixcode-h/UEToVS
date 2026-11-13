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
                if (!seen.Add(loc.FilePath + "|" + loc.Line)) continue;
                list.Add((t, i++));
            }
            return list.OrderBy(x => GeneratedFiles.Rank(x.T.Symbol.Location.FilePath))
                .ThenBy(x => SymbolPresentation.IsNonProject(x.T.Symbol.Origin) ? 1 : 0).ThenBy(x => x.Index).Select(x => x.T).ToList();
        }
    }
}
