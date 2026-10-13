//::///////////////////////////////////////////////
//:: chrome -- a stock titled window whose own draw list holds a full-window rect, a frame, a ring and text; nui.<id>.background=0 must leave them alone
//:://////////////////////////////////////////////
#include "inc_theme"

void ChromeWindow(object oPC)
{
    json jBack = ThAdd(JsonArray(), ThBox(0.0, 0.0, 400.0, 260.0, "#802020"));
    jBack = ThAdd(jBack, ThBox(4.0, 4.0, 392.0, 252.0, "#e0c070", FALSE, 2.0));
    jBack = ThAdd(jBack, ThRing(300.0, 150.0, 60.0, 60.0, "#60a0e0", FALSE, 3.0));
    jBack = ThAdd(jBack, ThText(20.0, 20.0, "drawn by the server", "#ffffff", 200.0));
    json jRoot = ThDecor(NuiCol(ThAdd(ThAdd(JsonArray(), ThGap(380.0, 200.0)), NuiLabel(JsonString("a widget"), JsonInt(NUI_HALIGN_LEFT), JsonInt(NUI_VALIGN_MIDDLE)))), jBack);
    ShowOpen(oPC, "chr", "Stock title", jRoot, 200.0, 150.0, 400.0, 300.0);
}

void Show(object oPC)
{
    DelayCommand(6.0, ChromeWindow(oPC));
}
