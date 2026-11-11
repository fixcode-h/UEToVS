#pragma once

#include "CoreMinimal.h"
#include "Containers/Queue.h"
#include "Containers/Ticker.h"
#include "Modules/ModuleInterface.h"

class FSocket;
class FTcpListener;
struct FIPv4Endpoint;

/**
 * Localhost TCP server for the UEToVS Visual Studio extension.
 * Protocol: one JSON object per line, e.g. {"cmd":"openAsset","path":"/Game/Hero/BP_Hero.BP_Hero"};
 * one JSON reply line {"ok":true} then the connection is closed.
 * The listening port is written to <Project>/Saved/UEToVSLink/port.txt.
 */
class FUEToVSLinkModule : public IModuleInterface
{
public:
	virtual void StartupModule() override;
	virtual void ShutdownModule() override;

private:
	struct FClient
	{
		FSocket* Socket = nullptr;
		TArray<uint8> Buffer;
		double StartTime = 0.0;
	};

	bool OnConnection(FSocket* Socket, const FIPv4Endpoint& Endpoint);
	bool Tick(float DeltaTime);
	FString Handle(const FString& Line);
	void Reply(FSocket* Socket, const FString& Json);
	FString PortFilePath() const;

	TUniquePtr<FTcpListener> Listener;
	TQueue<FSocket*, EQueueMode::Mpsc> Pending;
	TArray<FClient> Clients;
	FTSTicker::FDelegateHandle TickHandle;
};
