//::///////////////////////////////////////////////
//:: inc_show -- the shared half of a live-harness showcase (see docs/live-harness.md)
//:: A showcase is one script that includes this and defines Show(), plus a <name>.policy of the server
//:: policy lines it needs. This file owns the plumbing: arming on the PC's first area, spawning a creature
//:: with its id logged, and opening one NUI window at a time. A themed window is a Show() that builds its
//:: own root and hands it to ShowRegister() or ShowOpen(); nothing here decides how a window looks.
//:: Every step writes "CXSHOW: <step>" to the engine's log. "CXSHOW: id <key> <hex>" is the creature
//:: id the harness puts in for @<key>@ in the policy file.
//:://////////////////////////////////////////////
#include "nw_inc_nui"

const float SHOW_ARM_POLL = 0.5;
const int SHOW_ARM_TRIES = 120;
const string SHOW_TOKEN = "CXSHOW_TOKEN";

// Defined by the showcase script. Runs once, with the PC standing in an area.
void Show(object oPC);

void ShowMark(string sStep)
{
    WriteTimestampedLogEntry("CXSHOW: " + sStep);
}

// The stock creature sResref, fDist metres from the PC at fBearing degrees, facing the PC, passive to it.
object ShowSpawn(object oPC, string sKey, string sResref, float fBearing, float fDist)
{
    vector vPC = GetPosition(oPC);
    vector vAt = Vector(vPC.x + cos(fBearing) * fDist, vPC.y + sin(fBearing) * fDist, vPC.z);
    object oCre = CreateObject(OBJECT_TYPE_CREATURE, sResref, Location(GetArea(oPC), vAt, fBearing + 180.0), FALSE, "CXSHOW_" + sKey);
    if (!GetIsObjectValid(oCre)) { ShowMark("spawn-FAILED: " + sKey + " " + sResref); return oCre; }
    ChangeToStandardFaction(oCre, STANDARD_FACTION_MERCHANT);
    SetPlotFlag(oCre, TRUE);
    ShowMark("id " + sKey + " " + ObjectToString(oCre));
    return oCre;
}

// Closes the window ShowRegister opened last, if any.
void ShowClose(object oPC)
{
    int nOld = GetLocalInt(oPC, SHOW_TOKEN);
    if (nOld == 0) return;
    NuiDestroy(oPC, nOld);
    DeleteLocalInt(oPC, SHOW_TOKEN);
    ShowMark("close token " + IntToString(nOld));
}

// Opens jWin as window sId after closing the previous one: the model view draws one window at a time.
int ShowRegister(object oPC, json jWin, string sId)
{
    ShowClose(oPC);
    int nToken = NuiCreate(oPC, jWin, sId);
    SetLocalInt(oPC, SHOW_TOKEN, nToken);
    ShowMark("open " + sId + " token " + IntToString(nToken));
    return nToken;
}

// A plain window: the root, a title, a rectangle, a border, nothing else.
int ShowOpen(object oPC, string sId, string sTitle, json jRoot, float fX, float fY, float fW, float fH)
{
    json jWin = NuiWindow(jRoot, JsonString(sTitle), NuiRect(fX, fY, fW, fH),
                          JsonBool(FALSE), JsonBool(FALSE), JsonBool(FALSE), JsonBool(FALSE), JsonBool(TRUE));
    return ShowRegister(oPC, jWin, sId);
}

// The @model:sKey doll, stretched over a spacer of the view's own shape (its size comes from model.<key>.size).
json ShowModelImage(string sKey, float fW, float fH)
{
    json jItems = JsonArrayInsert(JsonArray(), NuiDrawListImage(JsonBool(TRUE), JsonString("@model:" + sKey),
        NuiRect(0.0, 0.0, fW, fH), JsonInt(NUI_ASPECT_STRETCH), JsonInt(NUI_HALIGN_CENTER), JsonInt(NUI_VALIGN_MIDDLE)));
    return NuiDrawList(NuiHeight(NuiWidth(NuiSpacer(), fW), fH), JsonBool(FALSE), jItems);
}

// One window: a caption over the doll for sKey. Window id show_<sKey>, so ids stay distinct.
void ShowDoll(object oPC, string sKey, string sCaption, float fW, float fH)
{
    json jCap = NuiLabel(JsonString(sCaption), JsonInt(NUI_HALIGN_CENTER), JsonInt(NUI_VALIGN_MIDDLE));
    json jRoot = NuiCol(JsonArrayInsert(JsonArrayInsert(JsonArray(), jCap), ShowModelImage(sKey, fW, fH)));
    ShowOpen(oPC, "show_" + sKey, sCaption, jRoot, 20.0, 20.0, fW + 40.0, fH + 90.0);
}

// The PC takes on creature appearance nAppearance (appearance.2da row). A doll that targets the player follows it
// within a frame, so this shows any creature's model without a second creature for the client to resolve.
void ShowMorph(object oPC, int nAppearance)
{
    SetCreatureAppearanceType(oPC, nAppearance);
    ShowMark("morph " + IntToString(nAppearance));
}

// A full-screen dark window opened before anything else, so the themed windows sit on a plain ground instead of the chat and the world.
void ShowBackdrop(object oPC, int nR = 14, int nG = 14, int nB = 18)
{
    json jFill = NuiDrawListRect(JsonBool(TRUE), NuiColor(nR, nG, nB, 255), JsonBool(TRUE), JsonFloat(1.0), NuiRect(0.0, 0.0, 1500.0, 900.0),
        NUI_DRAW_LIST_ITEM_ORDER_BEFORE);
    json jRoot = NuiDrawList(NuiHeight(NuiWidth(NuiSpacer(), 1500.0), 900.0), JsonBool(FALSE), JsonArrayInsert(JsonArray(), jFill));
    json jWin = NuiWindow(jRoot, JsonBool(FALSE), NuiRect(0.0, 0.0, 1500.0, 900.0), JsonBool(FALSE), JsonBool(FALSE), JsonBool(FALSE),
                          JsonBool(TRUE), JsonBool(FALSE), JsonBool(FALSE));
    NuiCreate(oPC, jWin, "show_backdrop");
    ShowMark("backdrop");
}

void ShowArm(object oPC, int nTry)
{
    if (!GetIsObjectValid(oPC)) return;
    if (!GetIsObjectValid(GetArea(oPC)))
    {
        if (nTry >= SHOW_ARM_TRIES) { ShowMark("arm-FAILED: the PC never landed in an area"); return; }
        DelayCommand(SHOW_ARM_POLL, ShowArm(oPC, nTry + 1));
        return;
    }
    SetImmortal(oPC, TRUE);
    ShowMark("enter " + GetName(oPC) + " in " + GetName(GetArea(oPC)));
    ShowBackdrop(oPC);
    Show(oPC);
}

void main()
{
    object oPC = GetEnteringObject();
    if (GetIsPC(oPC)) ShowArm(oPC, 0);
}
