#include "AgentPanelCommandlet.h"
#include "AgentPanelBridge.h"
#include "AssetRegistry/AssetRegistryModule.h"
#include "Containers/Ticker.h"
#include "Async/TaskGraphInterfaces.h"
#include "HAL/PlatformProcess.h"
#include "HAL/PlatformTime.h"
#include "Misc/CoreDelegates.h"
#include "Misc/Parse.h"

UAgentPanelCommandlet::UAgentPanelCommandlet()
{
	IsClient = false;
	IsEditor = true;
	IsServer = false;
	LogToConsole = true;
}

int32 UAgentPanelCommandlet::Main(const FString& Params)
{
	// commandlet 在引擎初始化完之前就跑，运行时模块等的 OnPostEngineInit 不会来，这里自己起服务
	int32 IdleMinutes = 10;
	FParse::Value(*Params, TEXT("AgentPanelIdle="), IdleMinutes);
	if (IdleMinutes <= 0)
	{
		IdleMinutes = 10;
	}

	// 先把资源注册表扫完：按名字前缀搜资源、按类列资源都靠它
	IAssetRegistry& Registry = FModuleManager::LoadModuleChecked<FAssetRegistryModule>(TEXT("AssetRegistry")).Get();
	Registry.SearchAllAssets(true);

	FAgentPanelBridge& Bridge = FAgentPanelBridge::Get();
	if (!Bridge.Start(TEXT("background")))
	{
		UE_LOG(LogTemp, Error, TEXT("AgentPanel: 后台编辑器的桥接服务没起来（端口 30110-30199 都被占了？）"));
		return 1;
	}
	UE_LOG(LogTemp, Display, TEXT("AgentPanel: 后台编辑器已就绪，闲置 %d 分钟后退出"), IdleMinutes);

	const double IdleSeconds = IdleMinutes * 60.0;
	double Last = FPlatformTime::Seconds();
	while (!IsEngineExitRequested())
	{
		const double Now = FPlatformTime::Seconds();
		// HTTP 服务挂在核心 Ticker 上，请求的处理排在游戏线程：两样都要自己泵
		FTSTicker::GetCoreTicker().Tick(static_cast<float>(Now - Last));
		FTaskGraphInterface::Get().ProcessThreadUntilIdle(ENamedThreads::GameThread);
		Last = Now;

		const double Since = Bridge.LastRequestTime() > 0 ? Bridge.LastRequestTime() : 0;
		static const double Started = FPlatformTime::Seconds();
		if (Now - FMath::Max(Since, Started) > IdleSeconds)
		{
			UE_LOG(LogTemp, Display, TEXT("AgentPanel: 闲置 %d 分钟，后台编辑器退出"), IdleMinutes);
			break;
		}
		FPlatformProcess::Sleep(0.02f);
	}
	Bridge.Stop();
	return 0;
}
