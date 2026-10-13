//::///////////////////////////////////////////////
//:: inc_perf -- the shared half of the perf-* showcases (see docs/live-harness.md)
//:: Same arming as inc_show, but no backdrop: a scenario is exactly the windows and creatures its Show() makes,
//:: so the frame-time probe's numbers subtract cleanly between scenarios. Markers are "CXSHOW:" lines.
//:://////////////////////////////////////////////
#include "nw_inc_nui"

const float PERF_ARM_POLL = 0.5;
const int PERF_ARM_TRIES = 120;

// Defined by the scenario. Runs once, with the PC standing in an area.
void Show(object oPC);

void ShowMark(string sStep)
{
    WriteTimestampedLogEntry("CXSHOW: " + sStep);
}

// The stock creature sResref, fDist metres in front of the PC and fSide metres to its left, facing the PC, passive to it.
object PerfSpawn(object oPC, string sKey, string sResref, float fDist, float fSide)
{
    float fFace = GetFacing(oPC);
    vector vPC = GetPosition(oPC);
    vector vAt = Vector(vPC.x + cos(fFace) * fDist - sin(fFace) * fSide, vPC.y + sin(fFace) * fDist + cos(fFace) * fSide, vPC.z);
    object oCre = CreateObject(OBJECT_TYPE_CREATURE, sResref, Location(GetArea(oPC), vAt, fFace + 180.0), FALSE, "CXSHOW_" + sKey);
    if (!GetIsObjectValid(oCre)) { ShowMark("spawn-FAILED: " + sKey + " " + sResref); return oCre; }
    ChangeToStandardFaction(oCre, STANDARD_FACTION_MERCHANT);
    SetPlotFlag(oCre, TRUE);
    ShowMark("id " + sKey + " " + ObjectToString(oCre));
    return oCre;
}

// A titled window around jRoot at (fX, fY), fW by fH, with the id sId.
int PerfWindow(object oPC, string sId, json jRoot, float fX, float fY, float fW, float fH)
{
    json jWin = NuiWindow(jRoot, JsonString(sId), NuiRect(fX, fY, fW, fH),
                          JsonBool(FALSE), JsonBool(FALSE), JsonBool(FALSE), JsonBool(FALSE), JsonBool(TRUE));
    int nToken = NuiCreate(oPC, jWin, sId);
    ShowMark("open " + sId + " token " + IntToString(nToken));
    return nToken;
}

// The @model:sKey doll, stretched over a spacer fW by fH (its size comes from model.<sKey>.size).
json PerfDoll(string sKey, float fW, float fH)
{
    json jItems = JsonArrayInsert(JsonArray(), NuiDrawListImage(JsonBool(TRUE), JsonString("@model:" + sKey),
        NuiRect(0.0, 0.0, fW, fH), JsonInt(NUI_ASPECT_STRETCH), JsonInt(NUI_HALIGN_CENTER), JsonInt(NUI_VALIGN_MIDDLE)));
    return NuiDrawList(NuiHeight(NuiWidth(NuiSpacer(), fW), fH), JsonBool(FALSE), jItems);
}

// A stock texture stretched over fW by fH the same way: the window with no doll in it.
json PerfPlain(float fW, float fH)
{
    json jItems = JsonArrayInsert(JsonArray(), NuiDrawListImage(JsonBool(TRUE), JsonString("ir_attack"),
        NuiRect(0.0, 0.0, fW, fH), JsonInt(NUI_ASPECT_STRETCH), JsonInt(NUI_HALIGN_CENTER), JsonInt(NUI_VALIGN_MIDDLE)));
    return NuiDrawList(NuiHeight(NuiWidth(NuiSpacer(), fW), fH), JsonBool(FALSE), jItems);
}

// One window at (fX, fY) holding jBody, sized to a fW by fH body.
void PerfOpen(object oPC, string sId, json jBody, float fW, float fH, float fX, float fY)
{
    PerfWindow(oPC, sId, NuiCol(JsonArrayInsert(JsonArray(), jBody)), fX, fY, fW + 30.0, fH + 50.0);
}

void PerfArm(object oPC, int nTry)
{
    if (!GetIsObjectValid(oPC)) return;
    if (!GetIsObjectValid(GetArea(oPC)))
    {
        if (nTry >= PERF_ARM_TRIES) { ShowMark("arm-FAILED: the PC never landed in an area"); return; }
        DelayCommand(PERF_ARM_POLL, PerfArm(oPC, nTry + 1));
        return;
    }
    SetImmortal(oPC, TRUE);
    ShowMark("enter " + GetName(oPC) + " in " + GetName(GetArea(oPC)));
    Show(oPC);
}

void main()
{
    object oPC = GetEnteringObject();
    if (GetIsPC(oPC)) PerfArm(oPC, 0);
}
