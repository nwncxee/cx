//::///////////////////////////////////////////////
//:: airsky -- the meadow's wisps against the sky: the PC jumps to the module's first outdoor area, which the policy
//:: holds a meadow, at noon; the camera looks across the ground and then up as far as the client lets it, and at 23:00,
//:: under a saturated blue fog thin enough for the air to show, the same two views again. No backdrop; the keys time
//:: the shots to the marks.
//:://////////////////////////////////////////////
#include "inc_perf"

void Camera(object oPC, string sStep, float fDist, float fPitch)
{
    AssignCommand(oPC, SetCameraFacing(GetFacing(oPC), fDist, fPitch, CAMERA_TRANSITION_TYPE_SNAP));
    ShowMark(sStep);
}

void Night(object oPC)
{
    object oArea = GetArea(oPC);
    SetTime(23, 0, 0, 0);
    SetFogColor(FOG_TYPE_MOON, FOG_COLOR_BLUE, oArea);
    SetFogAmount(FOG_TYPE_MOON, 4, oArea);
    ShowMark("night blue fog");
    DelayCommand(6.0, Camera(oPC, "night-across", 9.0, 80.0));
    DelayCommand(12.0, Camera(oPC, "night-up", 1.0, 170.0));
}

void Show(object oPC)
{
    object oArea = GetFirstArea();
    while (GetIsObjectValid(oArea) && GetIsAreaInterior(oArea)) oArea = GetNextArea();
    if (!GetIsObjectValid(oArea)) { ShowMark("meadow-FAILED: the module has no outdoor area"); return; }
    SetTime(12, 0, 0, 0);
    vector vMid = Vector(GetAreaSize(AREA_WIDTH, oArea) * 5.0, GetAreaSize(AREA_HEIGHT, oArea) * 5.0, 0.0);
    AssignCommand(oPC, JumpToLocation(Location(oArea, vMid, 0.0)));
    ShowMark("meadow " + GetName(oArea) + " " + GetTilesetResRef(oArea));
    DelayCommand(10.0, Camera(oPC, "across", 9.0, 80.0));
    DelayCommand(16.0, Camera(oPC, "up", 1.0, 170.0));
    DelayCommand(22.0, Night(oPC));
}
