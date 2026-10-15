using System.Runtime.InteropServices;
using System.Threading;
using CX.Engine;
using CX.Framework;

namespace CX.Services.Sound;

/// <summary>What the teardown hook needs: the trampoline and the log, for the one call it ever makes.</summary>
internal sealed unsafe class SoundExitState
{
    internal SoundShutDownFn ShutDown;
    internal ExtenderLog? Log;
}

/// <summary>Quitting. <c>CExoSoundInternal::ShutDown</c> destroys the OpenAL context while the engine's own
/// detached stream thread and CX's three audio threads are still running, so CX parks all of them first: its
/// streaming spares leave the engine's stream lists, that thread is told to stop, and the audio core latches off.
/// </summary>
internal static unsafe class SoundExit
{
    /// <summary>Two cycles of the engine's stream thread (it sleeps 10 ms between passes). It is detached and
    /// keeps no join handle, so the stop-flag write plus one full cycle is all there is to wait for.</summary>
    private const int StreamThreadCycleMs = 20;

    private static SoundExitState? _state;
    private static readonly IntPtr StreamThread = Native.Sym(SymbolMap.ExoSoundStreamThread);

    internal static bool Install(ExtenderLog log, HookService hooks)
    {
        if (!SymbolMap.Require(log, "sound teardown", SymbolMap.SoundShutDown, SymbolMap.ExoSoundStreamThread)
            || !EngineLayout.Require(log, "sound teardown", "CExoSoundStreamThread"))
            return false;
        var s = new SoundExitState { Log = log, ShutDown = hooks.InstallSoundShutDown(&OnShutDown) };
        _state = s;
        return s.ShutDown != null;
    }

    [UnmanagedCallersOnly]
    private static void OnShutDown(IntPtr sound)
    {
        SoundExitState s = _state!;   // the hook exists only once the state does, so a null here is loud, not quiet
        int live = SoundCore.cxa_al_live();
        int parked = SoundStreams.Park(out int made);
        int flag = -1;
        if (StreamThread != IntPtr.Zero)
        {
            new CExoSoundStreamThread(StreamThread).Stop = 1;
            BlockingWait.Sleep(StreamThreadCycleMs, "the shutdown path, after the frame loop has stopped");
            flag = new CExoSoundStreamThread(StreamThread).Stop;
        }
        int idle = SoundCore.cxa_shutdown();
        s.Log?.Always($"sound: engine sound teardown -- AL live={live}, stream-thread stop flag={flag}, "
                    + $"CX spares parked={parked}/{made}; parking CX audio and the engine's stream thread "
                    + $"(CX audio threads out of the core: {idle})");
        s.ShutDown(sound);
    }
}
