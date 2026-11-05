//:: perf-snow -- footfalls and cover re-fits in settled snow: the PC jumps to the module's first outdoor area, noon,
//:: snow already falling, and runs back and forth between two points far enough apart to re-bake the cover map.
#include "inc_perf"

const float SNOW_LEG = 25.0;

void Leg(object oPC, vector vMid, float fSide)
{
    vector vTo = Vector(vMid.x + fSide, vMid.y, vMid.z);
    AssignCommand(oPC, ClearAllActions());
    AssignCommand(oPC, ActionForceMoveToLocation(Location(GetArea(oPC), vTo, 0.0), TRUE));
    DelayCommand(12.0, Leg(oPC, vMid, -fSide));
}

void Settle(object oPC, vector vMid)
{
    SetTime(12, 0, 0, 0);
    AssignCommand(oPC, SetCameraFacing(20.0, 12.0, 65.0, CAMERA_TRANSITION_TYPE_SNAP));
    ShowMark("snow " + IntToString(GetWeather(GetArea(oPC))) + " in " + GetName(GetArea(oPC)));
    Leg(oPC, vMid, SNOW_LEG);
}

void Show(object oPC)
{
    object oArea = GetFirstArea();
    while (GetIsObjectValid(oArea) && GetIsAreaInterior(oArea)) oArea = GetNextArea();
    if (!GetIsObjectValid(oArea)) { ShowMark("outdoor-FAILED: the module has no outdoor area"); return; }
    SetWeather(oArea, WEATHER_SNOW);
    vector vMid = Vector(GetAreaSize(AREA_WIDTH, oArea) * 5.0, GetAreaSize(AREA_HEIGHT, oArea) * 5.0, 0.0);
    AssignCommand(oPC, JumpToLocation(Location(oArea, vMid, 0.0)));
    ShowMark("jump " + GetName(oArea));
    DelayCommand(8.0, Settle(oPC, vMid));
}
