//:: perf-vfx -- one doll with two permanent visual effects, 256x384. perf-vfx.policy gives it a see-through background (the two-pass
//:: solve); perf-vfxopaque.policy is the same doll over an opaque one.
#include "inc_perf"

void Show(object oPC)
{
    DelayCommand(4.0, PerfOpen(oPC, "perf_doll", PerfDoll("me", 256.0, 384.0), 256.0, 384.0, 10.0, 10.0));
}
