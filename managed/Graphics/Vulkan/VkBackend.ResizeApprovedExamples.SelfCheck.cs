using System;
using System.Collections.Generic;
using CX.Framework;

namespace CX.Graphics.Rhi;

/// <summary>Depth of field and FSR through a window restore (approved-spec, E8, E9 and the settling frames): one context
/// at the size the window comes back to. Oracle: the restore's sizes written out here, from the reporter's log, scaled
/// down, and the ready lines the device's own log writes.</summary>
internal sealed unsafe partial class VkBackend
{
    /// <summary>The sizes one restore passes through on its way back, one frame each.</summary>
    private static readonly (int W, int H)[] RestoreSteps =
    [
        (320, 180), (384, 216), (448, 252), (512, 288), (576, 324), (640, 360), (704, 396), (768, 432),
        (832, 468), (896, 504), (960, 540), (1024, 576), (1088, 612), (1152, 648), (1216, 684),
    ];

    /// <summary>The size the window comes back to and the size it had before it was minimised.</summary>
    private const int RestoredW = 1280, RestoredH = 720, BeforeW = 1600, BeforeH = 900;

    /// <summary>Frames the restored size is held for: a second at 30 frames a second.</summary>
    private const int HeldFrames = 30;

    internal void ResizeApprovedExamplesSelfCheck(Checker check)
    {
        Cx569E8(check);
        Cx569E9(check);
    }

    // Given depth of field is on
    // When the player restores the minimised game window
    // Then cx.log shows depth of field made ready once, at the restored size, and depth of field stays on for the session
    private void Cx569E8(Checker check)
    {
        if (DofReady(BeforeW, BeforeH) is string no) { check.Skip($"E8: depth of field does not run on this device ({no})"); return; }
        var heard = new List<string>();
        int settling = 0;
        string? last;
        Action<string>? tap = _log.Tap;
        _log.Tap = heard.Add;
        try
        {
            foreach ((int w, int h) in RestoreSteps) if (DofReady(w, h) != null) settling++;
            last = null;
            for (int f = 0; f < HeldFrames; f++) last = DofReady(RestoredW, RestoredH);
        }
        finally { _log.Tap = tap; ForgetUpscalers(); }
        int ready = Count(heard, "VkBackend: depth of field ready at ");
        check.Expect(ready == 1, $"E8: one restore made depth of field ready {ready} times, not once");
        check.Expect(heard.Exists(l => l == $"VkBackend: depth of field ready at {RestoredW}x{RestoredH}"), "E8: depth of field was not made ready at the restored size");
        check.Expect(last == null && _dof.Refused == null, $"E8: after the restore depth of field is off ({last ?? _dof.Refused})");
        check.Expect(settling == RestoreSteps.Length, $"settling: {RestoreSteps.Length - settling} of the restore's passing sizes drew with depth of field");
    }

    // Given FSR is the upscaler
    // When the player restores the minimised game window
    // Then cx.log shows FSR made ready once, at the restored size
    private void Cx569E9(Checker check)
    {
        if (FsrReady(BeforeW / 2, BeforeH / 2, BeforeW, BeforeH) is string no) { check.Skip($"E9: FSR does not run on this device ({no})"); return; }
        var heard = new List<string>();
        int settling = 0;
        string? last;
        Action<string>? tap = _log.Tap;
        _log.Tap = heard.Add;
        try
        {
            foreach ((int w, int h) in RestoreSteps) if (FsrReady(w / 2, h / 2, w, h) != null) settling++;
            last = null;
            for (int f = 0; f < HeldFrames; f++) last = FsrReady(RestoredW / 2, RestoredH / 2, RestoredW, RestoredH);
        }
        finally { _log.Tap = tap; ForgetUpscalers(); }
        int ready = Count(heard, "VkBackend: FSR 3.1 ready, ");
        check.Expect(ready == 1, $"E9: one restore made FSR ready {ready} times, not once");
        check.Expect(heard.Exists(l => l.StartsWith($"VkBackend: FSR 3.1 ready, up to {RestoredW / 2}x{RestoredH / 2} into {RestoredW}x{RestoredH}", StringComparison.Ordinal)),
            "E9: FSR was not made ready at the restored size");
        check.Expect(last == null && _fsr.Refused == null, $"E9: after the restore FSR is off ({last ?? _fsr.Refused})");
        check.Expect(settling == RestoreSteps.Length, $"settling: {RestoreSteps.Length - settling} of the restore's passing sizes drew with FSR");
    }

    private static int Count(List<string> heard, string start)
    {
        int n = 0;
        foreach (string l in heard) if (l.StartsWith(start, StringComparison.Ordinal)) n++;
        return n;
    }

    /// <summary>No DoF or FSR context, as on a fresh device: the goldens after these ask for sizes of their own.</summary>
    private void ForgetUpscalers()
    {
        _vkd.vkDeviceWaitIdle();
        DisposeDof();
        DisposeFsr();
    }
}
