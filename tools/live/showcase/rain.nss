//::///////////////////////////////////////////////
//:: rain -- the fixed rain capture: the PC jumps to the module's first outdoor area, heavy rain at noon there, then a
//:: slow scripted camera pan, so a before and after under FSR (post.scale=1) or TAA (post.scale=1.5) see the same frames.
//:: No backdrop.
//:://////////////////////////////////////////////
#include "inc_perf"

void Pan(object oPC)
{
    AssignCommand(oPC, SetCameraFacing(110.0, 12.0, 65.0, CAMERA_TRANSITION_TYPE_VERY_SLOW));
    ShowMark("pan");
}

void Rain(object oPC)
{
    object oArea = GetArea(oPC);
    SetTime(12, 0, 0, 0);
    SetWeather(oArea, WEATHER_RAIN);
    AssignCommand(oPC, SetCameraFacing(20.0, 12.0, 65.0, CAMERA_TRANSITION_TYPE_SNAP));
    ShowMark("rain " + IntToString(GetWeather(oArea)) + " in " + GetName(oArea));
    DelayCommand(10.0, Pan(oPC));
}

void Show(object oPC)
{
    object oArea = GetFirstArea();
    while (GetIsObjectValid(oArea) && GetIsAreaInterior(oArea)) oArea = GetNextArea();
    if (!GetIsObjectValid(oArea)) { ShowMark("outdoor-FAILED: the module has no outdoor area"); return; }
    vector vMid = Vector(GetAreaSize(AREA_WIDTH, oArea) * 5.0, GetAreaSize(AREA_HEIGHT, oArea) * 5.0, 0.0);
    AssignCommand(oPC, JumpToLocation(Location(oArea, vMid, 0.0)));
    ShowMark("jump " + GetName(oArea));
    DelayCommand(8.0, Rain(oPC));
}
