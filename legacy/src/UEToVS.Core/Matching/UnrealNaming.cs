namespace UEToVS.Core.Matching
{
    public static class UnrealNaming
    {
        /// <summary>UE type prefixes: A(ctor) U(Object) F(struct) E(num) I(nterface) T(emplate) S(late widget).</summary>
        public const string TypePrefixes = "AUFEITS";

        /// <summary>Returns the name without its UE type prefix ("ACharacter" -> "Character"), or null.</summary>
        public static string StripTypePrefix(string name)
        {
            if (name == null || name.Length < 3) return null;
            if (TypePrefixes.IndexOf(name[0]) < 0) return null;
            if (!char.IsUpper(name[1])) return null;
            // Avoid stripping acronyms like "UI" or "FPS" fully upper-case words.
            if (char.IsUpper(name[2]) && name.Length > 3 && char.IsUpper(name[3])) return null;
            return name.Substring(1);
        }
    }
}
