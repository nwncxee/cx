using CX.Framework;

namespace CX.Services.DayNight;

/// <summary>
/// The server's opt-in for owned day/night. A component property on the policy channel, not a knob: the
/// renderer's knobs stay the player's, and a server that never sends this line gets the vanilla look.
/// </summary>
internal static class DayNightPolicy
{
    /// <summary>The policy line a server sends to own the day/night look (<c>docs/ui-policy-contract.md</c>).</summary>
    internal const string Key = "daynight.server";

    /// <summary>Is owned mode held right now? Cold -- the per-frame path caches this on the policy epoch.</summary>
    internal static bool Read() => ServerPolicy.Bool(Key, false);
}
