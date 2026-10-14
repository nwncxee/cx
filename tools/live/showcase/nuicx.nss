//::///////////////////////////////////////////////
//:: nuicx -- a doll, a card and a themed window set by "cx" members in the window JSON, with no policy line
//:: The doll's yaw is a bind no element uses: NuiSetBind turns it at +6 s and +18 s. The label's card declares one bad member and the doll one bad value,
//:: which reach nuicx_nui as "refused" events on the element "cx".
//:://////////////////////////////////////////////
#include "inc_show"

const string BIND_YAW = "cxyaw";

json CxBind(string sName)
{
    return JsonObjectSet(JsonObject(), "bind", JsonString(sName));
}

// The doll: a draw list image of @model:cxdoll whose settings ride on the item itself.
json Doll()
{
    json jModel = JsonObject();
    jModel = JsonObjectSet(jModel, "target", JsonString("player"));
    jModel = JsonObjectSet(jModel, "size", JsonString("300x420"));
    jModel = JsonObjectSet(jModel, "yaw", CxBind(BIND_YAW));
    jModel = JsonObjectSet(jModel, "drag", JsonInt(1));
    jModel = JsonObjectSet(jModel, "zoom", JsonString("banana"));
    json jItem = NuiDrawListImage(JsonBool(TRUE), JsonString("@model:cxdoll"), NuiRect(0.0, 0.0, 300.0, 420.0),
        JsonInt(NUI_ASPECT_STRETCH), JsonInt(NUI_HALIGN_CENTER), JsonInt(NUI_VALIGN_MIDDLE));
    jItem = JsonObjectSet(jItem, "cx", JsonObjectSet(JsonObject(), "model", jModel));
    return NuiDrawList(NuiHeight(NuiWidth(NuiSpacer(), 300.0), 420.0), JsonBool(FALSE), JsonArrayInsert(JsonArray(), jItem));
}

// A label with a card and one member no element reads.
json Tip()
{
    json jBlocks = JsonArrayInsert(JsonArray(), JsonObjectSet(JsonObjectSet(JsonObject(), "type", JsonString("text")), "text", JsonString("<c=@accent>Set from the window JSON</c>")));
    json jCard = JsonObjectSet(JsonObject(), "blocks", jBlocks);
    json jCx = JsonObjectSet(JsonObject(), "card", JsonArrayInsert(JsonArray(), jCard));
    jCx = JsonObjectSet(jCx, "bogus", JsonInt(1));
    json jLabel = NuiLabel(JsonString("Hover for a card"), JsonInt(NUI_HALIGN_CENTER), JsonInt(NUI_VALIGN_MIDDLE));
    jLabel = NuiId(NuiHeight(jLabel, 32.0), "cxtip");
    return JsonObjectSet(jLabel, "cx", jCx);
}

void Turn(object oPC, int nToken, int nYaw)
{
    NuiSetBind(oPC, nToken, BIND_YAW, JsonInt(nYaw));
    ShowMark("yaw " + IntToString(nYaw));
}

void Open(object oPC)
{
    json jRoot = NuiCol(JsonArrayInsert(JsonArrayInsert(JsonArray(), Tip()), Doll()));
    json jWin = NuiWindow(jRoot, JsonString("Set from the window JSON"), NuiRect(40.0, 40.0, 340.0, 560.0),
        JsonBool(FALSE), JsonBool(FALSE), JsonBool(FALSE), JsonBool(FALSE), JsonBool(TRUE));
    json jCx = JsonObjectSet(JsonObject(), "fadein", JsonInt(600));
    jCx = JsonObjectSet(jCx, "opacity", JsonFloat(0.85));
    jWin = JsonObjectSet(jWin, "cx", jCx);
    int nToken = ShowRegister(oPC, jWin, "nuicx");
    NuiSetBind(oPC, nToken, BIND_YAW, JsonInt(270));
    DelayCommand(6.0, Turn(oPC, nToken, 90));
    DelayCommand(18.0, Turn(oPC, nToken, 180));
}

void Show(object oPC)
{
    SetEventScript(GetModule(), EVENT_SCRIPT_MODULE_ON_NUI_EVENT, "nuicx_nui");
    DelayCommand(4.0, Open(oPC));
}
