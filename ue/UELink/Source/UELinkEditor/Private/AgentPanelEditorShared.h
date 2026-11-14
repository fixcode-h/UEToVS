// 编辑器模块各组操作共用的东西：资源寻址（蓝图资源本身）、事务与演练、图表节点的 id 和查找、编译回读。
// 操作分文件登记：AgentPanelBridgeEditor.cpp（DataTable、保存、PIE）、AgentPanelAssetOps.cpp（资源通用层）、
// AgentPanelBlueprintRead.cpp（蓝图大纲、图表、搜节点）、AgentPanelBlueprintEdit.cpp（节点候选、操作列表、编译）、
// AgentPanelTextify.cpp（一版资源文件转文本，评审用）。
#pragma once

#include "AgentPanelBridge.h"
#include "AgentPanelBridgeUtil.h"
#include "CoreMinimal.h"

class UBlueprint;
class UEdGraph;
class UEdGraphNode;
class UEdGraphPin;
class UPackage;
struct FEdGraphPinType;

namespace AgentPanelEd
{
	inline TSharedPtr<FJsonValue> JObj(const TSharedPtr<FJsonObject>& O) { return MakeShared<FJsonValueObject>(O); }
	inline TSharedPtr<FJsonValue> JStr(const FString& S) { return MakeShared<FJsonValueString>(S); }

	/** 标脏并记进「面板改过没保存」的列表 */
	void NoteDirty(UObject* Obj);
	/** 包对应的磁盘文件（面板保存前按它签出 / 添加） */
	FString PackageFile(UPackage* Pkg);
	/** 这次操作新弄脏的包都记进列表：改名、删除、蓝图编译会连带弄脏别的包。Before 是操作前已经脏的包 */
	TSet<UPackage*> DirtyNow();
	void TrackNewDirty(const TSet<UPackage*>& Before, TSharedPtr<FJsonObject>& Out);

	/** 按名字或路径找类：原生类名、/Script 路径、蓝图资源路径（取它生成的类） */
	UClass* FindClassByName(const FString& Name);
	UScriptStruct* FindStructByName(const FString& Name);
	UEnum* FindEnumByName(const FString& Name);

	/** 资源本身（蓝图不换成类默认对象） */
	UObject* LoadAsset(const FString& Path, FString& Error);
	/** 蓝图资源：/Game/BP_Hero、/Game/BP_Hero.BP_Hero、/Game/BP_Hero.BP_Hero_C 都认 */
	UBlueprint* LoadBlueprint(const FString& Path, FString& Error);

	/**
	 * 一次写入包在一个编辑器事务里：编辑器里 Ctrl+Z 能撤，ue_undo 按 Id 撤同一步。
	 * Finish(false) 取消：把事务撤掉，不留改动（失败回滚和演练都走这里）。
	 */
	struct FTx
	{
		FString Id;
		int32 Index = INDEX_NONE;
		bool bOpen = false;

		explicit FTx(const FString& Summary);
		~FTx();
		bool IsValid() const { return bOpen; }
		/** bKeep=false 时撤掉；撤不掉返回 false 并写 Error */
		bool Finish(bool bKeep, FString& Error);
	};

	/** 撤掉栈顶的事务，前提是它就是 TxId 那一步（后面又有别的修改时拒绝，免得撤错） */
	bool UndoTx(const FString& TxId, FString& Error);

	// ---------- 蓝图图表 ----------
	/** 节点 id：节点 GUID 的前 8 位（小写十六进制）；同一张图里撞了就加长 */
	FString NodeId(const UEdGraphNode* Node);
	FString NodeTitle(const UEdGraphNode* Node);
	FString PinTypeText(const FEdGraphPinType& Type);
	/** 蓝图里所有的图（事件图、函数、宏、接口函数、嵌套的子图） */
	TArray<UEdGraph*> AllGraphs(UBlueprint* BP);
	/** 图的种类：event / function / macro / delegate / interface / sub */
	FString GraphKind(UBlueprint* BP, const UEdGraph* Graph);
	UEdGraph* FindGraph(UBlueprint* BP, const FString& Name, FString& Error);
	/** 按 id 前缀找节点；Graph 为空时在整个蓝图里找。有多个对得上时报错让写长一点 */
	UEdGraphNode* FindNode(UBlueprint* BP, UEdGraph* Graph, const FString& Id, FString& Error);
	/** 引脚：先按名字，再不分大小写、按显示名；exec / then / self / return 这几个常用别名也认。找不到时 Error 里列出有哪些引脚 */
	UEdGraphPin* FindPin(UEdGraphNode* Node, const FString& Name, FString& Error);
	bool IsExec(const UEdGraphPin* Pin);

	/** 一张图的伪代码：沿执行线一行一句，数据引脚内联成表达式，行尾带节点 id（设计稿「蓝图读写 · 读」） */
	FString GraphPseudo(UBlueprint* BP, UEdGraph* Graph, int32 MaxLines, bool& bMore);

	/**
	 * 编译并回读：状态、错误和警告（带图名和节点 id）、受影响的子类和调用方蓝图的状态。
	 * bCompile=false 时只读上次编译留在节点上的信息
	 */
	TSharedPtr<FJsonObject> CompileReport(UBlueprint* BP, bool bCompile);
	FString StatusText(const UBlueprint* BP);

	void RegisterAssetOps();
	void RegisterBlueprintReadOps();
	void RegisterBlueprintEditOps();
	/** 一版 .uasset 文件转成文本（AgentPanelTextify.cpp）：asset.textify，评审看二进制资产的差异用 */
	void RegisterTextifyOps();
	/** trace 分析（AgentPanelTrace.cpp）：trace.query */
	void RegisterTraceOps();
	/** 推送通道那边的 HTTP 操作：tests.run、tests.status、livecoding.status（UELinkStreamOps.cpp） */
	void RegisterStreamOps();
}
