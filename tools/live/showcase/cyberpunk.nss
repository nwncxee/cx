//::///////////////////////////////////////////////
//:: cyberpunk -- a loot list under a yellow banner: framed rows, one selected, a cyan scan doll, a hard-edged card
//:://////////////////////////////////////////////
#include "inc_theme"

// One loot row: icon, name, price. The selected row is a yellow bar with black type.
json LootRow(int nRow, string sIcon, string sName, string sTier, string sPrice, int bSelected)
{
    string sId = "row" + IntToString(nRow), sInk = "#fcee09", sBar = "#14141a", sEdge = "#2c2c36";
    if (bSelected) { sInk = "#0a0a0d"; sBar = "#fcee09"; sEdge = "#fcee09"; }
    json jRow = NuiRow(ThAdd(ThAdd(ThAdd(ThAdd(JsonArray(), ThSlot(sId, sIcon, 48.0, 48.0, "#0a0a0d", "#00f0ff")),
        ThSize(ThLabel("  " + sName, sInk, 330.0, 48.0), sId, 330.0, 48.0)),
        ThLabel(sTier, "#00f0ff", 90.0, 48.0)), ThLabel(sPrice, sInk, 100.0, 48.0, NUI_HALIGN_RIGHT)));
    return ThDecor(jRow, ThAdd(ThAdd(JsonArray(), ThBox(0.0, 0.0, 600.0, 48.0, sBar)), ThBox(0.0, 0.0, 600.0, 48.0, sEdge, FALSE, 1.0)));
}

void CyberpunkWindow(object oPC)
{
    json jList = ThAdd(JsonArray(), ThGap(600.0, 52.0));
    jList = ThAdd(jList, LootRow(0, "iit_ring_003", "Mantis Blades", "ICONIC", "12,400", FALSE));
    jList = ThAdd(jList, LootRow(1, "ife_wepfoc_lsw", "Satori Katana", "LEGENDARY", "58,000", TRUE));
    jList = ThAdd(jList, LootRow(2, "iit_glove_003", "Kiroshi Optics Mk.3", "EPIC", "9,150", FALSE));
    jList = ThAdd(jList, LootRow(3, "iit_potion_b_025", "MaxDoc Mk.2", "RARE", "320", FALSE));
    jList = ThAdd(jList, LootRow(4, "iit_gem_009", "Quickhack: Overheat", "RARE", "1,780", FALSE));
    jList = ThAdd(jList, LootRow(5, "iit_key_001", "Access Chip", "COMMON", "60", FALSE));
    json jBack = ThAdd(ThAdd(ThAdd(ThAdd(ThAdd(ThAdd(ThAdd(JsonArray(),
        ThBox(0.0, 0.0, 880.0, 420.0, "#07070a")), ThCut(0.0, 0.0, 880.0, 38.0, "#fcee09", 8.0)),
        ThBox(0.0, 0.0, 880.0, 420.0, "#fcee09", FALSE, 2.0)), ThBox(10.0, 54.0, 240.0, 356.0, "#00f0ff", FALSE, 1.0)),
        ThLine(10.0, 380.0, 250.0, 380.0, "#ff003c", 2.0)),
        ThText(16.0, 10.0, "LOOT   //   Afterlife stash   //   weight 41 / 90", "#07070a", 560.0)),
        ThText(18.0, 388.0, "SCAN: cyberware sync 94%", "#00f0ff", 220.0));
    json jRoot = ThDecor(NuiRow(ThAdd(ThAdd(JsonArray(), NuiCol(ThAdd(ThAdd(JsonArray(), ThGap(240.0, 54.0)), ThDoll("me", "doll", 240.0, 320.0)))),
        NuiCol(jList))), jBack);
    ThOpen(oPC, "cp", jRoot, 70.0, 110.0, 880.0, 420.0);
}

void Show(object oPC)
{
    DelayCommand(6.0, CyberpunkWindow(oPC));
}
