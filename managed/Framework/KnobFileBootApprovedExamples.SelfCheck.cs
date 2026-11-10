using System;

namespace CX.Framework;

/// <summary>Boot writes nothing to <c>cx.cfg</c>: the hold's own rules, and the live proof that a boot arm
/// never reaches the writer however long boot runs. The failure this holds off reads as a player's chosen
/// file rewritten with values a self-check moved and put back.</summary>
public static class KnobFileBootApprovedExamples
{
    /// <summary>Slack over <see cref="KnobFile.DebounceMs"/> before the timing example reads the counters: a
    /// pool thread has to be scheduled, and a late callback must not read as a held arm.</summary>
    private const int SettleMs = 500;

    public static void SelfCheck(ExtenderLog log)
    {
        var check = new Checker(log);

        Rules(check);
        if (Native.Sym(SymbolMap.AppManager) != IntPtr.Zero)
            check.Skip("in a session: the timing example waits out the debounce, and boot does not wait");
        else
            BootArmNeverFires(check);

        log.Info(check.Ok ? "KnobFileBootApprovedExamples.SelfCheck: passed" : "KnobFileBootApprovedExamples.SelfCheck: FAILED");
    }

    /// <summary>E1 and E3, on the hold alone: a boot arm is counted and dropped, and a first run's one write
    /// is the only arm that outlives the hold.</summary>
    private static void Rules(Checker check)
    {
        var hold = new BootWriteHold();
        check.Expect(!hold.Arm(), "a boot arm is not scheduled");
        check.Expect(!hold.Arm(), "...nor is a second one");
        check.Expect(hold.HeldArms == 2, "both are counted, so the log can name them");
        check.Expect(!hold.End(), "a boot that found its file owes no write");
        check.Expect(hold.Arm(), "and once boot ends an arm is scheduled");
        check.Expect(hold.HeldArms == 2, "...without being counted as held");

        var first = new BootWriteHold();
        first.ArmFirstRun();
        check.Expect(!first.Arm(), "a first run holds boot's own arms the same way");
        check.Expect(first.End(), "but its missing file is still owed one write");
        check.Expect(!first.End(), "which is owed once, not on every call");
    }

    /// <summary>E1 live: the arm boot raises sits past the debounce without the writer firing. The counter is
    /// the whole point -- the write itself stands down off-device, so only a fire would show.</summary>
    private static void BootArmNeverFires(Checker check)
    {
        check.Expect(KnobFile.Holding, "the writer is still held while the examples run");
        int held = KnobFile.HeldArms;
        int fires = KnobFile.Fires;
        KnobFile.Arm();
        BlockingWait.Sleep(KnobFile.DebounceMs + SettleMs, "off-device boot self-check: the debounce has to be waited out to prove it never fires");
        check.Expect(KnobFile.Fires == fires, "a boot arm never reaches the writer, however long boot takes");
        check.Expect(KnobFile.HeldArms == held + 1, "...and the hold counted it");
        check.Expect(KnobFile.Writes == 0, "so cx.cfg is not rewritten at boot");
    }
}
