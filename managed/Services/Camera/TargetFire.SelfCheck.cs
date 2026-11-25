using System;
using CX.Framework;

namespace CX.Services.Camera;

/// <summary>The target cursor's grading over a covering window: inside <c>CActionTarget::Update</c> the window's answer is
/// the pick, and outside it (a click, the keyhole read) the world's is. Oracle: the object pointers and verdicts written here.</summary>
internal static unsafe partial class TargetFire
{
    /// <summary>A stand-in CNWCObject* the cover answers; never dereferenced.</summary>
    private static readonly IntPtr Held = new(0x1000);   // not-an-offset: a fake object pointer

    [SelfCheckSuite(SuitePhase.BeforeServices)]
    internal static void SelfCheck(ExtenderLog log)
    {
        var check = new Checker(log, "TargetFire.CoverSelfCheck");
        CoverPick? cover = S.Cover;
        bool grading = S.Grading;
        IntPtr at = S.At;
        try
        {
            S.At = IntPtr.Zero;
            IntPtr held = Held;
            bool covers = true;
            S.Cover = (out IntPtr o) => { o = held; return covers; };

            // Given a CX window covers the pointer over an item
            // When the target cursor is graded
            // Then the world pick answers that item, verdict "object"
            S.Grading = true;
            IntPtr obj = IntPtr.Zero;
            bool took = OverridePick(&obj, IntPtr.Zero, out int verdict);
            check.Expect(took && obj == Held && verdict == PickObject, $"a grading over the window's item picks it ({took} {obj:x} {verdict})");

            // Given the window covers the pointer over an empty cell
            // When the target cursor is graded
            // Then the pick is nothing, not the world behind the window
            held = IntPtr.Zero;
            obj = Held;
            took = OverridePick(&obj, IntPtr.Zero, out verdict);
            check.Expect(took && obj == IntPtr.Zero && verdict == 0, $"a grading over an empty cell picks nothing ({took} {obj:x} {verdict})");

            // Given the pointer is off the window
            // When the target cursor is graded
            // Then the world pick runs as it would
            covers = false;
            check.Expect(!OverridePick(&obj, IntPtr.Zero, out _), "a grading off the window leaves the pick to the world");

            // Given the window covers the pointer
            // When a pick runs outside a grading (a click, the frame's own read)
            // Then the world pick runs as it would
            covers = true;
            held = Held;
            S.Grading = false;
            check.Expect(!OverridePick(&obj, IntPtr.Zero, out _), "a pick outside a grading is the world's");
        }
        finally { S.Cover = cover; S.Grading = grading; S.At = at; }
        log.Info(check.Ok ? "TargetFire.CoverSelfCheck: passed" : "TargetFire.CoverSelfCheck: FAILED");
    }
}
