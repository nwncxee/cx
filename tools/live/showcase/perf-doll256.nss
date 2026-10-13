//:: perf-doll256 -- one doll of the player, 256x384, in the window perf-image uses.
#include "inc_perf"

void Show(object oPC)
{
    DelayCommand(4.0, PerfOpen(oPC, "perf_doll", PerfDoll("me", 256.0, 384.0), 256.0, 384.0, 10.0, 10.0));
}
