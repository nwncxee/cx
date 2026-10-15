using CX.Framework;

namespace CX.Services.DayNight;

/// <summary>
/// Everything owned day/night carries between frames: the chain slots, the two render globals, the machine's
/// memory, and the policy verdict cached on its epoch.
/// </summary>
internal sealed unsafe class DayNightState
{
    internal ExtenderLog? Log;
    internal AreaUpdateDayNightCycleFn* Next;
    internal AreaLoadAreaFn* Load;
    internal AreaSetAsDawnFn* AsDawn;
    internal AreaSetAsDuskFn* AsDusk;
    internal AreaSetAsDayFn* AsDay;
    internal AreaSetAsNightFn* AsNight;
    /// <summary>The <c>shadowalpha</c> and <c>projectionalpha</c> globals the transition drives.</summary>
    internal float* Shadow, Projection;
    internal DayNightMemory Memory;

    /// <summary>An area is loading into the current object: nothing is pushed, and the <c>SetAs*</c> calls
    /// inside the load must not put the previous area's light into the new one.</summary>
    internal bool InLoad;
    /// <summary>An area has loaded and the machine has not picked the new one up yet.</summary>
    internal bool Edge;
    /// <summary>How deep the <c>SetAs*</c> cuts are nested; the hold waits for the outermost to return.</summary>
    internal int Cutting;

    /// <summary>Which diagnoses have already been logged for the area the memory is on.</summary>
    internal int Said;
    internal const int SaidCut = 1, SaidRestart = 2, SaidFlip = 4;

    private int _epoch = -1;
    private bool _owned;

    /// <summary>Everything the machine remembers, dropped: the next owned frame seeds itself from whatever
    /// the engine is showing then.</summary>
    internal void Forget()
    {
        Memory.Have = false;
        Said = 0;
    }

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
