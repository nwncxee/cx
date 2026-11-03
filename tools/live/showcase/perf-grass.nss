//:: perf-grass -- the grass class on its own: the PC jumps to the module's first outdoor area, noon, clear weather,
//:: the camera snapped to one facing, and nothing else made. perf-base in an area whose tileset declares grass, so
//:: the grass label is the difference between the two.
#include "inc_perf"

void Settle(object oPC)
{
    object oArea = GetArea(oPC);
    SetTime(12, 0, 0, 0);
    SetWeather(oArea, WEATHER_CLEAR);
    AssignCommand(oPC, SetCameraFacing(20.0, 12.0, 65.0, CAMERA_TRANSITION_TYPE_SNAP));
    ShowMark("grass in " + GetName(oArea));
}

void Show(object oPC)
{
    object oArea = GetFirstArea();
    while (GetIsObjectValid(oArea) && GetIsAreaInterior(oArea)) oArea = GetNextArea();
    if (!GetIsObjectValid(oArea)) { ShowMark("outdoor-FAILED: the module has no outdoor area"); return; }
    vector vMid = Vector(GetAreaSize(AREA_WIDTH, oArea) * 5.0, GetAreaSize(AREA_HEIGHT, oArea) * 5.0, 0.0);
    AssignCommand(oPC, JumpToLocation(Location(oArea, vMid, 0.0)));
    ShowMark("jump " + GetName(oArea));
    DelayCommand(8.0, Settle(oPC));
}
