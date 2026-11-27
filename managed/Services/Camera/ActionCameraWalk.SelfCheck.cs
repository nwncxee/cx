using System;
using System.Runtime.InteropServices;
using CX.Engine;
using CX.Framework;

namespace CX.Services.Camera;

/// <summary>Under the action camera a click's walk is refused and a walk CX asks for itself goes through, both
/// reaching a stand-in engine walk with the app's Internal object as its this. Oracle: the walk's arguments, the call
/// counts and the fake app's Internal pointer written here.</summary>
public sealed unsafe partial class MouseLookService
{
    /// <summary>What the stand-in engine walk saw: how many walks, and the last one's this, object and x.</summary>
    private sealed class FakeWalkState { public int Walks; public IntPtr This; public uint Obj; public float X; }
    private static readonly FakeWalkState Fake = new();

    [UnmanagedCallersOnly]
    private static int FakeWalk(IntPtr self, float x, float y, float z, int flags, uint obj, int extra)
    {
        Fake.Walks++; Fake.This = self; Fake.Obj = obj; Fake.X = x; return 1;
    }

    [SelfCheckSuite(SuitePhase.AfterServices)]
    internal static bool ActionCameraWalk(ExtenderLog log)
    {
        var check = new Checker(log, "ActionCameraWalk.SelfCheck");
        WalkPlayerToPointFn orig = _walkOrig;
        bool action = _action;
        int refused = _walksRefused;
        var hook = (delegate* unmanaged<IntPtr, float, float, float, int, uint, int, int>)&OnWalkPlayerToPoint;
        check.Expect(EngineLayout.ExoAppInternal >= 0, $"precondition: CClientExoApp's internal offset is known ({EngineLayout.ExoAppInternal})");
        byte* app = (byte*)NativeMemory.AllocZeroed((nuint)(EngineLayout.ExoAppInternal + sizeof(IntPtr)));
        byte* inner = (byte*)NativeMemory.AllocZeroed(16);
        new CClientExoApp((IntPtr)app).Internal = new CClientExoAppInternal((IntPtr)inner);
        try
        {
            _walkOrig = &FakeWalk; Fake.Walks = 0;
            _action = true;
            int click = hook(IntPtr.Zero, 4f, 5f, 0f, 0, 77, 1);
            check.Expect(click == 0 && Fake.Walks == 0, $"under the action camera a click's walk is refused and never reaches the engine (verdict {click}, {Fake.Walks} walk(s))");
            int own = WalkPastActionCamera(new CClientExoApp((IntPtr)app), 6f, 7f, 0f, 0, 88, 1);
            check.Expect(own == 1 && Fake.Walks == 1 && Fake.Obj == 88 && Fake.X == 6f,
                $"under the action camera Interact's own walk reaches the engine with its target (verdict {own}, {Fake.Walks} walk(s), object {Fake.Obj})");
            check.Expect(Fake.This == (IntPtr)inner,
                $"Interact's own walk hands the engine the app's Internal object as its this (got 0x{Fake.This:x}, Internal 0x{(IntPtr)inner:x}, app 0x{(IntPtr)app:x})");
            int after = hook(IntPtr.Zero, 4f, 5f, 0f, 0, 77, 1);
            check.Expect(after == 0 && Fake.Walks == 1, $"a click after Interact's walk is still refused (verdict {after}, {Fake.Walks} walk(s))");
            _action = false;
            int free = hook((IntPtr)inner, 4f, 5f, 0f, 0, 77, 1);
            check.Expect(free == 1 && Fake.Walks == 2 && Fake.Obj == 77, $"control: with the action camera off a click's walk reaches the engine (verdict {free}, {Fake.Walks} walk(s))");
            check.Expect(Fake.This == (IntPtr)inner, $"control: a click's walk keeps the engine's own this (got 0x{Fake.This:x}, Internal 0x{(IntPtr)inner:x})");
        }
        finally
        {
            _walkOrig = orig; _action = action; _walksRefused = refused;
            NativeMemory.Free(app); NativeMemory.Free(inner);
        }
        log.Info(check.Ok ? "ActionCameraWalk.SelfCheck: passed" : "ActionCameraWalk.SelfCheck: FAILED");
        return check.Ok;
    }
}
