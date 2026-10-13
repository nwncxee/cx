//::///////////////////////////////////////////////
//:: charcreate_nui -- the click handler for charcreate: each button changes the creature, and the doll follows by itself
//:://////////////////////////////////////////////
#include "nw_inc_nui"

// Steps nValue by nStep through 0..nCount-1, wrapping.
int Cycle(int nValue, int nStep, int nCount) { return (nValue + nStep + nCount) % nCount; }

void main()
{
    if (NuiGetEventType() != "mouseup") return;
    object oPC = NuiGetEventPlayer();
    int nToken = NuiGetEventWindow();
    string sId = NuiGetEventElement();
    int nStep = GetStringRight(sId, 4) == "next" ? 1 : -1;

    if (sId == "focus_head") NuiSetBind(oPC, nToken, "view", JsonString("@model:head"));
    else if (sId == "focus_body") NuiSetBind(oPC, nToken, "view", JsonString("@model:body"));
    else if (sId == "head_prev" || sId == "head_next")
        SetCreatureBodyPart(CREATURE_PART_HEAD, 1 + Cycle(GetCreatureBodyPart(CREATURE_PART_HEAD, oPC) - 1, nStep, 8), oPC);
    else if (sId == "hair_prev" || sId == "hair_next")
        SetColor(oPC, COLOR_CHANNEL_HAIR, Cycle(GetColor(oPC, COLOR_CHANNEL_HAIR), nStep * 8, 64));
    else if (sId == "skin_prev" || sId == "skin_next")
        SetColor(oPC, COLOR_CHANNEL_SKIN, Cycle(GetColor(oPC, COLOR_CHANNEL_SKIN), nStep * 8, 64));
    else if (sId == "race_prev" || sId == "race_next")
        SetCreatureAppearanceType(oPC, Cycle(GetAppearanceType(oPC), nStep, 7));

    NuiSetBind(oPC, nToken, "info", JsonString("Race " + IntToString(GetAppearanceType(oPC)) +
        "   Head " + IntToString(GetCreatureBodyPart(CREATURE_PART_HEAD, oPC)) +
        "   Hair " + IntToString(GetColor(oPC, COLOR_CHANNEL_HAIR)) +
        "   Skin " + IntToString(GetColor(oPC, COLOR_CHANNEL_SKIN))));
    WriteTimestampedLogEntry("CXSHOW: click " + sId);
}
