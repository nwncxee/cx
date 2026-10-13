//::///////////////////////////////////////////////
//:: diablo -- a gothic inventory: equipment around a doll, a pack grid, a unique-item card on a gold-framed black
//:://////////////////////////////////////////////
#include "inc_theme"

// The window: a doll between two slot columns, a pack grid beside it, painted on one draw list.
void DiabloWindow(object oPC)
{
    string sLine = "#4a3b2c", sFill = "#0e0a08";
    json jL = NuiCol(ThAdd(ThAdd(ThAdd(JsonArray(), ThSlot("eqh", "iit_neck_003", 44.0, 44.0, sFill, sLine)),
        ThSlot("eqa", "iit_glove_002", 44.0, 44.0, sFill, sLine)), ThSlot("eqw", "ife_wepfoc_gsw", 44.0, 88.0, sFill, sLine)));
    json jR = NuiCol(ThAdd(ThAdd(ThAdd(JsonArray(), ThSlot("eqr", "iit_ring_003", 44.0, 44.0, sFill, sLine)),
        ThSlot("eqb", "iit_belt_004", 44.0, 44.0, sFill, sLine)), ThSlot("eqs", "ife_shieprof", 44.0, 88.0, sFill, sLine)));
    json jDoll = NuiRow(ThAdd(ThAdd(ThAdd(JsonArray(), jL), ThDoll("me", "doll", 170.0, 280.0)), jR));
    json jBack = ThAdd(JsonArray(), ThBox(0.0, 0.0, 740.0, 430.0, "#0a0706"));
    jBack = ThAdd(jBack, ThBox(1.0, 1.0, 738.0, 428.0, "#8a7448", FALSE, 2.0));
    jBack = ThAdd(jBack, ThBox(6.0, 6.0, 728.0, 418.0, "#4a3b2c", FALSE, 1.0));
    jBack = ThAdd(jBack, ThBox(8.0, 10.0, 272.0, 410.0, "#140d0a"));
    jBack = ThAdd(jBack, ThLine(8.0, 34.0, 280.0, 34.0, "#7a1a14", 2.0));
    jBack = ThAdd(jBack, ThLine(290.0, 34.0, 730.0, 34.0, "#7a1a14", 2.0));
    jBack = ThAdd(jBack, ThText(24.0, 14.0, "EQUIPMENT", "#c9a35a"));
    jBack = ThAdd(jBack, ThText(300.0, 12.0, "BACKPACK", "#c9a35a", 120.0));
    jBack = ThAdd(jBack, ThText(560.0, 12.0, "gold 24,810", "#e0c070", 120.0));
    jBack = ThAdd(jBack, ThText(20.0, 340.0, "Barbarian, level 61", "#c9a35a", 250.0));
    jBack = ThAdd(jBack, ThText(20.0, 362.0, "Life 4,812        Fury 120", "#d8cdb0", 250.0));
    jBack = ThAdd(jBack, ThText(20.0, 384.0, "Armor 12,448    Damage 6,120", "#d8cdb0", 250.0));
    json jBag = NuiCol(ThAdd(ThAdd(JsonArray(), ThGap(400.0, 24.0)), ThGrid("bag", 10, 8, 38.0, 38.0, sFill, sLine)));
    json jRoot = ThDecor(NuiRow(ThAdd(ThAdd(JsonArray(), NuiCol(ThAdd(ThAdd(JsonArray(), ThGap(280.0, 24.0)), jDoll))), jBag)), jBack);
    ThOpen(oPC, "dia", jRoot, 60.0, 80.0, 740.0, 430.0);
}

void Show(object oPC)
{
    DelayCommand(6.0, DiabloWindow(oPC));
}
