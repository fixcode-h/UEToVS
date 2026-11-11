using UnrealBuildTool;

public class UEToVSLink : ModuleRules
{
	public UEToVSLink(ReadOnlyTargetRules Target) : base(Target)
	{
		PCHUsage = PCHUsageMode.UseExplicitOrSharedPCHs;

		PrivateDependencyModuleNames.AddRange(new string[]
		{
			"Core",
			"CoreUObject",
			"Engine",
			"Json",
			"Sockets",
			"Networking",
			"Slate",
			"SlateCore",
			"UnrealEd",
			"AssetRegistry",
			"ContentBrowser",
		});

		// Only the interface header is needed; the module is looked up at runtime.
		if (Target.Platform == UnrealTargetPlatform.Win64)
		{
			PrivateIncludePathModuleNames.Add("LiveCoding");
			PrivateDefinitions.Add("UETOVS_WITH_LIVE_CODING=1");
		}
		else
		{
			PrivateDefinitions.Add("UETOVS_WITH_LIVE_CODING=0");
		}
	}
}
