using System;
using System.Runtime.InteropServices;
using CX.Engine;
using CX.Framework;

namespace CX.Alloy;

/// <summary>The engine's vertex/index pools: the engine uploads and draws its own, and Alloy's geometry lives
/// in its own <see cref="Db.MeshArena"/>, so what is left here is the shared-VDO capacity pin.</summary>
internal static unsafe class EnginePools
{
    /// <summary>The pinned capacity, read once at install.</summary>
    private sealed class PoolsState
    {
        internal int MaxElements;
    }
    private static PoolsState _s = new();

    /// <summary>NVIDIA's GL_MAX_ELEMENTS answer, which the engine has always pooled under there. The value is a
    /// glDrawRangeElements hint in GL, never a buffer limit, and the pools grow by doubling past it anyway.</summary>
    internal const int MaxElementsDefault = 1 << 20;

    /// <summary>Pin the shared-VDO capacity: a Mesa answer of 3000 fragments it 350x against NVIDIA's
    /// (see <see cref="SymbolMap.GlMaxElementVertices"/>).</summary>
    internal static void Install(ExtenderLog log, HookService hooks)
    {
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
}
