//::///////////////////////////////////////////////
//:: turn -- one draggable doll, for proving a drag keeps turning once the pointer leaves its window
//:: turn.keys presses on the image, drags out across the screen and shoots before and after the release.
//:://////////////////////////////////////////////
#include "inc_show"

void OpenDoll(object oPC, string sKey, string sTitle, float fX, float fY)
{
    json jDoll = NuiDrawList(NuiHeight(NuiWidth(NuiSpacer(), 280.0), 300.0), JsonBool(FALSE),
        JsonArrayInsert(JsonArray(), NuiDrawListImage(JsonBool(TRUE), JsonString("@model:" + sKey),
            NuiRect(0.0, 0.0, 280.0, 300.0), JsonInt(NUI_ASPECT_STRETCH),
            JsonInt(NUI_HALIGN_CENTER), JsonInt(NUI_VALIGN_MIDDLE))));
    json jWin = NuiWindow(NuiCol(JsonArrayInsert(JsonArray(), jDoll)), JsonString(sTitle),
        NuiRect(fX, fY, 305.0, 345.0), JsonBool(FALSE), JsonBool(FALSE), JsonBool(FALSE), JsonBool(FALSE), JsonBool(TRUE));
    int nToken = NuiCreate(oPC, jWin, "show_" + sKey);
    ShowMark("open show_" + sKey + " token " + IntToString(nToken));
}

void Show(object oPC)
{
    DelayCommand(8.0, OpenDoll(oPC, "ogre", "Ogre, turnable", 8.0, 8.0));
}
