using System;
using System.Collections.Generic;
using CX.Framework;

namespace CX.Graphics.Rhi;

/// <summary>Regression tests, E10 and E11, which held before the restore waited for its size: a fresh device draws depth
/// of field and FSR from the first frame, and a real resize gets depth of field back within a second. Oracle: the
/// device's own answers and ready lines.</summary>
internal sealed unsafe partial class VkBackend
{
    /// <summary>The size a drag lets go at.</summary>
    private const int DraggedW = 1200, DraggedH = 800;

    internal void ResizeHoldsTodaySelfCheck(Checker check)
    {
        Cx569E11(check);
        Cx569E10(check);
    }

    // Given depth of field and FSR are on
    // When the game starts and the window keeps its size
    // Then both draw from the first frame, as today
    private void Cx569E11(Checker check)
    {
        check.Expect(_dof.Ctx == 0 && _fsr.Ctx == 0, "E11: the device already holds a DoF or FSR context before its first frame");
        string? dof = DofReady(RestoredW, RestoredH), fsr = FsrReady(RestoredW / 2, RestoredH / 2, RestoredW, RestoredH);
        if (dof != null && _dof.Refused != null) check.Skip($"E11: depth of field does not run on this device ({dof})");
        else check.Expect(dof == null, $"E11: depth of field does not draw on the first frame ({dof})");
        if (fsr != null && _fsr.Refused != null) check.Skip($"E11: FSR does not run on this device ({fsr})");
        else check.Expect(fsr == null, $"E11: FSR does not draw on the first frame ({fsr})");
    }

    // Given depth of field is on
    // When the player drags the window to a new size and lets go
    // Then depth of field draws again within a second, at the new size
    private void Cx569E10(Checker check)
    {
        if (DofReady(RestoredW, RestoredH) is string no) { check.Skip($"E10: depth of field does not run on this device ({no})"); return; }
        var heard = new List<string>();
        int frames = 0;
        Action<string>? tap = _log.Tap;
        _log.Tap = heard.Add;
        try { while (frames < HeldFrames && DofReady(DraggedW, DraggedH) != null) frames++; }
        finally { _log.Tap = tap; ForgetUpscalers(); }
        check.Expect(frames < HeldFrames, $"E10: depth of field did not draw again within {HeldFrames} frames of the drag");
        check.Expect(heard.Exists(l => l == $"VkBackend: depth of field ready at {DraggedW}x{DraggedH}"), "E10: depth of field was not made ready at the dragged size");
    }
}
