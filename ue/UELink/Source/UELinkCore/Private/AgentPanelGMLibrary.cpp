#include "AgentPanelGMLibrary.h"

#include "Engine/World.h"
#include "GameFramework/PlayerController.h"

APlayerController* UAgentPanelGMLibrary::GetLocalPlayerController() const
{
	UWorld* W = World.Get();
	return W ? W->GetFirstPlayerController() : nullptr;
}
