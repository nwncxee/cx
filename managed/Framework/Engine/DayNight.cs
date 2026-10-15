using System;
using CX.Framework;

namespace CX.Engine;

/// <summary>The dawn or dusk transition an area is running, if any.</summary>
public enum DayNightPhase { None, Dawn, Dusk }

/// <summary>The area's day/night transition as one value: the two running flags are never both set, since
/// <c>SetAsDawn</c> and <c>SetAsDusk</c> write the pair as one store.</summary>
public readonly unsafe partial struct CNWCArea
{
    /// <summary>The transition this area is running, from <see cref="DawnRunning"/> and <see cref="DuskRunning"/>.</summary>
    public DayNightPhase Transition
        => DawnRunning != 0 ? DayNightPhase.Dawn : DuskRunning != 0 ? DayNightPhase.Dusk : DayNightPhase.None;
}

/// <summary>The client module as the engine reaches it, and its transition clock as a fraction.</summary>
public readonly unsafe partial struct CNWCModule
{
    private static readonly IntPtr AppManagerSym = Native.Sym(SymbolMap.AppManager);

    /// <summary>The client module through <c>g_pAppManager</c> and its client app, or the null view while either
    /// is not up yet (and in a host without the symbol). Two pointer loads and the engine's getter.</summary>
    public static CNWCModule Current
    {
        get
        {
            IntPtr mgr = AppManagerSym == IntPtr.Zero ? IntPtr.Zero : *(IntPtr*)AppManagerSym;
            IntPtr app = mgr == IntPtr.Zero ? IntPtr.Zero : *(IntPtr*)mgr;
            return app == IntPtr.Zero ? default : new CClientExoApp(app).GetModule();
        }
    }

    /// <summary>The transition clock's t, <see cref="TransitionElapsed"/> / <see cref="TransitionLength"/>,
    /// unclamped; 1 when the length is not positive, a transition with nothing left to run.</summary>
    public float TransitionProgress => TransitionLength > 0f ? TransitionElapsed / TransitionLength : 1f;
}
