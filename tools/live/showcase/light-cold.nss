//::///////////////////////////////////////////////
//:: light-cold -- the player's doll under the "cold" light recipe, for the wiki's Light section
//:: The recipe is the policy file beside this one; the window is a bare doll.
//:://////////////////////////////////////////////
#include "inc_show"

void Show(object oPC)
{
    DelayCommand(4.0, ShowDoll(oPC, "recipe", "Cold rim", 300.0, 420.0));
}
