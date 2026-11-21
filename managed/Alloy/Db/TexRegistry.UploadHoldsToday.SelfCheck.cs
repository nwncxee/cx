using System;
using System.Collections.Generic;
using CX.Framework;

namespace CX.Alloy.Db;

/// <summary>A regression test, E6, which held before the retry was built: a texture the game's files lack stays the
/// white stand-in when asked for again, reported once. Oracle: the "did not load" lines the log hears.</summary>
internal static unsafe partial class TexRegistry
{
    internal static bool UploadHoldsTodaySelfCheck(ExtenderLog log)
    {
        var check = new Checker(log, "TexRegistry.UploadHoldsToday.SelfCheck");
        if (Native.Sym(SymbolMap.AppManager) != IntPtr.Zero)
        { check.Skip("in a session: the suite waits out the retry, which a player's boot does not pay for"); return true; }
        ExtenderLog? saved = _log;
        Clock was = UseRetryClock(Clock.Stepped());
        try { Cx569E6(check); }
        finally { _log = saved; UseRetryClock(was); }
        log.Info(check.Ok ? "TexRegistry.UploadHoldsToday.SelfCheck: passed" : "TexRegistry.UploadHoldsToday.SelfCheck: FAILED");
        return check.Ok;
    }

    // Given a texture that failed to load because the game's files have no texture of that name
    // When the game asks for it again
    // Then it stays the white stand-in, and cx.log does not report it a second time
    private static void Cx569E6(Checker check)
    {
        List<string> heard = Listen();
        TexId id = Alloc("cx_e6", TexKind.Plain, 0, out _);
        Fail(id.Index, "no DDS, TGA or PLT resource of that name");
        _clk.Clock.Step(RetryWaitMs);
        TexId again = Alloc("cx_e6", TexKind.Plain, 0, out bool offer);
        check.Expect(!offer && StateOf(again) == TexState.Failed, "E6: a texture the game's files lack was loaded again");
        Fail(again.Index, "no DDS, TGA or PLT resource of that name");
        check.Expect(Reports(heard, "cx_e6") == 1, $"E6: a missing texture was reported {Reports(heard, "cx_e6")} times, not once");
        Release(again); Release(id);
    }
}
