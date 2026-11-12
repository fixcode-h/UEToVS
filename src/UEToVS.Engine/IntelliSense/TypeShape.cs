using System;
using System.Collections.Generic;
using System.Text;

namespace UEToVS.Engine.IntelliSense
{
    /// <summary>How a value of a type is accessed with <c>.</c> / <c>-&gt;</c>.</summary>
    public enum AccessShape : byte
    {
        Unknown = 0,
        /// <summary>Object, struct or reference: members with <c>.</c>.</summary>
        Value = 1,
        /// <summary><c>T*</c>: members with <c>-&gt;</c>.</summary>
        RawPointer = 2,
        /// <summary><c>TObjectPtr&lt;T&gt;</c>: a pointer for all practical purposes (VA treats it as one).</summary>
        ObjectPtr = 3,
        /// <summary>Smart pointer with its own API (<c>TSharedPtr</c>, <c>TWeakObjectPtr</c>...): <c>-&gt;</c> reaches the pointee, <c>.</c> the wrapper.</summary>
        SmartPointer = 4,
    }

    /// <summary>A type text split into head, template arguments and pointer depth: <c>const TArray&lt;AActor*&gt;&amp;</c> → TArray / [AActor*] / 0.</summary>
    public sealed class TypeShape
    {
        private static readonly HashSet<string> Smart = new HashSet<string>(StringComparer.Ordinal)
        {
            "TSharedPtr", "TSharedRef", "TWeakPtr", "TUniquePtr", "TWeakObjectPtr", "TSoftObjectPtr", "TSoftClassPtr", "TLazyObjectPtr",
            "TStrongObjectPtr", "TScriptInterface", "TWeakInterfacePtr", "TSubclassOf", "TNonNullPtr",
            "std::unique_ptr", "std::shared_ptr", "std::weak_ptr", "unique_ptr", "shared_ptr", "weak_ptr",
        };

        private TypeShape(string text, string head, List<string> args, int pointers)
        {
            Text = text;
            Head = head;
            Args = args;
            PointerDepth = pointers;
        }

        public string Text { get; }

        /// <summary>Type name without template arguments, cv-qualifiers, pointers and references (may be qualified: <c>Game::FFoo</c>).</summary>
        public string Head { get; }

        public IReadOnlyList<string> Args { get; }
        public int PointerDepth { get; }

        public string Arg(int i) => i < Args.Count ? Args[i] : null;

        public AccessShape Shape
        {
            get
            {
                if (string.IsNullOrEmpty(Head)) return AccessShape.Unknown;
                if (PointerDepth > 0) return AccessShape.RawPointer;
                if (Head == "TObjectPtr") return AccessShape.ObjectPtr;
                if (Smart.Contains(Head)) return AccessShape.SmartPointer;
                return AccessShape.Value;
            }
        }

        public bool IsPointerLike => Shape == AccessShape.RawPointer || Shape == AccessShape.ObjectPtr || Shape == AccessShape.SmartPointer;

        /// <summary>Type reached through <c>-&gt;</c> / <c>*</c>: <c>AActor*</c> → AActor, <c>TSharedPtr&lt;FFoo&gt;</c> → FFoo, <c>TSubclassOf&lt;X&gt;</c> → UClass.</summary>
        public string Pointee()
        {
            switch (Shape)
            {
                case AccessShape.RawPointer:
                    {
                        int i = Text.LastIndexOf('*');
                        return i > 0 ? Text.Substring(0, i).Trim() : null;
                    }
                case AccessShape.ObjectPtr:
                case AccessShape.SmartPointer:
                    if (Head == "TSubclassOf" || Head == "TSoftClassPtr") return "UClass";
                    return Arg(0);
                default:
                    return null;
            }
        }

        public static TypeShape Parse(string typeText)
        {
            if (string.IsNullOrWhiteSpace(typeText)) return new TypeShape(string.Empty, null, new List<string>(), 0);
            string t = StripWords(typeText.Trim()).Replace("&", " ").Trim();
            // pointer depth: '*' outside template arguments
            int depth = 0, pointers = 0, lt = -1;
            for (int i = 0; i < t.Length; i++)
            {
                char c = t[i];
                if (c == '<') { if (depth++ == 0 && lt < 0) lt = i; }
                else if (c == '>') depth--;
                else if (c == '*' && depth == 0) pointers++;
            }
            string clean = t;
            if (pointers > 0)
            {
                var sb = new StringBuilder(t.Length);
                depth = 0;
                foreach (char c in t)
                {
                    if (c == '<') depth++;
                    else if (c == '>') depth--;
                    if (c == '*' && depth == 0) continue;
                    sb.Append(c);
                }
                clean = sb.ToString().Trim();
                lt = clean.IndexOf('<');
            }
            string head = (lt >= 0 ? clean.Substring(0, lt) : clean).Trim();
            if (head.StartsWith("::", StringComparison.Ordinal)) head = head.Substring(2);
            head = head.Replace(" ", string.Empty);
            var args = new List<string>();
            if (lt >= 0)
            {
                int d = 0, start = lt + 1;
                for (int i = lt; i < clean.Length; i++)
                {
                    char c = clean[i];
                    if (c == '<' || c == '(') d++;
                    else if (c == '>' || c == ')')
                    {
                        if (--d == 0)
                        {
                            AddArg(args, clean.Substring(start, i - start));
                            break;
                        }
                    }
                    else if (c == ',' && d == 1)
                    {
                        AddArg(args, clean.Substring(start, i - start));
                        start = i + 1;
                    }
                }
            }
            return new TypeShape(typeText.Trim(), head.Length == 0 ? null : head, args, pointers);
        }

        private static void AddArg(List<string> args, string s)
        {
            s = s.Trim();
            if (s.Length > 0) args.Add(s);
        }

        private static readonly string[] Drop = { "const", "volatile", "class", "struct", "enum", "typename", "mutable", "static", "inline", "constexpr", "FORCEINLINE" };

        private static string StripWords(string s)
        {
            var sb = new StringBuilder(s.Length);
            int i = 0;
            while (i < s.Length)
            {
                if ((char.IsLetter(s[i]) || s[i] == '_') && (i == 0 || !(char.IsLetterOrDigit(s[i - 1]) || s[i - 1] == '_')))
                {
                    int j = i;
                    while (j < s.Length && (char.IsLetterOrDigit(s[j]) || s[j] == '_')) j++;
                    string w = s.Substring(i, j - i);
                    if (Array.IndexOf(Drop, w) < 0) sb.Append(w);
                    else sb.Append(' ');
                    i = j;
                    continue;
                }
                sb.Append(s[i++]);
            }
            return sb.ToString();
        }
    }
}
