using System;
using System.IO;

namespace UEToVS.Engine.Tests
{
    /// <summary>
    /// A synthetic UE layout in a temp folder:
    /// <code>
    /// root/UE/Engine/Build/Build.version                      5.4.2 CL 123
    /// root/UE/Engine/Source/Runtime/Core (+Public)            module Core, FString
    /// root/UE/Engine/Source/ThirdParty/zlib/zlib.h            ThirdParty
    /// root/UE/Engine/Plugins/FX/Niagara                       EnabledByDefault=true
    /// root/UE/Engine/Plugins/Editor/Disabled                  not enabled
    /// root/UE/Engine/Plugins/Misc/Referenced                  enabled by the uproject
    /// root/UE/Engine/Plugins/Misc/DepOfProjPlugin             enabled because a project plugin depends on it
    /// root/Game/MyGame.uproject                               EngineAssociation "../UE"
    /// root/Game/Source/MyGame (Public/Private)                AMyCharacter (UCLASS, UFUNCTION, UPROPERTY)
    /// root/Game/Plugins/Tools/MyTools                         project plugin with content
    /// root/Game/Content/Maps/L_Main.umap, Characters/BP_Hero.uasset
    /// root/Game/Intermediate, Binaries, Saved                 must be skipped
    /// </code>
    /// </summary>
    public sealed class FakeUnrealTree : IDisposable
    {
        public FakeUnrealTree()
        {
            Root = Path.Combine(Path.GetTempPath(), "uetovs-engine-tests", Guid.NewGuid().ToString("N"));
            EngineRoot = Path.Combine(Root, "UE");
            ProjectRoot = Path.Combine(Root, "Game");
            ProjectFile = Path.Combine(ProjectRoot, "MyGame.uproject");

            Write("UE/Engine/Build/Build.version", "{\"MajorVersion\":5,\"MinorVersion\":4,\"PatchVersion\":2,\"Changelist\":123,\"CompatibleChangelist\":100,\"BranchName\":\"++UE5+Release-5.4\"}");
            Write("UE/Engine/Source/Runtime/Core/Core.Build.cs",
                "public class Core : ModuleRules { public Core(ReadOnlyTargetRules Target) : base(Target) { PublicDependencyModuleNames.AddRange(new string[] { \"TraceLog\" }); } }");
            Write("UE/Engine/Source/Runtime/Core/Public/Containers/UnrealString.h", "#pragma once\nclass CORE_API FString\n{\npublic:\n\tint32 Len() const;\n};\n");
            Write("UE/Engine/Source/Runtime/Core/Private/UnrealString.cpp", "#include \"Containers/UnrealString.h\"\n");
            Write("UE/Engine/Source/ThirdParty/zlib/zlib.Build.cs", "public class zlib : ModuleRules { public zlib(ReadOnlyTargetRules Target) : base(Target) { Type = ModuleType.External; } }");
            Write("UE/Engine/Source/ThirdParty/zlib/include/zlib.h", "struct z_stream_s { int avail_in; };\n");
            Write("UE/Engine/Source/ThirdParty/zlib/src/inflate.c", "int inflate() { return 0; }\n");
            Write("UE/Engine/Shaders/Private/Common.ush", "// shader\n");
            Write("UE/Engine/Config/BaseEngine.ini", "[Core.System]\n");
            Write("UE/Engine/Content/EngineMaterials/DefaultMaterial.uasset", "");

            Write("UE/Engine/Plugins/FX/Niagara/Niagara.uplugin", "{\"FriendlyName\":\"Niagara\",\"EnabledByDefault\":true,\"CanContainContent\":true,\"Modules\":[{\"Name\":\"Niagara\",\"Type\":\"Runtime\"},{\"Name\":\"NiagaraEditor\",\"Type\":\"Editor\"}]}");
            Write("UE/Engine/Plugins/FX/Niagara/Source/Niagara/Niagara.Build.cs", BuildCs("Niagara", "Core"));
            Write("UE/Engine/Plugins/FX/Niagara/Source/Niagara/Public/NiagaraSystem.h", "UCLASS()\nclass NIAGARA_API UNiagaraSystem : public UObject\n{\n\tGENERATED_BODY()\n};\n");
            Write("UE/Engine/Plugins/FX/Niagara/Source/NiagaraEditor/NiagaraEditor.Build.cs", BuildCs("NiagaraEditor", "Niagara"));
            Write("UE/Engine/Plugins/FX/Niagara/Content/DefaultSystem.uasset", "");
            Write("UE/Engine/Plugins/Editor/Disabled/Disabled.uplugin", "{\"EnabledByDefault\":false}");
            Write("UE/Engine/Plugins/Editor/Disabled/Source/Disabled/Disabled.Build.cs", BuildCs("Disabled"));
            Write("UE/Engine/Plugins/Editor/Disabled/Source/Disabled/Public/DisabledThing.h", "class FDisabledThing {};\n");
            Write("UE/Engine/Plugins/Misc/Referenced/Referenced.uplugin", "{}");
            Write("UE/Engine/Plugins/Misc/Referenced/Source/Referenced/Referenced.Build.cs", BuildCs("Referenced"));
            Write("UE/Engine/Plugins/Misc/DepOfProjPlugin/DepOfProjPlugin.uplugin", "{}");

            Write("Game/MyGame.uproject",
                "{\n  \"FileVersion\": 3,\n  \"EngineAssociation\": \"../UE\",\n  \"Modules\": [ { \"Name\": \"MyGame\", \"Type\": \"Runtime\", \"LoadingPhase\": \"Default\" } ],\n" +
                "  \"Plugins\": [ { \"Name\": \"Referenced\", \"Enabled\": true }, { \"Name\": \"Niagara\", \"Enabled\": true }, ],\n}");
            Write("Game/Source/MyGame/MyGame.Build.cs", BuildCs("MyGame", "Core", "Niagara"));
            Write("Game/Source/MyGame/Public/MyCharacter.h",
                "#pragma once\n#include \"GameFramework/Character.h\"\n#include \"MyCharacter.generated.h\"\n\n" +
                "UCLASS(Blueprintable)\nclass MYGAME_API AMyCharacter : public ACharacter\n{\n\tGENERATED_BODY()\npublic:\n" +
                "\tUFUNCTION(BlueprintCallable, Category = \"Combat\")\n\tvoid Fire(float Power);\n\n" +
                "\tUPROPERTY(EditAnywhere)\n\tfloat Health = 100.f;\n};\n");
            Write("Game/Source/MyGame/Private/MyCharacter.cpp", "#include \"MyCharacter.h\"\nvoid AMyCharacter::Fire(float Power) {}\n");
            Write("Game/Source/MyGame/Private/notes.txt", "not listed");
            Write("Game/Source/MyGame.Target.cs", "public class MyGameTarget : TargetRules {}");
            Write("Game/Config/DefaultEngine.ini", "[/Script/Engine.Engine]\n");
            Write("Game/Content/Maps/L_Main.umap", "");
            Write("Game/Content/Characters/BP_Hero.uasset", "");
            Write("Game/Content/Maps/__ExternalActors__/L_Main/A/B/KCUWB.uasset", "");
            Write("Game/Plugins/Tools/MyTools/MyTools.uplugin", "{\"Modules\":[{\"Name\":\"MyToolsEditor\",\"Type\":\"Editor\"}],\"Plugins\":[{\"Name\":\"DepOfProjPlugin\",\"Enabled\":true}]}");
            Write("Game/Plugins/Tools/MyTools/Source/MyToolsEditor/MyToolsEditor.Build.cs", BuildCs("MyToolsEditor", "Core", "MyGame"));
            Write("Game/Plugins/Tools/MyTools/Source/MyToolsEditor/Public/ToolWidget.h", "class MYTOOLSEDITOR_API SToolWidget {};\n");
            Write("Game/Plugins/Tools/MyTools/Source/MyToolsEditor/Private/ThirdParty/lib.h", "struct FLibThing {};\n");
            Write("Game/Plugins/Tools/MyTools/Content/Icons/T_Tool.uasset", "");
            Write("Game/Intermediate/Build/Win64/MyCharacter.generated.h", "class Generated {};\n");
            Write("Game/Binaries/Win64/x.h", "");
            Write("Game/Saved/Logs/y.ini", "");
            Write("Game/Source/MyGame/.git/z.h", "");
        }

        public string Root { get; }
        public string EngineRoot { get; }
        public string ProjectRoot { get; }
        public string ProjectFile { get; }

        public string P(string relative) => Path.Combine(Root, relative.Replace('/', Path.DirectorySeparatorChar));

        public string Write(string relative, string content)
        {
            string path = P(relative);
            Directory.CreateDirectory(Path.GetDirectoryName(path));
            File.WriteAllText(path, content);
            return path;
        }

        private static string BuildCs(string name, params string[] deps) =>
            $"using UnrealBuildTool;\npublic class {name} : ModuleRules\n{{\n\tpublic {name}(ReadOnlyTargetRules Target) : base(Target)\n\t{{\n" +
            $"\t\tPublicDependencyModuleNames.AddRange(new string[] {{ {string.Join(", ", Array.ConvertAll(deps, d => "\"" + d + "\""))} }});\n" +
            "\t\tPrivateDependencyModuleNames.Add(\"Slate\");\n\t}\n}\n";

        public void Dispose()
        {
            for (int i = 0; i < 5; i++)
            {
                try
                {
                    if (Directory.Exists(Root)) Directory.Delete(Root, true);
                    return;
                }
                catch (Exception)
                {
                    System.Threading.Thread.Sleep(100);
                }
            }
        }
    }
}
