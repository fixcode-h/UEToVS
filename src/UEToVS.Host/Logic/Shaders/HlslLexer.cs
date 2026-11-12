using System;
using System.Collections.Generic;
using UEToVS.Host.Logic.Search;

namespace UEToVS.Host.Logic.Shaders
{
    public enum HlslTokenKind
    {
        Comment,
        String,
        Number,
        Keyword,
        Preprocessor,
        Type,
        Intrinsic,
        Semantic,
    }

    public readonly struct HlslToken
    {
        public HlslToken(int start, int length, HlslTokenKind kind)
        {
            Start = start;
            Length = length;
            Kind = kind;
        }

        public int Start { get; }
        public int Length { get; }
        public HlslTokenKind Kind { get; }
        public override string ToString() => Kind + "[" + Start + "," + (Start + Length) + ")";
    }

    /// <summary>
    /// Lexical colouring of HLSL / UE shader files (.usf / .ush): comments, strings, numbers and preprocessor from
    /// <see cref="CppLexer"/>, plus HLSL keywords, vector / matrix / resource types, intrinsics and SV_ semantics. Line based with
    /// a carried block-comment state, like the preview colouring. Pure.
    /// </summary>
    public static class HlslLexer
    {
        private static readonly HashSet<string> Keywords = new HashSet<string>(StringComparer.Ordinal)
        {
            "break", "case", "cbuffer", "centroid", "class", "const", "continue", "default", "discard", "do", "else", "extern", "false", "for",
            "groupshared", "if", "in", "inline", "inout", "interface", "linear", "nointerpolation", "noperspective", "out", "precise", "register",
            "return", "row_major", "column_major", "sample", "shared", "static", "struct", "switch", "tbuffer", "true", "typedef", "uniform",
            "volatile", "while", "namespace", "template", "typename", "this", "packoffset", "triangle", "line", "point", "lineadj", "triangleadj",
        };

        private static readonly HashSet<string> Types = new HashSet<string>(StringComparer.Ordinal)
        {
            "void", "bool", "int", "uint", "dword", "half", "float", "double", "min16float", "min10float", "min16int", "min12int", "min16uint",
            "int64_t", "uint64_t", "float16_t", "int16_t", "uint16_t", "matrix", "vector", "string",
            "Texture1D", "Texture1DArray", "Texture2D", "Texture2DArray", "Texture2DMS", "Texture2DMSArray", "Texture3D", "TextureCube", "TextureCubeArray",
            "RWTexture1D", "RWTexture1DArray", "RWTexture2D", "RWTexture2DArray", "RWTexture3D", "Buffer", "RWBuffer", "ByteAddressBuffer",
            "RWByteAddressBuffer", "StructuredBuffer", "RWStructuredBuffer", "AppendStructuredBuffer", "ConsumeStructuredBuffer", "ConstantBuffer",
            "SamplerState", "SamplerComparisonState", "RaytracingAccelerationStructure", "RayDesc", "TriangleStream", "LineStream", "PointStream",
            "InputPatch", "OutputPatch", "FeedbackTexture2D",
            // UE shader typedefs
            "FLWCVector3", "FDFVector3", "FDFScalar", "MaterialFloat", "MaterialFloat2", "MaterialFloat3", "MaterialFloat4",
        };

        private static readonly HashSet<string> Intrinsics = new HashSet<string>(StringComparer.Ordinal)
        {
            "abs", "acos", "all", "any", "asfloat", "asin", "asint", "asuint", "atan", "atan2", "ceil", "clamp", "clip", "cos", "cosh", "countbits",
            "cross", "ddx", "ddy", "ddx_fine", "ddy_fine", "ddx_coarse", "ddy_coarse", "degrees", "determinant", "distance", "dot", "exp", "exp2",
            "faceforward", "firstbithigh", "firstbitlow", "floor", "fmod", "frac", "frexp", "fwidth", "isfinite", "isinf", "isnan", "ldexp", "length",
            "lerp", "log", "log10", "log2", "mad", "max", "min", "modf", "mul", "normalize", "pow", "radians", "rcp", "reflect", "refract",
            "reversebits", "round", "rsqrt", "saturate", "sign", "sin", "sincos", "sinh", "smoothstep", "sqrt", "step", "tan", "tanh", "transpose",
            "trunc", "f16tof32", "f32tof16", "GroupMemoryBarrierWithGroupSync", "AllMemoryBarrierWithGroupSync", "DeviceMemoryBarrier",
            "InterlockedAdd", "InterlockedMin", "InterlockedMax", "InterlockedOr", "InterlockedAnd", "InterlockedExchange", "InterlockedCompareExchange",
            "WaveActiveSum", "WaveActiveMax", "WaveActiveMin", "WaveReadLaneFirst", "WaveGetLaneIndex", "WaveIsFirstLane",
            "Sample", "SampleLevel", "SampleGrad", "SampleBias", "SampleCmp", "SampleCmpLevelZero", "Load", "Gather", "GetDimensions", "TraceRay",
            // UE shader helpers
            "Square", "Pow2", "Pow4", "Pow5", "Luminance", "Texture2DSample", "Texture2DSampleLevel", "TextureCubeSample", "PositiveClampedPow",
        };

        /// <summary>Lexes one line from <paramref name="state"/>; returns the state at its end.</summary>
        public static CppLexState LexLine(string line, CppLexState state, List<HlslToken> output)
        {
            line = line ?? string.Empty;
            var basic = new List<CppToken>();
            var next = CppLexer.LexLine(line, state, basic);
            int bi = 0;
            int i = 0;
            while (i < line.Length)
            {
                // copy the base tokens that start here (comments, strings, numbers, preprocessor)
                if (bi < basic.Count && basic[bi].Start <= i)
                {
                    var b = basic[bi++];
                    if (b.Start + b.Length <= i) continue;
                    if (b.Kind != CppTokenKind.Keyword) output.Add(new HlslToken(b.Start, b.Length, Map(b.Kind)));
                    else Word(line, b.Start, b.Start + b.Length, output);
                    i = Math.Max(i, b.Start + b.Length);
                    continue;
                }
                int limit = bi < basic.Count ? basic[bi].Start : line.Length;
                char c = line[i];
                if (char.IsLetter(c) || c == '_')
                {
                    int s = i;
                    while (i < limit && (char.IsLetterOrDigit(line[i]) || line[i] == '_')) i++;
                    Word(line, s, i, output);
                    continue;
                }
                i++;
            }
            return next;
        }

        private static HlslTokenKind Map(CppTokenKind k)
        {
            switch (k)
            {
                case CppTokenKind.Comment: return HlslTokenKind.Comment;
                case CppTokenKind.String: return HlslTokenKind.String;
                case CppTokenKind.Number: return HlslTokenKind.Number;
                case CppTokenKind.Preprocessor: return HlslTokenKind.Preprocessor;
                default: return HlslTokenKind.Keyword;
            }
        }

        private static void Word(string line, int s, int e, List<HlslToken> output)
        {
            string w = line.Substring(s, e - s);
            if (Keywords.Contains(w)) output.Add(new HlslToken(s, e - s, HlslTokenKind.Keyword));
            else if (IsType(w)) output.Add(new HlslToken(s, e - s, HlslTokenKind.Type));
            else if (Intrinsics.Contains(w) && NextNonSpace(line, e) == '(') output.Add(new HlslToken(s, e - s, HlslTokenKind.Intrinsic));
            else if (w.StartsWith("SV_", StringComparison.OrdinalIgnoreCase) && PrevNonSpace(line, s) == ':') output.Add(new HlslToken(s, e - s, HlslTokenKind.Semantic));
        }

        /// <summary>float, float3, half4x4, uint2, bool3, int1x2 … and the resource / UE types.</summary>
        public static bool IsType(string w)
        {
            if (Types.Contains(w)) return true;
            foreach (var b in new[] { "float", "half", "int", "uint", "bool", "double", "min16float", "min16int", "min16uint" })
            {
                if (!w.StartsWith(b, StringComparison.Ordinal) || w.Length == b.Length) continue;
                string rest = w.Substring(b.Length);
                if (rest.Length == 1 && rest[0] >= '1' && rest[0] <= '4') return true;
                if (rest.Length == 3 && rest[0] >= '1' && rest[0] <= '4' && rest[1] == 'x' && rest[2] >= '1' && rest[2] <= '4') return true;
            }
            return false;
        }

        private static char NextNonSpace(string line, int i)
        {
            while (i < line.Length && char.IsWhiteSpace(line[i])) i++;
            return i < line.Length ? line[i] : '\0';
        }

        private static char PrevNonSpace(string line, int i)
        {
            i--;
            while (i >= 0 && char.IsWhiteSpace(line[i])) i--;
            return i >= 0 ? line[i] : '\0';
        }

        /// <summary>The path of an <c>#include "…"</c> line when the column is on it (e.g. "/Engine/Private/Common.ush"), else null.</summary>
        public static string IncludeAt(string line, int column, out int start, out int length)
        {
            start = length = 0;
            if (string.IsNullOrEmpty(line)) return null;
            string t = line.TrimStart();
            if (!t.StartsWith("#", StringComparison.Ordinal)) return null;
            string rest = t.Substring(1).TrimStart();
            if (!rest.StartsWith("include", StringComparison.Ordinal)) return null;
            int q1 = line.IndexOfAny(new[] { '"', '<' });
            if (q1 < 0) return null;
            int q2 = line.IndexOf(line[q1] == '"' ? '"' : '>', q1 + 1);
            if (q2 < 0) return null;
            if (column >= 0 && (column < q1 || column > q2)) return null;
            start = q1 + 1;
            length = q2 - q1 - 1;
            return line.Substring(start, length);
        }

        /// <summary>
        /// Candidate files for a shader include. Virtual paths map like UE does: "/Engine/…" → &lt;Engine&gt;/Shaders/…, "/Plugin/X/…"
        /// via <paramref name="pluginShaderDirs"/> (virtual root → folder), "/Project/…" → &lt;Project&gt;/Shaders/…; relative paths
        /// resolve against the including file's folder. Pure: callers check existence.
        /// </summary>
        public static List<string> ResolveInclude(string include, string currentFile, string engineDir, string projectDir, IReadOnlyDictionary<string, string> pluginShaderDirs)
        {
            var list = new List<string>();
            if (string.IsNullOrWhiteSpace(include)) return list;
            string inc = include.Replace('\\', '/');
            if (inc.StartsWith("/", StringComparison.Ordinal))
            {
                if (pluginShaderDirs != null)
                    foreach (var kv in pluginShaderDirs)
                    {
                        string root = kv.Key.TrimEnd('/') + "/";
                        if (inc.StartsWith(root, StringComparison.OrdinalIgnoreCase)) list.Add(Combine(kv.Value, inc.Substring(root.Length)));
                    }
                if (engineDir != null && inc.StartsWith("/Engine/", StringComparison.OrdinalIgnoreCase))
                    list.Add(Combine(Combine(engineDir, "Shaders"), inc.Substring("/Engine/".Length)));
                if (projectDir != null && inc.StartsWith("/Project/", StringComparison.OrdinalIgnoreCase))
                    list.Add(Combine(Combine(projectDir, "Shaders"), inc.Substring("/Project/".Length)));
            }
            else if (!string.IsNullOrEmpty(currentFile))
            {
                string dir = System.IO.Path.GetDirectoryName(currentFile) ?? string.Empty;
                list.Add(Combine(dir, inc));
            }
            return list;
        }

        private static string Combine(string a, string b) =>
            System.IO.Path.GetFullPath(System.IO.Path.Combine(a, b.Replace('/', System.IO.Path.DirectorySeparatorChar)));
    }
}
