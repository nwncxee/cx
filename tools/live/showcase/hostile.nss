//::///////////////////////////////////////////////
//:: hostile -- model views fed ids and names the client has no row or file for
//:: Each window names something a careless or hostile server could send: an effect row past visualeffects.2da, a base item the
//:: client lacks, an item whose model files are missing, a cloak and a helmet with numbers no table holds, an animation the model
//:: lacks, an appearance row past appearance.2da and an object id no creature has. None may crash the client or draw a model the
//:: server did not ask for; the window that follows them is a plain player and a valid sword, which must still draw.
//:://////////////////////////////////////////////
#include "inc_show"

void OpenDoll(object oPC, string sKey, float fX, float fY)
{
    json jWin = NuiWindow(NuiCol(JsonArrayInsert(JsonArray(), ShowModelImage(sKey, 200.0, 260.0))), JsonString(sKey),
        NuiRect(fX, fY, 230.0, 310.0), JsonBool(FALSE), JsonBool(FALSE), JsonBool(FALSE), JsonBool(FALSE), JsonBool(TRUE));
    int nToken = NuiCreate(oPC, jWin, "show_" + sKey);
    ShowMark("open show_" + sKey + " token " + IntToString(nToken));
}

void Show(object oPC)
{
    DelayCommand(8.0, OpenDoll(oPC, "vfx", 8.0, 8.0));
    DelayCommand(8.5, OpenDoll(oPC, "base", 248.0, 8.0));
    DelayCommand(9.0, OpenDoll(oPC, "miss", 488.0, 8.0));
    DelayCommand(9.5, OpenDoll(oPC, "wear", 728.0, 8.0));
    DelayCommand(10.0, OpenDoll(oPC, "app", 8.0, 330.0));
    DelayCommand(10.5, OpenDoll(oPC, "id", 248.0, 330.0));
    DelayCommand(11.0, OpenDoll(oPC, "ok", 488.0, 330.0));
    DelayCommand(11.5, OpenDoll(oPC, "sword", 728.0, 330.0));
}
