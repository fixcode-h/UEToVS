using System.Collections.Generic;

namespace UEToVS.Core.Matching
{
    /// <summary>How a pattern matched a name. Higher values rank higher.</summary>
    public enum MatchKind
    {
        None = 0,

        /// <summary>Typo-tolerant match (swapped / missing / extra / wrong letter); only with a tolerant matcher.</summary>
        Typo = 1,

        /// <summary>Pattern letters in order anywhere after a word start ("umcmp" -> UMeshComponent); only with a tolerant matcher.</summary>
        Subsequence = 2,
        Wildcard = 3,
        Substring = 4,
        CamelHump = 5,
        Prefix = 6,
        Exact = 7,
    }

    public readonly struct TextRange
    {
        public TextRange(int start, int length)
        {
            Start = start;
            Length = length;
        }

        public int Start { get; }
        public int Length { get; }
        public int End => Start + Length;

        public override string ToString() => $"[{Start},{Length}]";
    }

    public sealed class MatchResult
    {
        public static readonly MatchResult NoMatch = new MatchResult(MatchKind.None, 0, new TextRange[0]);

        public MatchResult(MatchKind kind, int score, IReadOnlyList<TextRange> ranges)
        {
            Kind = kind;
            Score = score;
            Ranges = ranges;
        }

        public MatchKind Kind { get; }

        /// <summary>Larger is better. Only comparable between results of the same pattern.</summary>
        public int Score { get; }

        /// <summary>Matched character ranges in the candidate, for highlighting.</summary>
        public IReadOnlyList<TextRange> Ranges { get; }

        public bool IsMatch => Kind != MatchKind.None;

        public MatchResult WithPenalty(int penalty) => new MatchResult(Kind, Score - penalty, Ranges);

        public MatchResult ShiftRanges(int offset)
        {
            if (offset == 0) return this;
            var shifted = new TextRange[Ranges.Count];
            for (int i = 0; i < Ranges.Count; i++) shifted[i] = new TextRange(Ranges[i].Start + offset, Ranges[i].Length);
            return new MatchResult(Kind, Score, shifted);
        }
    }
}
