//::///////////////////////////////////////////////
//:: cxnui -- a fixed NUI window for the live harness
//:: Module OnClientEnter. Once the PC stands in an area it is given a torch and,
//:: three seconds later, opens one NUI window at a known place whose elements carry JSON ids: a label,
//:: an @item: image with a tooltip, and a list whose row template has one id'd
//:: cell. Margin, padding and size modifiers are on the id'd elements on purpose.
//:: Every step writes "CXNUI: <step>" to the engine's log.
//:://////////////////////////////////////////////
#include "nw_inc_nui"

const float ARM_POLL = 0.5;
const int ARM_TRIES = 120;
const string TORCH = "nw_it_torch001";
const string TORCH_TAG = "CXNUI_TORCH";
const string WINDOW_ID = "cxnui";
const int LIST_ROWS = 30;

void Mark(string sStep)
{
    WriteTimestampedLogEntry("CXNUI: " + sStep);
}

void Change(object oPC)
{
    SetColor(oPC, COLOR_CHANNEL_HAIR, 120);
    SetCreatureBodyPart(CREATURE_PART_HEAD, 7, oPC);
    Mark("changed hair colour and head");
}

void OpenWindow(object oPC)
{
    object oTorch = GetItemPossessedBy(oPC, TORCH_TAG);
    if (!GetIsObjectValid(oTorch)) { Mark("open-FAILED: no torch"); return; }

    json jLabel = NuiId(NuiPadding(NuiMargin(NuiHeight(NuiWidth(
        NuiLabel(JsonString("label"), JsonInt(NUI_HALIGN_CENTER), JsonInt(NUI_VALIGN_MIDDLE)),
        300.0), 30.0), 6.0), 4.0), "lbl");

    json jImage = NuiId(NuiTooltip(NuiMargin(NuiHeight(NuiWidth(
        NuiImage(JsonString("@item:" + ObjectToString(oTorch)), JsonInt(NUI_ASPECT_FIT), JsonInt(NUI_HALIGN_CENTER), JsonInt(NUI_VALIGN_MIDDLE)),
        64.0), 64.0), 3.0), JsonString("a torch")), "img");

    json jModel = NuiId(NuiMargin(NuiHeight(NuiWidth(
        NuiImage(JsonString("@model:me"), JsonInt(NUI_ASPECT_FIT), JsonInt(NUI_HALIGN_CENTER), JsonInt(NUI_VALIGN_MIDDLE)),
        200.0), 300.0), 3.0), "mdl");

    json jCell = NuiId(NuiPadding(NuiMargin(
        NuiLabel(NuiBind("rows"), JsonInt(NUI_HALIGN_LEFT), JsonInt(NUI_VALIGN_MIDDLE)), 2.0), 2.0), "cell");
    json jCells = JsonArray();
    jCells = JsonArrayInsert(jCells, NuiListTemplateCell(jCell, 0.0, TRUE));
    json jList = NuiHeight(NuiList(jCells, JsonInt(LIST_ROWS), 24.0), 120.0);

    json jRoot = NuiCol(JsonArrayInsert(JsonArrayInsert(JsonArrayInsert(JsonArrayInsert(JsonArray(), jLabel), jImage), jModel), jList));
    json jWin = NuiWindow(jRoot, JsonString("CX hit test"), NuiRect(20.0, 20.0, 340.0, 560.0),
                          JsonBool(FALSE), JsonBool(FALSE), JsonBool(FALSE), JsonBool(FALSE), JsonBool(TRUE));
    int nToken = NuiCreate(oPC, jWin, WINDOW_ID);
    json jRows = JsonArray();
    int i;
    for (i = 0; i < LIST_ROWS; i++) jRows = JsonArrayInsert(jRows, JsonString("row " + IntToString(i)));
    NuiSetBind(oPC, nToken, "rows", jRows);
    Mark("open token " + IntToString(nToken) + " torch " + ObjectToString(oTorch));
    DelayCommand(14.0, Change(oPC));
    DelayCommand(24.0, NuiDestroy(oPC, nToken));
}

void Arm(object oPC, int nTry)
{
    if (!GetIsObjectValid(oPC)) return;
    if (!GetIsObjectValid(GetArea(oPC)))
    {
        if (nTry >= ARM_TRIES) { Mark("open-FAILED: the PC never landed in an area"); return; }
        DelayCommand(ARM_POLL, Arm(oPC, nTry + 1));
        return;
    }
    SetImmortal(oPC, TRUE);
    Mark("enter " + GetName(oPC) + " in " + GetName(GetArea(oPC)));
    // The torch is made first and the window opened later: the client must know the item before @item: can draw it.
    if (!GetIsObjectValid(GetItemPossessedBy(oPC, TORCH_TAG))) CreateItemOnObject(TORCH, oPC, 1, TORCH_TAG);
    DelayCommand(3.0, OpenWindow(oPC));
}

void main()
{
    object oPC = GetEnteringObject();
    if (GetIsPC(oPC)) Arm(oPC, 0);
}
