//::///////////////////////////////////////////////
//:: creatures -- six stock creatures as NUI dolls, one window at a time
//:: The PC takes each creature's appearance in turn and a window shows the player's doll, ten seconds apart.
//:: creatures.policy holds the doll's look; creatures.keys takes a shot of each.
//:: OpenDoll is the part a wiki page quotes: the whole window, the image, nothing else.
//:://////////////////////////////////////////////
#include "inc_show"

// One window: a title bar and the doll for model.<sKey>.* in the policy.
void OpenDoll(object oPC, string sKey, string sTitle)
{
    json jDoll = NuiDrawList(NuiHeight(NuiWidth(NuiSpacer(), 340.0), 460.0), JsonBool(FALSE),
        JsonArrayInsert(JsonArray(), NuiDrawListImage(JsonBool(TRUE), JsonString("@model:" + sKey),
            NuiRect(0.0, 0.0, 340.0, 460.0), JsonInt(NUI_ASPECT_STRETCH),
            JsonInt(NUI_HALIGN_CENTER), JsonInt(NUI_VALIGN_MIDDLE))));
    json jWin = NuiWindow(NuiCol(JsonArrayInsert(JsonArray(), jDoll)), JsonString(sTitle),
        NuiRect(20.0, 20.0, 380.0, 520.0), JsonBool(FALSE), JsonBool(FALSE), JsonBool(FALSE), JsonBool(FALSE), JsonBool(TRUE));
    ShowRegister(oPC, jWin, "show_" + sKey);
}

void Doll(object oPC, string sKey, string sTitle, int nAppearance)
{
    ShowMorph(oPC, nAppearance);
    DelayCommand(1.0, OpenDoll(oPC, sKey, sTitle));
}

void Show(object oPC)
{
    DelayCommand(8.0, Doll(oPC, "hound", "Hell hound", APPEARANCE_TYPE_DOG_HELL_HOUND));
    DelayCommand(18.0, Doll(oPC, "ogre", "Ogre", APPEARANCE_TYPE_OGRE));
    DelayCommand(28.0, Doll(oPC, "beholder", "Beholder", APPEARANCE_TYPE_BEHOLDER));
    DelayCommand(38.0, Doll(oPC, "skeleton", "Skeleton warrior", APPEARANCE_TYPE_SKELETON_WARRIOR));
    DelayCommand(48.0, Doll(oPC, "golem", "Stone golem", APPEARANCE_TYPE_GOLEM_STONE));
    DelayCommand(58.0, Doll(oPC, "dragon", "Red dragon", APPEARANCE_TYPE_DRAGON_RED));
}
