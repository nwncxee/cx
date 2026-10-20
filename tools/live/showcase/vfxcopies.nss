//::///////////////////////////////////////////////
//:: vfxcopies -- a mirroring doll while the PC wears two copies of one effect
//:: The PC wears the same row twice, each copy with its own scale and height: a small globe at its feet, then a large one
//:: raised above it. The second goes on in a later update, the only way the engine sends a second copy of a row. The doll
//:: beside the world view mirrors the PC with vfx=all, so one shot shows whether it wears both copies.
//:://////////////////////////////////////////////
#include "inc_show"

// visualeffects.2da VFX_DUR_GLOBE_INVULNERABILITY: a sphere around the creature, big enough to read at a glance.
const int SHOW_GLOBE = 4;

void Globe(object oPC, float fScale, float fUp, string sWhat)
{
    ApplyEffectToObject(DURATION_TYPE_PERMANENT, EffectVisualEffect(SHOW_GLOBE, FALSE, fScale, Vector(0.0, 0.0, fUp)), oPC);
    ShowMark("globe " + sWhat);
}

void Show(object oPC)
{
    NuiDestroy(oPC, NuiFindWindow(oPC, "show_backdrop"));
    DelayCommand(2.0, ShowDoll(oPC, "me", "vfx=all", 300.0, 420.0));
    DelayCommand(10.0, Globe(oPC, 0.6, 0.0, "first copy, small"));
    DelayCommand(16.0, Globe(oPC, 1.8, 1.6, "second copy, large and raised"));
}
