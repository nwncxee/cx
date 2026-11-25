//::///////////////////////////////////////////////
//:: invtakefull -- Take all from a chest with a backpack filled with rings (inc_take; tools/live/inventory_takeall_regression.sh).
//:://////////////////////////////////////////////
#include "inc_take"

void main()
{
    object oPC = GetEnteringObject();
    if (GetIsPC(oPC)) Arm(oPC, 1, 0);
}
