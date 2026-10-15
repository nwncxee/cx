//::///////////////////////////////////////////////
//:: helm -- the player's doll while a helmet goes on and comes off
//:: A plain doll of the player. The PC wears a sword, an off-hand dagger, a helmet and a cloak all at once, changes head and hair,
//:: drops the helmet and puts it on again, so a shot taken between each step shows whether the doll's head and helmet follow the creature.
//:://////////////////////////////////////////////
#include "inc_show"

void OpenDoll(object oPC, string sKey, string sTitle)
{
    json jDoll = NuiDrawList(NuiHeight(NuiWidth(NuiSpacer(), 340.0), 460.0), JsonBool(FALSE),
        JsonArrayInsert(JsonArray(), NuiDrawListImage(JsonBool(TRUE), JsonString("@model:" + sKey),
            NuiRect(0.0, 0.0, 340.0, 460.0), JsonInt(NUI_ASPECT_STRETCH),
            JsonInt(NUI_HALIGN_CENTER), JsonInt(NUI_VALIGN_MIDDLE))));
    json jWin = NuiWindow(NuiCol(JsonArrayInsert(JsonArray(), jDoll)), JsonString(sTitle),
        NuiRect(20.0, 20.0, 380.0, 520.0), JsonBool(FALSE), JsonBool(FALSE), JsonBool(FALSE), JsonBool(FALSE), JsonBool(TRUE));
    ShowRegister(oPC, jWin, "show_" + sKey);
}

void WearAll(object oPC)
{
    object oSword = CreateItemOnObject("nw_wswls001", oPC);
    object oOffhand = CreateItemOnObject("nw_wswdg001", oPC);
    object oHelm = CreateItemOnObject("nw_arhe001", oPC, 1, "SHOW_HELM");
    object oCloak = CreateItemOnObject("x2_it_mcloak001", oPC);
    SetIdentified(oCloak, TRUE);
    AssignCommand(oPC, ActionEquipItem(oSword, INVENTORY_SLOT_RIGHTHAND));
    AssignCommand(oPC, ActionEquipItem(oOffhand, INVENTORY_SLOT_LEFTHAND));
    AssignCommand(oPC, ActionEquipItem(oHelm, INVENTORY_SLOT_HEAD));
    AssignCommand(oPC, ActionEquipItem(oCloak, INVENTORY_SLOT_CLOAK));
    ShowMark("wear all");
}

void Change(object oPC)
{
    SetColor(oPC, COLOR_CHANNEL_HAIR, 120);
    SetCreatureBodyPart(CREATURE_PART_HEAD, 7, oPC);
    ShowMark("changed hair colour and head");
}

void TakeOff(object oPC)
{
    object oHelm = GetItemInSlot(INVENTORY_SLOT_HEAD, oPC);
    ShowMark("helm off " + ObjectToString(oHelm));
    AssignCommand(oPC, ActionUnequipItem(oHelm));
}

void PutOn(object oPC)
{
    object oHelm = GetItemPossessedBy(oPC, "SHOW_HELM");
    ShowMark("helm on " + ObjectToString(oHelm));
    AssignCommand(oPC, ActionEquipItem(oHelm, INVENTORY_SLOT_HEAD));
}

void Show(object oPC)
{
    DelayCommand(2.0, OpenDoll(oPC, "me", "The player"));
    DelayCommand(12.0, WearAll(oPC));
    DelayCommand(22.0, Change(oPC));
    DelayCommand(32.0, TakeOff(oPC));
    DelayCommand(42.0, PutOn(oPC));
    DelayCommand(52.0, TakeOff(oPC));
}
