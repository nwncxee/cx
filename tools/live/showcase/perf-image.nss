//:: perf-image -- the 286x434 window of perf-doll256 with a plain texture where the doll was: what the window costs by itself.
#include "inc_perf"

void Show(object oPC)
{
    DelayCommand(4.0, PerfOpen(oPC, "perf_image", PerfPlain(256.0, 384.0), 256.0, 384.0, 10.0, 10.0));
}
