//::///////////////////////////////////////////////
//:: camjerk -- the camera-jerk capture: in the middle of the module's first outdoor area, away from any wall,
//:: the PC shoots three goblins with a longbow, closes on them with a spear and casts at them. Nothing but its own
//:: gear, the goblins, their corpses, the arrows and the spell is ever between it and the camera.
//:://////////////////////////////////////////////
#include "inc_show"

const float CAMJERK_BEARING = 90.0;

void Melee(object oPC)
{
    object oSpear = CreateItemOnObject("nw_wplss001", oPC);
    AssignCommand(oPC, ClearAllActions(TRUE));
    AssignCommand(oPC, ActionEquipItem(oSpear, INVENTORY_SLOT_RIGHTHAND));
    AssignCommand(oPC, ActionAttack(GetNearestCreature(CREATURE_TYPE_REPUTATION, REPUTATION_TYPE_ENEMY, oPC)));
    ShowMark("melee " + GetTag(oSpear));
}

void Cast(object oPC)
{
    object oFoe = GetNearestCreature(CREATURE_TYPE_REPUTATION, REPUTATION_TYPE_ENEMY, oPC);
    AssignCommand(oPC, ClearAllActions(TRUE));
    AssignCommand(oPC, ActionCastSpellAtObject(SPELL_MAGIC_MISSILE, oFoe, METAMAGIC_ANY, TRUE));
    AssignCommand(oPC, ActionCastSpellAtObject(SPELL_FLAME_ARROW, oFoe, METAMAGIC_ANY, TRUE));
    ShowMark("cast " + IntToString(GetIsObjectValid(oFoe)));
}

void Fight(object oPC)
{
    vector vPC = GetPosition(oPC);
    int i;
    for (i = 0; i < 3; i++)
    {
        float fAt = CAMJERK_BEARING - 15.0 + 15.0 * IntToFloat(i);
        vector vAt = Vector(vPC.x + cos(fAt) * 9.0, vPC.y + sin(fAt) * 9.0, vPC.z);
        CreateObject(OBJECT_TYPE_CREATURE, "nw_goblina", Location(GetArea(oPC), vAt, fAt + 180.0));
    }
    object oBow = CreateItemOnObject("nw_wbwln001", oPC);
    object oArrows = CreateItemOnObject("nw_wamar001", oPC, 99);
    AssignCommand(oPC, ActionEquipItem(oArrows, INVENTORY_SLOT_ARROWS));
    AssignCommand(oPC, ActionEquipItem(oBow, INVENTORY_SLOT_RIGHTHAND));
    AssignCommand(oPC, ActionAttack(GetNearestCreature(CREATURE_TYPE_REPUTATION, REPUTATION_TYPE_ENEMY, oPC)));
    ShowMark("bow " + IntToString(GetIsObjectValid(oBow)));
    DelayCommand(14.0, Melee(oPC));
    DelayCommand(26.0, Cast(oPC));
}

void Place(object oPC)
{
    NuiDestroy(oPC, NuiFindWindow(oPC, "show_backdrop"));
    SetImmortal(oPC, TRUE);
    SetTime(12, 0, 0, 0);
    AssignCommand(oPC, SetFacing(CAMJERK_BEARING));
    AssignCommand(oPC, SetCameraFacing(CAMJERK_BEARING, 8.0, 70.0, CAMERA_TRANSITION_TYPE_SNAP));
    ShowMark("placed");
    DelayCommand(2.0, Fight(oPC));
}

void Show(object oPC)
{
    object oArea = GetFirstArea();
    while (GetIsObjectValid(oArea) && GetIsAreaInterior(oArea)) oArea = GetNextArea();
    if (!GetIsObjectValid(oArea)) { ShowMark("outdoor-FAILED: the module has no outdoor area"); return; }
    vector vMid = Vector(GetAreaSize(AREA_WIDTH, oArea) * 5.0, GetAreaSize(AREA_HEIGHT, oArea) * 5.0, 0.0);
    AssignCommand(oPC, JumpToLocation(Location(oArea, vMid, CAMJERK_BEARING)));
    ShowMark("jump " + GetName(oArea));
    DelayCommand(6.0, Place(oPC));
}
