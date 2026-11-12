// IntelliSense corpus (v0.4.5): generic template instantiation carets (expectations in TemplateInstantiationTests).
#include "Templates/GameTemplates.h"

void FTemplateUser::Run(AActor* Actor)
{
	// user class templates: parameters, member typedef / using, base templates, nested instances
	PtrBox.Get()->Char/*@complete:tpl.box.alias.return*/;
	PtrBox.Value->Char/*@complete:tpl.box.field*/;
	AliasBox.GetPtr()->Char/*@complete:tpl.box.typedef*/;
	Ring.At(0).He/*@complete:tpl.derived.ref*/;
	Ring.Get()->He/*@complete:tpl.base.member*/;
	Ring.Value->He/*@complete:tpl.base.field*/;
	Ring.Front().Value.He/*@complete:tpl.return.instance*/;
	Nested.Get().Get()->Char/*@complete:tpl.nested*/;
	Nested.Value.Value->Char/*@complete:tpl.nested.field*/;
	Pair.GetValue()->Char/*@complete:tpl.pair*/;
	// operator-> of a user smart pointer, aliases and alias templates
	Ptr->Char/*@complete:tpl.arrow.op*/;
	Ptr.Get()->Cool/*@complete:tpl.arrow.get*/;
	AliasPtr->Char/*@complete:tpl.alias.arrow*/;
	AliasBox.Get().Char/*@complete:tpl.alias.box*/;
	AliasTemplate.Get()->He/*@complete:tpl.alias.template*/;
	SharedAlias->Dam/*@complete:tpl.alias.shared*/;
	TGameBox<AGadget>::ValueType Dependent;
	Dependent.Char/*@complete:tpl.dependent*/;
	// UE templates declared in the index with their real parameter names
	MaybeLoc.GetValue().Si/*@complete:tpl.optional*/;
	MaybeGadget.GetValue()->Char/*@complete:tpl.optional.ptr*/;
	MaybeGadget.GetPtrOrNull()->/*@complete:tpl.optional.ptrornull*/;
	Shared.Get()->Dam/*@complete:tpl.shared.get*/;
	Shared->Dam/*@complete:tpl.shared.arrow*/;
	SharedRef->Dam/*@complete:tpl.sharedref.arrow*/;
	SharedRef.Get().Dam/*@complete:tpl.sharedref.get*/;
	Shared.ToSharedRef()->Dam/*@complete:tpl.shared.toref*/;
	WeakShared.Pin()->Dam/*@complete:tpl.weakptr.pin*/;
	Unique->Dam/*@complete:tpl.unique.arrow*/;
	Unique.Get()->Dam/*@complete:tpl.unique.get*/;
	Weak.Get()->Char/*@complete:tpl.weak.get*/;
	Weak->Char/*@complete:tpl.weak.arrow*/;
	Soft.LoadSynchronous()->Char/*@complete:tpl.soft.load*/;
	Soft.Get()->Char/*@complete:tpl.soft.get*/;
	GadgetClass.GetDefaultObject()->Char/*@complete:tpl.subclass.cdo*/;
	Iface->Pi/*@complete:tpl.iface.arrow*/;
	Iface.GetInterface()->Pi/*@complete:tpl.iface.get*/;
	// function templates with explicit arguments
	MakeGameThing<AGadget>(1)->Char/*@complete:tpl.func.explicit*/;
	BoxOf<AGadget*>(nullptr).Get()->Char/*@complete:tpl.func.instance*/;
	Cast<AGadget>(Actor)->Char/*@complete:tpl.cast*/;
	NewObject<UHealthComponent>(Actor)->He/*@complete:tpl.newobject*/;
	Actor->FindComponentByClass<UHealthComponent>()->He/*@complete:tpl.findcomp*/;
	PtrBox.As<UHealthComponent>()->He/*@complete:tpl.member.explicit*/;
	auto* FromRing = Ring.Get();
	FromRing->He/*@complete:tpl.auto*/;
	auto Boxed = BoxOf<UHealthComponent*>(nullptr);
	Boxed.Value->He/*@complete:tpl.auto.instance*/;
	for (AGadget* G : PtrBox.Value->Linked) { G->Char/*@complete:tpl.rangefor*/; }
}
