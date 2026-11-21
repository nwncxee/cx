//::///////////////////////////////////////////////
//:: fonts -- a font the module's own content supplies (fonts.res packs DejaVu Sans Bold Oblique as cxfontbi.ttf), and a
//:: server holding the UI's font rows at it (fonts.policy, applied late, once the module's fonts are listed). The PC
//:: stands in the first area with the chat window open; the shots are before and after the hold. No backdrop.
//:://////////////////////////////////////////////
#include "inc_perf"

void Show(object oPC)
{
    SendMessageToPC(oPC, "The quick brown fox jumps over the lazy dog: 0123456789");
    ShowMark("fonts ready in " + GetName(GetArea(oPC)));
}
