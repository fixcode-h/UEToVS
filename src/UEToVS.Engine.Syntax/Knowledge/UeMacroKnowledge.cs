using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Text.Json;
using UEToVS.Contracts;
using UEToVS.Core.Diagnostics;

namespace UEToVS.Engine.Syntax
{
    /// <summary>What the parser does with an identifier that names a known macro.</summary>
    public enum MacroRole : byte
    {
        None = 0,
        /// <summary>UCLASS/UPROPERTY/...: attach flags and specifier text to the next declaration.</summary>
        Reflection = 1,
        /// <summary>GENERATED_BODY family: mark the class and inject members.</summary>
        GeneratedBody = 2,
        /// <summary>Declares a named symbol (delegate types, log categories, stats...).</summary>
        SymbolDeclaration = 3,
        /// <summary>Attribute-like no-op without arguments (FORCEINLINE, UE_NODISCARD, PRAGMA_...).</summary>
        Attribute = 4,
        /// <summary>Attribute-like no-op with arguments (UE_DEPRECATED(...), MS_ALIGN(16)).</summary>
        AttributeCall = 5,
        /// <summary><c>MODULE_API</c> export macros (expand to nothing for the parser).</summary>
        Export = 6,
        /// <summary>TEXT("...") and friends: string wrappers, not reported as references.</summary>
        StringLiteral = 7,
        /// <summary>PURE_VIRTUAL(Name, body): a function body.</summary>
        PureVirtual = 8,
    }

    /// <summary>Which GENERATED_BODY flavour a class has.</summary>
    public enum GeneratedFamily : byte
    {
        Auto = 0,
        UClass = 1,
        UStruct = 2,
        UInterface = 3,
        IInterface = 4,
    }

    /// <summary>One knowledge base entry.</summary>
    public sealed class MacroInfo
    {
        public string Name { get; internal set; }
        public MacroRole Role { get; internal set; }

        /// <summary>Reflection flag (reflection macros) or extra flags of declared symbols (dynamic delegates).</summary>
        public UeFlags Flag { get; internal set; }

        /// <summary>Symbol declarations: kind of the declared symbol.</summary>
        public SymbolKind Kind { get; internal set; }

        /// <summary>Symbol declarations: 0-based macro argument holding the declared name.</summary>
        public int NameArg { get; internal set; }

        /// <summary>Symbol declarations: 0-based argument holding a return type, or -1.</summary>
        public int ReturnArg { get; internal set; } = -1;

        public bool IsDefinition { get; internal set; } = true;
        public DeclModifiers Traits { get; internal set; }
        public GeneratedFamily Family { get; internal set; }

        /// <summary>GENERATED_UCLASS_BODY style: declares the FObjectInitializer constructor and switches to public access.</summary>
        public bool Legacy { get; internal set; }

        internal bool IsPrefix { get; set; }
        internal byte[] PrefixBytes { get; set; }
    }

    /// <summary>A member injected by GENERATED_BODY.</summary>
    public sealed class InjectedMember
    {
        public string Name { get; internal set; }
        public SymbolKind Kind { get; internal set; }
        public bool IsStatic { get; internal set; }
        public bool RequiresSuper { get; internal set; }

        /// <summary>Signature template with {Class}, {ClassNoPrefix} and {Super} placeholders.</summary>
        public string Signature { get; internal set; }
    }

    /// <summary>
    /// Data-driven description of UE macros, loaded from the embedded <c>UeMacros.json</c> (or any stream with the same schema).
    /// Thread-safe for reads once constructed.
    /// </summary>
    public sealed class UeMacroKnowledge
    {
        private static readonly Logger L = Log.For("Syntax");
        private static readonly Lazy<UeMacroKnowledge> LazyDefault = new Lazy<UeMacroKnowledge>(LoadEmbedded);

        private readonly Utf8Map<MacroInfo> _exact = new Utf8Map<MacroInfo>(256);
        private readonly List<MacroInfo> _prefixes = new List<MacroInfo>();
        private readonly Dictionary<GeneratedFamily, InjectedMember[]> _injected = new Dictionary<GeneratedFamily, InjectedMember[]>();
        private byte[] _exportSuffix = Encoding.ASCII.GetBytes("_API");

        private UeMacroKnowledge()
        {
        }

        /// <summary>The embedded knowledge base.</summary>
        public static UeMacroKnowledge Default => LazyDefault.Value;

        public string Version { get; private set; }
        public string LegacyConstructorSignature { get; private set; }

        /// <summary>Loads a knowledge base from JSON. Throws <see cref="JsonException"/>/<see cref="InvalidDataException"/> on bad input.</summary>
        public static UeMacroKnowledge Load(Stream json)
        {
            using var doc = JsonDocument.Parse(json, new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true });
            var kb = new UeMacroKnowledge();
            kb.Read(doc.RootElement);
            return kb;
        }

        private static UeMacroKnowledge LoadEmbedded()
        {
            var asm = typeof(UeMacroKnowledge).Assembly;
            using var s = asm.GetManifestResourceStream("UEToVS.Engine.Syntax.UeMacros.json");
            if (s == null)
            {
                L.Error("syntax.kb.missing", "Embedded UeMacros.json not found", null);
                return new UeMacroKnowledge { Version = "empty" };
            }
            return Load(s);
        }

        private void Read(JsonElement root)
        {
            Version = Str(root, "version") ?? "unknown";
            var suffix = Str(root, "exportSuffix");
            if (!string.IsNullOrEmpty(suffix)) _exportSuffix = Encoding.ASCII.GetBytes(suffix);
            LegacyConstructorSignature = Str(root, "legacyConstructor");

            foreach (var e in Arr(root, "reflection"))
                Add(new MacroInfo { Name = Str(e, "name"), Role = MacroRole.Reflection, Flag = ParseEnum<UeFlags>(Str(e, "flag")) });

            foreach (var e in Arr(root, "generatedBody"))
                Add(new MacroInfo
                {
                    Name = Str(e, "name"),
                    Role = MacroRole.GeneratedBody,
                    Family = ParseEnum<GeneratedFamily>(Str(e, "family")),
                    Legacy = Bool(e, "legacy", false),
                    Flag = UeFlags.GeneratedBody,
                });

            foreach (var e in Arr(root, "attributeMacros")) Add(new MacroInfo { Name = e.GetString(), Role = MacroRole.Attribute });
            foreach (var e in Arr(root, "attributeCallMacros")) Add(new MacroInfo { Name = e.GetString(), Role = MacroRole.AttributeCall });
            foreach (var e in Arr(root, "stringMacros")) Add(new MacroInfo { Name = e.GetString(), Role = MacroRole.StringLiteral });
            foreach (var e in Arr(root, "pureVirtualMacros")) Add(new MacroInfo { Name = e.GetString(), Role = MacroRole.PureVirtual });

            foreach (var e in Arr(root, "symbolMacros"))
            {
                var traits = DeclModifiers.None;
                foreach (var t in Arr(e, "traits")) traits |= ParseEnum<DeclModifiers>(t.GetString());
                Add(new MacroInfo
                {
                    Name = Str(e, "match"),
                    Role = MacroRole.SymbolDeclaration,
                    Kind = ParseEnum<SymbolKind>(Str(e, "kind")),
                    NameArg = Int(e, "nameArg", 0),
                    ReturnArg = Int(e, "returnArg", -1),
                    IsDefinition = Bool(e, "definition", true),
                    Flag = ParseEnum<UeFlags>(Str(e, "ue")),
                    Traits = traits,
                });
            }

            if (root.TryGetProperty("injectedMembers", out var inj) && inj.ValueKind == JsonValueKind.Object)
            {
                foreach (var fam in inj.EnumerateObject())
                {
                    var family = ParseEnum<GeneratedFamily>(fam.Name);
                    var list = new List<InjectedMember>();
                    foreach (var m in fam.Value.EnumerateArray())
                        list.Add(new InjectedMember
                        {
                            Name = Str(m, "name"),
                            Kind = ParseEnum<SymbolKind>(Str(m, "kind")),
                            IsStatic = Bool(m, "static", false),
                            RequiresSuper = Bool(m, "requiresSuper", false),
                            Signature = Str(m, "signature"),
                        });
                    _injected[family] = list.ToArray();
                }
            }
        }

        private void Add(MacroInfo info)
        {
            if (string.IsNullOrEmpty(info.Name)) throw new InvalidDataException("Knowledge base entry without a name");
            if (info.Name.EndsWith("*", StringComparison.Ordinal))
            {
                info.Name = info.Name.Substring(0, info.Name.Length - 1);
                info.IsPrefix = true;
                info.PrefixBytes = Encoding.UTF8.GetBytes(info.Name);
                _prefixes.Add(info);
                return;
            }
            var key = Encoding.UTF8.GetBytes(info.Name);
            if (!_exact.ContainsKey(key)) _exact.Set(key, info);
        }

        /// <summary>Looks up a macro by name (exact entries first, then prefix entries in file order).</summary>
        public MacroInfo Lookup(string name) => Lookup(Encoding.UTF8.GetBytes(name ?? string.Empty));

        internal MacroInfo Lookup(ReadOnlySpan<byte> name)
        {
            if (name.Length == 0) return null;
            if (_exact.TryGetValue(name, out var info)) return info;
            for (int i = 0; i < _prefixes.Count; i++)
                if (name.StartsWith(_prefixes[i].PrefixBytes)) return _prefixes[i];
            return null;
        }

        /// <summary>True for <c>XXX_API</c>-style export macros: upper case, digits and underscores ending with the export suffix.</summary>
        internal bool IsExportMacro(ReadOnlySpan<byte> name)
        {
            if (name.Length <= _exportSuffix.Length || !name.EndsWith(_exportSuffix)) return false;
            foreach (byte b in name)
                if (b >= (byte)'a' && b <= (byte)'z') return false;
            return true;
        }

        public bool IsExportMacro(string name) => IsExportMacro(Encoding.UTF8.GetBytes(name ?? string.Empty));

        public IReadOnlyList<InjectedMember> GetInjectedMembers(GeneratedFamily family) =>
            _injected.TryGetValue(family, out var m) ? m : Array.Empty<InjectedMember>();

        private static string Str(JsonElement e, string name) =>
            e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

        private static int Int(JsonElement e, string name, int def) =>
            e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetInt32() : def;

        private static bool Bool(JsonElement e, string name, bool def) =>
            e.TryGetProperty(name, out var v) && (v.ValueKind == JsonValueKind.True || v.ValueKind == JsonValueKind.False) ? v.GetBoolean() : def;

        private static IEnumerable<JsonElement> Arr(JsonElement e, string name) =>
            e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Array ? v.EnumerateArray() : (IEnumerable<JsonElement>)Array.Empty<JsonElement>();

        private static T ParseEnum<T>(string s) where T : struct
        {
            if (string.IsNullOrEmpty(s)) return default;
            if (Enum.TryParse<T>(s, true, out var v)) return v;
            throw new InvalidDataException($"Unknown {typeof(T).Name} value '{s}' in knowledge base");
        }
    }
}
