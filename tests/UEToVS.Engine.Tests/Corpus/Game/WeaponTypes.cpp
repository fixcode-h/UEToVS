#include "WeaponTypes.h" //@caret:include.WeaponTypes

namespace Game
{
namespace Weapons
{
	float FSpread::/*@def:Game.Weapons.FSpread.Compute*/Compute(int32 Shots) const
	{
		const float Base = /*@caret:Game.Weapons.FSpread.Angle*/Angle * Shots;
		return /*@caret:Game.Weapons.Clamp01*/Clamp01(Base / 90.f);
	}

	float /*@def:Game.Weapons.DefaultSpread*/DefaultSpread()
	{
		FSpread Spread;
		return Spread./*@caret:Game.Weapons.FSpread.Compute*/Compute(3);
	}

	float /*@def:Game.Weapons.Clamp01*/Clamp01(float V) { return V < 0.f ? 0.f : (V > 1.f ? 1.f : V); }
}
}

float /*@def:Clamp01*/Clamp01(float V)
{
	return Game::Weapons::/*@caret:Game.Weapons.Clamp01*/Clamp01(V);
}

float UseGlobalClamp(float X)
{
	return ::/*@caret:Clamp01*/Clamp01(X) + /*@caret:Clamp01*/Clamp01(X * 2.f);
}

float UseQualifiedStruct()
{
	Game::Weapons::/*@caret:Game.Weapons.FSpread*/FSpread S;
	return S./*@caret:Game.Weapons.FSpread.Angle*/Angle + Game::Weapons::/*@caret:Game.Weapons.DefaultSpread*/DefaultSpread();
}

template<typename T>
void TRingBuffer<T>::/*@def:TRingBuffer.Push*/Push(const T& Value)
{
	/*@caret:TRingBuffer.Count*/Count++;
}
