//:: perf-doll8 -- eight dolls of the player, 256x384, in eight windows: the most model views there are.
#include "inc_perf"

void Show(object oPC)
{
    int i;
    for (i = 0; i < 8; i++)
        DelayCommand(4.0 + 0.3 * IntToFloat(i), PerfOpen(oPC, "perf_doll" + IntToString(i), PerfDoll("d" + IntToString(i), 256.0, 384.0), 256.0, 384.0,
            8.0 + IntToFloat(i % 4) * 320.0, 8.0 + IntToFloat(i / 4) * 440.0));
}
