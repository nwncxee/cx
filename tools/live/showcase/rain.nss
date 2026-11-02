//::///////////////////////////////////////////////
//:: rain -- the fixed rain capture: heavy rain at noon over the start area, then a slow scripted camera pan,
//:: so a before and after under FSR (post.scale=1) or TAA (post.scale=1.5) see the same frames. No backdrop.
//:://////////////////////////////////////////////
#include "inc_perf"

void Pan(object oPC)
{
    AssignCommand(oPC, SetCameraFacing(110.0, 12.0, 65.0, CAMERA_TRANSITION_TYPE_VERY_SLOW));
    ShowMark("pan");
}

void Show(object oPC)
{
    object oArea = GetArea(oPC);
    SetTime(12, 0, 0, 0);
    SetWeather(oArea, WEATHER_RAIN);
    AssignCommand(oPC, SetCameraFacing(20.0, 12.0, 65.0, CAMERA_TRANSITION_TYPE_SNAP));
    ShowMark("rain " + IntToString(GetWeather(oArea)));
    DelayCommand(10.0, Pan(oPC));
}
