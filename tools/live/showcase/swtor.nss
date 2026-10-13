//::///////////////////////////////////////////////
//:: swtor -- a sci-fi character panel: cut-corner glass, rarity-framed gear, the doll in a scan ring, a compare card pair
//:://////////////////////////////////////////////
#include "inc_theme"

// One gear slot framed in its rarity colour.
json Gear(string sId, string sIcon, string sRarity)
{
    return ThSlot(sId, sIcon, 54.0, 54.0, "#07121c", sRarity, 2.0);
}

void SwtorWindow(object oPC)
{
    json jL = NuiCol(ThAdd(ThAdd(ThAdd(ThAdd(ThAdd(JsonArray(), Gear("eq0", "iit_neck_002", "#a05cff")), Gear("eq1", "iit_glove_003", "#3a8cff")),
        Gear("eq2", "iit_cloak_005", "#4cc86a")), Gear("eq3", "iit_belt_004", "#e89b2c")), Gear("eq4", "iit_boots_m_021", "#3a8cff")));
    json jR = NuiCol(ThAdd(ThAdd(ThAdd(ThAdd(ThAdd(JsonArray(), Gear("eq5", "iit_ring_003", "#e89b2c")), Gear("eq6", "iit_bracer_002", "#a05cff")),
        Gear("eq7", "ife_wepfoc_lsw", "#a05cff")), Gear("eq8", "ife_wepfoc_dag", "#4cc86a")), Gear("eq9", "iit_ring_005", "#3a8cff")));
    json jBack = ThAdd(ThAdd(ThAdd(ThAdd(ThAdd(ThAdd(ThAdd(ThAdd(ThAdd(JsonArray(),
        ThCut(0.0, 0.0, 700.0, 400.0, "#06101af0", 18.0)), ThCut(0.0, 0.0, 700.0, 400.0, "#37c8e8", 18.0, FALSE, 2.0)),
        ThCut(6.0, 6.0, 688.0, 388.0, "#1a5a70", 15.0, FALSE, 1.0)), ThCut(10.0, 10.0, 680.0, 34.0, "#0d2a3c", 8.0)),
        ThRing(210.0, 60.0, 280.0, 280.0, "#37c8e855", FALSE, 2.0)), ThRing(228.0, 78.0, 244.0, 244.0, "#e89b2c55", FALSE, 1.0)),
        ThLine(210.0, 340.0, 490.0, 340.0, "#37c8e8", 2.0)),
        ThText(24.0, 18.0, "CHARACTER   -   Jedi Knight, level 50", "#37c8e8", 400.0)),
        ThText(250.0, 352.0, "Strength 1,480     Endurance 2,210     Armor 7,320", "#9fe4f4", 420.0));
    json jRoot = ThDecor(NuiRow(ThAdd(ThAdd(ThAdd(JsonArray(), NuiCol(ThAdd(ThAdd(JsonArray(), ThGap(100.0, 52.0)), jL))),
        NuiCol(ThAdd(ThAdd(JsonArray(), ThGap(300.0, 56.0)), ThDoll("me", "doll", 300.0, 300.0)))),
        NuiCol(ThAdd(ThAdd(JsonArray(), ThGap(100.0, 52.0)), jR)))), jBack);
    ThOpen(oPC, "sw", jRoot, 90.0, 90.0, 700.0, 400.0);
}

void Show(object oPC)
{
    DelayCommand(6.0, SwtorWindow(oPC));
}
