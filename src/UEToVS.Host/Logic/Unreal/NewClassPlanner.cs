using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using UEToVS.Host.Logic.Refactoring;

namespace UEToVS.Host.Logic.Unreal
{
    /// <summary>What the new type is: decides the prefix, the macro and the generated members.</summary>
    public enum NewClassKind
    {
        /// <summary>UCLASS deriving from AActor (A prefix; BeginPlay / Tick optional).</summary>
        Actor = 0,

        /// <summary>UCLASS deriving from UActorComponent (U prefix; BeginPlay / TickComponent optional).</summary>
        ActorComponent = 1,

        /// <summary>Any other UCLASS (U prefix; FObjectInitializer constructor, which every UObject accepts — UUserWidget has no default one).</summary>
        Object = 2,

        /// <summary>USTRUCT (F prefix).</summary>
        Struct = 3,

        /// <summary>Plain C++ class without reflection (F prefix, no .generated.h).</summary>
        Plain = 4,
    }

    /// <summary>A parent class choice: a common one (pinned at the top of the list) or a type found in the index.</summary>
    public sealed class ParentClassInfo
    {
        /// <summary>C++ name ("AActor"); null for "no parent".</summary>
        public string Name { get; set; }

        /// <summary>#include path of its header ("GameFramework/Actor.h"); null when unknown / not needed.</summary>
        public string Include { get; set; }

        public NewClassKind Kind { get; set; }

        /// <summary>Module of the parent (display only).</summary>
        public string Module { get; set; }

        /// <summary>Pinned common parent (not from the index).</summary>
        public bool IsCommon { get; set; }

        public string DisplayName => Name ?? (Kind == NewClassKind.Struct ? "USTRUCT" : "C++");

        public override string ToString() => DisplayName;
    }

    /// <summary>A project or plugin module (folder that holds &lt;Name&gt;.Build.cs).</summary>
    public sealed class ModuleInfo
    {
        public string Name { get; set; }
        public string Directory { get; set; }
        public string BuildCsPath { get; set; }

        /// <summary>Plugin name for plugin modules; null for the project's own Source modules.</summary>
        public string Plugin { get; set; }

        public bool HasPublic { get; set; }
        public bool HasPrivate { get; set; }
        public bool HasClasses { get; set; }

        /// <summary>Header / source split possible: a Public (or Classes) and a Private folder.</summary>
        public bool CanSplit => (HasPublic || HasClasses) && HasPrivate;

        public string ApiMacro => NewClassPlanner.ApiMacro(Name);

        public string DisplayName => Plugin == null ? Name : Name + "  (" + Plugin + ")";

        public override string ToString() => DisplayName;
    }

    /// <summary>Everything needed to write the two files.</summary>
    public sealed class NewClassSpec
    {
        /// <summary>Full C++ name with prefix ("AMyActor").</summary>
        public string ClassName { get; set; }

        public NewClassKind Kind { get; set; }
        public string ParentName { get; set; }
        public string ParentInclude { get; set; }

        /// <summary>"MYGAME_API"; null for none.</summary>
        public string ApiMacro { get; set; }

        /// <summary>Actor / component: BeginPlay and Tick (TickComponent) overrides.</summary>
        public bool WithBeginPlayAndTick { get; set; } = true;

        /// <summary>The .cpp's #include of its header ("Sub/MyActor.h" or "MyActor.h").</summary>
        public string HeaderInclude { get; set; }

        /// <summary>"Copyright …" line from DefaultGame.ini (CopyrightNotice); null = none.</summary>
        public string Copyright { get; set; }

        public string NewLine { get; set; } = "\r\n";
    }

    /// <summary>Where the two files go.</summary>
    public sealed class NewClassTarget
    {
        public string HeaderPath { get; set; }
        public string SourcePath { get; set; }
        public string HeaderInclude { get; set; }
    }

    /// <summary>
    /// New Unreal class wizard (pure): common parents, the A/U/F prefix and name validation, project / plugin module discovery,
    /// Public / Private placement (or one folder when the module has no Public folder), the folder → module mapping for the
    /// Solution Explorer folder command, and the .h / .cpp templates (UE editor wizard style).
    /// </summary>
    public static class NewClassPlanner
    {
        public const int MaxNameLength = 64;

        /// <summary>Parents pinned at the top of the picker, most used first.</summary>
        public static IReadOnlyList<ParentClassInfo> CommonParents { get; } = new[]
        {
            Common("AActor", "GameFramework/Actor.h", NewClassKind.Actor),
            Common("UObject", "UObject/Object.h", NewClassKind.Object),
            Common("UActorComponent", "Components/ActorComponent.h", NewClassKind.ActorComponent),
            Common("USceneComponent", "Components/SceneComponent.h", NewClassKind.ActorComponent),
            Common("ACharacter", "GameFramework/Character.h", NewClassKind.Actor),
            Common("APawn", "GameFramework/Pawn.h", NewClassKind.Actor),
            Common("UUserWidget", "Blueprint/UserWidget.h", NewClassKind.Object),
            Common("APlayerController", "GameFramework/PlayerController.h", NewClassKind.Actor),
            Common("AGameModeBase", "GameFramework/GameModeBase.h", NewClassKind.Actor),
            Common("AGameStateBase", "GameFramework/GameStateBase.h", NewClassKind.Actor),
            Common("APlayerState", "GameFramework/PlayerState.h", NewClassKind.Actor),
            Common("AHUD", "GameFramework/HUD.h", NewClassKind.Actor),
            Common("UGameInstance", "Engine/GameInstance.h", NewClassKind.Object),
            Common("UGameInstanceSubsystem", "Subsystems/GameInstanceSubsystem.h", NewClassKind.Object),
            Common("UWorldSubsystem", "Subsystems/WorldSubsystem.h", NewClassKind.Object),
            Common("UBlueprintFunctionLibrary", "Kismet/BlueprintFunctionLibrary.h", NewClassKind.Object),
            Common("UDataAsset", "Engine/DataAsset.h", NewClassKind.Object),
            Common("UPrimaryDataAsset", "Engine/DataAsset.h", NewClassKind.Object),
            Common("UAnimInstance", "Animation/AnimInstance.h", NewClassKind.Object),
            Common("FTableRowBase", "Engine/DataTable.h", NewClassKind.Struct),
            new ParentClassInfo { Name = null, Kind = NewClassKind.Struct, IsCommon = true },
            new ParentClassInfo { Name = null, Kind = NewClassKind.Plain, IsCommon = true },
        };

        private static ParentClassInfo Common(string name, string include, NewClassKind kind) =>
            new ParentClassInfo { Name = name, Include = include, Kind = kind, IsCommon = true, Module = "Engine" };

        public static ParentClassInfo FindCommon(string name) =>
            CommonParents.FirstOrDefault(p => p.Name != null && string.Equals(p.Name, name, StringComparison.Ordinal));

        /// <summary>
        /// Kind of a parent found in the index: reflected class → Actor (A prefix) / ActorComponent (U…Component) / Object;
        /// USTRUCT → Struct; anything else → Plain.
        /// </summary>
        public static NewClassKind KindForParent(string name, bool isUClass, bool isUStruct)
        {
            var common = FindCommon(name);
            if (common != null) return common.Kind;
            if (isUClass)
            {
                if (name.Length > 1 && name[0] == 'A' && char.IsUpper(name[1])) return NewClassKind.Actor;
                if (name.EndsWith("Component", StringComparison.Ordinal)) return NewClassKind.ActorComponent;
                return NewClassKind.Object;
            }
            return isUStruct ? NewClassKind.Struct : NewClassKind.Plain;
        }

        public static char PrefixFor(NewClassKind kind) => kind == NewClassKind.Actor ? 'A' : kind == NewClassKind.Struct || kind == NewClassKind.Plain ? 'F' : 'U';

        public static bool IsReflected(NewClassKind kind) => kind != NewClassKind.Plain;

        public static bool HasBeginPlayAndTick(NewClassKind kind) => kind == NewClassKind.Actor || kind == NewClassKind.ActorComponent;

        /// <summary>"MyActor" → "AMyActor"; a name already carrying the prefix ("AMyActor") is kept. The first letter is upper-cased.</summary>
        public static string ApplyPrefix(string name, NewClassKind kind)
        {
            name = (name ?? string.Empty).Trim();
            if (name.Length == 0) return name;
            char prefix = PrefixFor(kind);
            if (name.Length > 1 && name[0] == prefix && char.IsUpper(name[1])) return name;
            return prefix + char.ToUpperInvariant(name[0]).ToString() + name.Substring(1);
        }

        /// <summary>The name without the prefix (file names are "MyActor.h", like UE's wizard).</summary>
        public static string FileBaseName(string className, NewClassKind kind)
        {
            if (string.IsNullOrEmpty(className)) return className;
            char prefix = PrefixFor(kind);
            return className.Length > 1 && className[0] == prefix && char.IsUpper(className[1]) ? className.Substring(1) : className;
        }

        /// <summary>Problem code of a typed name (before prefixing): "empty", "invalid", "too-long", "same-as-parent"; null when fine.</summary>
        public static string ValidateName(string typed, NewClassKind kind, string parentName)
        {
            string t = (typed ?? string.Empty).Trim();
            if (t.Length == 0) return "empty";
            string full = ApplyPrefix(t, kind);
            if (!RenamePlanner.IsValidIdentifier(full) || !char.IsLetter(t[0])) return "invalid";
            if (full.Length > MaxNameLength) return "too-long";
            if (parentName != null && string.Equals(full, parentName, StringComparison.Ordinal)) return "same-as-parent";
            return null;
        }

        public static string ApiMacro(string module) => string.IsNullOrEmpty(module) ? null : module.ToUpperInvariant() + "_API";

        // ---------------------------------------------------------------- modules

        private static readonly HashSet<string> SkippedFolders = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "Intermediate", "Binaries", "Content", "Saved", "DerivedDataCache", "Config", "Resources", "Shaders", "ThirdParty", "Docs", ".git", ".vs", "Build",
        };

        /// <summary>
        /// Project modules (&lt;root&gt;/Source/**/X.Build.cs) and plugin modules (&lt;root&gt;/Plugins/**/Source/**/X.Build.cs), project
        /// first then by plugin and name. Skips Intermediate / Binaries / ThirdParty …; at most <paramref name="maxDepth"/> levels deep.
        /// </summary>
        public static List<ModuleInfo> ScanModules(string projectRoot, int maxDepth = 6)
        {
            var list = new List<ModuleInfo>();
            if (string.IsNullOrEmpty(projectRoot) || !System.IO.Directory.Exists(projectRoot)) return list;
            string source = Path.Combine(projectRoot, "Source");
            if (System.IO.Directory.Exists(source)) Walk(source, null, 0, maxDepth, list);
            string plugins = Path.Combine(projectRoot, "Plugins");
            if (System.IO.Directory.Exists(plugins)) WalkPlugins(plugins, 0, maxDepth, list);
            return list.OrderBy(m => m.Plugin == null ? 0 : 1).ThenBy(m => m.Plugin, StringComparer.OrdinalIgnoreCase)
                       .ThenBy(m => m.Name, StringComparer.OrdinalIgnoreCase).ToList();
        }

        private static void WalkPlugins(string dir, int depth, int maxDepth, List<ModuleInfo> list)
        {
            if (depth > maxDepth) return;
            string[] uplugins;
            try
            {
                uplugins = System.IO.Directory.GetFiles(dir, "*.uplugin");
            }
            catch (Exception)
            {
                return;
            }
            if (uplugins.Length > 0)
            {
                string src = Path.Combine(dir, "Source");
                if (System.IO.Directory.Exists(src)) Walk(src, Path.GetFileNameWithoutExtension(uplugins[0]), 0, maxDepth, list);
                return;
            }
            foreach (var sub in SafeDirectories(dir))
                if (!SkippedFolders.Contains(Path.GetFileName(sub))) WalkPlugins(sub, depth + 1, maxDepth, list);
        }

        private static void Walk(string dir, string plugin, int depth, int maxDepth, List<ModuleInfo> list)
        {
            if (depth > maxDepth) return;
            string buildCs = null;
            try
            {
                buildCs = System.IO.Directory.GetFiles(dir, "*.Build.cs").FirstOrDefault();
            }
            catch (Exception)
            {
                return;
            }
            if (buildCs != null)
            {
                list.Add(Describe(buildCs, plugin));
                return; // modules do not nest
            }
            foreach (var sub in SafeDirectories(dir))
                if (!SkippedFolders.Contains(Path.GetFileName(sub))) Walk(sub, plugin, depth + 1, maxDepth, list);
        }

        private static IEnumerable<string> SafeDirectories(string dir)
        {
            try
            {
                return System.IO.Directory.GetDirectories(dir);
            }
            catch (Exception)
            {
                return Array.Empty<string>();
            }
        }

        public static ModuleInfo Describe(string buildCsPath, string plugin)
        {
            string dir = Path.GetDirectoryName(buildCsPath);
            string file = Path.GetFileName(buildCsPath);
            return new ModuleInfo
            {
                Name = file.EndsWith(".Build.cs", StringComparison.OrdinalIgnoreCase) ? file.Substring(0, file.Length - ".Build.cs".Length) : Path.GetFileNameWithoutExtension(file),
                Directory = dir,
                BuildCsPath = buildCsPath,
                Plugin = plugin,
                HasPublic = System.IO.Directory.Exists(Path.Combine(dir, "Public")),
                HasPrivate = System.IO.Directory.Exists(Path.Combine(dir, "Private")),
                HasClasses = System.IO.Directory.Exists(Path.Combine(dir, "Classes")),
            };
        }

        /// <summary>"A/B\C/" → "A\B\C" (OS separator); null when a segment is invalid ("..", ":", "*" …). Empty stays empty.</summary>
        public static string NormalizeSubdirectory(string subdir)
        {
            if (string.IsNullOrWhiteSpace(subdir)) return string.Empty;
            var parts = subdir.Split(new[] { '/', '\\' }, StringSplitOptions.RemoveEmptyEntries).Select(p => p.Trim()).Where(p => p.Length > 0).ToList();
            foreach (var p in parts)
                if (p == "." || p == ".." || p.IndexOfAny(new[] { ':', '*', '?', '"', '<', '>', '|' }) >= 0 || p.EndsWith(".", StringComparison.Ordinal)) return null;
            return string.Join(Path.DirectorySeparatorChar.ToString(), parts);
        }

        /// <summary>
        /// File locations. Public: header in Public (or Classes) / sub, source in Private / sub, and the .cpp includes "sub/Name.h".
        /// Private (or a module without Public / Private folders): both side by side, the .cpp includes "Name.h".
        /// </summary>
        public static NewClassTarget Target(ModuleInfo module, bool isPublic, string subdir, string fileBase)
        {
            string sub = NormalizeSubdirectory(subdir) ?? string.Empty;
            string header, source, include;
            if (module.CanSplit && isPublic)
            {
                string pub = Path.Combine(module.Directory, module.HasPublic ? "Public" : "Classes");
                header = Combine(pub, sub, fileBase + ".h");
                source = Combine(Path.Combine(module.Directory, "Private"), sub, fileBase + ".cpp");
                include = (sub.Length == 0 ? string.Empty : sub.Replace('\\', '/').Replace(Path.DirectorySeparatorChar, '/') + "/") + fileBase + ".h";
            }
            else
            {
                string root = module.HasPrivate ? Path.Combine(module.Directory, "Private")
                    : module.HasPublic ? Path.Combine(module.Directory, "Public") : module.Directory;
                header = Combine(root, sub, fileBase + ".h");
                source = Combine(root, sub, fileBase + ".cpp");
                include = fileBase + ".h";
            }
            return new NewClassTarget { HeaderPath = header, SourcePath = source, HeaderInclude = include };
        }

        private static string Combine(string root, string sub, string file) => sub.Length == 0 ? Path.Combine(root, file) : Path.Combine(root, sub, file);

        /// <summary>
        /// The module, Public / Private choice and subfolder for a folder (Solution Explorer command): the innermost module that
        /// contains it; Public / Classes → public, Private → private, elsewhere in the module → public when the module can split.
        /// </summary>
        public static (ModuleInfo Module, bool IsPublic, string Subdirectory) ForFolder(IReadOnlyList<ModuleInfo> modules, string folder)
        {
            if (string.IsNullOrEmpty(folder) || modules == null) return (null, true, string.Empty);
            string f = Norm(folder);
            ModuleInfo best = null;
            foreach (var m in modules)
            {
                string d = Norm(m.Directory);
                if ((f == d || f.StartsWith(d + "\\", StringComparison.OrdinalIgnoreCase)) && (best == null || d.Length > Norm(best.Directory).Length)) best = m;
            }
            if (best == null) return (null, true, string.Empty);
            string rel = f.Length > Norm(best.Directory).Length ? f.Substring(Norm(best.Directory).Length + 1) : string.Empty;
            var parts = rel.Split(new[] { '\\' }, StringSplitOptions.RemoveEmptyEntries).ToList();
            bool isPublic = best.CanSplit;
            if (parts.Count > 0)
            {
                if (string.Equals(parts[0], "Public", StringComparison.OrdinalIgnoreCase) || string.Equals(parts[0], "Classes", StringComparison.OrdinalIgnoreCase))
                {
                    isPublic = true;
                    parts.RemoveAt(0);
                }
                else if (string.Equals(parts[0], "Private", StringComparison.OrdinalIgnoreCase))
                {
                    isPublic = false;
                    parts.RemoveAt(0);
                }
            }
            return (best, isPublic, string.Join(Path.DirectorySeparatorChar.ToString(), parts));
        }

        private static string Norm(string p) => (p ?? string.Empty).Replace('/', '\\').TrimEnd('\\');

        /// <summary>Folder of a Solution Explorer filter: a file <paramref name="depth"/> levels below it → that many directories up.</summary>
        public static string FolderFromDescendant(string filePath, int depth)
        {
            if (string.IsNullOrEmpty(filePath)) return null;
            string dir = Path.GetDirectoryName(filePath);
            for (int i = 0; i < depth && dir != null; i++) dir = Path.GetDirectoryName(dir);
            return dir;
        }

        private static readonly Regex CopyrightLine = new Regex(@"^\s*CopyrightNotice\s*=\s*(?<v>.*)$", RegexOptions.CultureInvariant | RegexOptions.Multiline);

        /// <summary>CopyrightNotice of DefaultGame.ini ([/Script/EngineSettings.GeneralProjectSettings]); null when empty / missing.</summary>
        public static string CopyrightFromIni(string iniText)
        {
            if (string.IsNullOrEmpty(iniText)) return null;
            var m = CopyrightLine.Match(iniText);
            if (!m.Success) return null;
            string v = m.Groups["v"].Value.Trim().TrimEnd('\r').Trim();
            if (v.Length >= 2 && v[0] == '"' && v[v.Length - 1] == '"') v = v.Substring(1, v.Length - 2);
            return v.Length == 0 ? null : v;
        }

        // ---------------------------------------------------------------- templates

        public static string Header(NewClassSpec s)
        {
            var b = new Lines(s.NewLine);
            Copyright(b, s);
            b.Add("#pragma once").Add();
            b.Add("#include \"CoreMinimal.h\"");
            if (!string.IsNullOrEmpty(s.ParentInclude)) b.Add("#include \"" + s.ParentInclude + "\"");
            string baseName = FileBaseName(s.ClassName, s.Kind);
            if (IsReflected(s.Kind)) b.Add("#include \"" + baseName + ".generated.h\"");
            b.Add();
            string api = string.IsNullOrEmpty(s.ApiMacro) ? string.Empty : s.ApiMacro + " ";
            string inherit = string.IsNullOrEmpty(s.ParentName) ? string.Empty : " : public " + s.ParentName;
            bool tick = s.WithBeginPlayAndTick && HasBeginPlayAndTick(s.Kind);
            switch (s.Kind)
            {
                case NewClassKind.Struct:
                    b.Add("USTRUCT(BlueprintType)");
                    b.Add("struct " + api + s.ClassName + inherit);
                    b.Add("{");
                    b.Add("\tGENERATED_BODY()").Add();
                    b.Add("\t" + s.ClassName + "();");
                    b.Add("};");
                    break;
                case NewClassKind.Plain:
                    b.Add("class " + api + s.ClassName + inherit);
                    b.Add("{");
                    b.Add("public:");
                    b.Add("\t" + s.ClassName + "();");
                    b.Add("\t" + (string.IsNullOrEmpty(s.ParentName) ? "" : "virtual ") + "~" + s.ClassName + "();");
                    b.Add("};");
                    break;
                default:
                    b.Add(s.Kind == NewClassKind.ActorComponent ? "UCLASS(ClassGroup=(Custom), meta=(BlueprintSpawnableComponent))" : "UCLASS()");
                    b.Add("class " + api + s.ClassName + inherit);
                    b.Add("{");
                    b.Add("\tGENERATED_BODY()").Add();
                    b.Add("public:");
                    b.Add(s.Kind == NewClassKind.Object ? "\t" + s.ClassName + "(const FObjectInitializer& ObjectInitializer);" : "\t" + s.ClassName + "();");
                    if (tick)
                    {
                        b.Add().Add("protected:");
                        b.Add("\tvirtual void BeginPlay() override;").Add();
                        b.Add("public:");
                        b.Add(s.Kind == NewClassKind.Actor
                            ? "\tvirtual void Tick(float DeltaTime) override;"
                            : "\tvirtual void TickComponent(float DeltaTime, ELevelTick TickType, FActorComponentTickFunction* ThisTickFunction) override;");
                    }
                    b.Add("};");
                    break;
            }
            return b.ToString();
        }

        public static string Source(NewClassSpec s)
        {
            var b = new Lines(s.NewLine);
            Copyright(b, s);
            b.Add("#include \"" + (s.HeaderInclude ?? FileBaseName(s.ClassName, s.Kind) + ".h") + "\"").Add();
            string c = s.ClassName;
            bool tick = s.WithBeginPlayAndTick && HasBeginPlayAndTick(s.Kind);
            switch (s.Kind)
            {
                case NewClassKind.Actor:
                    b.Add(c + "::" + c + "()").Add("{");
                    b.Add("\tPrimaryActorTick.bCanEverTick = " + (tick ? "true" : "false") + ";");
                    b.Add("}");
                    if (tick)
                    {
                        b.Add().Add("void " + c + "::BeginPlay()").Add("{").Add("\tSuper::BeginPlay();").Add("}");
                        b.Add().Add("void " + c + "::Tick(float DeltaTime)").Add("{").Add("\tSuper::Tick(DeltaTime);").Add("}");
                    }
                    break;
                case NewClassKind.ActorComponent:
                    b.Add(c + "::" + c + "()").Add("{");
                    b.Add("\tPrimaryComponentTick.bCanEverTick = " + (tick ? "true" : "false") + ";");
                    b.Add("}");
                    if (tick)
                    {
                        b.Add().Add("void " + c + "::BeginPlay()").Add("{").Add("\tSuper::BeginPlay();").Add("}");
                        b.Add().Add("void " + c + "::TickComponent(float DeltaTime, ELevelTick TickType, FActorComponentTickFunction* ThisTickFunction)")
                         .Add("{").Add("\tSuper::TickComponent(DeltaTime, TickType, ThisTickFunction);").Add("}");
                    }
                    break;
                case NewClassKind.Object:
                    b.Add(c + "::" + c + "(const FObjectInitializer& ObjectInitializer)");
                    b.Add("\t: Super(ObjectInitializer)").Add("{").Add("}");
                    break;
                case NewClassKind.Struct:
                    b.Add(c + "::" + c + "()").Add("{").Add("}");
                    break;
                default:
                    b.Add(c + "::" + c + "()").Add("{").Add("}");
                    b.Add().Add(c + "::~" + c + "()").Add("{").Add("}");
                    break;
            }
            return b.ToString();
        }

        private static void Copyright(Lines b, NewClassSpec s)
        {
            if (string.IsNullOrWhiteSpace(s.Copyright)) return;
            string text = s.Copyright.Trim();
            b.Add(text.StartsWith("//", StringComparison.Ordinal) ? text : "// " + text).Add();
        }

        private sealed class Lines
        {
            private readonly StringBuilder _sb = new StringBuilder();
            private readonly string _nl;

            public Lines(string nl) => _nl = string.IsNullOrEmpty(nl) ? "\r\n" : nl;

            public Lines Add(string line = "")
            {
                _sb.Append(line).Append(_nl);
                return this;
            }

            public override string ToString() => _sb.ToString();
        }
    }
}
