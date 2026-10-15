using System;
using System.Globalization;
using System.Numerics;

namespace CX.Services.LightTrace;

/// <summary>
/// The lighting trace's pure half: the engine's slot, state and message numbers as names, one stable
/// <c>cx.log</c> line per event, and the once-a-second change-only decision the sampled lines run through.
/// Nothing here reads engine memory or GL -- every value arrives as an argument, so it is all checkable
/// off-device.
/// </summary>
internal static partial class LightTraceLines
{
    /// <summary>The prefix every line carries, so one grep is the whole trace.</summary>
    internal const string Tag = "[lighttrace]";

    /// <summary>Shortest gap between two sampled lines.</summary>
    internal const long PeriodMs = 1000;

    /// <summary>Colour movement below half an 8-bit step is the same colour: the wire carries bytes.</summary>
    internal const float ColourEps = 0.5f / 255f;

    /// <summary>A direction step the server sends is a quarter of a degree or more, about 0.004 per
    /// component; this sits well under it.</summary>
    internal const float DirEps = 1e-3f;

    /// <summary>The area's four colour slots by name. Slots 0/1 write the area's NIGHT ambient and diffuse
    /// pair, 2/3 its DAY pair, which is the branch table of <c>CNWCArea::SetAreaLightColor</c>.</summary>
    internal static string SlotName(uint slot) => slot switch
    {
        0 => "moon-ambient",
        1 => "moon-diffuse",
        2 => "sun-ambient",
        3 => "sun-diffuse",
        _ => "slot" + slot.ToString(CultureInfo.InvariantCulture),
    };

    /// <summary>The area's two light directions by name: slot 0 is the moon's, slot 1 the sun's.</summary>
    internal static string DirSlotName(uint slot) => slot switch
    {
        0 => "moon",
        1 => "sun",
        _ => "slot" + slot.ToString(CultureInfo.InvariantCulture),
    };

    /// <summary>The module's time-of-day state by name, after the <c>SetAs*</c> pair each one runs.</summary>
    internal static string StateName(byte state) => state switch
    {
        1 => "day",
        2 => "night",
        3 => "dawn",
        4 => "dusk",
        _ => "state" + state.ToString(CultureInfo.InvariantCulture),
    };

    /// <summary>The name of an area message minor that can move the area's light, or null for one that
    /// cannot -- so the trace shows a packet ARRIVING even when the area-id test then drops it.</summary>
    internal static string? MinorName(byte minor) => minor switch
    {
        0x01 => "area-entry",
        0x05 => "static-lights",
        0x06 => "change-day-night",
        0x08 => "fog-colour",
        0x09 => "fog-amount",
        0x0e or 0x0f or 0x11 or 0x12 => "light-colour",
        0x10 or 0x13 => "light-direction",
        _ => null,
    };

    internal static string Colour(uint slot, Vector3 rgb, float fade, uint area)
        => FormattableString.Invariant(
            $"{Tag} packet colour slot={SlotName(slot)} rgb={V3(rgb)} fade={fade:F2}s area={Hex(area)}");

    internal static string Direction(uint slot, Vector3 dir, float fade, uint area)
        => FormattableString.Invariant(
            $"{Tag} packet direction slot={DirSlotName(slot)} dir={V3(dir)} fade={fade:F2}s area={Hex(area)}");

    internal static string FogColour(Vector3 sun, Vector3 moon, float fade, uint area)
        => FormattableString.Invariant(
            $"{Tag} packet fog-colour sun={Fog(sun)} moon={Fog(moon)} fade={fade:F2}s area={Hex(area)}");

    internal static string FogAmount(int amount, uint area)
        => FormattableString.Invariant($"{Tag} packet fog-amount n={amount} area={Hex(area)}");

    internal static string AppliedFog(float amount)
        => FormattableString.Invariant($"{Tag} applied fog n={amount:F1}");

    internal static string Shadow(float alpha, int opacity, string cause)
        => FormattableString.Invariant($"{Tag} shadowalpha={alpha:F3} opacity={opacity} {cause}");

    internal static string Lightning(bool flashing)
        => flashing ? $"{Tag} lightning flash -- shadows dimmed" : $"{Tag} lightning done -- shadows restored";

    internal static string Loaded(uint area, int cycle, int night)
        => FormattableString.Invariant($"{Tag} area-loaded area={Hex(area)} cycle={cycle} night={night}");

    internal static string TimeOfDay(byte state, uint elapsedMs, int force)
        => FormattableString.Invariant(
            $"{Tag} time-of-day state={state} {StateName(state)} elapsed={elapsedMs}ms force={force}");

    internal static string Minor(byte minor, string name)
        => FormattableString.Invariant($"{Tag} packet minor=0x{minor:x2} {name}");

    internal static string SetAs(string which, uint area, int cycle, int night)
        => FormattableString.Invariant($"{Tag} set-as {which} area={Hex(area)} cycle={cycle} night={night}");

    internal static string TransitionStart(string which, float lengthSec)
        => FormattableString.Invariant($"{Tag} transition {which} start len={lengthSec:F1}s");

    internal static string TransitionEnd(string which, float afterSec)
        => FormattableString.Invariant($"{Tag} transition {which} end after={afterSec:F1}s");

    internal static string Applied(in LightTriple t)
        => FormattableString.Invariant($"{Tag} applied amb={V3(t.Ambient)} dif={V3(t.Diffuse)} dir={V3(t.Dir)}");

    internal static string Alloy(in LightTriple t)
        => FormattableString.Invariant($"{Tag} alloy amb={V3(t.Ambient)} dif={V3(t.Diffuse)} sun={V3(t.Dir)}");

    /// <summary>Should a sampled line go out? Never twice inside <see cref="PeriodMs"/>, and never while the
    /// values stand still; the first sample of a session always goes, so the log opens with a baseline.</summary>
    internal static bool Due(long nowMs, long lastMs, in LightTriple last, in LightTriple next)
        => lastMs < 0 || (nowMs - lastMs >= PeriodMs && next.Differs(in last));

    /// <summary><see cref="Due"/> for a single number: the fog amount and the shadow tint.</summary>
    internal static bool DueScalar(long nowMs, long lastMs, float last, float next, float eps)
        => lastMs < 0 || (nowMs - lastMs >= PeriodMs && MathF.Abs(next - last) > eps);

    /// <summary>What the shadow tint would be with no lightning: the area's shadow opacity as a fraction,
    /// scaled by |2t-1| while a dawn or dusk transition runs, which is the engine's own arithmetic.</summary>
    internal static float ShadowExpected(int opacity, float t, bool transition)
        => opacity / 100f * (transition ? MathF.Abs(2f * Math.Clamp(t, 0f, 1f) - 1f) : 1f);

    /// <summary>Is the shadow tint sitting at the lightning flash's 40% of <paramref name="expected"/>? The
    /// flash is the only writer that multiplies the tint, so the ratio names it; a tint the transition has
    /// already taken to nothing is no verdict either way, so it reads as not flashing.</summary>
    internal static bool Flashing(float alpha, float expected)
        => expected > 1e-3f && alpha <= 0.7f * expected;

    /// <summary>A fog vector <c>SetFogColors</c> leaves alone carries -1e7 in all three floats.</summary>
    internal static bool FogKept(Vector3 v) => v.X <= -1e6f && v.Y <= -1e6f && v.Z <= -1e6f;

    private static string Fog(Vector3 v) => FogKept(v) ? "keep" : V3(v);

    private static string V3(Vector3 v) => FormattableString.Invariant($"({v.X:F3},{v.Y:F3},{v.Z:F3})");

    private static string Hex(uint id) => "0x" + id.ToString("x8", CultureInfo.InvariantCulture);
}

/// <summary>One sampled lighting triple: an ambient colour, a diffuse colour and a direction, whether they
/// come off <c>gidy_sun</c> or out of Alloy's frame inputs.</summary>
internal readonly struct LightTriple(Vector3 ambient, Vector3 diffuse, Vector3 dir)
{
    internal readonly Vector3 Ambient = ambient;
    internal readonly Vector3 Diffuse = diffuse;
    internal readonly Vector3 Dir = dir;

    /// <summary>Has this triple moved from <paramref name="o"/> by enough to be worth a line?</summary>
    internal bool Differs(in LightTriple o)
        => Moved(Ambient, o.Ambient, LightTraceLines.ColourEps)
        || Moved(Diffuse, o.Diffuse, LightTraceLines.ColourEps)
        || Moved(Dir, o.Dir, LightTraceLines.DirEps);

    private static bool Moved(Vector3 a, Vector3 b, float eps)
        => MathF.Abs(a.X - b.X) > eps || MathF.Abs(a.Y - b.Y) > eps || MathF.Abs(a.Z - b.Z) > eps;
}
