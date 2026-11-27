using System;
using System.Numerics;

namespace CX.Alloy;

/// <summary>The wisp stage's march in C#, step for step, over the records <see cref="AirScene.PackWisps"/> wrote, its
/// numbers read out of the shipped <c>cx_airwisp_cs</c>; the meadow's regressions run it where no device can.</summary>
internal static class AirWispMarch
{
    /// <summary>The shipped stage's text.</summary>
    internal static string Stage => ShaderSource.Get(AirScene.WispCs + ".glsl");

    /// <summary>The share of what lies behind the wisps that a ray from <paramref name="eye"/> along <paramref name="dir"/>
    /// keeps, out to <paramref name="reach"/> metres, as the stage marches the band <paramref name="w"/> describes.</summary>
    internal static float Keep(in AirWisp w, Vector3 eye, Vector3 dir, float reach = float.MaxValue)
    {
        string stage = Stage;
        int steps = (int)AirDustMarch.Const("CX_WISP_STEPS", stage);
        float far = AirDustMarch.Const("CX_WISP_FAR", stage), clear = AirDustMarch.Const("CX_WISP_CLEAR", stage) * far;
        reach = MathF.Min(reach, far);
        float lo = w.Floor, depth = w.Depth, t0 = 0f, t1 = reach;
        if (MathF.Abs(dir.Z) > 1e-4f)
        {
            float a = (lo - eye.Z) / dir.Z, b = (lo + depth - eye.Z) / dir.Z;
            t0 = MathF.Max(t0, MathF.Min(a, b));
            t1 = MathF.Min(t1, MathF.Max(a, b));
        }
        else if (eye.Z <= lo || eye.Z >= lo + depth) return 1f;
        if (t1 <= t0) return 1f;
        float dt = (t1 - t0) / steps, tau = 0f;
        for (int k = 0; k < steps; k++)
        {
            Vector3 p = eye + dir * (t0 + (k + 0.5f) * dt);
            float z = p.Z - lo;
            float left = Vector2.Dot(new Vector2(p.X, p.Y) - w.Feet, w.Heading) > w.Line ? w.Ahead : w.Behind;
            float t = t0 + (k + 0.5f) * dt, s = Math.Clamp((t - clear) / (far - clear), 0f, 1f), fade = 1f - s * s * (3f - 2f * s);
            tau += w.Thick * left * fade * MathF.Sin(MathF.PI * Math.Clamp(z / depth, 0f, 1f)) * AirScene.StreakAt(p.X, p.Y) * dt;
        }
        return MathF.Exp(-tau);
    }
}
