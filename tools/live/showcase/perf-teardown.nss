//:: perf-teardown -- four dolls of the player in four windows; the windows close together at 20 s, so the frame-time probe's window
//:: that holds the teardown can be set against the ones before it. Runs with --cfg general.frametimes=1.
#include "inc_perf"

void ClosePerf(object oPC, int nToken, string sId)
{
    NuiDestroy(oPC, nToken);
    ShowMark("close " + sId);
}

void Show(object oPC)
{
    int i;
    for (i = 0; i < 4; i++)
    {
        string sId = "perf_down" + IntToString(i);
        json jBody = NuiCol(JsonArrayInsert(JsonArray(), PerfDoll("d" + IntToString(i), 256.0, 384.0)));
        int nToken = PerfWindow(oPC, sId, jBody, 8.0 + IntToFloat(i) * 320.0, 8.0, 286.0, 434.0);
        DelayCommand(20.0 + 0.1 * IntToFloat(i), ClosePerf(oPC, nToken, sId));
    }
}
