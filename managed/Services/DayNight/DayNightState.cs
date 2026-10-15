using System;
using CX.Framework;

namespace CX.Services.DayNight;

/// <summary>
/// Everything owned day/night carries between frames: the chain slot, the two render globals, the engine
/// handles the per-frame path needs, the machine's memory, and the policy verdict cached on its epoch.
/// </summary>
internal sealed unsafe class DayNightState
{
    internal ExtenderLog? Log;
    internal AreaUpdateDayNightCycleFn* Next;
    /// <summary>The <c>shadowalpha</c> and <c>projectionalpha</c> globals the transition drives.</summary>
    internal float* Shadow, Projection;
    /// <summary>The <c>g_pAppManager</c> global, the walk to the module's transition clock.</summary>
    internal IntPtr AppMgr;
    internal DayNightMemory Memory;
    /// <summary>Which diagnoses have already been logged for the area the memory is on.</summary>
    internal int Said;

    private int _epoch = -1;
    private bool _owned;

    /// <summary>Is the server holding the opt-in? One field compare per frame; the policy read happens only
    /// when a line has moved, and a change says so once in the log.</summary>
    internal bool Owned
    {
        get
        {
            int e = ServerPolicy.Epoch;
            if (e == _epoch) return _owned;
            _epoch = e;
            bool was = _owned;
            _owned = DayNightPolicy.Read();
            if (_owned != was)
                Log?.Info(_owned
                    ? $"DayNight: '{DayNightPolicy.Key}' held -- the server owns dawn and dusk (no midpoint dip, no re-cut)"
                    : $"DayNight: '{DayNightPolicy.Key}' released -- vanilla dawn/dusk shaping is back");
            return _owned;
        }
    }
}
