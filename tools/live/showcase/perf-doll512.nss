//:: perf-doll512 -- one doll of the player, 512x768.
#include "inc_perf"

void Show(object oPC)
{
    DelayCommand(4.0, PerfOpen(oPC, "perf_doll", PerfDoll("me", 512.0, 768.0), 512.0, 768.0, 10.0, 10.0));
}
