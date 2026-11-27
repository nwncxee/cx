//::///////////////////////////////////////////////
//:: cursorkey -- a quiet spot for the action camera's cursor key (CX-767): the player stands outside the inn with the
//:: swamp's encounters stopped, its creatures gone and its scripted triggers quiet, so the keys can hold the cursor key,
//:: let it go, type in chat and toggle the action camera off with nothing else happening. Logs "CXSHOW: ck ready".
//:://////////////////////////////////////////////
#include "inc_show"

void Show(object oPC)
{
    NuiDestroy(oPC, NuiFindWindow(oPC, "show_backdrop"));
    SetTime(12, 0, 0, 0);
    object oWp = GetWaypointByTag("InnoftheLance");
    if (!GetIsObjectValid(oWp)) { ShowMark("ck-FAILED: no InnoftheLance waypoint"); return; }
    object oArea = GetArea(oWp);
    object o = GetFirstObjectInArea(oArea);
    while (GetIsObjectValid(o))
    {
        int nType = GetObjectType(o);
        if (nType == OBJECT_TYPE_CREATURE && !GetIsPC(o)) { SetPlotFlag(o, FALSE); DestroyObject(o); }
        else if (nType == OBJECT_TYPE_ENCOUNTER) SetEncounterActive(FALSE, o);
        else if (nType == OBJECT_TYPE_TRIGGER) SetEventScript(o, EVENT_SCRIPT_TRIGGER_ON_OBJECT_ENTER, "");
        o = GetNextObjectInArea(oArea);
    }
    AssignCommand(oPC, ClearAllActions(TRUE));
    AssignCommand(oPC, JumpToLocation(GetLocation(oWp)));
    ShowMark("ck ready at " + GetName(oArea));
}
