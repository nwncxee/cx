//::///////////////////////////////////////////////
//:: cxnui -- a fixed NUI window for the live harness, and a second small one beside it
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
const float WINDOW_LIFE = 60.0;

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

// The model view's gear: a sword, a shield, a helmet and a cloak go on, then the shield and the sword come off,
// so a shot before and after each shows the doll following the equip and the unequip.
void Wear(object oPC)
{
    object oSword = CreateItemOnObject("nw_wswls001", oPC, 1, "CXNUI_SWORD");
    object oShield = CreateItemOnObject("nw_ashlw001", oPC, 1, "CXNUI_SHIELD");
    object oHelm = CreateItemOnObject("nw_arhe001", oPC, 1, "CXNUI_HELM");
    object oCloak = CreateItemOnObject("nw_cloth001", oPC, 1, "CXNUI_CLOAK");
    Mark("gear " + ObjectToString(oSword) + " " + ObjectToString(oShield) + " " + ObjectToString(oHelm) + " " + ObjectToString(oCloak));
    AssignCommand(oPC, ActionEquipItem(oSword, INVENTORY_SLOT_RIGHTHAND));
    AssignCommand(oPC, ActionEquipItem(oShield, INVENTORY_SLOT_LEFTHAND));
    AssignCommand(oPC, ActionEquipItem(oHelm, INVENTORY_SLOT_HEAD));
    AssignCommand(oPC, ActionEquipItem(oCloak, INVENTORY_SLOT_CLOAK));
    Mark("wear");
}

void Strip(object oPC, string sTag)
{
    object oItem = GetItemPossessedBy(oPC, sTag);
    AssignCommand(oPC, ActionUnequipItem(oItem));
    Mark("strip " + sTag);
}

// Visual effects on the player, two permanent and one temporary, so a shot shows which ones the doll carries.
void Glow(object oPC)
{
    ApplyEffectToObject(DURATION_TYPE_PERMANENT, EffectVisualEffect(VFX_DUR_ELEMENTAL_SHIELD), oPC);
    ApplyEffectToObject(DURATION_TYPE_PERMANENT, EffectVisualEffect(VFX_DUR_GLOW_LIGHT_BLUE), oPC);
    ApplyEffectToObject(DURATION_TYPE_TEMPORARY, EffectVisualEffect(VFX_DUR_PROT_PREMONITION), oPC, WINDOW_LIFE);
    Mark("vfx " + IntToString(VFX_DUR_ELEMENTAL_SHIELD) + " " + IntToString(VFX_DUR_GLOW_LIGHT_BLUE) + " permanent, "
         + IntToString(VFX_DUR_PROT_PREMONITION) + " temporary");
}

void CloseWindow(object oPC, int nToken, string sId)
{
    NuiDestroy(oPC, nToken);
    Mark("close " + sId);
}

// A second, small window that outlives the first: the held image follows the window the pointer was over, so closing the first
// one must end the hold while this one stays open.
void OpenSecond(object oPC)
{
    json jLabel = NuiLabel(JsonString("second window"), JsonInt(NUI_HALIGN_CENTER), JsonInt(NUI_VALIGN_MIDDLE));
    json jItems = JsonArrayInsert(JsonArray(), NuiDrawListImage(JsonBool(TRUE), JsonString("@model:you"),
        NuiRect(0.0, 0.0, 120.0, 160.0), JsonInt(NUI_ASPECT_STRETCH), JsonInt(NUI_HALIGN_CENTER), JsonInt(NUI_VALIGN_MIDDLE)));
    json jModel = NuiMargin(NuiDrawList(NuiHeight(NuiWidth(NuiSpacer(), 120.0), 160.0), JsonBool(FALSE), jItems), 3.0);
    json jWin = NuiWindow(NuiCol(JsonArrayInsert(JsonArrayInsert(JsonArray(), jLabel), jModel)), JsonString("CX second"), NuiRect(400.0, 20.0, 220.0, 240.0),
                          JsonBool(FALSE), JsonBool(FALSE), JsonBool(FALSE), JsonBool(FALSE), JsonBool(TRUE));
    int nToken = NuiCreate(oPC, jWin, WINDOW_ID + "2");
    Mark("open second token " + IntToString(nToken));
    DelayCommand(WINDOW_LIFE * 2.0, CloseWindow(oPC, nToken, WINDOW_ID + "2"));
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

    // The doll the way a consumer draws it: a draw-list image stretched over a spacer, not a NuiImage.
    json jItems = JsonArrayInsert(JsonArray(), NuiDrawListImage(JsonBool(TRUE), JsonString("@model:me"),
        NuiRect(0.0, 0.0, 200.0, 300.0), JsonInt(NUI_ASPECT_STRETCH), JsonInt(NUI_HALIGN_CENTER), JsonInt(NUI_VALIGN_MIDDLE)));
    json jModel = NuiId(NuiMargin(NuiDrawList(NuiHeight(NuiWidth(NuiSpacer(), 200.0), 300.0), JsonBool(FALSE), jItems), 3.0), "mdl");

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
    DelayCommand(1.0, Wear(oPC));
    DelayCommand(10.0, Strip(oPC, "CXNUI_SHIELD"));
    DelayCommand(13.0, Strip(oPC, "CXNUI_SWORD"));
    DelayCommand(16.0, Change(oPC));
    DelayCommand(20.0, Glow(oPC));
    OpenSecond(oPC);
    DelayCommand(WINDOW_LIFE, CloseWindow(oPC, nToken, WINDOW_ID));
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
