//::///////////////////////////////////////////////
//:: nuiheld -- a server that holds things, in stock NWScript, with no policy line
//:: The window JSON carries the held image's settings ("cx" on the window), a bind for held.image and a declared pick on
//:: three slots. nuiheld_nui is the module's NUI event script: it sets the bind on pick and clears it on place.
//::   d1 d2   drag: mousedown on d1 picks (@item: image), mouseup over a slot places it
//::   c1 c2   click: a click on c1 picks (@look: image), a click on a free slot places it
//::   u1      a pick is declared (a resref) and the server never answers, so the client drops it by itself
//:://////////////////////////////////////////////
#include "inc_show"

const string BIND_HELD = "heldimg";
// A look with a body part set, the one the cards and the model views use.
const string LOOK = "@look:003900000001000000000000000000000000000000000000000000000000000000000000000000000000010101010101";

json Slot(string sId, string sPick)
{
    json jBtn = NuiId(NuiHeight(NuiWidth(NuiButton(NuiBind("l_" + sId)), 130.0), 56.0), sId);
    if (sPick == "") return jBtn;
    return JsonObjectSet(jBtn, "cx", JsonObjectSet(JsonObject(), "pick", JsonString(sPick)));
}

json Row(json jA, json jB)
{
    return NuiRow(JsonArrayInsert(JsonArrayInsert(JsonArray(), jA), jB));
}

json Caption(string sText)
{
    return NuiHeight(NuiLabel(JsonString(sText), JsonInt(NUI_HALIGN_LEFT), JsonInt(NUI_VALIGN_MIDDLE)), 22.0);
}

void Put(object oPC, int nToken, string sId, string sImage, string sLabel)
{
    SetLocalString(oPC, "NH_I_" + sId, sImage);
    NuiSetBind(oPC, nToken, "l_" + sId, JsonString(sLabel));
}

void Open(object oPC, object oTorch)
{
    string sTorch = "@item:" + ObjectToString(oTorch);
    json jCol = JsonArray();
    jCol = JsonArrayInsert(jCol, Caption("Drag: press a slot, move, release over another"));
    jCol = JsonArrayInsert(jCol, Row(Slot("d1", sTorch), Slot("d2", "")));
    jCol = JsonArrayInsert(jCol, Caption("Click: click a slot, click a free one"));
    jCol = JsonArrayInsert(jCol, Row(Slot("c1", LOOK), Slot("c2", "")));
    jCol = JsonArrayInsert(jCol, Caption("A pick the server never confirms"));
    jCol = JsonArrayInsert(jCol, Row(Slot("u1", "iit_belt"), NuiSpacer()));
    jCol = JsonArrayInsert(jCol, NuiHeight(NuiLabel(NuiBind(BIND_HELD), JsonInt(NUI_HALIGN_LEFT), JsonInt(NUI_VALIGN_MIDDLE)), 22.0));
    json jWin = NuiWindow(NuiCol(jCol), JsonString("Held by the server"), NuiRect(40.0, 40.0, 330.0, 320.0),
        JsonBool(FALSE), JsonBool(FALSE), JsonBool(FALSE), JsonBool(FALSE), JsonBool(TRUE));
    json jHeld = JsonObjectSet(JsonObject(), "image", JsonObjectSet(JsonObject(), "bind", JsonString(BIND_HELD)));
    jHeld = JsonObjectSet(jHeld, "size", JsonInt(48));
    // The stock Use cursor looks like the default arrow, so the hold names the attack cursor.
    jHeld = JsonObjectSet(jHeld, "cursor", JsonInt(7));
    jHeld = JsonObjectSet(jHeld, "confirm", JsonInt(1500));
    jWin = JsonObjectSet(jWin, "cx", JsonObjectSet(JsonObject(), "held", jHeld));
    int nToken = ShowRegister(oPC, jWin, "nuiheld");
    NuiSetBind(oPC, nToken, BIND_HELD, JsonString(""));
    Put(oPC, nToken, "d1", sTorch, "torch");
    Put(oPC, nToken, "d2", "", "(empty)");
    Put(oPC, nToken, "c1", LOOK, "look");
    Put(oPC, nToken, "c2", "", "(empty)");
    Put(oPC, nToken, "u1", "", "never confirmed");
}

void Show(object oPC)
{
    SetEventScript(GetModule(), EVENT_SCRIPT_MODULE_ON_NUI_EVENT, "nuiheld_nui");
    object oTorch = CreateItemOnObject("nw_it_torch001", oPC, 1, "NH_TORCH");
    if (!GetIsObjectValid(oTorch)) { ShowMark("torch-FAILED"); return; }
    // The server sends a backpack item only while the inventory panel is open, and @item: draws only an item the client knows.
    OpenInventory(oPC, oPC);
    DelayCommand(4.0, Open(oPC, oTorch));
}
