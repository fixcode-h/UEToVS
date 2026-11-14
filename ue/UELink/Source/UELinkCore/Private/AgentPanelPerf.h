// 实时性能读数：每帧往环形缓冲记一条和 stat unit 同源的数（perf.live），网页的性能页一打开就能看到实时帧时间和内存
#pragma once

#include "CoreMinimal.h"

class FAgentPanelBridge;

namespace AgentPanel
{
	/** 登记 perf.live：按游标取最近的每帧读数 */
	void RegisterPerfOps(FAgentPanelBridge& Bridge);
}
