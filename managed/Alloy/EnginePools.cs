using System;
using System.Runtime.InteropServices;
using CX.Engine;
using CX.Framework;

namespace CX.Alloy;

/// <summary>The engine's vertex/index pools, read and never written: the engine uploads and draws its own,
/// and Alloy's geometry lives in its own <see cref="Db.MeshArena"/>. What is left here is the shared-VDO
/// capacity pin and <see cref="PoolHost"/>, whose last reader was the GUI draw list while that was harvested
/// from the engine's funnels; it retires with the pool views and their rows rather than alone.</summary>
internal static unsafe class EnginePools
{
    /// <summary>The pool table and the pinned capacity, resolved once at install.</summary>
    private sealed class PoolsState
    {
        /// <summary>The {base, count} pair, NOT the table it points at: <c>RequestPool</c> @0x4d1ec0 grows the
        /// table with <c>calloc</c>+<c>free</c>, so the base moves and every lookup re-reads it.</summary>
        internal GlPoolTable Table;
        internal int MaxElements;
    }
    private static PoolsState _s = new();

    /// <summary>NVIDIA's GL_MAX_ELEMENTS answer, which the engine has always pooled under there. The value is a
    /// glDrawRangeElements hint in GL, never a buffer limit, and the pools grow by doubling past it anyway.</summary>
    internal const int MaxElementsDefault = 1 << 20;

    /// <summary>Resolve the pool table, and pin the shared-VDO capacity: a Mesa answer of 3000 fragments it
    /// 350x against NVIDIA's (see <see cref="SymbolMap.GlMaxElementVertices"/>).</summary>
    internal static void Install(ExtenderLog log, HookService hooks)
    {
        _s.Table = GlPoolTable.AtAnchor(Native.Sym(SymbolMap.GlPoolTableAnchor));
        if (_s.Table.IsNull) log.Info("EnginePools: the pool table did not resolve -- no host shadow can be read");
        _s.MaxElements = Renderer.MaxElements.Boot;
        if (_s.MaxElements <= 0) return;
        bool a = hooks.InstallGlMaxElementVertices(&OnMaxElements) != null;
        bool b = hooks.InstallGlMaxElementIndices(&OnMaxElements) != null;
        log.Info(a && b
            ? $"EnginePools: shared-VDO capacity pinned to {_s.MaxElements} vertices/indices regardless of the driver's GL_MAX_ELEMENTS answer (alloy.maxelements=0 restores it)"
            : "EnginePools: GetVertexBufferMaxElement* did not resolve -- the driver's GL_MAX_ELEMENTS answer sizes the shared VDOs");
    }

    /// <summary>Replaces both <c>GLRender::GetVertexBufferMaxElement*</c> wholesale; the engine's cached copy
    /// is never consulted. Cold: read per VDO allocation.</summary>
    [UnmanagedCallersOnly]
    private static int OnMaxElements() => _s.MaxElements;

    /// <summary>The host shadow of the pool this token names, or null. The bound check is the engine's own
    /// (<c>UnlockPool</c> tests the token against the descriptor count before it indexes), which is what makes
    /// this safe on an arbitrary token, the no-pool token included.</summary>
    internal static byte* PoolHost(uint token)
    {
        GlPoolTable table = _s.Table;
        if (table.IsNull) return null;
        GlPool pools = table.Base;
        if (pools.IsNull || token >= table.Count) return null;
        return (byte*)pools[(int)token].Host;
    }

    /// <summary>Off-device, over a fake two-descriptor table. Oracle: the hand-placed host pointers and the
    /// token bounds the fixture sets.</summary>
    internal static bool SelfCheck(ExtenderLog log)
    {
        var check = new Checker(log);
        PoolsState saved = _s;
        byte* tableMem = stackalloc byte[0x20];
        byte* poolMem = stackalloc byte[2 * EngineLayout.PoolStride];   // a SIZE here, not a field read
        byte* a = stackalloc byte[8], b = stackalloc byte[8];
        var table = new GlPoolTable((IntPtr)tableMem);
        var pools = new GlPool((IntPtr)poolMem);
        table.Base = pools;
        table.Count = 2;
        pools[0].Host = (IntPtr)a;
        pools[1].Host = (IntPtr)b;
        _s = new PoolsState { Table = table };
        check.Expect(PoolHost(0) == a && PoolHost(1) == b, "a token in range reads its own descriptor's host shadow");
        check.Expect(PoolHost(2) == null, "a token past the descriptor count reads nothing");
        _s = new PoolsState();
        check.Expect(PoolHost(0) == null, "an unresolved table reads nothing");
        _s = saved;
        log.Info(check.Ok ? "EnginePools.SelfCheck: passed" : "EnginePools.SelfCheck: FAILED");
        return check.Ok;
    }
}
