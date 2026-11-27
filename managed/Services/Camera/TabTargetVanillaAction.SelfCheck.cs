using System;
using System.Runtime.InteropServices;
using System.Threading;
using CX.Framework;

namespace CX.Services.Camera;

/// <summary>Interact's default action reaches the engine past click-to-lock, so a press never changes the lock: with a
/// stand-in trampoline, VanillaDefaultAction calls it with the press's object and leaves the lock as it was. Oracle: the
/// pointers, the call count and the lock id written here.</summary>
public sealed unsafe partial class TabTargetService
{
    /// <summary>What the stand-in trampoline saw: how many calls, and the last one's this and object.</summary>
    private sealed class FakeActionState { public int Calls; public IntPtr This, Obj; }
    private static readonly FakeActionState FakeAction = new();

    [UnmanagedCallersOnly]
    private static int FakeDefaultAction(IntPtr self, IntPtr obj)
    {
        FakeAction.Calls++; FakeAction.This = self; FakeAction.Obj = obj; return 1;
    }

    [SelfCheckSuite(SuitePhase.AfterServices)]
    internal static bool VanillaActionSelfCheck(ExtenderLog log)
    {
        var check = new Checker(log, "TabTargetVanillaAction.SelfCheck");
        PerformDefaultActionOnObjectFn orig = _defaultActionOrig;
        uint locked = _lock;
        try
        {
            _defaultActionOrig = &FakeDefaultAction;
            FakeAction.Calls = 0;
            Volatile.Write(ref _lock, 7u);
            int verdict = VanillaDefaultAction((IntPtr)0x1000, (IntPtr)0x2000);
            check.Expect(verdict == 1 && FakeAction.Calls == 1 && FakeAction.This == (IntPtr)0x1000 && FakeAction.Obj == (IntPtr)0x2000,
                $"with the hook in, Interact's default action runs the engine's own through the trampoline, not the hooked entry: verdict {verdict}, {FakeAction.Calls} call(s), this 0x{FakeAction.This:x}, object 0x{FakeAction.Obj:x}");
            check.Expect(Lock == 7u, $"a press never changes the lock: {Lock}");
        }
        finally
        {
            _defaultActionOrig = orig;
            Volatile.Write(ref _lock, locked);
        }
        log.Info(check.Ok ? "TabTargetVanillaAction.SelfCheck: passed" : "TabTargetVanillaAction.SelfCheck: FAILED");
        return check.Ok;
    }
}
