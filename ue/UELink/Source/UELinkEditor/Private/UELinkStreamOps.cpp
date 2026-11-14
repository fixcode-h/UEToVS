// 推送通道那边能力的 HTTP 入口：面板（以及以后的 VS 扩展）不订阅也能用请求 / 应答拿到测试和 Live Coding 的结果。
//   tests.run    {filter, id?}  开始跑自动化测试（同一时间只能跑一组）
//   tests.status {}             当前或最近一次测试运行：running、passed、failed、每条测试的结果和错误
//   livecoding.status {}        当前或最近一次 Live Coding：phase（idle / compiling / finished）、result、编译输出末尾
#include "AgentPanelBridge.h"
#include "AgentPanelBridgeUtil.h"
#include "AgentPanelEditorShared.h"
#include "UELinkStream.h"

#include "Misc/Guid.h"

using namespace AgentPanel;

void AgentPanelEd::RegisterStreamOps()
{
	FAgentPanelBridge& B = FAgentPanelBridge::Get();

	B.AddOp(TEXT("tests.run"), [](const TSharedPtr<FJsonObject>& A, TSharedPtr<FJsonObject>& Out, FString& Error)
	{
		if (IsRunningCommandlet())
		{
			Error = TEXT("后台编辑器里不能跑自动化测试，要在开着的编辑器里跑");
			return false;
		}
		FString Id = Str(A, TEXT("id"));
		if (Id.IsEmpty())
		{
			Id = FGuid::NewGuid().ToString(EGuidFormats::Digits).Left(8).ToLower();
		}
		if (!FUELinkStream::Get().StartTests(Id, Str(A, TEXT("filter")), Error))
		{
			return false;
		}
		Out->SetStringField(TEXT("id"), Id);
		return true;
	});

	B.AddOp(TEXT("tests.status"), [](const TSharedPtr<FJsonObject>& A, TSharedPtr<FJsonObject>& Out, FString& Error)
	{
		Out = FUELinkStream::Get().TestsStatus();
		return true;
	});

	B.AddOp(TEXT("livecoding.status"), [](const TSharedPtr<FJsonObject>& A, TSharedPtr<FJsonObject>& Out, FString& Error)
	{
		Out = FUELinkStream::Get().LiveCodingStatus();
		return true;
	});
}
