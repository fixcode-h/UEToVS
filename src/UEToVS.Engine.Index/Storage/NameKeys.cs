using System;
using System.Collections.Generic;
using UEToVS.Core.Matching;

namespace UEToVS.Engine.Index.Storage
{
    /// <summary>Search-acceleration keys over a name: lowercase trigrams and lowercase word-start initials.</summary>
    internal static class NameKeys
    {
        public static ulong Trigram(char a, char b, char c) => ((ulong)a << 32) | ((ulong)b << 16) | c;

        /// <summary>Distinct trigrams of an already lowercased string.</summary>
        public static void Trigrams(string lower, HashSet<ulong> into)
        {
            into.Clear();
            for (int i = 0; i + 3 <= lower.Length; i++) into.Add(Trigram(lower[i], lower[i + 1], lower[i + 2]));
        }

        /// <summary>
        /// Distinct lowercase characters at word starts (camel humps, after separators, digits). Every exact, prefix
        /// or camel-hump match of <see cref="FuzzyMatcher"/> starts at one of them. The character after a UE type prefix
        /// (AActor -> 'a' of Actor) is always included so <see cref="FuzzyMatcher.MatchUnreal"/> stays covered.
        /// </summary>
        public static void Initials(string name, HashSet<uint> into)
        {
            into.Clear();
            for (int i = 0; i < name.Length; i++)
                if (FuzzyMatcher.IsWordStart(name, i)) into.Add(char.ToLowerInvariant(name[i]));
            if (UnrealNaming.StripTypePrefix(name) != null) into.Add(char.ToLowerInvariant(name[1]));
        }
    }
}
