using System;
using CX.Framework;

namespace CX.Engine;

public readonly unsafe partial struct GlPoolTable
{
    /// <summary>The table whose {base, count} pair sits <see cref="EngineLayout.PoolTableAnchorOffset"/> past the
    /// anchor symbol; an empty view for a null anchor.</summary>
    public static GlPoolTable AtAnchor(IntPtr anchor)
        => new(anchor == IntPtr.Zero ? IntPtr.Zero : anchor + EngineLayout.PoolTableAnchorOffset);
}
