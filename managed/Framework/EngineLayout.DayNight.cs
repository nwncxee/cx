namespace CX.Framework;

/// <summary>Rows of <see cref="EngineLayout"/> for the day/night machine: the area's cycle and transition
/// state, its two light directions, every light slot's fade target and timer, and the module's clock.</summary>
public static unsafe partial class EngineLayout
{
    // Every displacement below is IDENTICAL on both builds, read off the Windows bodies of the same
    // functions that write them on Linux: SetAsDay (rva 0x7e5390) and SetAsNight (0x7e55f0) for the cycle
    // flag, the two directions and the shadow opacity; SetAreaLightColor (0x7e5010), SetAreaLightDirection
    // (0x7e5250) and SetFogColors (0x7e5a50) for the eight target/timer pairs; SetTimeOfDayState (0x7cfbb0)
    // for the module's three. The LIVE colours and fog already have rows (AreaNightAmbient and friends).

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

    // The eight fade slots. A fade > 0 packet writes the target plus a timer in MILLISECONDS; CNWCArea::Update
    // walks the timers down and lerps (slerps, for a direction) the live value toward the target. Timer 0 means
    // "no fade in flight", which is also what a fade-0 packet leaves behind. The fog AMOUNT has no pair.

    /// <summary>CNWCArea -&gt; the fade TARGET of the night ambient slot (Vector), and its timer in ms.
    /// Slot 0 of <c>SetAreaLightColor</c>; <see cref="AreaNightAmbient"/> is the live value.</summary>
    public static readonly int AreaNightAmbientTarget = Field<float>("CNWCArea", "nightAmbientTarget", count: 3);
    /// <inheritdoc cref="AreaNightAmbientTarget"/>
    public static readonly int AreaNightAmbientFade = Field<int>("CNWCArea", "nightAmbientFade");
    /// <summary>CNWCArea -&gt; the night diffuse slot's fade target and timer (slot 1).</summary>
    public static readonly int AreaNightDiffuseTarget = Field<float>("CNWCArea", "nightDiffuseTarget", count: 3);
    /// <inheritdoc cref="AreaNightDiffuseTarget"/>
    public static readonly int AreaNightDiffuseFade = Field<int>("CNWCArea", "nightDiffuseFade");
    /// <summary>CNWCArea -&gt; the day ambient slot's fade target and timer (slot 2).</summary>
    public static readonly int AreaDayAmbientTarget = Field<float>("CNWCArea", "dayAmbientTarget", count: 3);
    /// <inheritdoc cref="AreaDayAmbientTarget"/>
    public static readonly int AreaDayAmbientFade = Field<int>("CNWCArea", "dayAmbientFade");
    /// <summary>CNWCArea -&gt; the day diffuse slot's fade target and timer (slot 3).</summary>
    public static readonly int AreaDayDiffuseTarget = Field<float>("CNWCArea", "dayDiffuseTarget", count: 3);
    /// <inheritdoc cref="AreaDayDiffuseTarget"/>
    public static readonly int AreaDayDiffuseFade = Field<int>("CNWCArea", "dayDiffuseFade");
    /// <summary>CNWCArea -&gt; the moon direction's fade target and timer (<c>SetAreaLightDirection</c> 0).</summary>
    public static readonly int AreaMoonDirectionTarget = Field<float>("CNWCArea", "moonDirectionTarget", count: 3);
    /// <inheritdoc cref="AreaMoonDirectionTarget"/>
    public static readonly int AreaMoonDirectionFade = Field<int>("CNWCArea", "moonDirectionFade");
    /// <summary>CNWCArea -&gt; the sun direction's fade target and timer (<c>SetAreaLightDirection</c> 1).</summary>
    public static readonly int AreaSunDirectionTarget = Field<float>("CNWCArea", "sunDirectionTarget", count: 3);
    /// <inheritdoc cref="AreaSunDirectionTarget"/>
    public static readonly int AreaSunDirectionFade = Field<int>("CNWCArea", "sunDirectionFade");
    /// <summary>CNWCArea -&gt; the night fog colour's fade target and timer (<c>SetFogColors</c>' second
    /// vector). <see cref="AreaNightFog"/> is the live value.</summary>
    public static readonly int AreaNightFogTarget = Field<float>("CNWCArea", "nightFogTarget", count: 3);
    /// <inheritdoc cref="AreaNightFogTarget"/>
    public static readonly int AreaNightFogFade = Field<int>("CNWCArea", "nightFogFade");
    /// <summary>CNWCArea -&gt; the day fog colour's fade target and timer (<c>SetFogColors</c>' first
    /// vector).</summary>
    public static readonly int AreaDayFogTarget = Field<float>("CNWCArea", "dayFogTarget", count: 3);
    /// <inheritdoc cref="AreaDayFogTarget"/>
    public static readonly int AreaDayFogFade = Field<int>("CNWCArea", "dayFogFade");

    /// <summary>CNWCModule -&gt; the live time-of-day state (byte): 1 day, 2 night, 3 dawn, 4 dusk. This is
    /// also the "day flag" the fog handlers compare against 1 to pick which fog slot a packet writes, so dawn,
    /// dusk and night all take the moon slot. <c>SetTimeOfDayState</c> is its only writer.</summary>
    public static readonly int ModuleTimeOfDayState = Field<byte>("CNWCModule", "timeOfDayState");
    /// <summary>CNWCModule -&gt; seconds elapsed in the current dawn/dusk transition (float).
    /// <c>SetTimeOfDayState</c> seeds it from the packet's milliseconds and <c>Area_ChangeDayNight</c> zeroes
    /// it; <c>UpdateDayNightCycle</c> divides it by the length for its t, and the sky shader reads it too.</summary>
    public static readonly int ModuleTransitionElapsed = Field<float>("CNWCModule", "transitionElapsed");
    /// <summary>CNWCModule -&gt; the transition's length in seconds (float). <c>SetTimeOfDayState</c> recomputes
    /// it as minutes-per-hour x 60 on every call, so a live client keeps the length it logged in with;
    /// <c>Area_ChangeDayNight</c> overwrites it with the packet's own duration.</summary>
    public static readonly int ModuleTransitionLength = Field<float>("CNWCModule", "transitionLength");
}
