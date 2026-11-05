//::///////////////////////////////////////////////
//:: sssr-metal -- the reflection trial's indoor scene: the module's first room, run with an override folder of .mtr files
//:: that make its tiles polished metal. A low camera, and a figure walking to and fro. The camera pans after 37 s.
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
    AssignCommand(oPC, SetCameraFacing(290.0, 6.0, 65.0, CAMERA_TRANSITION_TYPE_VERY_SLOW));
    ShowMark("pan");
}

void Show(object oPC)
{
    object oArea = GetArea(oPC);
    AssignCommand(oPC, SetCameraFacing(200.0, 6.0, 65.0, CAMERA_TRANSITION_TYPE_SNAP));
    vector v = GetPosition(oPC);
    object oCre = PerfSpawn(oPC, "walker", "nw_elfmerc001", 4.0, 0.0);
    Walk(oCre, Location(oArea, Vector(v.x - 4.0, v.y + 3.0, v.z), 0.0), Location(oArea, Vector(v.x - 4.0, v.y - 3.0, v.z), 0.0));
    DelayCommand(37.0, Pan(oPC));
}
