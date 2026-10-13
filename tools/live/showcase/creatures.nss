//::///////////////////////////////////////////////
//:: creatures -- dolls of things that are not the player, side by side
//:: A doll's target is declared in creatures.policy: an appearance row (app:<row>), a creature the client
//:: knows (its id), a model by name (model:<resref>) or an item (item:<look>). Some play a named animation.
//:: Eight windows are open at once, which is the most model views there are. creatures.keys takes one shot.
//:: OpenDoll is the part a wiki page quotes: the whole window, the image, nothing else.
//:://////////////////////////////////////////////
#include "inc_show"

// One window at (fX, fY): the doll for model.<sKey>.* in the policy, and nothing else.
void OpenDoll(object oPC, string sKey, string sTitle, float fX, float fY)
{
    json jDoll = NuiDrawList(NuiHeight(NuiWidth(NuiSpacer(), 280.0), 300.0), JsonBool(FALSE),
        JsonArrayInsert(JsonArray(), NuiDrawListImage(JsonBool(TRUE), JsonString("@model:" + sKey),
            NuiRect(0.0, 0.0, 280.0, 300.0), JsonInt(NUI_ASPECT_STRETCH),
            JsonInt(NUI_HALIGN_CENTER), JsonInt(NUI_VALIGN_MIDDLE))));
    json jWin = NuiWindow(NuiCol(JsonArrayInsert(JsonArray(), jDoll)), JsonString(sTitle),
        NuiRect(fX, fY, 305.0, 345.0), JsonBool(FALSE), JsonBool(FALSE), JsonBool(FALSE), JsonBool(FALSE), JsonBool(TRUE));
    int nToken = NuiCreate(oPC, jWin, "show_" + sKey);
    ShowMark("open show_" + sKey + " token " + IntToString(nToken));
}

void Show(object oPC)
{
    ShowSpawn(oPC, "merc", "nw_elfmerc001", 0.0, 3.0);
    DelayCommand(8.0, OpenDoll(oPC, "hound", "Hell hound, taunting", 8.0, 8.0));
    DelayCommand(8.5, OpenDoll(oPC, "ogre", "Ogre, ready", 323.0, 8.0));
    DelayCommand(9.0, OpenDoll(oPC, "beholder", "Beholder", 638.0, 8.0));
    DelayCommand(9.5, OpenDoll(oPC, "troll", "Troll, casting", 953.0, 8.0));
    DelayCommand(10.0, OpenDoll(oPC, "dragon", "Red dragon", 8.0, 358.0));
    DelayCommand(10.5, OpenDoll(oPC, "merc", "Elf mercenary", 323.0, 358.0));
    DelayCommand(11.0, OpenDoll(oPC, "wolf", "A model by name", 638.0, 358.0));
    DelayCommand(11.5, OpenDoll(oPC, "sword", "An item", 953.0, 358.0));
}
