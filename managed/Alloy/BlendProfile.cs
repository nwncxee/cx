using System;
using CX.Framework;
using CX.Graphics;
using static CX.Alloy.AlloyRenderer;

namespace CX.Alloy;

/// <summary><c>/alloy bench</c>: ONE frame of the blended accumulation timed per drawing batch -- a GL timestamp
/// before each draw and one after the last, read back without waiting and logged as the costliest batches.
/// ponytail: timestamps serialise the profiled frame, so a delta is that draw's own time plus nothing it overlapped.</summary>
internal static unsafe class BlendProfile
{
    private const int Cap = 512;
    private static uint* _q;
    private static readonly int[] _g = new int[Cap], _len = new int[Cap], _tex = new int[Cap];
    private static readonly byte[] _prog = new byte[Cap], _cls = new byte[Cap];
    private static readonly IntPtr[] _gob = new IntPtr[Cap];
    private static readonly ulong[] _t = new ulong[Cap + 1];
    private static readonly int[] _order = new int[Cap];
    private static int _n, _state;   // 0 idle, 1 armed for the next accumulation, 2 waiting on the GPU

    /// <summary>Context loss: the query names died with it. The block is freed, not deleted from, and a
    /// profile in flight is dropped; the next <see cref="Stamp"/> mints a fresh set.</summary>
    internal static void ContextLost()
    {
        if (_q != null) { System.Runtime.InteropServices.NativeMemory.Free(_q); _q = null; }
        _n = 0; _state = 0;
    }

    /// <summary>Time the next blended accumulation.</summary>
    internal static void Arm() { if (_state == 0) _state = 1; }

    /// <summary>True while the issue loop should stamp: armed, inside the OIT bracket.</summary>
    internal static bool Recording => _state == 1 && Issue._oitOn;

    /// <summary>The issue loop, just before batch <paramref name="g"/>'s draw under program <paramref name="prog"/>.</summary>
    internal static void Stamp(int g, int prog)
    {
        if (_q == null)
        {
            _q = (uint*)System.Runtime.InteropServices.NativeMemory.Alloc((nuint)(Cap + 1) * sizeof(uint));
            Gl.GenQueries(Cap + 1, _q);
        }
        if (_n >= Cap) return;
        Gl.QueryCounter(_q[_n], Gl.TIMESTAMP);
        _g[_n] = g; _prog[_n] = (byte)prog; _cls[_n] = _gCls[g]; _len[_n] = _gLen[g]; _tex[_n] = _gTex[g]; _gob[_n] = _gGob[g];
        _n++;
    }

    /// <summary>The issue loop's end: the closing stamp, and the wait for the GPU begins.</summary>
    internal static void Close()
    {
        if (_n == 0) return;
        Gl.QueryCounter(_q[_n], Gl.TIMESTAMP);
        _state = 2;
    }

    /// <summary>Per frame, inside the 4.6 bracket: once the closing stamp has landed, log the batches by cost and go idle.</summary>
    internal static void Poll(ExtenderLog? log)
    {
        if (_state != 2) return;
        int ready = 0;
        Gl.GetQueryObjectiv(_q[_n], Gl.QUERY_RESULT_AVAILABLE, &ready);
        if (ready == 0) return;
        fixed (ulong* t = _t)
            for (int i = 0; i <= _n; i++) Gl.GetQueryObjectui64v(_q[i], Gl.QUERY_RESULT, &t[i]);
        for (int i = 0; i < _n; i++) _order[i] = i;
        Array.Sort(_order, 0, _n, Comparer.Instance);
        double total = (_t[_n] - _t[0]) / 1e6;
        string s = "";
        for (int k = 0; k < Math.Min(_n, 15); k++)
        {
            int i = _order[k];
            s += $"\n    #{i} g{_g[i]} {(_t[i + 1] - _t[i]) / 1e6:F3} ms: prog {_prog[i]} ({ProgName(_prog[i])}), class {(AlloyClass)_cls[i]}, "
               + $"{_len[i]} draws, tex {_tex[i]} '{Db.TexRegistry.ResrefOfGl((uint)_tex[i])}', gob 0x{(long)_gob[i]:x}";
        }
        log?.Always($"AlloyRenderer: [bench] blended per batch: {_n} drawing batches, {total:F3} ms stamped end to end; costliest:{s}");
        _n = 0; _state = 0;
    }

    private static string ProgName(int p) => p switch { 0 => "fullbright", 1 => "lit", 2 => "water", 3 => "hosted", 4 => "lit lists", 5 => "deferred twin", _ => "?" };

    private sealed class Comparer : System.Collections.Generic.IComparer<int>
    {
        internal static readonly Comparer Instance = new();
        public int Compare(int a, int b) => (_t[b + 1] - _t[b]).CompareTo(_t[a + 1] - _t[a]);
    }
}
