using CX.Framework;

namespace CX.Alloy;

/// <summary>One view takes one pass block, and a block is never reused past what changed it. Runs the real
/// <see cref="BindPass"/> off-device. Oracle: hand-written viewports, knob values and counts.</summary>
public sealed unsafe partial class AlloyRenderer
{
    private static void PassBinds2(int x, int y, int w, int h, int count)
    {
        int* viewport = stackalloc int[4] { x, y, w, h };
        for (int k = 0; k < count; k++) BindPass(-1, viewport, 1f, 100f);
    }

    /// <summary>The pass block clause over one frame's worth of counts.</summary>
    private static string PassNoteOne()
    {
        int frames = Counters._frames;
        Counters._frames = 1;
        string note = PassNote();
        Counters._frames = frames;
        return note;
    }

    private static PassBlock* PassStagedBlock()
    {
        fixed (byte* b = PassStaged) return (PassBlock*)b;
    }

    internal static bool PassBlockSelfCheck(ExtenderLog log)
    {
        var check = new Checker(log, "AlloyRenderer.PassBlockApprovedExamples.SelfCheck");
        int frameWas = _frameSeq, bindsWas = PassBinds;
        var tapsWas = Shadows.Taps.V;
        _frameSeq = 1;

        // Given a frame whose colour pass switches program many times
        // When the [alloy] period line prints
        // Then the pass block clause reports how many blocks were built per frame, and the number follows views rather than program switches
        {
            ResetPassPeriod();
            PassBinds2(0, 0, 640, 480, 5);
            check.Expect(PassNoteOne().Contains("1 built/frame"), $"E11: five binds of one view did not build one block (got \"{PassNoteOne()}\")");
            PassBinds2(0, 0, 320, 240, 2);
            check.Expect(PassNoteOne().Contains("2 built/frame"), $"E11: a second view did not build one more (got \"{PassNoteOne()}\")");

            _frameSeq = 2;
            _vkc.ArenaUsed = 0; _vkc.PassAt = -1;
            PassBinds2(0, 0, 640, 480, 1);
            int first = VkArenaPass();
            PassBinds2(0, 0, 640, 480, 1);
            check.Expect(first >= 0 && VkArenaPass() == first, "E11: two binds of one view took two copies in the arena");
            _vkc.ArenaUsed = 0; _vkc.PassAt = -1;
        }

        // Given a frame with a water mirror, a probe capture or a half-size pass
        // When each is drawn
        // Then each is lit as today's build lights it
        {
            _frameSeq = 3;
            PassBinds2(0, 0, 640, 480, 1);
            check.Expect(PassStagedBlock()->Tile.Z == 20f, "E12: the full view's block has the wrong tile size");
            PassBinds2(0, 0, 320, 240, 1);
            check.Expect(PassStagedBlock()->Tile.Z == 10f, "E12: a half-size pass took the full view's block");
            int* viewport = stackalloc int[4] { 0, 0, 320, 240 };
            BindPass(-1, viewport, 2f, 100f);
            check.Expect(PassStagedBlock()->Z.X == 2f, "E12: a pass with its own depth range took another's block");
        }

        // Given a player changing a /light dial or a shadow knob
        // When the next frame draws
        // Then it draws with the new value, as today
        {
            _frameSeq = 5;
            PassBinds2(0, 0, 640, 480, 1);
            Shadows.Taps.V = tapsWas + 1;
            _frameSeq = 6;
            ResetPassPeriod();
            PassBinds2(0, 0, 640, 480, 2);
            check.Expect(PassStagedBlock()->LocalPcss.Z == tapsWas + 1, "E13: the next frame does not carry the retuned knob");
            check.Expect(PassNoteOne().Contains("1 built/frame"), "E13: a frame with no retune and a still camera does not build its block once");
            Shadows.Taps.V = tapsWas;
        }

        // Given a pass block reused when a refill would have written different bytes
        // When the block's audit frame comes round
        // Then cx.log names it in a pass block MISS line and the period line counts it
        {
            ExtenderLog? was = _log;
            _log = log;
            _frameSeq = 64;
            ResetPassPeriod();
            string clean = AllOf(LinesFrom(log, () => PassBinds2(0, 0, 640, 480, 3)));
            check.Expect(!clean.Contains("MISS"), $"E14: a period with no such change names a miss (got \"{clean}\")");
            check.Expect(PassNoteOne().Contains("0 misses"), "E14: a period with no such change does not read 0 misses");
            Shadows.Taps.V = tapsWas + 1;
            string text = AllOf(LinesFrom(log, () => PassBinds2(0, 0, 640, 480, 1)));
            _log = was;
            check.Expect(text.Contains("pass block MISS"), $"E14: the audit did not name the stale block (got \"{text}\")");
            check.Expect(PassNoteOne().Contains("1 misses"), "E14: the period line did not count the miss");
            Shadows.Taps.V = tapsWas;
        }

        _frameSeq = frameWas; PassBinds = bindsWas;
        ResetPassPeriod();
        log.Info(check.Ok ? "AlloyRenderer.PassBlockApprovedExamples.SelfCheck: passed" : "AlloyRenderer.PassBlockApprovedExamples.SelfCheck: FAILED -- see above");
        return check.Ok;
    }
}
