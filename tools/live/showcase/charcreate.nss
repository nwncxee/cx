//::///////////////////////////////////////////////
//:: charcreate -- a character creation screen: the paper doll fills the window, buttons over its left side change the creature
//:: The doll is a draw list image behind the widgets. Its name is a bind, so the Head and Body buttons swap one view for the other.
//:: The < and > buttons are handled by charcreate_nui.nss, the module's NUI event script, set at runtime in Show().
//:://////////////////////////////////////////////
#include "inc_show"

const float W = 900.0;
const float H = 600.0;

// One row of the button column: < name >. Element ids are <name>_prev and <name>_next.
json Pair(string sName)
{
    json jRow = JsonArray();
    jRow = JsonArrayInsert(jRow, NuiId(NuiWidth(NuiButton(JsonString("<")), 40.0), sName + "_prev"));
    jRow = JsonArrayInsert(jRow, NuiWidth(NuiLabel(JsonString(sName), JsonInt(NUI_HALIGN_CENTER), JsonInt(NUI_VALIGN_MIDDLE)), 80.0));
    jRow = JsonArrayInsert(jRow, NuiId(NuiWidth(NuiButton(JsonString(">")), 40.0), sName + "_next"));
    return NuiHeight(NuiRow(JsonArrayInsert(jRow, NuiSpacer())), 32.0);
}

json Column()
{
    json jRow = JsonArray();
    jRow = JsonArrayInsert(jRow, NuiId(NuiWidth(NuiButton(JsonString("Head")), 80.0), "focus_head"));
    jRow = JsonArrayInsert(jRow, NuiId(NuiWidth(NuiButton(JsonString("Body")), 80.0), "focus_body"));
    json jCol = JsonArray();
    jCol = JsonArrayInsert(jCol, NuiHeight(NuiRow(JsonArrayInsert(jRow, NuiSpacer())), 32.0));
    jCol = JsonArrayInsert(jCol, Pair("head"));
    jCol = JsonArrayInsert(jCol, Pair("hair"));
    jCol = JsonArrayInsert(jCol, Pair("skin"));
    jCol = JsonArrayInsert(jCol, Pair("race"));
    jCol = JsonArrayInsert(jCol, NuiSpacer());
    jCol = JsonArrayInsert(jCol, NuiHeight(NuiLabel(NuiBind("info"), JsonInt(NUI_HALIGN_LEFT), JsonInt(NUI_VALIGN_TOP)), 60.0));
    return NuiCol(jCol);
}

void Open(object oPC)
{
    // The doll: an image whose name is the bind "view", drawn behind everything else in the root.
    json jDoll = NuiDrawListImage(JsonBool(TRUE), NuiBind("view"), NuiRect(0.0, 0.0, W - 20.0, H - 20.0),
        JsonInt(NUI_ASPECT_STRETCH), JsonInt(NUI_HALIGN_CENTER), JsonInt(NUI_VALIGN_MIDDLE), NUI_DRAW_LIST_ITEM_ORDER_BEFORE);
    json jRoot = NuiDrawList(Column(), JsonBool(FALSE), JsonArrayInsert(JsonArray(), jDoll));
    json jWin = NuiWindow(jRoot, JsonString("Create your character"), NuiRect(190.0, 60.0, W, H),
        JsonBool(FALSE), JsonBool(FALSE), JsonBool(FALSE), JsonBool(FALSE), JsonBool(TRUE));
    int nToken = ShowRegister(oPC, jWin, "charcreate");
    NuiSetBind(oPC, nToken, "view", JsonString("@model:body"));
    NuiSetBind(oPC, nToken, "info", JsonString("Choose your look"));
}

void Show(object oPC)
{
    SetEventScript(GetModule(), EVENT_SCRIPT_MODULE_ON_NUI_EVENT, "charcreate_nui");
    DelayCommand(4.0, Open(oPC));
}
