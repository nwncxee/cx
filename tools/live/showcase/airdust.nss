//::///////////////////////////////////////////////
//:: airdust -- the crypt floor dust and the night mist: the PC jumps to the module's first crypt, dungeon or cave area
//:: at noon, the camera looks across the floor and then down at it, the PC walks 8 m and stops, then jumps to the
//:: module's first outdoor area at night. No backdrop; the keys time the shots to the marks.
//:://////////////////////////////////////////////
#include "inc_perf"

int Crypt(object oArea)
{
    string sSet = GetTilesetResRef(oArea);
    return GetIsAreaInterior(oArea) && (sSet == "tdc01" || sSet == "tde01" || sSet == "tdm01");
}

void Camera(object oPC, string sStep, float fPitch)
{
    AssignCommand(oPC, SetCameraFacing(GetFacing(oPC), 9.0, fPitch, CAMERA_TRANSITION_TYPE_SNAP));
    ShowMark(sStep);
}

void Walk(object oPC)
{
    vector vPC = GetPosition(oPC);
    float fFace = GetFacing(oPC);
    vector vTo = Vector(vPC.x + cos(fFace) * 8.0, vPC.y + sin(fFace) * 8.0, vPC.z);
    AssignCommand(oPC, ClearAllActions());
    AssignCommand(oPC, ActionForceMoveToLocation(Location(GetArea(oPC), vTo, fFace), FALSE));
    ShowMark("walk");
}

void Night(object oPC)
{
    object oArea = GetFirstArea();
    while (GetIsObjectValid(oArea) && GetIsAreaInterior(oArea)) oArea = GetNextArea();
    if (!GetIsObjectValid(oArea)) { ShowMark("night-FAILED: the module has no outdoor area"); return; }
    SetTime(23, 0, 0, 0);
    vector vMid = Vector(GetAreaSize(AREA_WIDTH, oArea) * 5.0, GetAreaSize(AREA_HEIGHT, oArea) * 5.0, 0.0);
    AssignCommand(oPC, JumpToLocation(Location(oArea, vMid, 0.0)));
    ShowMark("night " + GetName(oArea));
    DelayCommand(10.0, Camera(oPC, "night-across", 78.0));
}

void Show(object oPC)
{
    object oArea = GetFirstArea();
    while (GetIsObjectValid(oArea) && !Crypt(oArea)) oArea = GetNextArea();
    if (!GetIsObjectValid(oArea)) { ShowMark("crypt-FAILED: the module has no crypt, dungeon or cave area"); return; }
    SetTime(12, 0, 0, 0);
    vector vMid = Vector(GetAreaSize(AREA_WIDTH, oArea) * 5.0, GetAreaSize(AREA_HEIGHT, oArea) * 5.0, 0.0);
    AssignCommand(oPC, JumpToLocation(Location(oArea, vMid, 0.0)));
    ShowMark("crypt " + GetName(oArea) + " " + GetTilesetResRef(oArea));
    DelayCommand(10.0, Camera(oPC, "across", 80.0));
    DelayCommand(16.0, Camera(oPC, "down", 25.0));
    DelayCommand(22.0, Camera(oPC, "level", 80.0));
    DelayCommand(23.0, Walk(oPC));
    DelayCommand(36.0, Night(oPC));
}
