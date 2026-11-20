using System;
using System.Collections.Generic;
using CX.Framework;

namespace CX.Graphics.Rhi;

/// <summary>A size that goes back to the context's own before it has held: the count starts again, so a window
/// rocking between two sizes makes no new depth of field context. Oracle: the frame counts written out here.</summary>
internal sealed unsafe partial class VkBackend
{
    /// <summary>Frames on the new size each time it is visited: fewer than the settle asks for, more than half of it.</summary>
    private const int WobbleFrames = 5;

    internal void ResizeWobbleSelfCheck(Checker check)
    {
        if (DofReady(RestoredW, RestoredH) is string no) { check.Skip($"wobble: depth of field does not run on this device ({no})"); return; }
        var heard = new List<string>();
        Action<string>? tap = _log.Tap;
        _log.Tap = heard.Add;
        try
        {
            for (int f = 0; f < WobbleFrames; f++) DofReady(DraggedW, DraggedH);
            DofReady(RestoredW, RestoredH);
            for (int f = 0; f < WobbleFrames; f++) DofReady(DraggedW, DraggedH);
        }
        finally { _log.Tap = tap; ForgetUpscalers(); }
        int ready = Count(heard, "VkBackend: depth of field ready at ");
        check.Expect(ready == 0, $"wobble: a size that came back to the context's own between two short visits made {ready} new context(s)");
    }
}
