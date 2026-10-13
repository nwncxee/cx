//::///////////////////////////////////////////////
//:: pathfinder -- a parchment spellbook: a red ribbon, level tabs, a page of spells, a memorised row, a spell card on paper
//:://////////////////////////////////////////////
#include "inc_theme"

void PathfinderWindow(object oPC)
{
    string sInk = "#3a2614";
    json jTabs = JsonArray();
    int n;
    for (n = 1; n <= 5; n++)
    {
        string sC = sInk;
        if (n == 3) sC = "#f0e2bd";
        jTabs = ThAdd(jTabs, ThLabel("Level " + IntToString(n) + "   " + IntToString(6 - n) + " known", sC, 150.0, 40.0));
    }
    json jPage = ThGrid("sp", 6, 3, 60.0, 60.0, "#c6ab7c", "#6b4a2a", 0, TRUE);
    json jMem = JsonArray();
    for (n = 0; n < 7; n++) jMem = ThAdd(jMem, ThSlot("mem" + IntToString(n), ThSpell(n * 3 + 1), 52.0, 52.0, "#c6ab7c", "#7d1f1a", 2.0));
    json jBack = ThAdd(ThAdd(ThAdd(ThAdd(ThAdd(ThAdd(ThAdd(JsonArray(),
        ThBox(0.0, 0.0, 640.0, 430.0, "#d8c49a")), ThBox(0.0, 0.0, 640.0, 430.0, "#6b4a2a", FALSE, 3.0)),
        ThBox(6.0, 6.0, 628.0, 418.0, "#a07a48", FALSE, 1.0)), ThBox(0.0, 0.0, 640.0, 56.0, "#7d1f1a")),
        ThBox(168.0, 70.0, 2.0, 350.0, "#6b4a2a")), ThBox(14.0, 150.0, 146.0, 40.0, "#7d1f1a")),
        ThText(20.0, 18.0, "SPELLBOOK  -  Evoker, level 9", "#f0e2bd", 400.0));
    json jRoot = ThDecor(NuiCol(ThAdd(ThAdd(ThAdd(ThAdd(JsonArray(), ThGap(600.0, 50.0)),
        NuiRow(ThAdd(ThAdd(JsonArray(), NuiCol(jTabs)), jPage))), ThLabel("  Memorised today", sInk, 300.0, 30.0)), NuiRow(jMem))), jBack);
    ThOpen(oPC, "pf", jRoot, 120.0, 100.0, 640.0, 430.0);
}

void Show(object oPC)
{
    DelayCommand(6.0, PathfinderWindow(oPC));
}
