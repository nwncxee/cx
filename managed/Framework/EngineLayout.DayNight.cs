namespace CX.Framework;

/// <summary>Rows of <see cref="EngineLayout"/> for the day/night machine: the area's cycle and transition
/// state, its two light directions, and the module's transition clock.</summary>
public static unsafe partial class EngineLayout
{
    // Every displacement below is IDENTICAL on both builds, read off the Windows bodies of the same
    // functions that write them on Linux: SetAsDay (rva 0x7e5390) and SetAsNight (0x7e55f0) for the cycle
    // flag, the two directions and the shadow opacity; SetTimeOfDayState (0x7cfbb0) for the module's two. The LIVE colours and fog already have rows (AreaNightAmbient and friends).

    /// <summary>CNWCArea -&gt; "this area runs the day/night cycle" (int). Zero makes the four <c>SetAs*</c>
    /// degrade to "stay on the palette you are on", and the engine's time-of-day broadcasts stop changing the
    /// light; the <c>Area_ChangeDayNight</c> message forces it to 1 for the length of its own call.</summary>
    public static readonly int AreaUsesDayNightCycle = Field<int>("CNWCArea", "usesDayNightCycle", Expect.Byte01);
    /// <summary>CNWCArea -&gt; "a dawn transition is running" (int). <c>SetAsDawn</c> sets it, every other
    /// <c>SetAs*</c> clears it, and <c>UpdateDayNightCycle</c> clears it when t reaches 1.</summary>
    public static readonly int AreaDawnRunning = Field<int>("CNWCArea", "dawnRunning", Expect.Byte01);
    /// <summary>CNWCArea -&gt; the same for dusk, the dword straight after <see cref="AreaDawnRunning"/> --
    /// <c>SetAsDawn</c> and <c>SetAsDusk</c> write the pair as one 8-byte store.</summary>
    public static readonly int AreaDuskRunning = Field<int>("CNWCArea", "duskRunning", Expect.Byte01);
    /// <summary>CNWCArea -&gt; the area's shadow opacity, 0..100 (byte). Every writer of the global
    /// <c>shadowalpha</c> computes it as this / 100, scaled by |2t-1| across a transition.</summary>
    public static readonly int AreaShadowOpacity = Field<byte>("CNWCArea", "shadowOpacity");
    /// <summary>CNWCArea -&gt; the live SUN direction (Vector of floats), what the day palette hands
    /// <c>SetGlobalLightDirection</c>. It points TO the light, which is why the engine places the object at
    /// direction x 9233.093.</summary>
    public static readonly int AreaSunDirection = Field<float>("CNWCArea", "sunDirection", count: 3);
    /// <summary>CNWCArea -&gt; the live MOON direction, <see cref="AreaSunDirection"/>'s night twin.</summary>
    public static readonly int AreaMoonDirection = Field<float>("CNWCArea", "moonDirection", count: 3);
    /// <summary>CNWCArea -&gt; seconds left of the client-side lightning flash (float): positive while one
    /// runs, and -1 when the flash model has no animation. <c>CNWCArea::Update</c> sets it to the flash model's length, counts it down and zeroes it at
    /// the end -- Linux @0x707560 and Windows rva 0x7e7da0, where MSVC inlined <c>SetLightningDone</c> into it.
    /// The next-flash delay sits beside it; the constructor zeroes the pair as one store.</summary>
    public static readonly int AreaLightningFlash = Field<float>("CNWCArea", "lightningFlash");

    /// <summary>CNWCModule -&gt; seconds elapsed in the current dawn/dusk transition (float).
    /// <c>SetTimeOfDayState</c> seeds it from the packet's milliseconds and <c>Area_ChangeDayNight</c> zeroes
    /// it; <c>UpdateDayNightCycle</c> divides it by the length for its t, and the sky shader reads it too.</summary>
    public static readonly int ModuleTransitionElapsed = Field<float>("CNWCModule", "transitionElapsed");
    /// <summary>CNWCModule -&gt; the transition's length in seconds (float). <c>SetTimeOfDayState</c> recomputes
    /// it as minutes-per-hour x 60 on every call, so a live client keeps the length it logged in with;
    /// <c>Area_ChangeDayNight</c> overwrites it with the packet's own duration.</summary>
    public static readonly int ModuleTransitionLength = Field<float>("CNWCModule", "transitionLength");
}
