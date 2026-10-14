//::///////////////////////////////////////////////
//:: nuiheld_nui -- the server's half of nuiheld: set held.image on a pick, clear it on a place
//:: Slots d* pick on mousedown and place on mouseup (the drag); slots c* pick and place on click.
//:: Slot u1 gets no answer. Every step logs a "CXSHOW:" line.
//:://////////////////////////////////////////////
#include "nw_inc_nui"

const string BIND_HELD = "heldimg";

void Mark(string sStep)
{
    WriteTimestampedLogEntry("CXSHOW: " + sStep);
}

void Put(object oPC, int nToken, string sId, string sImage, string sLabel)
{
    SetLocalString(oPC, "NH_I_" + sId, sImage);
    NuiSetBind(oPC, nToken, "l_" + sId, JsonString(sLabel));
}

void Lift(object oPC, int nToken, string sId)
{
    string sImage = GetLocalString(oPC, "NH_I_" + sId);
    if (sImage == "") return;
    SetLocalString(oPC, "NH_HELD", sImage);
    SetLocalString(oPC, "NH_SRC", sId);
    Put(oPC, nToken, sId, "", "(lifted)");
    NuiSetBind(oPC, nToken, BIND_HELD, JsonString(sImage));
    Mark("held.image set from " + sId);
}

// A free slot takes the image; an occupied one sends it back where it came from.
void Drop(object oPC, int nToken, string sId)
{
    string sImage = GetLocalString(oPC, "NH_HELD");
    if (GetLocalString(oPC, "NH_I_" + sId) != "") sId = GetLocalString(oPC, "NH_SRC");
    Put(oPC, nToken, sId, sImage, "placed");
    DeleteLocalString(oPC, "NH_HELD");
    DeleteLocalString(oPC, "NH_SRC");
    NuiSetBind(oPC, nToken, BIND_HELD, JsonString(""));
    Mark("held.image cleared onto " + sId);
}

void main()
{
    object oPC = NuiGetEventPlayer();
    int nToken = NuiGetEventWindow();
    string sType = NuiGetEventType();
    string sId = NuiGetEventElement();
    string sKind = GetStringLeft(sId, 1);
    Mark("event " + sType + " " + sId);
    string sHeld = GetLocalString(oPC, "NH_HELD");
    string sFrom = GetStringLeft(GetLocalString(oPC, "NH_SRC"), 1);
    if (sType == "mousedown" && sKind == "d" && sHeld == "") Lift(oPC, nToken, sId);
    else if (sType == "mouseup" && sKind == "d" && sFrom == "d") Drop(oPC, nToken, sId);
    else if (sType == "click" && sKind == "c")
    {
        if (sHeld == "") Lift(oPC, nToken, sId);
        else if (sFrom == "c") Drop(oPC, nToken, sId);
    }
}
