using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace UEToVS.Engine.Cli
{
    /// <summary>A file position, 1-based line and column as written in golden files (bench samples are 0-based).</summary>
    public readonly struct GoldenLocation : IEquatable<GoldenLocation>
    {
        public GoldenLocation(string file, int line)
        {
            File = file;
            Line = line;
        }

        public string File { get; }

        /// <summary>1-based.</summary>
        public int Line { get; }

        public bool Equals(GoldenLocation other) => Line == other.Line && GoldenSet.SamePath(File, other.File);
        public override bool Equals(object obj) => obj is GoldenLocation g && Equals(g);
        public override int GetHashCode() => Line;
        public override string ToString() => File + ":" + Line.ToString(CultureInfo.InvariantCulture);
    }

    /// <summary>One position of a golden file and the locations a compiler-accurate tool (clangd) says it resolves to.</summary>
    public sealed class GoldenEntry
    {
        public BenchSample Sample { get; set; }

        /// <summary>Accepted answers for go to definition: clangd's definition and declaration locations. Empty = no answer (skipped).</summary>
        public List<GoldenLocation> Expected { get; } = new List<GoldenLocation>();
    }

    /// <summary>How the definition results of one run compare with a golden file.</summary>
    public sealed class GoldenScore
    {
        /// <summary>Entries that have an expected answer (the others are not scored).</summary>
        public int Scored { get; set; }

        /// <summary>The first target is one of the expected locations.</summary>
        public int Top1 { get; set; }

        /// <summary>Some target is one of the expected locations.</summary>
        public int AnyHit { get; set; }

        /// <summary>No target at all.</summary>
        public int Empty { get; set; }

        /// <summary>Targets, none of them expected.</summary>
        public int Wrong => Scored - AnyHit - Empty;

        public double Top1Ratio => Scored == 0 ? 0 : Math.Round((double)Top1 / Scored, 4);
        public double AnyHitRatio => Scored == 0 ? 0 : Math.Round((double)AnyHit / Scored, 4);

        public JsonObject ToJson() => new JsonObject
        {
            ["scored"] = Scored, ["top1"] = Top1, ["anyHit"] = AnyHit, ["empty"] = Empty, ["wrong"] = Wrong,
            ["top1Ratio"] = Top1Ratio, ["anyHitRatio"] = AnyHitRatio,
        };

        public static GoldenScore FromJson(JsonNode n) => n == null ? null : new GoldenScore
        {
            Scored = (int?)n["scored"] ?? 0, Top1 = (int?)n["top1"] ?? 0, AnyHit = (int?)n["anyHit"] ?? 0, Empty = (int?)n["empty"] ?? 0,
        };

        public string Format() => string.Format(CultureInfo.InvariantCulture,
            "accuracy: definition top-1 {0:0.0}%, any {1:0.0}%, wrong {2}, empty {3} (of {4} scored positions)",
            Top1Ratio * 100, AnyHitRatio * 100, Wrong, Empty, Scored);
    }

    /// <summary>
    /// Golden files for the accuracy benchmark (docs/基准测试.md, "准确率"): JSON lines
    /// <c>{"file":"…","line":12,"col":5,"word":"Foo","expected":[{"file":"…","line":40}]}</c>, 1-based.
    /// <c>bench --emit-samples</c> writes the positions with an empty "expected"; scripts/golden_clangd.py fills it from
    /// clangd; <c>bench --golden</c> runs on those positions and scores go to definition.
    /// </summary>
    public static class GoldenSet
    {
        public static bool SamePath(string a, string b)
        {
            if (a == null || b == null) return a == b;
            return string.Equals(Normalize(a), Normalize(b), StringComparison.OrdinalIgnoreCase);
        }

        public static string Normalize(string path) => path.Replace('\\', '/').TrimEnd('/');

        public static string ToJsonLine(GoldenEntry e)
        {
            var expected = new JsonArray();
            foreach (var x in e.Expected) expected.Add(new JsonObject { ["file"] = x.File, ["line"] = x.Line });
            return new JsonObject
            {
                ["file"] = e.Sample.File, ["line"] = e.Sample.Line + 1, ["col"] = e.Sample.Column + 1, ["word"] = e.Sample.Word,
                ["expected"] = expected,
            }.ToJsonString();
        }

        /// <summary>Parses one golden line (null for blank or malformed lines).</summary>
        public static GoldenEntry Parse(string line)
        {
            if (string.IsNullOrWhiteSpace(line)) return null;
            try
            {
                if (!(JsonNode.Parse(line) is JsonObject n)) return null;
                string file = (string)n["file"];
                int ln = (int?)n["line"] ?? 0, col = (int?)n["col"] ?? 0;
                if (string.IsNullOrEmpty(file) || ln < 1 || col < 1) return null;
                var e = new GoldenEntry { Sample = new BenchSample { File = file, Line = ln - 1, Column = col - 1, Word = (string)n["word"] ?? string.Empty } };
                if (n["expected"] is JsonArray arr)
                    foreach (var x in arr)
                    {
                        string f = (string)x?["file"];
                        int l = (int?)x?["line"] ?? 0;
                        if (!string.IsNullOrEmpty(f) && l >= 1 && !e.Expected.Contains(new GoldenLocation(f, l))) e.Expected.Add(new GoldenLocation(f, l));
                    }
                return e;
            }
            catch (Exception ex) when (ex is JsonException || ex is FormatException || ex is InvalidOperationException)
            {
                return null;
            }
        }

        public static List<GoldenEntry> Load(string file) =>
            File.ReadLines(file).Select(Parse).Where(e => e != null).ToList();

        public static void Save(string file, IEnumerable<GoldenEntry> entries)
        {
            string dir = Path.GetDirectoryName(Path.GetFullPath(file));
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            File.WriteAllLines(file, entries.Select(ToJsonLine));
        }

        /// <summary>
        /// Scores the definition targets of each entry (<paramref name="actual"/>[i] belongs to <paramref name="entries"/>[i];
        /// 1-based lines). Entries without an expected answer are not scored.
        /// </summary>
        public static GoldenScore Score(IReadOnlyList<GoldenEntry> entries, IReadOnlyList<IReadOnlyList<GoldenLocation>> actual)
        {
            var s = new GoldenScore();
            for (int i = 0; i < entries.Count; i++)
            {
                var e = entries[i];
                if (e.Expected.Count == 0) continue;
                s.Scored++;
                var got = i < actual.Count ? actual[i] : null;
                if (got == null || got.Count == 0) { s.Empty++; continue; }
                if (e.Expected.Contains(got[0])) s.Top1++;
                if (got.Any(g => e.Expected.Contains(g))) s.AnyHit++;
            }
            return s;
        }
    }
}
