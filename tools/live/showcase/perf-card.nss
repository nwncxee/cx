//:: perf-card -- a window with one image (id "perf") whose card holds a doll, 256x384. Point at the image to show the card.
#include "inc_perf"

void Show(object oPC)
{
    json jImage = NuiId(NuiTooltip(NuiMargin(NuiHeight(NuiWidth(
        NuiImage(JsonString("ir_attack"), JsonInt(NUI_ASPECT_FIT), JsonInt(NUI_HALIGN_CENTER), JsonInt(NUI_VALIGN_MIDDLE)),
        96.0), 96.0), 3.0), JsonString("a card")), "perf");
    DelayCommand(4.0, PerfOpen(oPC, "perf_card", jImage, 96.0, 96.0, 10.0, 10.0));
}
