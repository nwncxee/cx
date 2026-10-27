namespace CX.Graphics;

/// <summary>Double-buffered GPU query: begin on the slot whose result has already been harvested, so the
/// readback never stalls the pass waiting for a result the GPU has not produced. Harvesting happens in
/// BEGIN, unconditionally -- End only runs when Begin took the pass, so harvesting there would wedge the
/// meter for the session the first time both slots are pending at once.</summary>
public unsafe struct GpuQuery
{
    /// <summary>GL timer queries are off: Alloy renders on Vulkan, whose frame times itself. Every Begin declines,
    /// so no query is issued or harvested. ponytail: the GL meters retire with the GL backend.</summary>
    public static bool Enabled;
    private uint _q0, _q1;
    private bool _p0, _p1;
    private int _at;
    private ulong _sum;
    private int _n;

    public bool Begin(uint target)
    {
        if (!Enabled) return false;
        if (Gl.GenQueries == null || Gl.GetQueryObjectui64v == null || Gl.GetQueryObjectiv == null) return false;
        if (_q0 == 0) { uint* q = stackalloc uint[2]; Gl.GenQueries(2, q); _q0 = q[0]; _q1 = q[1]; }
        if (_q0 == 0 || _q1 == 0) return false;
        Harvest(_q0, ref _p0);
        Harvest(_q1, ref _p1);
        if (_at == 0 ? _p0 : _p1) return false;
        Gl.BeginQuery(target, _at == 0 ? _q0 : _q1);
        return true;
    }

    public void End(uint target)
    {
        Gl.EndQuery(target);
        if (_at == 0) _p0 = true; else _p1 = true;
        _at = 1 - _at;
    }

    private void Harvest(uint q, ref bool pending)
    {
        if (!pending) return;
        int ready; Gl.GetQueryObjectiv(q, Gl.QUERY_RESULT_AVAILABLE, &ready);
        if (ready == 0) return;
        ulong v; Gl.GetQueryObjectui64v(q, Gl.QUERY_RESULT, &v);
        pending = false;
        // zink answered 8.2e15 ns for one term (2026-09-05): a sample longer than ten seconds is a driver's
        // nonsense, not a frame, and one of them would own the period mean
        if (v > 10_000_000_000UL) return;
        _sum += v; _n++;
    }

    /// <summary>Period mean, 0 when nothing was harvested (which reads as "no measurement", not "free").</summary>
    public double Avg => _n == 0 ? 0.0 : (double)_sum / _n;
    public bool Measured => _n > 0;
    /// <summary>Raw running totals since the last <see cref="Reset"/>, for a caller that takes its own deltas.</summary>
    public ulong Sum => _sum;
    public int N => _n;
    public void Reset() { _sum = 0; _n = 0; }
}
