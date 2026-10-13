//::///////////////////////////////////////////////
//:: bg3 -- a party bar: four lit portraits (four dolls at once), spell-slot pips, a two-row hotbar, a tall spell card above it
//:://////////////////////////////////////////////
#include "inc_theme"

// Spell-slot pips for one level: nFull filled, the rest hollow.
json Pips(json jBack, float fX, float fY, int nFull, int nAll)
{
    int n;
    for (n = 0; n < nAll; n++)
        jBack = ThAdd(jBack, ThRing(fX + n * 14.0, fY, 10.0, 10.0, "#d9a441", n < nFull, 1.5));
    return jBack;
}

void PartyBar(object oPC)
{
    json jFace = NuiCol(ThAdd(ThAdd(JsonArray(),
        NuiRow(ThAdd(ThAdd(JsonArray(), ThDoll("p1", "pt1", 70.0, 84.0, "#b08a4a")), ThDoll("p2", "pt2", 70.0, 84.0, "#b08a4a")))),
        NuiRow(ThAdd(ThAdd(JsonArray(), ThDoll("p3", "pt3", 70.0, 84.0, "#b08a4a")), ThDoll("p4", "pt4", 70.0, 84.0, "#b08a4a")))));
    json jBar = ThGrid("sp", 12, 2, 44.0, 44.0, "#15161a", "#6d5a3a", 0, TRUE);
    json jBack = ThAdd(JsonArray(), ThCut(0.0, 0.0, 780.0, 230.0, "#0d0e11ee", 10.0));
    jBack = ThAdd(jBack, ThCut(1.0, 1.0, 778.0, 228.0, "#b08a4a", 10.0, FALSE, 2.0));
    jBack = ThAdd(jBack, ThLine(176.0, 28.0, 770.0, 28.0, "#b08a4a66", 1.0));
    jBack = ThAdd(jBack, ThText(184.0, 8.0, "Level 1", "#d9a441", 60.0));
    jBack = ThAdd(jBack, ThText(430.0, 8.0, "Level 2", "#d9a441", 60.0));
    jBack = ThAdd(jBack, ThText(560.0, 8.0, "Level 3", "#d9a441", 60.0));
    jBack = Pips(jBack, 252.0, 12.0, 4, 4);
    jBack = Pips(jBack, 490.0, 12.0, 3, 3);
    jBack = Pips(jBack, 622.0, 12.0, 1, 2);
    jBack = ThAdd(jBack, ThBox(10.0, 196.0, 150.0, 8.0, "#2a0d0d"));
    jBack = ThAdd(jBack, ThBox(10.0, 196.0, 112.0, 8.0, "#b8352c"));
    json jRoot = ThDecor(NuiRow(ThAdd(ThAdd(JsonArray(), NuiCol(ThAdd(ThAdd(JsonArray(), ThGap(160.0, 4.0)), jFace))),
        NuiCol(ThAdd(ThAdd(JsonArray(), ThGap(580.0, 26.0)), jBar)))), jBack);
    ThOpen(oPC, "bg", jRoot, 150.0, 440.0, 780.0, 230.0);
}

void Show(object oPC)
{
    DelayCommand(6.0, PartyBar(oPC));
}
