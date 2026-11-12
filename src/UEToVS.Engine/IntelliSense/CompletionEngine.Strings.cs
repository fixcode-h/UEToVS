using System;
using System.Collections.Generic;
using System.Text;
using UEToVS.Contracts;
using UEToVS.Core.Unreal;
using UEToVS.Engine.Indexing;
using UEToVS.Engine.Navigation;

namespace UEToVS.Engine.IntelliSense
{
    /// <summary>Data behind string context completion (design §7.6): assets, ini files, gameplay tags. Any part may be null.</summary>
    public sealed class StringCompletionSources
    {
        public StringCompletionSources(ISymbolIndexBackend backend, FileTable files, AssetTable assets)
        {
            Backend = backend;
            Files = files;
            Assets = assets;
            Config = new ConfigCatalog();
            Tags = new GameplayTagCatalog(Config);
        }

        public ISymbolIndexBackend Backend { get; }
        public FileTable Files { get; }
        public AssetTable Assets { get; }
        public ConfigCatalog Config { get; }
        public GameplayTagCatalog Tags { get; }
    }

    /// <summary>
    /// String context completion (design §7.6): gameplay tags segment by segment, asset paths folder by folder (filtered by the
    /// FObjectFinder / LoadObject class when known), UFUNCTION names for BindUFunction / FindFunction, ini sections and keys
    /// for GConfig calls. The caret analysis is <see cref="StringLiteralContext"/> (shared with the host).
    /// </summary>
    public sealed partial class CompletionEngine
    {
        private const int PString = 8000;

        /// <summary>Set by <see cref="IntelliSenseService"/>; without it string contexts list nothing.</summary>
        public StringCompletionSources Strings { get; set; }

        private readonly object _assetGate = new object();
        private AssetFolders _folders;
        private (AssetEntry[] Source, string Dir, List<AssetEntry> Assets) _dirAssets;

        /// <summary>Log name of a string context ("gameplayTag", "assetPath", "ufunctionName", "iniKey"), else null.</summary>
        public static string StringContextName(CompletionContextKind kind)
        {
            switch (kind)
            {
                case CompletionContextKind.GameplayTag: return "gameplayTag";
                case CompletionContextKind.AssetPath: return "assetPath";
                case CompletionContextKind.UFunctionName: return "ufunctionName";
                case CompletionContextKind.IniKey: return "iniKey";
                default: return null;
            }
        }

        private string StringContext(IsSession s, CompletionSite site, Collector c, ref bool incomplete)
        {
            var ss = site.StringSite;
            if (ss == null) return null;
            switch (site.Kind)
            {
                case CompletionContextKind.GameplayTag:
                    return GameplayTags(s, site, ss, c);
                case CompletionContextKind.AssetPath:
                    return AssetPaths(site, ss, c, ref incomplete);
                case CompletionContextKind.UFunctionName:
                    return UFunctionNames(s, site, ss, c);
                case CompletionContextKind.IniKey:
                    return IniEntries(site, ss, c);
                default:
                    return null;
            }
        }

        /// <summary>Match rank with a substring fallback (tag / path / ini names are not identifiers).</summary>
        private static int TextRank(Collector c, string name, string prefix, out bool exact)
        {
            int r = c.Rank(name, false, out exact);
            if (r < 0 && !string.IsNullOrEmpty(prefix) && name != null && name.IndexOf(prefix, StringComparison.OrdinalIgnoreCase) >= 0) r = 0;
            return r;
        }

        // ================================================================== gameplay tags

        private string GameplayTags(IsSession s, CompletionSite site, StringLiteralSite ss, Collector c)
        {
            string typed = ss.Typed ?? string.Empty;
            int comma = typed.LastIndexOf(',');
            if (comma >= 0) typed = typed.Substring(comma + 1);
            typed = typed.Trim();
            int dot = typed.LastIndexOf('.');
            string parent = dot < 0 ? string.Empty : typed.Substring(0, dot);
            var src = Strings;
            var tree = src?.Tags.Tree(src.Backend, src.Files, s.Ct);
            int candidates = 0;
            if (tree != null && tree.TryGetValue(parent, out var children))
            {
                foreach (var n in children)
                {
                    candidates++;
                    int rank = TextRank(c, n.Segment, site.Prefix, out bool exact);
                    if (rank < 0) continue;
                    c.Add(TagItem(n.Segment, n.FullTag, n.Comment, n.Source, n.HasChildren, n.Declared, rank, exact), "g");
                }
            }
            // tags defined in the open (possibly unsaved) document
            if (ContainsAscii(s.Doc.Text, "UE_DEFINE_GAMEPLAY_TAG"))
            {
                var own = GameplayTagCatalog.Extract(Encoding.UTF8.GetString(s.Doc.Text), System.IO.Path.GetFileName(s.Doc.Path));
                var docTree = GameplayTagCatalog.Build(own.FindAll(t => !string.Equals(t.Tag, typed, StringComparison.OrdinalIgnoreCase)));
                if (docTree.TryGetValue(parent, out var docChildren))
                    foreach (var n in docChildren)
                    {
                        candidates++;
                        int rank = TextRank(c, n.Segment, site.Prefix, out bool exact);
                        if (rank < 0) continue;
                        c.Add(TagItem(n.Segment, n.FullTag, n.Comment, n.Source, n.HasChildren, n.Declared, rank, exact), "g");
                    }
            }
            site.Candidates = candidates;
            return parent.Length == 0 ? "tag:" : "tag:" + parent;
        }

        private static CompletionItem TagItem(string segment, string full, string comment, string source, bool hasChildren, bool declared, int rank, bool exact) => new CompletionItem
        {
            Label = segment,
            InsertText = segment,
            Kind = hasChildren ? SymbolKind.Namespace : SymbolKind.EnumValue,
            Detail = !string.IsNullOrEmpty(comment) ? Truncate(comment) : full,
            Container = source,
            Score = rank * RankUnit + PString + (declared ? 100 : 0) + (exact ? 100 : 0) - Math.Min(segment.Length, 50) * 2,
        };

        private static bool ContainsAscii(byte[] text, string needle)
        {
            var n = Encoding.ASCII.GetBytes(needle);
            return text != null && new ReadOnlySpan<byte>(text).IndexOf(n) >= 0;
        }

        // ================================================================== asset paths

        /// <summary>Package folders derived from the asset table (rebuilt when the table changes): parent → child folder names.</summary>
        private sealed class AssetFolders
        {
            public AssetEntry[] Source;
            public Dictionary<string, List<string>> Children = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
            public HashSet<string> Mounts = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        }

        private AssetFolders Folders(AssetEntry[] snap)
        {
            lock (_assetGate)
            {
                if (_folders != null && ReferenceEquals(_folders.Source, snap)) return _folders;
                var f = new AssetFolders { Source = snap };
                var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                string last = null;
                foreach (var a in snap)
                {
                    string dir = a.PackageDir;
                    if (dir == null || ReferenceEquals(dir, last)) continue;
                    last = dir;
                    // add the folder and its ancestors: "/Game/A/B" → "/Game" ⊃ "A" ⊃ "B"
                    while (dir.Length > 1 && seen.Add(dir))
                    {
                        int slash = dir.LastIndexOf('/');
                        if (slash <= 0)
                        {
                            f.Mounts.Add(dir.Substring(1));
                            break;
                        }
                        string parent = dir.Substring(0, slash);
                        if (!f.Children.TryGetValue(parent, out var list)) f.Children[parent] = list = new List<string>();
                        list.Add(dir.Substring(slash + 1));
                        dir = parent;
                    }
                }
                _folders = f;
                return f;
            }
        }

        /// <summary>Assets directly in <paramref name="dir"/> (one-entry cache: typing stays in the same folder).</summary>
        private List<AssetEntry> AssetsIn(AssetEntry[] snap, string dir)
        {
            lock (_assetGate)
            {
                if (ReferenceEquals(_dirAssets.Source, snap) && string.Equals(_dirAssets.Dir, dir, StringComparison.OrdinalIgnoreCase)) return _dirAssets.Assets;
            }
            var list = new List<AssetEntry>();
            foreach (var a in snap)
                if (a.PackageDir != null && a.PackageDir.Length == dir.Length && string.Equals(a.PackageDir, dir, StringComparison.OrdinalIgnoreCase)) list.Add(a);
            lock (_assetGate) _dirAssets = (snap, dir, list);
            return list;
        }

        /// <summary>UE naming conventions per class (Epic's asset naming guide), used to rank likely assets first.</summary>
        private static readonly Dictionary<string, string[]> ClassPrefixes = new Dictionary<string, string[]>(StringComparer.Ordinal)
        {
            ["UStaticMesh"] = new[] { "SM_", "S_" },
            ["USkeletalMesh"] = new[] { "SK_", "SKM_" },
            ["USkeleton"] = new[] { "SKEL_" },
            ["UMaterial"] = new[] { "M_" },
            ["UMaterialInstance"] = new[] { "MI_", "MIC_" },
            ["UMaterialInstanceConstant"] = new[] { "MI_", "MIC_" },
            ["UMaterialInterface"] = new[] { "M_", "MI_", "MIC_" },
            ["UTexture"] = new[] { "T_", "TX_" },
            ["UTexture2D"] = new[] { "T_", "TX_" },
            ["USoundBase"] = new[] { "S_", "SC_", "SW_", "A_" },
            ["USoundWave"] = new[] { "S_", "SW_", "A_" },
            ["USoundCue"] = new[] { "SC_", "S_" },
            ["UAnimMontage"] = new[] { "AM_" },
            ["UAnimSequence"] = new[] { "A_", "AS_" },
            ["UBlendSpace"] = new[] { "BS_" },
            ["UAnimBlueprint"] = new[] { "ABP_" },
            ["UParticleSystem"] = new[] { "P_", "PS_" },
            ["UNiagaraSystem"] = new[] { "NS_", "FX_" },
            ["UDataTable"] = new[] { "DT_" },
            ["UDataAsset"] = new[] { "DA_" },
            ["UPrimaryDataAsset"] = new[] { "DA_" },
            ["UCurveFloat"] = new[] { "C_", "Curve_" },
            ["UInputAction"] = new[] { "IA_" },
            ["UInputMappingContext"] = new[] { "IMC_" },
            ["UPhysicalMaterial"] = new[] { "PM_" },
            ["UUserWidget"] = new[] { "WBP_", "W_" },
            ["UBlueprint"] = new[] { "BP_", "B_" },
        };

        private static readonly string[] BlueprintPrefixes = { "BP_", "B_", "WBP_", "ABP_", "GA_", "GE_", "W_" };

        private string AssetPaths(CompletionSite site, StringLiteralSite ss, Collector c, ref bool incomplete)
        {
            var table = Strings?.Assets;
            if (table == null) return null;
            string typed = ss.Typed ?? string.Empty;
            int slash = typed.LastIndexOf('/');
            string dir = slash <= 0 ? string.Empty : typed.Substring(0, slash);
            string segment = typed.Substring(slash + 1);
            string cls = ss.TemplateArgument?.Replace("class ", string.Empty).Trim().TrimEnd('*').Trim();
            var snap = table.Snapshot();
            var folders = Folders(snap);
            int candidates = 0;

            if (dir.Length == 0)
            {
                // mount points: "/Game", "/MyPlugin"
                if (ss.Callee == null) return null;
                foreach (var m in folders.Mounts)
                {
                    candidates++;
                    int rank = TextRank(c, m, site.Prefix, out bool exact);
                    if (rank < 0) continue;
                    c.Add(new CompletionItem
                    {
                        Label = m,
                        InsertText = slash < 0 ? "/" + m : m,
                        Kind = SymbolKind.Namespace,
                        Detail = "/" + m + "/",
                        Score = rank * RankUnit + PString + (string.Equals(m, "Game", StringComparison.OrdinalIgnoreCase) ? 300 : 0) + (exact ? 100 : 0),
                    }, "d");
                }
                site.Candidates = candidates;
                return "asset:/";
            }

            string mount = dir.Substring(1);
            int ms = mount.IndexOf('/');
            if (ms >= 0) mount = mount.Substring(0, ms);
            if (!folders.Mounts.Contains(mount)) return null; // "/usr/bin/…" in a plain string: not an asset path

            if (folders.Children.TryGetValue(dir, out var subs))
            {
                foreach (var sub in subs)
                {
                    candidates++;
                    int rank = TextRank(c, sub, site.Prefix, out bool exact);
                    if (rank < 0) continue;
                    c.Add(new CompletionItem
                    {
                        Label = sub,
                        InsertText = sub,
                        Kind = SymbolKind.Namespace,
                        Detail = dir + "/" + sub + "/",
                        Score = rank * RankUnit + PString + 50 + (exact ? 100 : 0) - Math.Min(sub.Length, 50) * 2,
                    }, "d");
                }
            }

            string[] prefixes = cls != null && ClassPrefixes.TryGetValue(cls, out var p) ? p : ss.WantsClass ? BlueprintPrefixes : null;
            bool worldOnly = cls == "UWorld";
            // "BP_Hero.B|": completing the object name of a package already typed
            int pkgDot = segment.IndexOf('.');
            string package = pkgDot >= 0 ? segment.Substring(0, pkgDot) : null;
            int added = 0;
            foreach (var a in AssetsIn(snap, dir))
            {
                candidates++;
                if (worldOnly != a.IsMap && (worldOnly || cls != null || ss.WantsClass)) continue;
                if (package != null && !string.Equals(a.Name, package, StringComparison.OrdinalIgnoreCase)) continue;
                int rank = TextRank(c, a.Name, site.Prefix, out bool exact);
                if (rank < 0) continue;
                string objectName = ss.WantsClass ? a.Name + "_C" : a.Name;
                bool likely = prefixes != null && Array.Exists(prefixes, x => a.Name.StartsWith(x, StringComparison.OrdinalIgnoreCase));
                string insert = package != null ? objectName : a.Name + "." + objectName;
                c.Add(new CompletionItem
                {
                    Label = a.Name,
                    InsertText = insert,
                    Kind = SymbolKind.Unknown,
                    Detail = a.IsMap ? "World" : dir + "/" + a.Name + "." + objectName,
                    Container = a.IsMap ? "Map" : null,
                    Score = rank * RankUnit + PString + (likely ? 600 : 0) + (exact ? 100 : 0) + (TypeText.IsProject(a.Origin) ? 50 : 0) - Math.Min(a.Name.Length, 50) * 2,
                    Location = new SourceLocation { FilePath = a.FilePath },
                }, "a");
                if (++added >= 5000)
                {
                    incomplete = true;
                    break;
                }
            }
            site.Candidates = candidates;
            return "asset:" + dir + (cls != null ? " <" + cls + ">" : string.Empty);
        }

        // ================================================================== UFUNCTION names

        private static string UFunctionNames(IsSession s, CompletionSite site, StringLiteralSite ss, Collector c)
        {
            var doc = s.Doc;
            string cls = doc.EnclosingClass(site.ReplaceStart, out _);
            string type = null;
            var types = new ExpressionTypes(s, cls);
            if (ss.TargetStart >= 0 && ss.TargetEnd > ss.TargetStart)
            {
                // BindUFunction(Target, "…"): the type of the first argument
                int end = doc.TokenIndexFrom(doc.OffsetOf(site.Line, ss.TargetEnd)) - 1;
                if (end >= 0) type = types.TypeOf(end);
            }
            else if (ss.ReceiverOperatorColumn >= 0)
            {
                // Target->FindFunction("…")
                int end = doc.TokenIndexFrom(doc.OffsetOf(site.Line, ss.ReceiverOperatorColumn)) - 1;
                if (end >= 0) type = types.TypeOf(end);
            }
            else if (cls != null) type = cls;
            if (type == null) return null;
            var shape = TypeShape.Parse(type);
            string head = shape.IsPointerLike ? TypeShape.Parse(shape.Pointee() ?? string.Empty).Head : shape.Head;
            if (head == null) return type;
            var rec = s.FindRecord(head, site.ReplaceStart);
            if (rec == null) return head;
            var members = s.MembersOf(rec);
            int candidates = 0;
            foreach (var e in members.Members)
            {
                var d = e.Decl;
                if (d.Kind != SymbolKind.Method || (d.Ue & UeFlags.UFunction) == 0) continue;
                candidates++;
                int rank = c.Rank(d.Name, false, out bool exact);
                if (rank < 0) continue;
                int band = Math.Max(PMember - 500 * e.Depth, PNamespace + 500);
                c.Add(FromDecl(e.Doc, e.Index, e.DeclaringType, e.Depth, rank * RankUnit + Proximity(band, d.Name, ItemOrigin.Project, UeFlags.None, exact, false), s.Ctx), "f");
            }
            site.Candidates = candidates;
            return members.QualifiedName;
        }

        // ================================================================== ini sections / keys

        private string IniEntries(CompletionSite site, StringLiteralSite ss, Collector c)
        {
            var src = Strings;
            if (src?.Files == null) return null;
            string baseName = StringLiteralContext.IniBaseName(ss.IniFile);
            bool key = ss.Kind == StringLiteralKind.IniKey;
            int candidates = 0;
            var files = src.Config.Files(src.Files);
            foreach (var f in files)
            {
                if (baseName != null && !System.IO.Path.GetFileNameWithoutExtension(f.Name).EndsWith(baseName, StringComparison.OrdinalIgnoreCase)) continue;
                foreach (var sec in f.Sections)
                {
                    if (!key)
                    {
                        candidates++;
                        int rank = TextRank(c, sec.Name, site.Prefix, out bool exact);
                        if (rank < 0) continue;
                        c.Add(new CompletionItem
                        {
                            Label = sec.Name,
                            InsertText = sec.Name,
                            Kind = SymbolKind.Namespace,
                            Detail = sec.Keys.Count + " keys",
                            Container = f.Name,
                            Score = rank * RankUnit + PString + (exact ? 100 : 0) - Math.Min(sec.Name.Length, 80),
                        }, "s");
                        continue;
                    }
                    if (!string.Equals(sec.Name, ss.IniSection, StringComparison.OrdinalIgnoreCase)) continue;
                    foreach (var kv in sec.Keys)
                    {
                        candidates++;
                        int rank = TextRank(c, kv.Key, site.Prefix, out bool exact);
                        if (rank < 0) continue;
                        c.Add(new CompletionItem
                        {
                            Label = kv.Key,
                            InsertText = kv.Key,
                            Kind = SymbolKind.Field,
                            Detail = "= " + kv.Value,
                            Container = f.Name,
                            Score = rank * RankUnit + PString + (exact ? 100 : 0) - Math.Min(kv.Key.Length, 50) * 2,
                        }, "k");
                    }
                }
            }
            site.Candidates = candidates;
            return (key ? "ini:" + ss.IniSection : "ini:sections") + (baseName != null ? " (" + baseName + ")" : string.Empty);
        }
    }
}
