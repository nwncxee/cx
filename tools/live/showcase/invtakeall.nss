//::///////////////////////////////////////////////
//:: invtakeall -- Take all from a chest with an empty backpack (inc_take; tools/live/inventory_takeall_regression.sh).
//:://////////////////////////////////////////////
#include "inc_take"

void main()
{
    object oPC = GetEnteringObject();
    if (GetIsPC(oPC)) Arm(oPC, 0, 0);
}
