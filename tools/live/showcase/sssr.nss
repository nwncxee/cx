//::///////////////////////////////////////////////
//:: sssr -- the reflection trial's scene: heavy rain at noon on the module's first outdoor area, a low camera and a figure
//:: walking to and fro in front of it. The camera holds still for 12 s, then pans slowly.
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
    AssignCommand(oPC, SetCameraFacing(290.0, 8.0, 74.0, CAMERA_TRANSITION_TYPE_VERY_SLOW));
    ShowMark("pan");
}

void Rain(object oPC)
{
    object oArea = GetArea(oPC);
    SetTime(12, 0, 0, 0);
    SetWeather(oArea, WEATHER_RAIN);
    AssignCommand(oPC, SetCameraFacing(200.0, 8.0, 74.0, CAMERA_TRANSITION_TYPE_SNAP));
    vector v = GetPosition(oPC);
    object oCre = PerfSpawn(oPC, "walker", "nw_elfmerc001", 5.0, 0.0);
    Walk(oCre, Location(oArea, Vector(v.x + 5.0, v.y + 4.0, v.z), 0.0), Location(oArea, Vector(v.x + 5.0, v.y - 4.0, v.z), 0.0));
    ShowMark("rain " + IntToString(GetWeather(oArea)) + " in " + GetName(oArea));
    DelayCommand(37.0, Pan(oPC));
}

void Show(object oPC)
{
    object oArea = GetFirstArea();
    object oA = oArea;
    while (GetIsObjectValid(oA)) { ShowMark("area " + GetName(oA) + " " + GetTag(oA) + " interior " + IntToString(GetIsAreaInterior(oA)) + " tileset " + GetTilesetResRef(oA)); oA = GetNextArea(); }
    oArea = GetFirstArea();
    while (GetIsObjectValid(oArea) && GetIsAreaInterior(oArea)) oArea = GetNextArea();
    if (!GetIsObjectValid(oArea)) { ShowMark("outdoor-FAILED: the module has no outdoor area"); return; }
    vector vMid = Vector(GetAreaSize(AREA_WIDTH, oArea) * 5.0, GetAreaSize(AREA_HEIGHT, oArea) * 5.0, 0.0);
    AssignCommand(oPC, JumpToLocation(Location(oArea, vMid, 0.0)));
    ShowMark("jump " + GetName(oArea));
    DelayCommand(8.0, Rain(oPC));
}
