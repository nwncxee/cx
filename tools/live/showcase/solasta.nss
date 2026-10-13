//::///////////////////////////////////////////////
//:: solasta -- a tabletop-fantasy character sheet: ability rings, the doll on a dark stage, gear slots, a plain-spoken item card
//:://////////////////////////////////////////////
#include "inc_theme"

// One ability: a ring with its score, the name and the modifier beside it.
json Ability(json jBack, float fY, string sName, string sScore, string sMod)
{
    jBack = ThAdd(jBack, ThRing(8.0, fY, 40.0, 40.0, "#1a1d24", TRUE));
    jBack = ThAdd(jBack, ThRing(8.0, fY, 40.0, 40.0, "#a58a52", FALSE, 2.0));
    jBack = ThAdd(jBack, ThText(20.0, fY + 11.0, sScore, "#f2e6c8", 30.0));
    jBack = ThAdd(jBack, ThText(60.0, fY + 3.0, sName, "#d8c9a6", 120.0));
    return ThAdd(jBack, ThText(60.0, fY + 21.0, sMod, "#8fb7a8", 120.0));
}

void SolastaWindow(object oPC)
{
    json jStats = JsonArray();
    jStats = Ability(jStats, 40.0, "Strength", "17", "modifier +3");
    jStats = Ability(jStats, 90.0, "Dexterity", "12", "modifier +1");
    jStats = Ability(jStats, 140.0, "Constitution", "16", "modifier +3");
    jStats = Ability(jStats, 190.0, "Intelligence", "10", "modifier +0");
    jStats = Ability(jStats, 240.0, "Wisdom", "13", "modifier +1");
    jStats = Ability(jStats, 290.0, "Charisma", "8", "modifier -1");
    jStats = ThAdd(jStats, ThText(8.0, 8.0, "Human Fighter, level 5", "#e8d9b0", 200.0));
    jStats = ThAdd(jStats, ThBox(8.0, 346.0, 190.0, 12.0, "#0b0c10"));
    jStats = ThAdd(jStats, ThBox(8.0, 346.0, 150.0, 12.0, "#5e9b5a"));
    jStats = ThAdd(jStats, ThText(8.0, 362.0, "Hit points 41 / 52", "#d8c9a6", 190.0));
    jStats = ThAdd(jStats, ThText(8.0, 380.0, "Armour 18", "#d8c9a6", 190.0));
    json jGear = ThGrid("eq", 2, 5, 52.0, 52.0, "#171a20", "#6a5c3c", 4);
    json jBack = ThAdd(ThAdd(ThAdd(ThAdd(JsonArray(), ThBox(0.0, 0.0, 700.0, 400.0, "#101217")),
        ThBox(0.0, 0.0, 700.0, 400.0, "#a58a52", FALSE, 2.0)), ThBox(5.0, 5.0, 690.0, 390.0, "#3a3a34", FALSE, 1.0)),
        ThBox(224.0, 14.0, 244.0, 372.0, "#0b0c10"));
    json jRoot = ThDecor(NuiRow(ThAdd(ThAdd(ThAdd(JsonArray(), ThDecor(ThGap(210.0, 380.0), jStats)),
        ThDoll("me", "doll", 240.0, 370.0)), NuiCol(ThAdd(ThAdd(JsonArray(), ThGap(100.0, 20.0)), jGear)))), jBack);
    ThOpen(oPC, "sol", jRoot, 90.0, 90.0, 700.0, 400.0);
}

void Show(object oPC)
{
    DelayCommand(6.0, SolastaWindow(oPC));
}
