// 蓝图图表的伪代码：从每个事件 / 函数入口沿执行线往下走，一个节点一行，数据引脚内联成表达式，行尾带节点 id。
// 用来读懂逻辑（比节点 JSON 省一个数量级的上下文）；要改图时再取 JSON。带执行线的节点的输出写成 @节点id.引脚名。
#include "AgentPanelEditorShared.h"

#include "EdGraph/EdGraph.h"
#include "EdGraph/EdGraphNode.h"
#include "EdGraph/EdGraphPin.h"
#include "EdGraphNode_Comment.h"
#include "EdGraphSchema_K2.h"
#include "Engine/Blueprint.h"
#include "K2Node_CallFunction.h"
#include "K2Node_DynamicCast.h"
#include "K2Node_Event.h"
#include "K2Node_FunctionEntry.h"
#include "K2Node_FunctionResult.h"
#include "K2Node_IfThenElse.h"
#include "K2Node_Knot.h"
#include "K2Node_MacroInstance.h"
#include "K2Node_Self.h"
#include "K2Node_Tunnel.h"
#include "K2Node_VariableGet.h"
#include "K2Node_VariableSet.h"

using namespace AgentPanelEd;

namespace
{
	bool HasExec(const UEdGraphNode* N)
	{
		for (const UEdGraphPin* P : N->Pins)
		{
			if (IsExec(P))
			{
				return true;
			}
		}
		return false;
	}

	bool IsData(const UEdGraphPin* P, EEdGraphPinDirection Dir)
	{
		return P && P->Direction == Dir && !IsExec(P) && (!P->bHidden || P->LinkedTo.Num() > 0);
	}

	struct FPseudo
	{
		UBlueprint* BP = nullptr;
		UEdGraph* Graph = nullptr;
		TArray<FString> Lines;
		int32 Max = 400;
		bool bMore = false;
		/** 沿执行线走过的节点 */
		TSet<const UEdGraphNode*> Visited;
		/** 被内联进表达式的纯节点 */
		TSet<const UEdGraphNode*> Inlined;

		void Emit(int32 Indent, const FString& Text, const UEdGraphNode* N = nullptr)
		{
			if (Lines.Num() >= Max)
			{
				bMore = true;
				return;
			}
			FString Line = FString::ChrN(Indent * 2, TEXT(' ')) + Text;
			if (N)
			{
				Line += TEXT("  #") + NodeId(N);
			}
			Lines.Add(Line);
		}

		/** 没连线的输入引脚的字面值；空的返回空串（调用方决定要不要写出来） */
		static FString Literal(const UEdGraphPin* Pin)
		{
			if (Pin->DefaultObject)
			{
				return Pin->DefaultObject->GetName();
			}
			if (!Pin->DefaultTextValue.IsEmpty())
			{
				return TEXT("\"") + Pin->DefaultTextValue.ToString() + TEXT("\"");
			}
			const FString S = Pin->GetDefaultAsString();
			if (S.IsEmpty())
			{
				return FString();
			}
			const FName Cat = Pin->PinType.PinCategory;
			if (Cat == UEdGraphSchema_K2::PC_String || Cat == UEdGraphSchema_K2::PC_Name || Cat == UEdGraphSchema_K2::PC_Text)
			{
				return TEXT("\"") + S + TEXT("\"");
			}
			return S;
		}

		/** 一个输入引脚的表达式 */
		FString Expr(const UEdGraphPin* In, int32 Depth)
		{
			if (In->LinkedTo.Num() == 0)
			{
				const FString L = Literal(In);
				if (L.IsEmpty() && In->PinName == UEdGraphSchema_K2::PN_Self)
				{
					return TEXT("self");
				}
				return L;
			}
			const UEdGraphPin* Src = In->LinkedTo[0];
			return Src ? OutExpr(Src, Depth) : FString();
		}

		/** 一个输出引脚的表达式：纯节点内联展开，带执行线的节点写成 @id.引脚 */
		FString OutExpr(const UEdGraphPin* Src, int32 Depth)
		{
			const UEdGraphNode* N = Src->GetOwningNodeUnchecked();
			if (!N)
			{
				return TEXT("?");
			}
			if (const UK2Node_Knot* Knot = Cast<UK2Node_Knot>(N))
			{
				// 走线用的转接点：看它的输入
				Inlined.Add(N);
				for (const UEdGraphPin* P : Knot->Pins)
				{
					if (P && P->Direction == EGPD_Input)
					{
						return Expr(P, Depth);
					}
				}
				return TEXT("?");
			}
			// 事件、函数入口的输出就是参数名
			if (N->IsA<UK2Node_Event>() || N->IsA<UK2Node_FunctionEntry>() || (N->IsA<UK2Node_Tunnel>() && !N->IsA<UK2Node_MacroInstance>()))
			{
				return Src->PinName.ToString();
			}
			if (HasExec(N) || Depth > 8)
			{
				return FString::Printf(TEXT("@%s.%s"), *NodeId(N), *Src->PinName.ToString());
			}
			Inlined.Add(N);
			if (N->IsA<UK2Node_Self>())
			{
				return TEXT("self");
			}
			if (const UK2Node_VariableGet* Get = Cast<UK2Node_VariableGet>(N))
			{
				return Target(N, Depth) + Get->GetVarName().ToString();
			}
			FString Text = Head(N, Depth) + TEXT("(") + Args(N, Depth + 1) + TEXT(")");
			int32 Outs = 0;
			for (const UEdGraphPin* P : N->Pins)
			{
				Outs += IsData(P, EGPD_Output) ? 1 : 0;
			}
			if (Outs > 1)
			{
				Text += TEXT(".") + Src->PinName.ToString();
			}
			return Text;
		}

		/** 连了线的 self 引脚写成「对象.」前缀 */
		FString Target(const UEdGraphNode* N, int32 Depth)
		{
			const UEdGraphPin* Self = const_cast<UEdGraphNode*>(N)->FindPin(UEdGraphSchema_K2::PN_Self);
			if (Self && Self->LinkedTo.Num())
			{
				return Expr(Self, Depth + 1) + TEXT(".");
			}
			return FString();
		}

		/** 节点写成调用时的名字部分 */
		FString Head(const UEdGraphNode* N, int32 Depth)
		{
			if (const UK2Node_CallFunction* Call = Cast<UK2Node_CallFunction>(N))
			{
				return Target(N, Depth) + Call->GetFunctionName().ToString();
			}
			if (const UK2Node_DynamicCast* CastNode = Cast<UK2Node_DynamicCast>(N))
			{
				return FString::Printf(TEXT("cast<%s>"), CastNode->TargetType ? *CastNode->TargetType->GetName() : TEXT("?"));
			}
			if (const UK2Node_MacroInstance* Macro = Cast<UK2Node_MacroInstance>(N))
			{
				return Macro->GetMacroGraph() ? Macro->GetMacroGraph()->GetName() : NodeTitle(N);
			}
			return NodeTitle(N);
		}

		/** 数据输入引脚：名字=表达式；没连线又没有默认值的不写 */
		FString Args(const UEdGraphNode* N, int32 Depth)
		{
			TArray<FString> Parts;
			for (const UEdGraphPin* P : N->Pins)
			{
				if (!IsData(P, EGPD_Input) || P->PinName == UEdGraphSchema_K2::PN_Self)
				{
					continue;
				}
				const FString E = Expr(P, Depth);
				if (!E.IsEmpty())
				{
					Parts.Add(P->PinName.ToString() + TEXT("=") + E);
				}
			}
			return FString::Join(Parts, TEXT(", "));
		}

		/** 入口节点的参数表：名字: 类型 */
		static FString Params(const UEdGraphNode* N, EEdGraphPinDirection Dir)
		{
			TArray<FString> Parts;
			for (const UEdGraphPin* P : N->Pins)
			{
				if (IsData(P, Dir) && !P->bHidden && P->PinName != UEdGraphSchema_K2::PN_Self && P->PinName != TEXT("OutputDelegate"))
				{
					Parts.Add(P->PinName.ToString() + TEXT(": ") + PinTypeText(P->PinType));
				}
			}
			return FString::Join(Parts, TEXT(", "));
		}

		void Follow(const UEdGraphPin* ExecOut, int32 Indent)
		{
			for (const UEdGraphPin* To : ExecOut->LinkedTo)
			{
				const UEdGraphNode* Next = To ? To->GetOwningNodeUnchecked() : nullptr;
				// 执行线上的转接点直接跳过去
				while (Next && Next->IsA<UK2Node_Knot>())
				{
					Inlined.Add(Next);
					const UEdGraphNode* After = nullptr;
					for (const UEdGraphPin* P : Next->Pins)
					{
						if (P && P->Direction == EGPD_Output && P->LinkedTo.Num() && P->LinkedTo[0])
						{
							After = P->LinkedTo[0]->GetOwningNodeUnchecked();
						}
					}
					Next = After;
				}
				if (!Next)
				{
					continue;
				}
				if (Visited.Contains(Next))
				{
					// 汇合或循环回到前面的节点：只写去向，不重复展开
					Emit(Indent, FString::Printf(TEXT("goto #%s"), *NodeId(Next)));
				}
				else
				{
					Walk(Next, Indent);
				}
			}
		}

		void Walk(const UEdGraphNode* N, int32 Indent)
		{
			if (bMore)
			{
				return;
			}
			Visited.Add(N);
			TArray<const UEdGraphPin*> Outs;
			for (const UEdGraphPin* P : N->Pins)
			{
				if (P && P->Direction == EGPD_Output && IsExec(P) && !P->bHidden)
				{
					Outs.Add(P);
				}
			}

			if (const UK2Node_IfThenElse* If = Cast<UK2Node_IfThenElse>(N))
			{
				const UEdGraphPin* Cond = If->GetConditionPin();
				Emit(Indent, FString::Printf(TEXT("if %s:"), Cond ? *Expr(Cond, 0) : TEXT("?")), N);
				if (const UEdGraphPin* Then = If->GetThenPin())
				{
					Follow(Then, Indent + 1);
				}
				const UEdGraphPin* Else = If->GetElsePin();
				if (Else && Else->LinkedTo.Num())
				{
					Emit(Indent, TEXT("else:"));
					Follow(Else, Indent + 1);
				}
				return;
			}

			FString Text;
			if (const UK2Node_Event* Event = Cast<UK2Node_Event>(N))
			{
				Text = FString::Printf(TEXT("Event %s(%s)"), *Event->GetFunctionName().ToString(), *Params(N, EGPD_Output));
			}
			else if (N->IsA<UK2Node_FunctionEntry>())
			{
				Text = FString::Printf(TEXT("Function %s(%s)"), *Graph->GetName(), *Params(N, EGPD_Output));
			}
			else if (N->IsA<UK2Node_FunctionResult>())
			{
				Text = FString::Printf(TEXT("return (%s)"), *Args(N, 0));
			}
			else if (const UK2Node_VariableSet* Set = Cast<UK2Node_VariableSet>(N))
			{
				const UEdGraphPin* Value = const_cast<UK2Node_VariableSet*>(Set)->FindPin(Set->GetVarName());
				Text = FString::Printf(TEXT("%s%s = %s"), *Target(N, 0), *Set->GetVarName().ToString(), Value ? *Expr(Value, 0) : TEXT("?"));
			}
			else if (N->IsA<UK2Node_Tunnel>() && !N->IsA<UK2Node_MacroInstance>())
			{
				// 宏图的入口 / 出口
				Text = Outs.Num() ? FString::Printf(TEXT("Macro %s(%s)"), *Graph->GetName(), *Params(N, EGPD_Output)) : FString::Printf(TEXT("output (%s)"), *Args(N, 0));
			}
			else
			{
				Text = Head(N, 0) + TEXT("(") + Args(N, 0) + TEXT(")");
			}
			Emit(Indent, Text, N);

			// 入口节点下面的内容缩进一级；只有一个执行输出的接着往下写；多个输出的每个分支单独缩进
			const bool bEntry = N->IsA<UK2Node_Event>() || N->IsA<UK2Node_FunctionEntry>() || (N->IsA<UK2Node_Tunnel>() && !N->IsA<UK2Node_MacroInstance>());
			if (Outs.Num() == 1)
			{
				Follow(Outs[0], bEntry ? Indent + 1 : Indent);
				return;
			}
			for (const UEdGraphPin* Out : Outs)
			{
				if (Out->LinkedTo.Num())
				{
					Emit(Indent + 1, FString::Printf(TEXT("-> %s:"), *Out->PinName.ToString()));
					Follow(Out, Indent + 2);
				}
			}
		}

		void Run()
		{
			TArray<const UEdGraphNode*> Nodes;
			for (const UEdGraphNode* N : Graph->Nodes)
			{
				if (N)
				{
					Nodes.Add(N);
				}
			}
			// 从上到下、从左到右，和图上看到的顺序一致
			Nodes.Sort([](const UEdGraphNode& L, const UEdGraphNode& R)
			{
				return L.NodePosY != R.NodePosY ? L.NodePosY < R.NodePosY : L.NodePosX < R.NodePosX;
			});

			for (const UEdGraphNode* N : Nodes)
			{
				if (const UEdGraphNode_Comment* C = Cast<UEdGraphNode_Comment>(N))
				{
					Emit(0, FString::Printf(TEXT("// [注释框] %s"), *C->NodeComment.Replace(TEXT("\n"), TEXT(" "))), N);
				}
			}

			// 入口：有执行输出、没有执行输入的节点（事件、函数入口、输入事件…）
			for (const UEdGraphNode* N : Nodes)
			{
				bool bIn = false;
				bool bOut = false;
				for (const UEdGraphPin* P : N->Pins)
				{
					if (IsExec(P))
					{
						(P->Direction == EGPD_Input ? bIn : bOut) = true;
					}
				}
				if (bOut && !bIn && !Visited.Contains(N))
				{
					if (Lines.Num())
					{
						Emit(0, FString());
					}
					Walk(N, 0);
				}
			}

			// 没接到任何入口上的执行节点：从没有上游的那个开始走，单独列出来免得漏看
			bool bHeader = false;
			for (const UEdGraphNode* N : Nodes)
			{
				if (Visited.Contains(N) || !HasExec(N) || N->IsA<UK2Node_Knot>())
				{
					continue;
				}
				bool bHasUpstream = false;
				for (const UEdGraphPin* P : N->Pins)
				{
					if (IsExec(P) && P->Direction == EGPD_Input && P->LinkedTo.Num())
					{
						bHasUpstream = true;
					}
				}
				if (bHasUpstream)
				{
					continue;
				}
				if (!bHeader)
				{
					Emit(0, FString());
					Emit(0, TEXT("// 没接到入口上的节点（不会执行）"));
					bHeader = true;
				}
				Walk(N, 0);
			}

			// 没被任何一行用到的纯节点
			bHeader = false;
			for (const UEdGraphNode* N : Nodes)
			{
				if (Visited.Contains(N) || Inlined.Contains(N) || HasExec(N) || N->IsA<UEdGraphNode_Comment>() || N->IsA<UK2Node_Knot>())
				{
					continue;
				}
				if (!bHeader)
				{
					Emit(0, FString());
					Emit(0, TEXT("// 没被用到的数据节点"));
					bHeader = true;
				}
				const UEdGraphPin* FirstOut = nullptr;
				for (const UEdGraphPin* P : N->Pins)
				{
					if (IsData(P, EGPD_Output))
					{
						FirstOut = P;
						break;
					}
				}
				Emit(0, FirstOut ? OutExpr(FirstOut, 0) : NodeTitle(N), N);
				Inlined.Remove(N);
			}
		}
	};
}

FString AgentPanelEd::GraphPseudo(UBlueprint* BP, UEdGraph* Graph, int32 MaxLines, bool& bMore)
{
	FPseudo P;
	P.BP = BP;
	P.Graph = Graph;
	P.Max = MaxLines;
	P.Run();
	bMore = P.bMore;
	return FString::Join(P.Lines, TEXT("\n"));
}
