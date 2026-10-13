//:: perf-creature -- one more creature in view, no window and no doll: what the bar is read against.
#include "inc_perf"

void Show(object oPC)
{
    PerfSpawn(oPC, "c0", "nw_elfmerc001", 3.0, 0.0);
}
