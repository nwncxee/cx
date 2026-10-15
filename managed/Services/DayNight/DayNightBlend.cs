using System;
using System.Numerics;

namespace CX.Services.DayNight;

/// <summary>Which transition an area is running: its dawn and dusk flags as one value.</summary>
internal enum Phase { None, Dawn, Dusk }

/// <summary>One of an area's two light palettes -- the four terms a dawn or dusk crossfades.</summary>
internal struct LightSet
{
    public Vector3 Ambient, Diffuse, Fog;
    /// <summary>The fog amount, a byte per palette in the area, an int at the engine's fog setter.</summary>
    public float FogAmount;
}

/// <summary>A palette plus the two render globals a transition drives: the shadow and beam-tint alphas.</summary>
internal struct LightFrame
{
    public LightSet Light;
    public float ShadowAlpha, ProjectionAlpha;
}

/// <summary>
/// The dawn/dusk light arithmetic, engine memory nowhere in it. <see cref="Vanilla"/> is the client's own
/// shaping ported term for term; owned mode crossfades <see cref="Steady"/> endpoints with <see cref="Mix"/>
/// instead, which drops the midpoint dip and starts from what is on screen.
/// </summary>
internal static class DayNightBlend
{
    /// <summary>The beam-tint alpha by day. Night is 0, and a transition runs between the two.</summary>
    internal const float DayProjectionAlpha = 0.05f;
    /// <summary>A palette change that arrives as a cut carries no fade of its own, so owned mode glides it
    /// over this many seconds: long enough to read as a fade, short enough not to lag a correction.</summary>
    internal const float CutFade = 0.5f;
    /// <summary>The divisor the engine applies to the area's shadow opacity byte.</summary>
    private const float OpacityScale = 100f;

    /// <summary>The straight crossfade of two palettes, in the engine's own term order.</summary>
    private static LightSet Lerp(in LightSet from, in LightSet to, float t)
    {
        float u = 1f - t;
        return new LightSet
        {
            Ambient = to.Ambient * t + from.Ambient * u,
            Diffuse = to.Diffuse * t + from.Diffuse * u,
            Fog = to.Fog * t + from.Fog * u,
            FogAmount = to.FogAmount * t + from.FogAmount * u,
        };
    }

    /// <summary>What the engine shows with no transition running: the live palette at full shadows, and the
    /// beam-tint alpha of the palette it is on.</summary>
    internal static LightFrame Steady(in LightSet live, bool isNight, byte opacity) => new()
    {
        Light = live,
        ShadowAlpha = opacity / OpacityScale,
        ProjectionAlpha = isNight ? 0f : DayProjectionAlpha,
    };

    /// <summary>The crossfade owned mode runs: every term of two frames mixed, the alphas with them. A
    /// transition is one of these from what was on screen to the destination's steady frame.</summary>
    internal static LightFrame Mix(in LightFrame from, in LightFrame to, float t)
    {
        float u = 1f - t;
        return new LightFrame
        {
            Light = Lerp(in from.Light, in to.Light, t),
            ShadowAlpha = to.ShadowAlpha * t + from.ShadowAlpha * u,
            ProjectionAlpha = to.ProjectionAlpha * t + from.ProjectionAlpha * u,
        };
    }

    /// <summary>Vanilla's own shaping: the crossfade, then direct light scaled by <c>|2t-1|</c> with half the
    /// loss moved into ambient and the shadows faded out and back in. Owned mode does none of it; this is the
    /// reference the self-check pins and the value owned mode seeds itself from.</summary>
    internal static LightFrame Vanilla(in LightSet from, in LightSet to, float t, Phase phase, byte opacity)
    {
        float tc = MathF.Min(t, 1f);
        var f = new LightFrame
        {
            Light = Lerp(in from, in to, tc),
            ProjectionAlpha = phase == Phase.Dawn ? tc * DayProjectionAlpha : (tc - 1f) * DayProjectionAlpha,
        };
        float s = tc + tc - 1f;
        if (s <= -1f || s > 1f) { f.ShadowAlpha = opacity / OpacityScale; return f; }
        float k = MathF.Abs(s);
        f.ShadowAlpha = opacity * k / OpacityScale;
        if (k >= 1f) return f;
        f.Light.Ambient += f.Light.Diffuse * (1f - k) * 0.5f;
        f.Light.Diffuse *= k;
        return f;
    }

    /// <summary>The shortest-arc turn between two light directions, so a sun-to-moon body change is a turn
    /// rather than a pop. Near-parallel ends mix straight; opposed ends swap at the midpoint, as vanilla does,
    /// because no arc between them is the short one.</summary>
    internal static Vector3 Turn(Vector3 from, Vector3 to, float t)
    {
        if (from.LengthSquared() <= 1e-12f) return to;
        if (to.LengthSquared() <= 1e-12f) return from;
        Vector3 a = Vector3.Normalize(from), b = Vector3.Normalize(to);
        float cos = Math.Clamp(Vector3.Dot(a, b), -1f, 1f);
        float ang = MathF.Acos(cos), sin = MathF.Sin(ang);
        if (sin < 1e-4f) return cos > 0f ? Vector3.Normalize(a + (b - a) * t) : t < 0.5f ? a : b;
        return a * (MathF.Sin((1f - t) * ang) / sin) + b * (MathF.Sin(t * ang) / sin);
    }
}
