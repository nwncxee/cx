//::///////////////////////////////////////////////
//:: dragonage -- a dialogue screen: the speaker in firelight beside a bronze-framed line and four replies, each with a card
//:://////////////////////////////////////////////
#include "inc_theme"

// One reply: an icon and the line, tinted by what it costs or earns.
json Reply(int nIdx, string sIcon, string sText, string sInk)
{
    string sId = "opt" + IntToString(nIdx);
    return NuiRow(ThAdd(ThAdd(JsonArray(), ThSlot(sId, sIcon, 36.0, 36.0, "#0d0a08", "#8a6a3a")), ThSize(ThLabel("  " + sText, sInk, 600.0, 36.0), sId, 600.0, 36.0)));
}

void DialogueWindow(object oPC)
{
    json jLines = ThAdd(JsonArray(), ThLabel("The Hedge-Witch", "#d9b45a", 640.0, 30.0));
    jLines = ThAdd(jLines, ThLabel("You walk the old road after dark, and carry a blade you do not know how to hold.", "#e6d9b8", 640.0, 24.0));
    jLines = ThAdd(jLines, ThLabel("Tell me who sent you, or tell me nothing and I will find out the slow way.", "#e6d9b8", 640.0, 24.0));
    jLines = ThAdd(jLines, ThGap(640.0, 10.0));
    jLines = ThAdd(jLines, Reply(0, "ir_dialog", "[Persuade]  Nobody sent me. I came for the road, and the road brought me here.", "#d9b45a"));
    jLines = ThAdd(jLines, Reply(1, "ir_threaten", "[Intimidate]  Put the knife down, old woman.", "#d9b45a"));
    jLines = ThAdd(jLines, Reply(2, "ir_goodbye", "Say nothing and leave.", "#b9ae98"));
    jLines = ThAdd(jLines, Reply(3, "ir_laugh", "[Charm]  Nice knife. Who taught you?", "#d9b45a"));
    json jBack = ThAdd(ThAdd(ThAdd(ThAdd(ThAdd(ThAdd(ThAdd(JsonArray(),
        ThBox(0.0, 0.0, 940.0, 330.0, "#0b0907f0")), ThBox(0.0, 0.0, 940.0, 330.0, "#8a6a3a", FALSE, 3.0)),
        ThBox(7.0, 7.0, 926.0, 316.0, "#3c2c18", FALSE, 1.0)), ThBox(12.0, 12.0, 232.0, 306.0, "#030303")),
        ThBox(12.0, 12.0, 232.0, 306.0, "#8a6a3a", FALSE, 1.0)), ThLine(262.0, 38.0, 922.0, 38.0, "#8a6a3a", 1.0)),
        ThLine(262.0, 108.0, 922.0, 108.0, "#8a6a3a", 1.0));
    json jRoot = ThDecor(NuiRow(ThAdd(ThAdd(JsonArray(), NuiCol(ThAdd(ThAdd(JsonArray(), ThGap(232.0, 4.0)), ThDoll("me", "doll", 232.0, 300.0)))),
        NuiCol(jLines))), jBack);
    ThOpen(oPC, "da", jRoot, 150.0, 330.0, 940.0, 330.0);
}

void Show(object oPC)
{
    DelayCommand(6.0, DialogueWindow(oPC));
}
