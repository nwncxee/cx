//::///////////////////////////////////////////////
//:: vfxxform -- a mirroring doll while the server changes an effect's transform
//:: The PC wears one duration effect, a globe around it, applied with a scale and a translation. The doll beside the world
//:: view mirrors the PC with vfx=all, so one shot shows both: the globe the engine draws on the creature and the globe the
//:: doll wears. The transform changes twice, each time the way the server changes one -- the effect goes and a new one with
//:: the same row and part comes in its place.
//:://////////////////////////////////////////////
#include "inc_show"

// visualeffects.2da VFX_DUR_GLOBE_INVULNERABILITY: a sphere around the creature, big enough to read at a glance.
const int SHOW_GLOBE = 4;

void ClearVfx(object oPC)
{
    effect eOld = GetFirstEffect(oPC);
    while (GetIsEffectValid(eOld))
    {
        if (GetEffectType(eOld) == EFFECT_TYPE_VISUALEFFECT) RemoveEffect(oPC, eOld);
        eOld = GetNextEffect(oPC);
    }
}

void Globe(object oPC, float fScale, float fUp, string sWhat)
{
    ClearVfx(oPC);
    ApplyEffectToObject(DURATION_TYPE_PERMANENT, EffectVisualEffect(SHOW_GLOBE, FALSE, fScale, Vector(0.0, 0.0, fUp)), oPC);
    ShowMark("globe " + sWhat);
}

void Show(object oPC)
{
    NuiDestroy(oPC, NuiFindWindow(oPC, "show_backdrop"));
    DelayCommand(2.0, ShowDoll(oPC, "me", "vfx=all", 300.0, 420.0));
    DelayCommand(10.0, Globe(oPC, 1.0, 0.0, "plain"));
    DelayCommand(20.0, Globe(oPC, 2.2, 1.5, "scaled and raised"));
    DelayCommand(30.0, Globe(oPC, 0.4, 0.0, "shrunk"));
}
