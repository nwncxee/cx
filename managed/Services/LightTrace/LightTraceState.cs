using System;
using CX.Framework;

namespace CX.Services.LightTrace;

/// <summary>
/// Everything the lighting trace remembers: the log it writes to, the chain slots it calls through, the two
/// sampled triples with the time each last went out, and the transition it believes is running. One owner, so
/// the trace has no loose state to forget.
/// </summary>
internal sealed unsafe class LightTraceState
{
    internal ExtenderLog? Log;

    /// <summary>Lines written since boot, for the <c>/lighttrace</c> status line.</summary>
    internal long Lines;

    /// <summary>What the engine last applied to <c>gidy_sun</c>, and what went out about it when.</summary>
    internal LightTriple Applied, AppliedSent;
    internal long AppliedAt = -1;

    /// <summary>What Alloy last rendered the area with, and what went out about it when.</summary>
    internal LightTriple Alloy, AlloySent;
    internal long AlloyAt = -1;

    /// <summary>The dawn or dusk transition the trace believes is running, null when none is, with the tick it
    /// started on and the length the engine gave it.</summary>
    internal string? Transition;
    internal long TransitionAt;
    internal float TransitionLen;
}
