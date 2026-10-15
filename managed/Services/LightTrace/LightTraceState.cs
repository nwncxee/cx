using System.Numerics;
using CX.Framework;

namespace CX.Services.LightTrace;

/// <summary>
/// Everything the lighting trace remembers: the log it writes to, the chain slots it calls through, the
/// sampled values with the time each last went out, and the transition and flash it believes are running.
/// One owner, so the trace has no loose state to forget.
/// </summary>
internal sealed unsafe class LightTraceState
{
    internal ExtenderLog? Log;

    /// <summary>Lines written since boot, for the <c>/lighttrace</c> status line.</summary>
    internal long Lines;

    // The chain slots, each the next layer down on its symbol. Filled at Commit; never dereferenced before.
    internal AreaSetAreaLightColorFn* Colour;
    internal AreaSetAreaLightDirectionFn* Direction;
    internal AreaSetFogColorsFn* FogColours;
    internal AreaSetFogAmountFn* FogAmount;
    internal AreaSetGlobalLightAmbientFn* GlobalAmbient;
    internal AreaSetGlobalLightDiffuseFn* GlobalDiffuse;
    internal AreaSetGlobalLightDirectionFn* GlobalDirection;
    internal ModuleSetTimeOfDayStateFn* TimeOfDay;
    internal AreaSetAsDawnFn* AsDawn;
    internal AreaSetAsDuskFn* AsDusk;
    internal AreaSetAsDayFn* AsDay;
    internal AreaSetAsNightFn* AsNight;
    internal AreaUpdateFn* Update;
    internal AreaLoadAreaFn* LoadArea;
    internal HandleServerToPlayerAreaFn* AreaMessage;

    /// <summary>The global shadow tint, <c>shadowalpha</c>; null when the symbol did not resolve.</summary>
    internal float* ShadowAlpha;

    /// <summary>What the engine last applied to <c>gidy_sun</c>, kept per setter because the three arrive on
    /// their own calls.</summary>
    internal Vector3 AppliedAmbient, AppliedDiffuse, AppliedDir;
    /// <summary>The applied triple as it last went out, and when. <see cref="AppliedSeen"/> is false until a
    /// setter has run since the trace came on, so the first line is a real value and not a default.</summary>
    internal LightTriple AppliedSent;
    internal long AppliedAt = -1;
    internal bool AppliedSeen;

    /// <summary>What Alloy last rendered the area with as it last went out, and when.</summary>
    internal LightTriple AlloySent;
    internal long AlloyAt = -1;

    /// <summary>The fog amount the engine last applied, as it last went out, and when.</summary>
    internal float Fog, FogSent;
    internal long FogAt = -1;
    internal bool FogSeen;

    /// <summary>The shadow tint as it last went out, and when.</summary>
    internal float ShadowSent;
    internal long ShadowAt = -1;

    /// <summary>The dawn or dusk transition the trace believes is running, null when none is, and the tick it
    /// was first seen on.</summary>
    internal string? Transition;
    internal long TransitionAt;

    /// <summary>True while the shadow tint sits at the lightning flash's fraction of what the area and any
    /// transition alone would make it.</summary>
    internal bool Flashing;
}
