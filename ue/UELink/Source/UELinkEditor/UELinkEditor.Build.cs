// UELink 的编辑器模块：资源读写和保存、蓝图图表读写和编译、PIE、Live Coding（触发和结果）、自动化测试、打开资源、
// 推送通道（UEToVS 的日志窗口用），以及后台编辑器（-run=AgentPanel）
using UnrealBuildTool;

public class UELinkEditor : ModuleRules
{
	public UELinkEditor(ReadOnlyTargetRules Target) : base(Target)
	{
		PCHUsage = PCHUsageMode.UseExplicitOrSharedPCHs;
		PublicDependencyModuleNames.AddRange(new string[] { "Core", "CoreUObject", "Engine" });
		PrivateDependencyModuleNames.AddRange(new string[]
		{
			"UELinkCore", "UnrealEd", "Json", "JsonUtilities", "AssetRegistry", "Slate", "SlateCore",
			// 资源新建 / 改名（AssetTools）、蓝图图表读写（BlueprintGraph：K2 节点和节点动作库）
			"AssetTools", "BlueprintGraph",
			// trace 分析（性能页二期）：Unreal Insights 读 trace 用的同一套模块
			"TraceAnalysis", "TraceServices",
			// 推送通道（从 UEToVSLink 合进来）：本机 TCP 服务、在内容浏览器里定位资源
			"Sockets", "Networking", "ContentBrowser"
		});
		if (Target.Platform == UnrealTargetPlatform.Win64)
		{
			PrivateDependencyModuleNames.Add("LiveCoding");
			PrivateDefinitions.Add("AGENT_PANEL_LIVE_CODING=1");
		}
		else
		{
			PrivateDefinitions.Add("AGENT_PANEL_LIVE_CODING=0");
		}
	}
}
