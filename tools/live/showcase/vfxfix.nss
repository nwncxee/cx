//::///////////////////////////////////////////////
//:: vfxfix -- the effects CX-663 fixes, in one outdoor scene: a lightning bolt's beam reaches its target, a
//:: fireball bursts on it, and a head effect on the PC keeps following the PC after its head and torso pieces
//:: are swapped and it walks away. The target is passive and plot, so the spells play and change nothing.
//:://////////////////////////////////////////////
#include "inc_show"

const float VFXFIX_BEARING = 90.0;

void Bolt(object oPC, object oFoe)
{
    AssignCommand(oPC, ClearAllActions(TRUE));
    AssignCommand(oPC, ActionCastSpellAtObject(SPELL_LIGHTNING_BOLT, oFoe, METAMAGIC_ANY, TRUE, 0, PROJECTILE_PATH_TYPE_DEFAULT, TRUE));
    ShowMark("cast lightning bolt");
}

void Blast(object oPC, object oFoe)
{
    AssignCommand(oPC, ClearAllActions(TRUE));
    AssignCommand(oPC, ActionCastSpellAtObject(SPELL_FIREBALL, oFoe, METAMAGIC_ANY, TRUE, 0, PROJECTILE_PATH_TYPE_DEFAULT, TRUE));
    ShowMark("cast fireball");
}

void Swap(object oPC)
{
    SetCreatureBodyPart(CREATURE_PART_HEAD, GetCreatureBodyPart(CREATURE_PART_HEAD, oPC) == 2 ? 3 : 2, oPC);
    SetCreatureBodyPart(CREATURE_PART_TORSO, GetCreatureBodyPart(CREATURE_PART_TORSO, oPC) == 2 ? 1 : 2, oPC);
    ShowMark("swapped head and torso");
}

void Walk(object oPC)
{
    vector vPC = GetPosition(oPC);
    location lTo = Location(GetArea(oPC), Vector(vPC.x + 6.0, vPC.y, vPC.z), 0.0);
    AssignCommand(oPC, ClearAllActions(TRUE));
    AssignCommand(oPC, ActionMoveToLocation(lTo));
    ShowMark("walk");
}

void Place(object oPC)
{
    NuiDestroy(oPC, NuiFindWindow(oPC, "show_backdrop"));
    SetImmortal(oPC, TRUE);
    SetTime(12, 0, 0, 0);
    AssignCommand(oPC, SetFacing(VFXFIX_BEARING));
    AssignCommand(oPC, SetCameraFacing(VFXFIX_BEARING + 60.0, 12.0, 65.0, CAMERA_TRANSITION_TYPE_SNAP));
    object oFoe = ShowSpawn(oPC, "foe", "nw_goblina", VFXFIX_BEARING, 9.0);
    ApplyEffectToObject(DURATION_TYPE_PERMANENT, EffectVisualEffect(VFX_DUR_MIND_AFFECTING_DOMINATED), oPC);
    ShowMark("placed");
    DelayCommand(6.0, Bolt(oPC, oFoe));
    DelayCommand(14.0, Blast(oPC, oFoe));
    DelayCommand(24.0, Swap(oPC));
    DelayCommand(28.0, Walk(oPC));
}

void Show(object oPC)
{
    object oArea = GetFirstArea();
    while (GetIsObjectValid(oArea) && GetIsAreaInterior(oArea)) oArea = GetNextArea();
    if (!GetIsObjectValid(oArea)) { ShowMark("outdoor-FAILED: the module has no outdoor area"); return; }
    vector vMid = Vector(GetAreaSize(AREA_WIDTH, oArea) * 5.0, GetAreaSize(AREA_HEIGHT, oArea) * 5.0, 0.0);
    AssignCommand(oPC, JumpToLocation(Location(oArea, vMid, VFXFIX_BEARING)));
    ShowMark("jump " + GetName(oArea));
    DelayCommand(6.0, Place(oPC));
}
