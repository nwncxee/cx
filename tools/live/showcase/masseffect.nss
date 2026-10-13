//::///////////////////////////////////////////////
//:: masseffect -- a holographic codex: a category list, a specimen on a scan ring, an entry in blue and amber, related-entry cards
//:://////////////////////////////////////////////
#include "inc_theme"

void CodexWindow(object oPC)
{
    string sBlue = "#7fd6ff", sAmber = "#f08a1c", sSlot = "#08304c80", sEdge = "#5ec8ff";
    json jCats = ThAdd(JsonArray(), ThGap(180.0, 44.0));
    jCats = ThAdd(jCats, ThLabel("  Planets", sBlue, 180.0, 34.0));
    jCats = ThAdd(jCats, ThLabel("  Species", sAmber, 180.0, 34.0));
    jCats = ThAdd(jCats, ThLabel("  Technology", sBlue, 180.0, 34.0));
    jCats = ThAdd(jCats, ThLabel("  History", sBlue, 180.0, 34.0));
    jCats = ThAdd(jCats, ThLabel("  Alien Races", sBlue, 180.0, 34.0));
    json jRel = ThAdd(JsonArray(), ThGap(240.0, 296.0));
    jRel = ThAdd(jRel, NuiRow(ThAdd(ThAdd(ThAdd(JsonArray(), ThSlot("rel0", "iit_book_003", 52.0, 52.0, sSlot, sEdge)),
        ThSlot("rel1", "iit_book_006", 52.0, 52.0, sSlot, sEdge)), ThSlot("rel2", "iit_scroll_001", 52.0, 52.0, sSlot, sEdge))));
    json jBack = ThAdd(JsonArray(), ThCut(0.0, 0.0, 760.0, 420.0, "#04182acc", 14.0));
    jBack = ThAdd(jBack, ThCut(1.0, 1.0, 758.0, 418.0, sEdge, 14.0, FALSE, 2.0));
    jBack = ThAdd(jBack, ThBox(10.0, 44.0, 190.0, 250.0, "#0a3a5a66"));
    jBack = ThAdd(jBack, ThBox(10.0, 82.0, 190.0, 34.0, "#f08a1c33"));
    jBack = ThAdd(jBack, ThLine(10.0, 82.0, 200.0, 82.0, sAmber, 2.0));
    jBack = ThAdd(jBack, ThRing(222.0, 64.0, 250.0, 250.0, "#5ec8ff88", FALSE, 2.0));
    jBack = ThAdd(jBack, ThRing(240.0, 82.0, 214.0, 214.0, "#f08a1c88", FALSE, 1.0));
    jBack = ThAdd(jBack, ThLine(222.0, 330.0, 472.0, 330.0, sEdge, 1.0));
    jBack = ThAdd(jBack, ThText(24.0, 14.0, "CODEX   >   Species   >   Humanity", sBlue, 440.0));
    jBack = ThAdd(jBack, ThText(224.0, 338.0, "New entry unlocked", sAmber, 200.0));
    jBack = ThAdd(jBack, ThText(500.0, 56.0, "HUMANS", sAmber, 200.0));
    jBack = ThAdd(jBack, ThText(500.0, 90.0, "Youngest of the council races,", "#cfeaff", 250.0));
    jBack = ThAdd(jBack, ThText(500.0, 110.0, "humanity reached the stars on", "#cfeaff", 250.0));
    jBack = ThAdd(jBack, ThText(500.0, 130.0, "relay technology it did not build.", "#cfeaff", 250.0));
    jBack = ThAdd(jBack, ThText(500.0, 160.0, "Homeworld       Earth", sBlue, 250.0));
    jBack = ThAdd(jBack, ThText(500.0, 180.0, "Lifespan           120 years", sBlue, 250.0));
    jBack = ThAdd(jBack, ThText(500.0, 330.0, "Related entries", sAmber, 200.0));
    json jMid = ThAdd(ThAdd(JsonArray(), ThGap(260.0, 56.0)), ThDoll("me", "doll", 250.0, 260.0));
    json jRoot = ThDecor(NuiRow(ThAdd(ThAdd(ThAdd(JsonArray(), NuiCol(jCats)), NuiCol(jMid)), NuiCol(jRel))), jBack);
    ThOpen(oPC, "mex", jRoot, 80.0, 100.0, 760.0, 420.0);
}

void Show(object oPC)
{
    DelayCommand(6.0, CodexWindow(oPC));
}
