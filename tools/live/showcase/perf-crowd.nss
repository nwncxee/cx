//:: perf-crowd -- sixty creatures walking around the PC, none fading: what a populated square asks of the route walk,
//:: the staging walk and the casters. The same creature every time, so runs compare.
#include "inc_perf"

const int CROWD = 60;

void Wander(object oCre)
{
    AssignCommand(oCre, ActionRandomWalk());
}

void Show(object oPC)
{
    int i;
    for (i = 0; i < CROWD; i++)
    {
        object oCre = PerfSpawn(oPC, "c" + IntToString(i), "nw_elfmerc001", 3.0 + IntToFloat(i / 10) * 2.0, (IntToFloat(i % 10) - 4.5) * 2.0);
        DelayCommand(1.0, Wander(oCre));
    }
}
