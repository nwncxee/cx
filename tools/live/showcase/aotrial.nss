//::///////////////////////////////////////////////
//:: aotrial -- the AO trial's outdoor scene: noon on the module's first outdoor area, a low close camera, two figures
//:: standing and one walking. The camera pans slowly after 37 s.
//:://////////////////////////////////////////////
#include "inc_perf"

void Walk(object oCre, location lA, location lB)
{
    AssignCommand(oCre, ClearAllActions());
    AssignCommand(oCre, ActionMoveToLocation(lA));
    AssignCommand(oCre, ActionMoveToLocation(lB));
    DelayCommand(9.0, Walk(oCre, lA, lB));
}

void Pan(object oPC)
{
    AssignCommand(oPC, SetCameraFacing(290.0, 6.0, 72.0, CAMERA_TRANSITION_TYPE_VERY_SLOW));
    ShowMark("pan");
}

void Settle(object oPC)
{
    object oArea = GetArea(oPC);
    SetTime(12, 0, 0, 0);
    SetWeather(oArea, WEATHER_CLEAR);
    AssignCommand(oPC, SetCameraFacing(200.0, 6.0, 72.0, CAMERA_TRANSITION_TYPE_SNAP));
    vector v = GetPosition(oPC);
    PerfSpawn(oPC, "a", "nw_elfmerc001", 2.0, 1.0);
    PerfSpawn(oPC, "b", "nw_elfmerc001", 3.0, -1.5);
    object oCre = PerfSpawn(oPC, "walker", "nw_elfmerc001", 5.0, 0.0);
    Walk(oCre, Location(oArea, Vector(v.x - 5.0, v.y + 4.0, v.z), 0.0), Location(oArea, Vector(v.x - 5.0, v.y - 4.0, v.z), 0.0));
    ShowMark("settled in " + GetName(oArea));
    DelayCommand(37.0, Pan(oPC));
}

void Show(object oPC)
{
    object oArea = GetFirstArea();
    while (GetIsObjectValid(oArea) && GetIsAreaInterior(oArea)) oArea = GetNextArea();
    if (!GetIsObjectValid(oArea)) { ShowMark("outdoor-FAILED: the module has no outdoor area"); return; }
    vector vMid = Vector(GetAreaSize(AREA_WIDTH, oArea) * 5.0, GetAreaSize(AREA_HEIGHT, oArea) * 5.0, 0.0);
    AssignCommand(oPC, JumpToLocation(Location(oArea, vMid, 0.0)));
    DelayCommand(8.0, Settle(oPC));
}
