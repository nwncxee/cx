//:: perf-creature8 -- eight more creatures in view, no window and no doll: the per-creature slope for perf-doll8.
#include "inc_perf"

void Show(object oPC)
{
    int i;
    for (i = 0; i < 8; i++)
        PerfSpawn(oPC, "c" + IntToString(i), "nw_elfmerc001", 3.0 + IntToFloat(i / 4) * 1.5, (IntToFloat(i % 4) - 1.5) * 1.2);
}
