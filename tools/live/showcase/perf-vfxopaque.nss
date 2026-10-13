//:: perf-vfxopaque -- perf-vfx over an opaque background: the same effects, the single pass.
#include "inc_perf"

void Show(object oPC)
{
    DelayCommand(4.0, PerfOpen(oPC, "perf_doll", PerfDoll("me", 256.0, 384.0), 256.0, 384.0, 10.0, 10.0));
}
