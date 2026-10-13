//::///////////////////////////////////////////////
//:: doll -- the player at three sizes, for drag, drag range, zoom and fit zone
//:: big turns inside -60..60 and zooms, small is framed on the head and shoulders, still has no drag and no zoom.
//:://////////////////////////////////////////////
#include "inc_show"

void OpenDoll(object oPC, string sKey, string sTitle, float fW, float fH, float fX, float fY)
{
    json jWin = NuiWindow(NuiCol(JsonArrayInsert(JsonArray(), ShowModelImage(sKey, fW, fH))), JsonString(sTitle),
        NuiRect(fX, fY, fW + 30.0, fH + 50.0), JsonBool(FALSE), JsonBool(FALSE), JsonBool(FALSE), JsonBool(FALSE), JsonBool(TRUE));
    int nToken = NuiCreate(oPC, jWin, "show_" + sKey);
    ShowMark("open show_" + sKey + " token " + IntToString(nToken));
}

void Show(object oPC)
{
    DelayCommand(4.0, OpenDoll(oPC, "big", "Big, drag and zoom", 300.0, 420.0, 10.0, 10.0));
    DelayCommand(4.0, OpenDoll(oPC, "still", "Still", 200.0, 280.0, 360.0, 10.0));
    DelayCommand(4.0, OpenDoll(oPC, "small", "Small, head", 120.0, 160.0, 620.0, 10.0));
}
