using System;
using System.Text;

namespace UEToVS.Engine.Syntax
{
    /// <summary>C++ keywords (plus the contextual <c>override</c>/<c>final</c>). <see cref="Token.Sub"/> for <see cref="TokenKind.Keyword"/>.</summary>
    public enum Keyword : byte
    {
        None = 0,
        Alignas, Alignof, Asm, Auto, Bool, Break, Case, Catch, Char, Char8_t, Char16_t, Char32_t, Class, Concept, Const,
        Consteval, Constexpr, Constinit, Const_cast, Continue, Co_await, Co_return, Co_yield, Decltype, Default, Delete,
        Do, Double, Dynamic_cast, Else, Enum, Explicit, Export, Extern, False, Final, Float, For, Friend, Goto, If, Inline,
        Int, Long, Mutable, Namespace, New, Noexcept, Nullptr, Operator, Override, Private, Protected, Public, Register,
        Reinterpret_cast, Requires, Return, Short, Signed, Sizeof, Static, Static_assert, Static_cast, Struct, Switch,
        Template, This, Thread_local, Throw, True, Try, Typedef, Typeid, Typename, Union, Unsigned, Using, Virtual, Void,
        Volatile, Wchar_t, While,
    }

    internal static class Keywords
    {
        private static readonly Utf8Map<Keyword> Map = Build();

        private static Utf8Map<Keyword> Build()
        {
            var m = new Utf8Map<Keyword>(128);
            foreach (Keyword k in Enum.GetValues(typeof(Keyword)))
            {
                if (k == Keyword.None) continue;
                m.Set(Encoding.ASCII.GetBytes(k.ToString().ToLowerInvariant()), k);
            }
            return m;
        }

        /// <summary>Returns the keyword for an identifier spelling, or <see cref="Keyword.None"/>.</summary>
        public static Keyword Lookup(ReadOnlySpan<byte> ident)
        {
            // every keyword starts with a lower-case letter and is 2..16 bytes long
            if (ident.Length < 2 || ident.Length > 16) return Keyword.None;
            byte c = ident[0];
            if (c < (byte)'a' || c > (byte)'z') return Keyword.None;
            return Map.TryGetValue(ident, out var k) ? k : Keyword.None;
        }

        public static bool IsBuiltinType(Keyword k)
        {
            switch (k)
            {
                case Keyword.Bool: case Keyword.Char: case Keyword.Char8_t: case Keyword.Char16_t: case Keyword.Char32_t:
                case Keyword.Double: case Keyword.Float: case Keyword.Int: case Keyword.Long: case Keyword.Short:
                case Keyword.Signed: case Keyword.Unsigned: case Keyword.Void: case Keyword.Wchar_t: case Keyword.Auto:
                    return true;
                default:
                    return false;
            }
        }
    }
}
